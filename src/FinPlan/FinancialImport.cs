using System.Globalization;
using System.Security.Cryptography;

namespace FinPlan;

public sealed record IngestionSummary(DateTimeOffset ReadAt, int Documents, string[] Formats, List<string> Warnings);

public static class FinancialImport
{
    public static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    public static string Fingerprint(DemoSnapshot snapshot)
    {
        var documents = snapshot.Data.Documents.OrderBy(d => d.Path, StringComparer.Ordinal).Select(d => d.Path + ":" + Hash(Path.Combine(snapshot.Directory, d.Path)));
        var bulk = DocumentApi.ReadVolume(snapshot)?.Files.Where(f => f.Kind != "control").Select(f => CorpusAccounting.Signature(snapshot.Directory, f)) ?? [];
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', documents.Concat(bulk)))));
    }

    public static void Calculate(string output, Dataset data)
    {
        var legacy = data.Obligations.Where(o => o.Kind == "document").ToDictionary(o => o.Id);
        var bindings = data.Documents.ToDictionary(d => d.Path, d => d.Obligations.ToArray());
        var parsed = new Dictionary<string, FinancialDocument>(); var warnings = new List<string>();
        foreach (var doc in data.Documents)
        {
            var path = Path.Combine(output, doc.Path); var sha = Hash(path);
            var report = (doc.Sha256 == sha && doc.Extraction?.ReaderVersion == FinancialTemplateReader.Version ? doc.Extraction : null) ?? FinancialTemplateReader.Read(path);
            if (!report.CanApply) throw new InvalidDataException("Неоднозначные данные распознавания не могут быть включены в расчёт.");
            doc.Sha256 = sha; doc.Extraction = report; parsed[doc.Path] = report;
            doc.Type = report.Type == "contract" && doc.Type == "contract_alternative" ? doc.Type : report.Type;
            doc.DocumentKey = report.Type == "contract" ? ["contract", report.Values["counterparty_id"], report.Values["contract"], report.Values.GetValueOrDefault("contract_date", "")] : [];
            warnings.AddRange(report.Warnings.Select(w => doc.Path + ": " + w));
        }
        var companies = parsed.Values.Where(p=>p.Values.ContainsKey("company")).Select(p=>p.Values["company"]).Distinct().ToArray();
        if (companies.Length > 1) throw new InvalidDataException("Документы относятся к разным компаниям. Проект обрабатывает одну компанию в наборе.");
        if (companies.Length == 1) data.Company = companies[0];
        var (operations, plans, duplicates) = DuplicateChecker.Analyze(output, data.Documents, parsed);
        var contracts = data.Documents.Where(d => parsed[d.Path].Type == "contract").GroupBy(d => Key(parsed[d.Path].Values, "contract")).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var group in contracts.Values) Compatible(group.Select(d => parsed[d.Path].Values), ["amount", "currency", "due", "direction", "counterparty", "account", "invoice", "obligation_id", "scheduled", "contract_date", "invoice_date"], "договоров");
        var invoices = data.Documents.Where(d => parsed[d.Path].Type == "invoice").GroupBy(d => Key(parsed[d.Path].Values, "invoice")).ToList();
        var acts = data.Documents.Where(d => parsed[d.Path].Type == "act").GroupBy(d => Key(parsed[d.Path].Values, "invoice")).ToDictionary(g => g.Key, g => g.ToList());
        var obligations = new List<Obligation>(); var accruals = new List<Accrual>(); var usedActs = new HashSet<string>();
        foreach (var group in invoices)
        {
            var docs = group.ToList(); var invoice = parsed[docs[0].Path].Values;
            Compatible(docs.Select(d => parsed[d.Path].Values), invoice.Keys.ToArray(), "счетов");
            if (!contracts.TryGetValue(Key(invoice, "contract"), out var contractDocs)) throw new InvalidDataException("Для счёта не найден договор с теми же реквизитами.");
            Compatible(contractDocs.Select(d => parsed[d.Path].Values).Append(invoice), ["amount", "currency", "due", "direction", "counterparty", "account", "invoice", "obligation_id", "scheduled", "contract_date", "invoice_date"], "договора и счёта");
            var id = invoice.GetValueOrDefault("obligation_id");
            if (id is null)
            {
                var ids = docs.SelectMany(d => bindings[d.Path]).Distinct().ToArray();
                if (ids.Length != 1 || !legacy.ContainsKey(ids[0])) throw new InvalidDataException("В счёте отсутствует поле «Обязательство ID». Используйте шаблон проекта с этим полем.");
                id = ids[0]; warnings.Add(docs[0].Path + ": внутренний ID старого шаблона взят из индекса документов, а не извлечён из файла.");
            }
            var scheduled = invoice.GetValueOrDefault("scheduled");
            DateOnly scheduledDate;
            if (scheduled is null)
            {
                if (!legacy.TryGetValue(id, out var configured)) throw new InvalidDataException("В счёте отсутствует ожидаемая дата. Заполните поле «Ожидаемая дата».");
                scheduledDate = configured.ScheduledDate;
                warnings.Add(docs[0].Path + ": ожидаемая дата сохранена из ручных настроек старого набора; в исходном файле её нет.");
            }
            else scheduledDate = Day(scheduled);
            var o = new Obligation { Id = id, Contract = invoice["contract"], Invoice = invoice["invoice"], CounterpartyId = invoice["counterparty_id"], Counterparty = invoice["counterparty"], Currency = invoice["currency"], Account = invoice["account"], Direction = invoice["direction"], Amount = Amount(invoice["amount"]), Due = Day(invoice["due"]), ScheduledDate = scheduledDate, Expected = scheduledDate, Kind = "document" };
            if (acts.TryGetValue(group.Key, out var actDocs))
            {
                var values = actDocs.Select(d => parsed[d.Path].Values).ToList();
                Compatible(values.Append(invoice), ["amount", "currency", "due", "direction", "counterparty", "account", "invoice", "contract", "obligation_id", "scheduled", "contract_date", "invoice_date"], "акта и счёта");
                Compatible(values, ["act", "recognized_date", "pnl_category"], "актов");
                var act = values[0];
                if ((o.Direction == "in") != (act["pnl_category"] == "revenue")) throw new InvalidDataException("Направление и статья ОПиУ в акте не согласованы.");
                o.Act = act["act"]; o.RecognizedDate = Day(act["recognized_date"]); o.PnlCategory = act["pnl_category"];
                accruals.Add(new("ACCR-" + o.Act, o.RecognizedDate.Value, o.PnlCategory, o.Amount, o.Currency, o.Counterparty, o.Id, actDocs[0].Path));
                foreach (var doc in actDocs) usedActs.Add(doc.Path);
            }
            foreach (var doc in data.Documents)
            {
                var p = parsed[doc.Path]; var v = p.Values;
                var linked = p.Type == "contract" ? Key(v, "contract") == Key(invoice, "contract") : p.Type is "invoice" or "act" ? Key(v, "invoice") == group.Key : p.Rows.Skip(1).Any(row => row[p.Type == "plan" ? 0 : 8] == id);
                if (linked) o.Sources.Add(doc.Path);
            }
            obligations.Add(o);
        }
        if (data.Documents.Any(d => parsed[d.Path].Type == "act" && !usedActs.Contains(d.Path))) throw new InvalidDataException("Для акта не найден связанный счёт. Акт не включён в расчёт.");
        foreach (var plan in plans) plan.Sources = data.Documents.Where(d => parsed[d.Path].Type == "plan" && parsed[d.Path].Rows.Skip(1).Any(r => r[0] == plan.Id)).Select(d => d.Path).ToList();
        if (obligations.Concat(plans).GroupBy(o => o.Id).Any(g => g.Count() != 1)) throw new InvalidDataException("Повтор внутреннего ID обязательства или плана.");
        var idsKnown = obligations.Concat(plans).Select(o => o.Id).ToHashSet();
        if (operations.Any(t => t.Obligation is not null && !idsKnown.Contains(t.Obligation))) throw new InvalidDataException("Банковская операция ссылается на отсутствующее обязательство.");
        var byId = obligations.Concat(plans).ToDictionary(o=>o.Id);
        foreach (var t in operations)
        {
            if (t.CashCategory != "internal_transfer" && (!FinanceCalculator.CashCategories.TryGetValue(t.CashCategory,out var category) || category.Direction != t.Direction)) throw new InvalidDataException($"Неверная статья ДДС: {t.Source}, строка {t.Row}.");
            if (t.Obligation is not null)
            {
                var o = byId[t.Obligation];
                if (o.Currency != t.Currency || o.Account != t.Account || o.CounterpartyId != t.CounterpartyId || o.Direction != t.Direction) throw new InvalidDataException($"Реквизиты банковской операции не совпадают с обязательством: {t.Source}, строка {t.Row}.");
            }
        }
        foreach (var o in byId.Values)
            if (operations.Where(t=>t.Obligation==o.Id).Sum(t=>t.Amount)>o.Amount) throw new InvalidDataException("Сумма банковских оплат превышает сумму связанного обязательства.");
        foreach (var doc in data.Documents)
        {
            var p = parsed[doc.Path];
            doc.Obligations = p.Type is "plan" or "bank_statement" ? p.Rows.Skip(1).Select(r => r[p.Type == "plan" ? 0 : 8]).Where(s => s != "").Distinct().ToList() : obligations.Where(o => o.Sources.Contains(doc.Path)).Select(o => o.Id).ToList();
        }
        ReconcileStatements(data, parsed, operations);
        data.Obligations = obligations.Concat(plans).ToList(); data.Operations = operations; data.Accruals = accruals; data.Duplicates = duplicates;
        var names = new Dictionary<string, string>();
        void Name(string id, string name) { if (names.TryGetValue(id, out var previous) && previous != name) throw new InvalidDataException("Один ID контрагента имеет разные названия в документах."); names[id] = name; }
        foreach (var o in data.Obligations) Name(o.CounterpartyId, o.Counterparty);
        foreach (var t in operations) Name(t.CounterpartyId, t.Counterparty);
        var registrations = data.Counterparties.ToDictionary(c => c.Id, c => c.Registration);
        data.Counterparties = names.Select(p => new Counterparty(p.Key, p.Value, registrations.GetValueOrDefault(p.Key, "Не указан в шаблоне"))).ToList();
        data.Ingestion = new(DateTimeOffset.UtcNow, data.Documents.Count, parsed.Values.Select(p => p.Format).Distinct().Order().ToArray(), warnings.Distinct().ToList());
        FinanceCalculator.Calculate(data);
    }
    public static string Identity(FinancialDocument report)
    {
        var v = report.Values;
        if (report.Type is "contract" or "invoice" or "act") return report.Type + ":" + Key(v, report.Type);
        if (report.Type == "bank_statement" && v.ContainsKey("period_start")) return report.Type + ":" + v["account"] + ":" + v["period_start"] + ":" + v["period_end"];
        return report.Type + ":" + string.Join("|", report.Rows.Skip(1).Select(r => r[0]).Distinct().Order(StringComparer.Ordinal));
    }
    private static string Key(Dictionary<string, string> values, string name) => values["counterparty_id"] + ":" + values[name];
    private static DateOnly Day(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static decimal Amount(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
    private static void Compatible(IEnumerable<Dictionary<string, string>> records, string[] names, string context)
    {
        foreach (var name in names)
            if (records.Where(v => v.ContainsKey(name)).Select(v => v[name]).Distinct().Skip(1).Any()) throw new InvalidDataException($"Не совпадает поле {name} у связанных {context}. Требуется проверка документов.");
    }
    private static void ReconcileStatements(Dataset data, Dictionary<string, FinancialDocument> parsed, List<Operation> operations)
    {
        var statements = parsed.Values.Where(p => p.Type == "bank_statement" && p.Values.ContainsKey("opening")).ToList();
        foreach (var group in statements.GroupBy(p => p.Values["account"]))
        {
            var first = group.OrderBy(p => p.Values["period_start"], StringComparer.Ordinal).First().Values;
            if (Day(first["period_start"]) != data.OpeningDate) throw new InvalidDataException("Нет выписки с входящим остатком на начало расчётного периода.");
            var opening = Amount(first["opening"]); var currency = first["currency"];
            data.Accounts.RemoveAll(a => a.Id == group.Key); data.Accounts.Add(new(group.Key, currency, opening));
            foreach (var statement in group)
            {
                var v = statement.Values; var start = Day(v["period_start"]); var end = Day(v["period_end"]);
                var selected = statement.Rows.Skip(1).Select(r => r[0]).ToHashSet(); var rows = operations.Where(t => t.Account == group.Key && selected.Contains(t.Id)).ToList();
                if (rows.Any(t => t.Date < start || t.Date > end || t.Currency != currency) || statement.Rows.Skip(1).Any(r => r[2] != group.Key || r[3] != currency)) throw new InvalidDataException("Строка выписки не соответствует её счёту, валюте или периоду.");
                var expected = opening + operations.Where(t => t.Account == group.Key && data.OpeningDate <= t.Date && t.Date < start).Sum(t => t.Direction == "in" ? t.Amount : -t.Amount);
                var incoming = rows.Where(t => t.Direction == "in").Sum(t => t.Amount); var outgoing = rows.Where(t => t.Direction == "out").Sum(t => t.Amount);
                if (expected != Amount(v["opening"]) || incoming != Amount(v["incoming"]) || outgoing != Amount(v["outgoing"]) || expected + incoming - outgoing != Amount(v["closing"]))
                    throw new InvalidDataException("Контрольные остатки или обороты выписки не совпадают с её уникальными операциями.");
            }
        }
    }
}

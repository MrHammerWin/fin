using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
namespace FinPlan;

public static class DuplicateChecker
{
    public static readonly string[] BankHeader = ["ID операции", "Дата", "Счёт", "Валюта", "Контрагент ID", "Контрагент", "Направление", "Сумма", "Обязательство", "Назначение", "Статья ДДС"];
    public static readonly string[] PlanHeader = ["ID плана", "Контрагент ID", "Контрагент", "Счёт", "Валюта", "Направление", "Сумма", "Срок", "Ожидаемая дата", "Назначение"];
    public static (List<Operation> Operations, List<Obligation> Plans, DuplicateReport Report) Analyze(string output, List<Document> documents, IReadOnlyDictionary<string, FinancialDocument>? parsed = null)
    {
        var report = new DuplicateReport(); var hashes = new Dictionary<string, string>(); var semantic = new Dictionary<string, string>();
        var operations = new List<Operation>(); var plans = new List<Obligation>();
        var seenOps = new Dictionary<string, (string Fingerprint, string Source, int Row)>(); var seenPlans = new Dictionary<string, (string Fingerprint, string Source, int Row)>();
        var bankRows = 0; var planRows = 0;
        foreach (var doc in documents)
        {
            var path = Path.Combine(output, doc.Path); doc.Sha256 = FinancialImport.Hash(path);
            var extraction = parsed?.GetValueOrDefault(doc.Path) ?? FinancialTemplateReader.Read(path);
            doc.DocumentKey = extraction.Type == "contract" ? ["contract", extraction.Values["counterparty_id"], extraction.Values["contract"], extraction.Values.GetValueOrDefault("contract_date", "")] : [];
            if (hashes.TryGetValue(doc.Sha256, out var original)) report.Documents.Add(new() { Source = doc.Path, OriginalSource = original, Reason = "Точное совпадение содержимого файла (SHA-256)" });
            else
            {
                hashes[doc.Sha256] = doc.Path;
                var key = JsonSerializer.Serialize(doc.DocumentKey);
                if (doc.DocumentKey.Count > 0 && semantic.TryGetValue(key, out original)) report.Documents.Add(new() { Source = doc.Path, OriginalSource = original, Reason = "Тот же договор в другом формате (по реквизитам, прочитанным из файлов)" });
                else if (doc.DocumentKey.Count > 0) semantic[key] = doc.Path;
            }
            if (extraction.Type is not ("bank_statement" or "plan")) continue;
            var rows = extraction.Rows; var header = extraction.Type == "plan" ? PlanHeader : BankHeader;
            var index = rows.FindIndex(row => row.SequenceEqual(header));
            if (index < 0) throw new InvalidDataException($"Не найдена шапка таблицы {doc.Path}.");
            doc.OperationRows = 0;
            for (var i = index + 1; i < rows.Count; i++)
            {
                var row = rows[i]; if (row.All(string.IsNullOrWhiteSpace)) continue;
                if (row.Length != header.Length) throw new InvalidDataException($"Неверное число колонок: {doc.Path}, строка {i + extraction.TableStartRow}.");
                doc.OperationRows++;
                if (extraction.Type == "bank_statement")
                {
                    bankRows++;
                    var t = new Operation(row[0], DateOnly.ParseExact(row[1], "yyyy-MM-dd", CultureInfo.InvariantCulture), row[2], row[3], row[4], row[5], row[6], decimal.Parse(row[7], CultureInfo.InvariantCulture), row[8] == "" ? null : row[8], row[9], row[10]) { Source = doc.Path, Row = i + extraction.TableStartRow };
                    var fingerprint = JsonSerializer.Serialize(new object?[] { t.Date, t.Currency, t.CounterpartyId, t.Direction, DocumentFiles.Number(t.Amount), t.Obligation, t.Purpose, t.CashCategory });
                    if (Accept(t.Id, t.Account + ":" + t.Id, fingerprint, doc.Path, i + extraction.TableStartRow, seenOps, report.Operations, report.Conflicts)) operations.Add(t);
                }
                else
                {
                    planRows++;
                    var o = new Obligation { Id = row[0], CounterpartyId = row[1], Counterparty = row[2], Account = row[3], Currency = row[4], Direction = row[5], Amount = decimal.Parse(row[6], CultureInfo.InvariantCulture), Due = DateOnly.ParseExact(row[7], "yyyy-MM-dd", CultureInfo.InvariantCulture), Expected = DateOnly.ParseExact(row[8], "yyyy-MM-dd", CultureInfo.InvariantCulture), Purpose = row[9], Kind = "manual_plan", Sources = [doc.Path] };
                    o.ScheduledDate = o.Expected;
                    var fingerprint = JsonSerializer.Serialize(new object?[] { o.Account, o.Currency, o.CounterpartyId, o.Direction, DocumentFiles.Number(o.Amount), o.Due, o.Expected, o.Purpose });
                    if (Accept(o.Id, o.Id, fingerprint, doc.Path, i + extraction.TableStartRow, seenPlans, report.Plans, report.Conflicts)) plans.Add(o);
                }
            }
        }
        report.Summary = new() { ["files"] = documents.Count, ["duplicate_files"] = report.Documents.Count, ["bank_rows"] = bankRows, ["unique_bank_operations"] = operations.Count, ["duplicate_bank_rows"] = report.Operations.Count, ["plan_rows"] = planRows, ["unique_plans"] = plans.Count, ["duplicate_plan_rows"] = report.Plans.Count, ["conflicts"] = report.Conflicts.Count };
        if (report.Conflicts.Count > 0) { JsonFiles.Write(Path.Combine(output, "duplicates.json"), report); throw new InvalidDataException("Конфликт финансовых полей. Расчёт остановлен; см. duplicates.json."); }
        return (operations, plans, report);
    }
    private static bool Accept(string id, string key, string fingerprint, string source, int row, Dictionary<string, (string Fingerprint, string Source, int Row)> seen, List<DuplicateEntry> repeats, List<DuplicateEntry> conflicts)
    {
        if (!seen.TryGetValue(key, out var previous)) { seen[key] = (fingerprint, source, row); return true; }
        var entry = new DuplicateEntry { Id = id, Source = source, Row = row, OriginalSource = previous.Source, OriginalRow = previous.Row, Reason = previous.Fingerprint == fingerprint ? "Совпадает стабильный ID и финансовые поля" : "Один ID, но финансовые поля различаются. Требуется ручная проверка." };
        if (previous.Fingerprint == fingerprint) repeats.Add(entry); else conflicts.Add(entry);
        return false;
    }
}

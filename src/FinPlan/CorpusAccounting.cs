using System.Globalization;
using System.Text.Json;

namespace FinPlan;

public sealed record CorpusCash(DateOnly Date, string Currency, string Account, string Direction, string CashCategory, decimal Amount, long Count);
public sealed record CorpusAccrual(DateOnly RecognizedDate, string Currency, string Category, decimal Amount, long Count);
public sealed class CorpusBucket
{
    public string Currency { get; set; } = "";
    public string Direction { get; set; } = "";
    public DateOnly Due { get; set; }
    public DateOnly ScheduledDate { get; set; }
    public decimal Amount { get; set; }
    public long Count { get; set; }
    public Dictionary<DateOnly, decimal> Payments { get; set; } = [];
    public Dictionary<DateOnly, long> Completed { get; set; } = [];
    public decimal Outstanding(DateOnly opening, DateOnly cutoff) => Amount - Payments.Where(p => opening <= p.Key && p.Key < cutoff).Sum(p => p.Value);
}
public sealed class CorpusSummary
{
    public long Obligations { get; set; }
    public long Operations { get; set; }
    public long Accruals { get; set; }
    public int Files { get; set; }
    public Dictionary<string, int> Formats { get; set; } = [];
    public List<Counterparty> Counterparties { get; set; } = [];
    public Dictionary<string, long> CounterpartyObligations { get; set; } = [];
    public Dictionary<string, long> CounterpartyOperations { get; set; } = [];
    public List<CorpusCash> Cash { get; set; } = [];
    public List<CorpusAccrual> Pnl { get; set; } = [];
    public List<CorpusBucket> Buckets { get; set; } = [];
}
public sealed record CorpusSource(string Path, string Kind, long Bytes, long Modified, string Sha256, long Rows);
public sealed record CorpusPart(int Number, long FirstId, long LastId, long Count, List<VolumeFile> Files);
public sealed class CorpusIndex
{
    public int Version { get; set; } = 2;
    public string Configuration { get; set; } = "";
    public List<CorpusSource> Sources { get; set; } = [];
    public List<CorpusPart> Parts { get; set; } = [];
    public CorpusSummary Summary { get; set; } = new();
}

/// <summary>Scans actual source rows, checks cross-file links, then keeps only additive financial events.</summary>
public static class CorpusAccounting
{
    public const string CacheFile = "corpus-accounting.json";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static long Number(string id) => long.Parse(id.AsSpan(3), NumberStyles.None, Inv);
    public static string Signature(string root, VolumeFile file)
    {
        var info = new FileInfo(Path.Combine(root, file.Path));
        return file.Path + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks;
    }
    public static CorpusIndex Load(string root, VolumeManifest manifest, Dataset data, Action<GenerationProgress>? progress = null, CancellationToken cancellation = default)
    {
        var files = manifest.Files.Where(f => f.Kind != "control").ToList();
        cancellation.ThrowIfCancellationRequested();
        if (files.Any(f => f.Kind is not ("counterparties" or "contracts" or "invoices" or "acts" or "obligations" or "bank"))) throw new InvalidDataException("Неизвестный реестр корпуса.");
        var cache = Path.Combine(root, CacheFile);
        var configuration = data.Company + ":" + data.OpeningDate + ":" + string.Join('|', data.Accounts.Select(a => a.Id + ":" + a.Currency));
        void ValidateCounterparties(CorpusSummary summary)
        {
            var names = summary.Counterparties.ToDictionary(cp => cp.Id, cp => cp.Name);
            if (data.Counterparties.Any(cp => names.TryGetValue(cp.Id, out var name) && name != cp.Name)) throw new InvalidDataException("Разные названия одного контрагента в основных файлах и корпусе.");
        }
        if (data.Obligations.Any(o => o.Id.StartsWith("BO-", StringComparison.Ordinal))) throw new InvalidDataException("ID BO- зарезервированы для обязательств корпуса.");
        CorpusIndex? previous = null;
        if (File.Exists(cache))
        {
            try { previous = JsonSerializer.Deserialize<CorpusIndex>(File.ReadAllText(cache), JsonFiles.Options); }
            catch (JsonException) { }
        }
        if (previous?.Version == 2 && previous.Configuration == configuration && previous.Sources.Count == files.Count)
        {
            var stamps = previous.Sources.ToDictionary(s => s.Path);
            if (files.All(f => stamps.TryGetValue(f.Path, out var source) && source.Kind == f.Kind && Signature(root, f) == source.Path + ":" + source.Bytes + ":" + source.Modified)) { ValidateCounterparties(previous.Summary); return previous; }
        }

        var result = new CorpusIndex { Configuration = configuration }; var summary = result.Summary;
        var master = files.Single(f => f.Kind == "counterparties");
        var cpRows = Read(master, ["counterparty_id", "name", "registration", "company"]);
        foreach (var row in cpRows)
        {
            // The old generator omitted the typographic quotes around this one demo company.
            static string Company(string name) => name.Replace("«", "").Replace("»", "");
            if (Company(row[3]) != Company(data.Company)) throw new InvalidDataException("Корпус относится к другой компании.");
            summary.Counterparties.Add(new(row[0], row[1], row[2]));
        }
        var counterparties = summary.Counterparties.ToDictionary(cp => cp.Id);
        ValidateCounterparties(summary);
        var cash = new Dictionary<(DateOnly, string, string, string, string), (decimal Amount, long Count)>();
        var pnl = new Dictionary<(DateOnly, string, string), (decimal Amount, long Count)>();
        var buckets = new Dictionary<(string, string, DateOnly, DateOnly), CorpusBucket>();
        string[]? carry = null; long lastId = 0; long scanned = master.Bytes;
        var dateCache = new Dictionary<string, DateOnly>();
        DateOnly Date(string value) { if (!dateCache.TryGetValue(value, out var date)) dateCache[value] = date = DateOnly.ParseExact(value, "yyyy-MM-dd", Inv); return date; }
        foreach (var group in files.Where(f => f.Path.StartsWith("bulk/part-", StringComparison.Ordinal)).GroupBy(f => int.Parse(f.Path.Split('-')[1], Inv)).OrderBy(g => g.Key))
        {
            cancellation.ThrowIfCancellationRequested();
            if (group.Count() != 5 || group.Select(f => f.Kind).Distinct().Count() != 5) throw new InvalidDataException("Неполная связанная часть корпуса.");
            VolumeFile Source(string kind) => group.Single(f => f.Kind == kind);
            var contracts = Read(Source("contracts"), ["contract_id", "contract_external_id", "counterparty_id", "date", "currency", "direction", "payment_terms_days", "subject"]).ToDictionary(r => r[0]);
            if (carry is not null && !contracts.ContainsKey(carry[0])) contracts.Add(carry[0], carry);
            var invoices = Read(Source("invoices"), ["invoice_id", "invoice_external_id", "contract_id", "contract_external_id", "counterparty_id", "currency", "account", "direction", "date", "due", "amount", "purpose"]).ToDictionary(r => r[0]);
            var acts = Read(Source("acts"), ["act_id", "act_external_id", "invoice_id", "invoice_external_id", "contract_id", "contract_external_id", "counterparty_id", "obligation_id", "recognized_date", "pnl_category", "currency", "amount"]).ToDictionary(r => r[7]);
            var bankFile = Source("bank");
            var bankRows = Read(bankFile, null); var legacy = bankRows.Header.SequenceEqual(DuplicateChecker.BankHeader);
            if (!legacy && !bankRows.Header.SequenceEqual(new[] { "operation_id", "date", "account", "currency", "counterparty_id", "direction", "amount", "obligation_id", "purpose", "cash_category" })) throw new InvalidDataException("Неизвестная схема банковского реестра корпуса.");
            var payments = new Dictionary<string, List<Operation>>(); var transactionIds = new HashSet<string>();
            for (var i = 0; i < bankRows.Count; i++)
            {
                var r = bankRows[i]; var shift = legacy ? 1 : 0;
                if (!counterparties.TryGetValue(r[4], out var cp) || legacy && r[5] != cp.Name) throw new InvalidDataException("Неизвестный контрагент банковской операции.");
                var t = new Operation(r[0], Date(r[1]), r[2], r[3], r[4], cp.Name, r[5 + shift], Money(r[6 + shift]), r[7 + shift], r[8 + shift], r[9 + shift]) { Source = bankFile.Path, Row = i + 2 };
                if (!transactionIds.Add(t.Id)) throw new InvalidDataException("Повтор ID операции корпуса.");
                if (!payments.TryGetValue(t.Obligation!, out var list)) payments[t.Obligation!] = list = [];
                list.Add(t);
            }
            var obligationsFile = Source("obligations");
            var obligations = Read(obligationsFile, ["obligation_id", "invoice_id", "invoice_external_id", "act_id", "act_external_id", "contract_id", "contract_external_id", "counterparty_id", "currency", "account", "direction", "amount", "paid", "outstanding", "due", "expected"]);
            long first = 0;
            foreach (var r in obligations)
            {
                var id = Number(r[0]); if (!r[0].StartsWith("BO-", StringComparison.Ordinal) || id <= lastId) throw new InvalidDataException("Повтор или неверный порядок ID корпуса.");
                if (first == 0) first = id; lastId = id;
                if (!invoices.Remove(r[1], out var invoice) || !acts.Remove(r[0], out var act) || !contracts.TryGetValue(r[5], out var contract)) throw new InvalidDataException("Разорвана связь договора, счёта, акта и обязательства.");
                var amount = Money(r[11]); var ccy = r[8]; var direction = r[10]; var cp = r[7]; var account = r[9];
                void Equal(string left, string right) { if (left != right) throw new InvalidDataException($"Несогласованные реквизиты корпуса: {r[0]}."); }
                Equal(r[1], $"BI-{id:000000000}"); Equal(r[3], $"BA-{id:000000000}"); Equal(r[5], $"BC-{(id - 1) / 25:000000000}");
                Equal(invoice[1], r[2]); Equal(invoice[2], r[5]); Equal(invoice[3], r[6]); Equal(invoice[4], cp); Equal(invoice[5], ccy); Equal(invoice[6], account); Equal(invoice[7], direction); Equal(invoice[9], r[14]);
                Equal(contract[1], r[6]); Equal(contract[2], cp); Equal(contract[4], ccy); Equal(contract[5], direction);
                Equal(act[0], r[3]); Equal(act[1], r[4]); Equal(act[2], r[1]); Equal(act[3], r[2]); Equal(act[4], r[5]); Equal(act[5], r[6]); Equal(act[6], cp); Equal(act[10], ccy);
                if (Money(invoice[10]) != amount || Money(act[11]) != amount || !counterparties.ContainsKey(cp) || !data.Accounts.Any(a => a.Id == account && a.Currency == ccy) || direction is not ("in" or "out") || !FinanceCalculator.PnlCategories.ContainsKey(act[9])) throw new InvalidDataException("Несогласованные сумма, счёт, валюта или статья корпуса.");
                var due = Date(r[14]); var scheduled = Date(r[15]); var recognized = Date(act[8]);
                if (due < Date(invoice[8]) || Date(contract[3]) > Date(invoice[8]) || scheduled < due || recognized < Date(contract[3])) throw new InvalidDataException("Неверные сроки документов корпуса.");
                var key = (ccy, direction, due, scheduled);
                if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new() { Currency = ccy, Direction = direction, Due = due, ScheduledDate = scheduled };
                bucket.Amount += amount; bucket.Count++; summary.Obligations++;
                summary.CounterpartyObligations[cp] = summary.CounterpartyObligations.GetValueOrDefault(cp) + 1;
                var pk = (recognized, ccy, act[9]); var pv = pnl.GetValueOrDefault(pk); pnl[pk] = (pv.Amount + amount, pv.Count + 1); summary.Accruals++;
                var paid = 0m; DateOnly completed = default;
                if (payments.Remove(r[0], out var operations)) foreach (var t in operations)
                {
                    if (t.Account != account || t.Currency != ccy || t.Direction != direction || t.CounterpartyId != cp || !t.Id.StartsWith($"BT-{id:000000000}-", StringComparison.Ordinal) || t.Date < data.OpeningDate || !FinanceCalculator.CashCategories.TryGetValue(t.CashCategory, out var category) || category.Direction != direction) throw new InvalidDataException("Неверное распределение банковской оплаты корпуса.");
                    paid += t.Amount; if (t.Date > completed) completed = t.Date;
                    bucket.Payments[t.Date] = bucket.Payments.GetValueOrDefault(t.Date) + t.Amount;
                    var ck = (t.Date, ccy, account, direction, t.CashCategory); var cv = cash.GetValueOrDefault(ck); cash[ck] = (cv.Amount + t.Amount, cv.Count + 1); summary.Operations++;
                    summary.CounterpartyOperations[cp] = summary.CounterpartyOperations.GetValueOrDefault(cp) + 1;
                }
                if (paid > amount || Money(r[12], true) != paid || Money(r[13], true) != amount - paid) throw new InvalidDataException("Оплаты и остаток корпуса не сходятся с выпиской.");
                if (paid == amount) bucket.Completed[completed] = bucket.Completed.GetValueOrDefault(completed) + 1;
                carry = contract;
            }
            if (invoices.Count != 0 || acts.Count != 0 || payments.Count != 0) throw new InvalidDataException("Есть документы или оплаты без связанного обязательства.");
            result.Parts.Add(new(group.Key, first, lastId, obligations.Count, group.ToList()));
            scanned += group.Sum(f => f.Bytes);
            progress?.Invoke(new(scanned, files.Sum(f => f.Bytes), summary.Obligations, $"Расчёт всего корпуса: проверена часть {group.Key}"));
            if (group.Key % 100 == 0) Console.WriteLine($"Корпус: {summary.Obligations:N0} обязательств, {scanned:N0} байт проверено");
        }
        summary.Files = files.Count;
        summary.Formats = files.GroupBy(f => Path.GetExtension(f.Path)[1..]).ToDictionary(g => g.Key, g => g.Count());
        summary.Cash = cash.Select(kv => new CorpusCash(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Key.Item4, kv.Key.Item5, kv.Value.Amount, kv.Value.Count)).ToList();
        summary.Pnl = pnl.Select(kv => new CorpusAccrual(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Value.Amount, kv.Value.Count)).ToList();
        summary.Buckets = buckets.Values.ToList();
        var temp = cache + ".tmp";
        try { JsonFiles.Write(temp, result); File.Move(temp, cache, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
        return result;

        RegisterRows Read(VolumeFile source, string[]? header)
        {
            cancellation.ThrowIfCancellationRequested();
            var path = Path.Combine(root, source.Path); var info = new FileInfo(path);
            if (!source.Path.StartsWith("bulk/", StringComparison.Ordinal) || Path.GetFileName(source.Path) != source.Path[5..] || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Неверный путь корпуса.");
            var modified = info.LastWriteTimeUtc.Ticks; var length = info.Length;
            var rows = new RegisterRows();
            foreach (var row in BulkRegister.Read(path))
            {
                if (rows.Header.Length == 0) { rows.Header = row; if (header is not null && !row.SequenceEqual(header)) throw new InvalidDataException("Неизвестная схема реестра: " + source.Path); }
                else { if (row.Length != rows.Header.Length) throw new InvalidDataException("Неполная строка корпуса."); rows.Add(row); }
                if (rows.Count > BulkVolume.RowsPerBatch * 2) throw new InvalidDataException("Превышен размер части корпуса.");
            }
            var hash = FinancialImport.Hash(path); info.Refresh();
            if (info.Length != length || info.LastWriteTimeUtc.Ticks != modified) throw new InvalidDataException("Исходный файл изменился во время чтения.");
            result.Sources.Add(new(source.Path, source.Kind, length, modified, hash, rows.Count));
            return rows;
        }
    }
    private sealed class RegisterRows : List<string[]> { public string[] Header { get; set; } = []; }
    private static decimal Money(string value, bool zero = false)
    {
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, Inv, out var amount) || amount < 0 || !zero && amount == 0 || decimal.Round(amount, 2) != amount) throw new InvalidDataException("Неверная денежная сумма корпуса.");
        return amount;
    }
}

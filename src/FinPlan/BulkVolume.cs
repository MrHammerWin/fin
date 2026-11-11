using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FinPlan;

public sealed record VolumeFile(string Path, string Kind, long Bytes, long Rows, string Sha256);
public sealed record VolumeChange(string Operation, long RequestedBytes, long BeforeBytes);
public sealed class VolumeSummary
{
    public long TargetBytes { get; set; }
    public long TotalBytes { get; set; }
    public long DocumentBytes { get; set; }
    public long Obligations { get; set; }
    public int Files { get; set; }
    public Dictionary<string, int> FormatFiles { get; set; } = [];
    public string Manifest { get; set; } = "volume-manifest.json";
    public VolumeChange? LastChange { get; set; }
    public bool MeasuredTarget { get; set; }
}
public sealed class VolumeManifest
{
    public int Version { get; set; } = 1;
    public int Seed { get; set; }
    public long NextObligationId { get; set; }
    public string Scope { get; set; } = "Все связанные финансовые записи корпуса участвуют в календаре, ДДС и ОПиУ. Форматы новых файлов распределяются поровну по количеству.";
    public VolumeSummary Summary { get; set; } = new();
    public List<VolumeFile> Files { get; set; } = [];
    public Dictionary<string, decimal> InvoiceTotals { get; set; } = [];
    public Dictionary<string, decimal> PaymentTotals { get; set; } = [];
}
public sealed record GenerationProgress(long Bytes, long TargetBytes, long Obligations, string Message);

/// <summary>Bounded-memory, meaningful registers; no padding, sparse files or repeated filler.</summary>
public static class BulkVolume
{
    public const long ToleranceBytes = 100_000_000;
    public const int RowsPerBatch = 20_000;
    public static long BytesIn(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);

    public static VolumeManifest Generate(string output, int seed, long targetBytes, Action<GenerationProgress>? progress = null, CancellationToken cancellation = default)
    {
        if (targetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(targetBytes));
        var total = BytesIn(output);
        if (total > targetBytes + ToleranceBytes) throw new ArgumentException("Целевой объём меньше уже созданного контрольного набора.");
        var bulk = Path.Combine(output, "bulk"); Directory.CreateDirectory(bulk);
        var manifest = new VolumeManifest { Seed = seed, Summary = new() { TargetBytes = targetBytes } };
        foreach (var file in Directory.EnumerateFiles(Path.Combine(output, "documents")))
        {
            using var stream = File.OpenRead(file);
            manifest.Files.Add(new("documents/" + Path.GetFileName(file), "control", stream.Length, 0, Convert.ToHexStringLower(SHA256.HashData(stream))));
        }
        using (var counterparties = new BulkRegister(Path.Combine(bulk, "counterparties.csv"), ["counterparty_id", "name", "registration", "company"]))
        {
            for (var i = 0; i < 1000; i++) counterparties.Row([$"BCP-{i:0000}", $"ООО Учебный поставщик {i:0000}", $"SYNTHETIC-{i:0000}", "ООО Демо Финансы (вымышленная)"]);
            var file = counterparties.Complete("bulk/counterparties.csv", "counterparties"); manifest.Files.Add(file); total += file.Bytes;
        }
        return Append(output, manifest, targetBytes, progress, cancellation);
    }

    public static VolumeManifest Append(string output, VolumeManifest manifest, long targetBytes, Action<GenerationProgress>? progress = null, CancellationToken cancellation = default)
    {
        var total = BytesIn(output);
        manifest.Summary.TargetBytes = targetBytes;
        // Start a new contract group; this also works after a suffix was removed.
        var next = Math.Max(1, manifest.NextObligationId == 0 ? manifest.Summary.Obligations + 1 : manifest.NextObligationId);
        var id = ((next - 1 + 24) / 25) * 25 + 1;
        var batch = manifest.Files.Where(f => f.Kind == "obligations").Select(f => int.Parse(f.Path.Split('-')[1], CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
        var seed = manifest.Seed; var bytesPerRecord = 1500L;
        var fresh = !manifest.Files.Any(f => f.Kind == "obligations");
        var formatCounts = BulkRegister.Formats.ToDictionary(f => f, f => fresh ? manifest.Files.Count(file => Path.GetExtension(file.Path) == "." + f) : 0);
        string Allocate(string kind)
        {
            var format = BulkRegister.Formats.MinBy(f => formatCounts[f])!; formatCounts[format]++;
            return $"bulk/part-{batch:000000}-{kind}.{format}";
        }
        bool Balanced() => formatCounts.Values.Distinct().Count() == 1;
        string[] currencies = ["RUB", "CHF", "GBP"];
        while (total < targetBytes || !Balanced())
        {
            cancellation.ThrowIfCancellationRequested();
            var count = (int)Math.Clamp((targetBytes - total) / Math.Max(1, bytesPerRecord) + 20, 20, RowsPerBatch);
            batch++; var firstId = id;
            var paths = new[] { "contracts", "invoices", "acts", "obligations", "bank" }.ToDictionary(kind => kind, Allocate);
            using var contracts = new BulkRegister(Path.Combine(output, paths["contracts"]), ["contract_id", "contract_external_id", "counterparty_id", "date", "currency", "direction", "payment_terms_days", "subject"]);
            using var invoices = new BulkRegister(Path.Combine(output, paths["invoices"]), ["invoice_id", "invoice_external_id", "contract_id", "contract_external_id", "counterparty_id", "currency", "account", "direction", "date", "due", "amount", "purpose"]);
            using var acts = new BulkRegister(Path.Combine(output, paths["acts"]), ["act_id", "act_external_id", "invoice_id", "invoice_external_id", "contract_id", "contract_external_id", "counterparty_id", "obligation_id", "recognized_date", "pnl_category", "currency", "amount"]);
            using var obligations = new BulkRegister(Path.Combine(output, paths["obligations"]), ["obligation_id", "invoice_id", "invoice_external_id", "act_id", "act_external_id", "contract_id", "contract_external_id", "counterparty_id", "currency", "account", "direction", "amount", "paid", "outstanding", "due", "expected"]);
            using var bank = new BulkRegister(Path.Combine(output, paths["bank"]), ["operation_id", "date", "account", "currency", "counterparty_id", "direction", "amount", "obligation_id", "purpose", "cash_category"]);
            for (var i = 0; i < count; i++, id++)
            {
                if (i % 1000 == 0) cancellation.ThrowIfCancellationRequested();
                var group = (id - 1) / 25; var cp = $"BCP-{group % 1000:0000}"; var currency = currencies[group % 3]; var account = "A-" + currency;
                var direction = group % 2 == 0 ? "out" : "in";
                var contract = $"BC-{group:000000000}"; var contractEid = $"CON-{seed}-{group:000000000}";
                var invoice = $"BI-{id:000000000}"; var invoiceEid = $"INV-{seed}-{id:000000000}";
                var act = $"BA-{id:000000000}"; var actEid = $"ACT-{seed}-{id:000000000}"; var obligation = $"BO-{id:000000000}";
                var recognized = new DateOnly(2026, 10, 1).AddDays((int)(id % 74) + 1);
                var due = recognized.AddDays(7); var expected = due < new DateOnly(2026, 10, 15) ? new DateOnly(2026, 10, 15) : due;
                var cents = 1000L + (long)(Mix((ulong)id ^ unchecked((uint)seed)) % 800_000_000UL);
                var paidCents = (id % 4) switch { 0 => 0, 1 => cents, 2 => cents / 4, _ => cents * 3 / 4 };
                var amount = cents / 100m; var paid = paidCents / 100m;
                var amountText = DocumentFiles.Number(amount);
                var purpose = $"Support services, order {id:000000000}, stage {id % 12 + 1}, invoice {invoiceEid}, contract {contractEid}";
                // Contracts may continue in the next part; only the first invoice creates the contract row.
                if (id % 25 == 1) contracts.Row([contract, contractEid, cp, "2026-10-01", currency, direction, "7", $"Support services for order group {group}"]);
                invoices.Row([invoice, invoiceEid, contract, contractEid, cp, currency, account, direction, "2026-10-01", DocumentFiles.Date(due), amountText, purpose]);
                acts.Row([act, actEid, invoice, invoiceEid, contract, contractEid, cp, obligation, DocumentFiles.Date(recognized), direction == "in" ? "revenue" : "operating_expenses", currency, amountText]);
                obligations.Row([obligation, invoice, invoiceEid, act, actEid, contract, contractEid, cp, currency, account, direction, amountText, DocumentFiles.Number(paid), DocumentFiles.Number(amount - paid), DocumentFiles.Date(due), DocumentFiles.Date(expected)]);
                if (paidCents > 0)
                {
                    var first = id % 3 == 0 ? paidCents / 2 : paidCents;
                    Payment(1, first); if (first != paidCents) Payment(2, paidCents - first);
                }
                manifest.InvoiceTotals[currency] = manifest.InvoiceTotals.GetValueOrDefault(currency) + amount;
                manifest.PaymentTotals[currency] = manifest.PaymentTotals.GetValueOrDefault(currency) + paid;
                void Payment(int part, long paymentCents) => bank.Row([$"BT-{id:000000000}-{part}", $"2026-10-{2 + id % 12:00}", account, currency, cp, direction, DocumentFiles.Number(paymentCents / 100m), obligation, purpose, direction == "in" ? "customer_receipts" : "operating_payments"]);
            }
            var batchFiles = new[] { contracts.Complete(paths["contracts"], "contracts"), invoices.Complete(paths["invoices"], "invoices"), acts.Complete(paths["acts"], "acts"), obligations.Complete(paths["obligations"], "obligations"), bank.Complete(paths["bank"], "bank") };
            var written = batchFiles.Sum(f => f.Bytes); total += written; bytesPerRecord = written / (id - firstId);
            manifest.Files.AddRange(batchFiles);
            manifest.Summary.Obligations += id - firstId;
            manifest.NextObligationId = id;
            progress?.Invoke(new(total, targetBytes, manifest.Summary.Obligations, $"Созданы связанные реестры, часть {batch}"));
        }
        manifest.Summary.DocumentBytes = manifest.Files.Sum(f => f.Bytes);
        manifest.Summary.Files = manifest.Files.Count;
        return manifest;
    }

    public static long RemovableBytes(VolumeManifest manifest) => manifest.Files.Where(f => f.Path.StartsWith("bulk/part-", StringComparison.Ordinal)).Sum(f => f.Bytes);

    public static void RemoveSuffix(string output, VolumeManifest manifest, long bytes, Action<GenerationProgress>? progress = null, CancellationToken cancellation = default)
    {
        if (bytes <= 0 || bytes > RemovableBytes(manifest)) throw new ArgumentException("Нельзя удалить такой объём: контрольные документы и справочник контрагентов сохраняются.");
        manifest.NextObligationId = Math.Max(manifest.NextObligationId, manifest.Summary.Obligations + 1);
        var groups = manifest.Files.Where(f => f.Path.StartsWith("bulk/part-", StringComparison.Ordinal))
            .GroupBy(f => f.Path[..f.Path.LastIndexOf('-')]).OrderByDescending(g => int.Parse(g.Key.Split('-')[1], CultureInfo.InvariantCulture)).ToList();
        long removed = 0; var deleted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            cancellation.ThrowIfCancellationRequested();
            var partBytes = group.Sum(f => f.Bytes);
            if (removed > 0 && Math.Abs(removed - bytes) <= Math.Abs(removed + partBytes - bytes)) break;
            if (partBytes > ToleranceBytes / 2) throw new InvalidDataException("Часть корпуса слишком велика для безопасного удаления с допуском 100 МБ.");
            var obligations = group.Single(f => f.Kind == "obligations");
            // A part has at most 20,000 obligations. Only the removed parts are read.
            foreach (var row in BulkRegister.Read(Path.Combine(output, obligations.Path)).Skip(1))
            {
                cancellation.ThrowIfCancellationRequested();
                var currency = row[8];
                manifest.InvoiceTotals[currency] -= decimal.Parse(row[11], CultureInfo.InvariantCulture);
                manifest.PaymentTotals[currency] -= decimal.Parse(row[12], CultureInfo.InvariantCulture);
            }
            foreach (var file in group) { File.Delete(Path.Combine(output, file.Path)); deleted.Add(file.Path); }
            manifest.Summary.Obligations -= obligations.Rows;
            removed += partBytes;
            progress?.Invoke(new(removed, bytes, manifest.Summary.Obligations, "Удалены последние связанные части корпуса"));
            if (removed >= bytes) break;
        }
        manifest.Files.RemoveAll(file => deleted.Contains(file.Path));
        manifest.Summary.DocumentBytes = manifest.Files.Sum(f => f.Bytes);
        manifest.Summary.Files = manifest.Files.Count;
    }

    public static void Finalize(string output, Dataset data, VolumeManifest manifest, DemoGenerator generator, Action<GenerationProgress>? progress = null, CancellationToken cancellation = default)
    {
        data.Volume = manifest.Summary;
        manifest.Scope = "Все записи корпуса включены в календарь, ДДС и ОПиУ; связанные документы учитываются один раз.";
        var index = CorpusAccounting.Load(output, manifest, data, progress, cancellation);
        data.Corpus = index.Summary;
        manifest.Summary.Obligations = data.Corpus.Obligations;
        manifest.InvoiceTotals = data.Corpus.Buckets.GroupBy(b => b.Currency).ToDictionary(g => g.Key, g => g.Sum(b => b.Amount));
        manifest.PaymentTotals = data.Corpus.Cash.GroupBy(t => t.Currency).ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));
        var sources = index.Sources.ToDictionary(s => s.Path);
        manifest.Files = manifest.Files.Select(f => sources.TryGetValue(f.Path, out var source) ? new VolumeFile(f.Path, f.Kind, source.Bytes, source.Rows, source.Sha256) : f).ToList();
        manifest.Summary.DocumentBytes = manifest.Files.Sum(f => f.Bytes); manifest.Summary.Files = manifest.Files.Count;
        manifest.Summary.FormatFiles = manifest.Files.GroupBy(f => Path.GetExtension(f.Path)[1..]).ToDictionary(g => g.Key, g => g.Count());
        FinanceCalculator.Calculate(data); generator.Save(output, data);
        // Include JSON, HTML, manifests and control files in the measured total. Iterate until digit widths settle.
        for (var pass = 0; pass < 6; pass++)
        {
            JsonFiles.Write(Path.Combine(output, manifest.Summary.Manifest), manifest);
            JsonFiles.Write(Path.Combine(output, "dataset.json"), data); generator.RenderViewer(output, data);
            var total = BytesIn(output);
            var targetChanged = manifest.Summary.MeasuredTarget && manifest.Summary.TargetBytes != total;
            if (total == manifest.Summary.TotalBytes && !targetChanged)
            {
                var difference = total - manifest.Summary.TargetBytes;
                if (!manifest.Summary.MeasuredTarget && (difference > ToleranceBytes || difference < (manifest.Summary.LastChange?.Operation == "remove" ? -ToleranceBytes : 0)))
                    throw new InvalidDataException("Фактический объём вышел за допустимый диапазон относительно цели.");
                return;
            }
            manifest.Summary.TotalBytes = total;
            if (manifest.Summary.MeasuredTarget) manifest.Summary.TargetBytes = total;
        }
        throw new InvalidOperationException("Не удалось зафиксировать точный объём набора.");
    }

    private static ulong Mix(ulong value)
    {
        value += 0x9e3779b97f4a7c15UL; value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL; return value ^ (value >> 31);
    }

}

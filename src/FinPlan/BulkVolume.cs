using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FinPlan;

public sealed record VolumeFile(string Path, string Kind, long Bytes, long Rows, string Sha256);
public sealed class VolumeSummary
{
    public long TargetBytes { get; set; }
    public long TotalBytes { get; set; }
    public long DocumentBytes { get; set; }
    public long Obligations { get; set; }
    public int Files { get; set; }
    public string Manifest { get; set; } = "volume-manifest.json";
}
public sealed class VolumeManifest
{
    public int Version { get; set; } = 1;
    public int Seed { get; set; }
    public string Scope { get; set; } = "Связанный синтетический корпус для импорта и нагрузки. Календарь содержит отдельный контрольный набор из 100 документов; строки корпуса автоматически в него не импортируются.";
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
        using (var counterparties = new Register(Path.Combine(bulk, "counterparties.csv"), ["counterparty_id", "name", "registration", "company"]))
        {
            for (var i = 0; i < 1000; i++) counterparties.Row([$"BCP-{i:0000}", $"ООО Учебный поставщик {i:0000}", $"SYNTHETIC-{i:0000}", "ООО Демо Финансы (вымышленная)"]);
            var file = counterparties.Complete("bulk/counterparties.csv", "counterparties"); manifest.Files.Add(file); total += file.Bytes;
        }
        long id = 1; var batch = 0; var bytesPerRecord = 1500L;
        string[] currencies = ["RUB", "CHF", "GBP"];
        while (total < targetBytes)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = (int)Math.Clamp((targetBytes - total) / Math.Max(1, bytesPerRecord) + 20, 20, RowsPerBatch);
            var prefix = $"part-{++batch:000000}"; var firstId = id;
            using var contracts = new Register(Path.Combine(bulk, prefix + "-contracts.csv"), ["contract_id", "contract_external_id", "counterparty_id", "date", "currency", "direction", "payment_terms_days", "subject"]);
            using var invoices = new Register(Path.Combine(bulk, prefix + "-invoices.csv"), ["invoice_id", "invoice_external_id", "contract_id", "contract_external_id", "counterparty_id", "currency", "account", "direction", "date", "due", "amount", "purpose"]);
            using var acts = new Register(Path.Combine(bulk, prefix + "-acts.csv"), ["act_id", "act_external_id", "invoice_id", "invoice_external_id", "contract_id", "contract_external_id", "counterparty_id", "obligation_id", "recognized_date", "pnl_category", "currency", "amount"]);
            using var obligations = new Register(Path.Combine(bulk, prefix + "-obligations.csv"), ["obligation_id", "invoice_id", "invoice_external_id", "act_id", "act_external_id", "contract_id", "contract_external_id", "counterparty_id", "currency", "account", "direction", "amount", "paid", "outstanding", "due", "expected"]);
            using var bank = new Register(Path.Combine(bulk, prefix + "-bank.csv"), DuplicateChecker.BankHeader);
            for (var i = 0; i < count; i++, id++)
            {
                if (i % 1000 == 0) cancellation.ThrowIfCancellationRequested();
                var group = (id - 1) / 25; var cp = $"BCP-{group % 1000:0000}"; var currency = currencies[group % 3]; var account = "A-" + currency;
                var direction = group % 2 == 0 ? "out" : "in";
                var contract = $"BC-{group:000000000}"; var contractEid = $"Д-{seed}-{group:000000000}";
                var invoice = $"BI-{id:000000000}"; var invoiceEid = $"СЧ-{seed}-{id:000000000}";
                var act = $"BA-{id:000000000}"; var actEid = $"АКТ-{seed}-{id:000000000}"; var obligation = $"BO-{id:000000000}";
                var recognized = new DateOnly(2026, 10, 1).AddDays((int)(id % 74) + 1);
                var due = recognized.AddDays(7); var expected = due < new DateOnly(2026, 10, 15) ? new DateOnly(2026, 10, 15) : due;
                var cents = 1000L + (long)(Mix((ulong)id ^ unchecked((uint)seed)) % 800_000_000UL);
                var paidCents = (id % 4) switch { 0 => 0, 1 => cents, 2 => cents / 4, _ => cents * 3 / 4 };
                var amount = cents / 100m; var paid = paidCents / 100m;
                var amountText = DocumentFiles.Number(amount);
                var purpose = $"Услуги сопровождения, заказ {id:000000000}, этап {id % 12 + 1}, счёт {invoiceEid}, договор {contractEid}";
                // Contracts may continue in the next part; only the first invoice creates the contract row.
                if (id % 25 == 1) contracts.Row([contract, contractEid, cp, "2026-10-01", currency, direction, "7", $"Сопровождение учебных заказов группы {group}"]);
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
                void Payment(int part, long paymentCents) => bank.Row([$"BT-{id:000000000}-{part}", $"2026-10-{2 + id % 12:00}", account, currency, cp, $"ООО Учебный поставщик {group % 1000:0000}", direction, DocumentFiles.Number(paymentCents / 100m), obligation, purpose, direction == "in" ? "customer_receipts" : "operating_payments"]);
            }
            var batchFiles = new[] { contracts.Complete($"bulk/{prefix}-contracts.csv", "contracts"), invoices.Complete($"bulk/{prefix}-invoices.csv", "invoices"), acts.Complete($"bulk/{prefix}-acts.csv", "acts"), obligations.Complete($"bulk/{prefix}-obligations.csv", "obligations"), bank.Complete($"bulk/{prefix}-bank.csv", "bank") };
            var written = batchFiles.Sum(f => f.Bytes); total += written; bytesPerRecord = written / (id - firstId);
            manifest.Files.AddRange(batchFiles);
            manifest.Summary.Obligations = id - 1;
            progress?.Invoke(new(total, targetBytes, id - 1, $"Созданы связанные реестры, часть {batch}"));
        }
        manifest.Summary.DocumentBytes = manifest.Files.Sum(f => f.Bytes);
        manifest.Summary.Files = manifest.Files.Count;
        return manifest;
    }

    public static void Finalize(string output, Dataset data, VolumeManifest manifest, DemoGenerator generator)
    {
        data.Volume = manifest.Summary;
        // Include JSON, HTML, manifests and control files in the measured total. Iterate until digit widths settle.
        for (var pass = 0; pass < 6; pass++)
        {
            JsonFiles.Write(Path.Combine(output, manifest.Summary.Manifest), manifest);
            JsonFiles.Write(Path.Combine(output, "dataset.json"), data); generator.RenderViewer(output, data);
            var total = BytesIn(output);
            if (total == manifest.Summary.TotalBytes)
            {
                if (total < manifest.Summary.TargetBytes || total - manifest.Summary.TargetBytes > ToleranceBytes)
                    throw new InvalidDataException("Фактический объём вышел за диапазон [цель; цель + 100 МБ].");
                return;
            }
            manifest.Summary.TotalBytes = total;
        }
        throw new InvalidOperationException("Не удалось зафиксировать точный объём набора.");
    }

    private static ulong Mix(ulong value)
    {
        value += 0x9e3779b97f4a7c15UL; value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL; return value ^ (value >> 31);
    }

    private sealed class Register : IDisposable
    {
        private readonly FileStream file;
        private readonly SHA256 hash = SHA256.Create();
        private readonly CryptoStream crypto;
        private readonly StreamWriter writer;
        private long rows;
        private bool completed;
        public Register(string path, string[] header)
        {
            file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.SequentialScan);
            crypto = new(file, hash, CryptoStreamMode.Write, leaveOpen: true);
            writer = new(crypto, new UTF8Encoding(true), 128 * 1024, leaveOpen: true) { NewLine = "\r\n" };
            Write(header);
        }
        public void Row(string[] values) { Write(values); rows++; }
        private void Write(string[] values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0) writer.Write(';');
                var value = values[i];
                if (value.IndexOfAny([';', '"', '\r', '\n']) >= 0) { writer.Write('"'); writer.Write(value.Replace("\"", "\"\"")); writer.Write('"'); }
                else writer.Write(value);
            }
            writer.WriteLine();
        }
        public VolumeFile Complete(string relativePath, string kind)
        {
            writer.Flush(); crypto.FlushFinalBlock(); file.Flush(); completed = true;
            return new(relativePath, kind, file.Length, rows, Convert.ToHexStringLower(hash.Hash!));
        }
        public void Dispose()
        {
            writer.Dispose(); if (!completed) crypto.FlushFinalBlock(); crypto.Dispose(); file.Dispose(); hash.Dispose();
        }
    }
}

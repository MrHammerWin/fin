using System.Globalization;
using System.Text.Json;
using FinPlan;
using UglyToad.PdfPig;
using Xunit;

namespace FinPlan.Tests;

public sealed class CorpusTests(ReaderFixture fixture) : IClassFixture<ReaderFixture>
{
    private static Dataset Clone(Dataset data) => JsonSerializer.Deserialize<Dataset>(JsonSerializer.Serialize(data, JsonFiles.Options), JsonFiles.Options)!;
    [Fact]
    public void AllFourRegisterFormatsContainTheSameVisibleCells()
    {
        var root = fixture.NewDirectory();
        string[][] expected = [["obligation_id", "amount", "purpose"], ["BO-000000001", "1234.56", "Support services; stage 1 (invoice INV-42-001)"], ["BO-000000002", "0.01", "Quoted \"contract\" and backslash \\ remain visible"]];
        expected = expected.Concat(Enumerable.Range(3, 200).Select(i => new[] { $"BO-{i:000000000}", "1234.56", "Support services with an extended visible purpose, contract CON-42-001 and invoice INV-42-001, delivery stage " + i })).ToArray();
        foreach (var format in BulkRegister.Formats)
        {
            var path = Path.Combine(root, "register." + format);
            using var register = new BulkRegister(path, expected[0]); foreach (var row in expected.Skip(1)) register.Row(row);
            register.Complete("register." + format, "obligations");
            Assert.Equal(expected.Select(r => string.Join('|', r)), BulkRegister.Read(path).Select(r => string.Join('|', r)));
            if (format == "pdf")
            {
                using var pdf = PdfDocument.Open(path);
                Assert.True(pdf.NumberOfPages > 1);
                var text = string.Concat(pdf.GetPages().Select(page => page.Text));
                foreach (var row in expected.Skip(1)) Assert.Contains(row[0], text);
                Assert.All(pdf.GetPages().SelectMany(page => page.Letters), letter => { Assert.True(letter.BoundingBox.Bottom >= 0); Assert.True(letter.BoundingBox.Right <= 1191); });
            }
            else Assert.Equal(expected.Select(r => string.Join('|', r)), DocumentContentReader.Read(path).Sections[0].Rows.Select(r => string.Join('|', r)));
        }
    }
    [Fact]
    public async Task EverySourceRowParticipatesAndAggregatesMatchAnExpandedLedgerAtDifferentCutoffs()
    {
        var store = new DemoStore(fixture.Root, fixture.NewDirectory()); var snapshot = await store.RegenerateAsync(101, 25_000_000);
        var manifest = DocumentApi.ReadVolume(snapshot)!; var corpus = snapshot.Data.Corpus!;
        var counts = BulkRegister.Formats.Select(f => manifest.Files.Count(file => Path.GetExtension(file.Path) == "." + f)).ToArray();
        Assert.True(counts[0] > 0); Assert.Single(counts.Distinct());
        Assert.Equal(corpus.Obligations, manifest.Summary.Obligations); Assert.True(corpus.Obligations > 100);
        var expanded = Clone(snapshot.Data); expanded.Corpus = null;
        var index = CorpusApi.Index(snapshot);
        foreach (var part in index.Parts)
        {
            var (os, bank, pnl) = CorpusApi.ReadPart(snapshot, part, snapshot.Data.Cutoff);
            var first = os[0]; var contractSource = first.Sources.Single(path => path.Contains("-contracts.", StringComparison.Ordinal));
            Assert.Contains(BulkRegister.Read(Path.Combine(snapshot.Directory, contractSource)).Skip(1), row => row[0] == first.ContractId && row[1] == first.Contract);
            expanded.Obligations.AddRange(os); expanded.Operations.AddRange(bank); expanded.Accruals.AddRange(pnl);
        }
        foreach (var date in new[] { new DateOnly(2026, 10, 15), new(2026, 10, 29), new(2026, 11, 15), new(2026, 12, 1), new(2026, 12, 31) })
        {
            var compact = Clone(snapshot.Data); compact.Cutoff = expanded.Cutoff = date;
            FinanceCalculator.Calculate(compact); FinanceCalculator.Calculate(expanded);
            Assert.Equal(JsonSerializer.Serialize(expanded.Calendar, JsonFiles.Options), JsonSerializer.Serialize(compact.Calendar, JsonFiles.Options));
            Assert.Equal(JsonSerializer.Serialize(expanded.Reports, JsonFiles.Options), JsonSerializer.Serialize(compact.Reports, JsonFiles.Options));
            foreach (var status in new[] { "all", "paid", "overdue", "in_progress" })
            {
                var page = CorpusApi.Page(new(snapshot.Id, snapshot.Directory, Clone(snapshot.Data)), "obligations", 0, 37, "RUB", status, "all", "", date);
                Assert.Equal(expanded.Obligations.LongCount(o => o.Currency == "RUB" && (status == "all" || o.Status == status)), page.Total);
                Assert.InRange(page.Obligations.Count, 0, 37);
            }
        }
        var last = index.Parts.Last(); var target = $"BO-{last.LastId:000000000}";
        var found = CorpusApi.Page(new(snapshot.Id, snapshot.Directory, Clone(snapshot.Data)), "obligations", 0, 50, "TOTAL", "all", "all", target, snapshot.Data.Cutoff);
        var o = Assert.Single(found.Obligations); Assert.Equal(target, o.Id); Assert.NotNull(o.InvoiceId); Assert.NotNull(o.ActId); Assert.NotEmpty(o.Sources);
        // The generated fixture allows the independent JS calculator to check the exact C# results too.
        var parity = Path.Combine(fixture.Root, ".test-artifacts", "corpus-parity.json"); Directory.CreateDirectory(Path.GetDirectoryName(parity)!); JsonFiles.Write(parity, snapshot.Data);
    }
    [Fact]
    public async Task ManifestTotalsAreNotFinancialInputsAndChangedSourceRowsInvalidateTheCache()
    {
        var store = new DemoStore(fixture.Root, fixture.NewDirectory()); var snapshot = await store.RegenerateAsync(102, 20_000_000);
        var manifest = DocumentApi.ReadVolume(snapshot)!; var original = snapshot.Data.Corpus!;
        manifest.InvoiceTotals = new() { ["RUB"] = 1 }; manifest.PaymentTotals = new() { ["RUB"] = 999999999999999 };
        File.Delete(Path.Combine(snapshot.Directory, CorpusAccounting.CacheFile));
        var reloaded = CorpusAccounting.Load(snapshot.Directory, manifest, snapshot.Data);
        Assert.Equal(original.Buckets.Sum(b => b.Amount), reloaded.Summary.Buckets.Sum(b => b.Amount));
        var act = manifest.Files.First(f => f.Kind == "acts"); var path = Path.Combine(snapshot.Directory, act.Path);
        var rows = BulkRegister.Read(path).ToList(); var amount = decimal.Parse(rows[1][11], CultureInfo.InvariantCulture); var date = rows[1][8]; var currency = rows[1][10]; var category = rows[1][9];
        rows[1][9] = "cost_of_sales";
        File.Delete(path); using (var writer = new BulkRegister(path, rows[0])) { foreach (var r in rows.Skip(1)) writer.Row(r); writer.Complete(act.Path, act.Kind); }
        var changed = CorpusAccounting.Load(snapshot.Directory, manifest, snapshot.Data);
        Assert.Equal(amount, changed.Summary.Pnl.Where(a => a.Currency == currency && a.RecognizedDate.ToString("yyyy-MM-dd") == date && a.Category == "cost_of_sales").Sum(a => a.Amount));
        Assert.NotEqual(JsonSerializer.Serialize(original.Pnl, JsonFiles.Options), JsonSerializer.Serialize(changed.Summary.Pnl, JsonFiles.Options));
        var invoice = manifest.Files.First(f => f.Kind == "invoices"); path = Path.Combine(snapshot.Directory, invoice.Path); rows = BulkRegister.Read(path).ToList(); rows[1][10] = "0.01";
        File.Delete(path); using (var writer = new BulkRegister(path, rows[0])) { foreach (var r in rows.Skip(1)) writer.Row(r); writer.Complete(invoice.Path, invoice.Kind); }
        Assert.Throws<InvalidDataException>(() => CorpusAccounting.Load(snapshot.Directory, manifest, snapshot.Data));
    }
}

using System.Globalization;
using System.Security.Cryptography;
using FinPlan;
using Xunit;

namespace FinPlan.Tests;

public sealed class DatasetResizeTests : IClassFixture<DemoFixture>
{
    private readonly string root;
    public DatasetResizeTests(DemoFixture _)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Solution1.sln"))) dir = dir.Parent;
        root = dir!.FullName;
    }
    private static string Workspace() => Path.Combine(Path.GetTempPath(), "finplan-resize-" + Guid.NewGuid().ToString("N"));
    private static Dictionary<string, string> Hashes(DemoSnapshot snapshot) => Directory.GetFiles(snapshot.Directory, "*", SearchOption.AllDirectories)
        .Where(p => p.Contains(Path.DirectorySeparatorChar + "documents" + Path.DirectorySeparatorChar) || p.Contains(Path.DirectorySeparatorChar + "bulk" + Path.DirectorySeparatorChar))
        .ToDictionary(p => Path.GetRelativePath(snapshot.Directory, p), p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))));
    private static void Preserved(Dictionary<string, string> hashes, DemoSnapshot next)
    {
        foreach (var (path, hash) in hashes) Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(next.Directory, path)))));
    }
    private static void Verify(DemoSnapshot snapshot)
    {
        var manifest = DocumentApi.ReadVolume(snapshot)!;
        Assert.Equal(BulkVolume.BytesIn(snapshot.Directory), manifest.Summary.TotalBytes);
        Assert.Equal(manifest.Files.Count, manifest.Summary.Files);
        Assert.Equal(manifest.Files.Sum(f => f.Bytes), manifest.Summary.DocumentBytes);
        var contracts = new HashSet<string>(); var counterparties = new HashSet<string>();
        var invoices = new Dictionary<string, string[]>(); var acts = new Dictionary<string, string[]>();
        var obligations = new Dictionary<string, string[]>(); var bank = new Dictionary<string, decimal>();
        var totals = new Dictionary<string, decimal>(); var paidTotals = new Dictionary<string, decimal>();
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(snapshot.Directory, file.Path);
            Assert.Equal(file.Bytes, new FileInfo(path).Length);
            Assert.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
            if (file.Kind == "control") continue;
            var rows = DocumentContentReader.ReadCsv(path).Skip(1).ToList(); Assert.Equal(file.Rows, rows.Count);
            foreach (var row in rows)
                switch (file.Kind)
                {
                    case "counterparties": Assert.True(counterparties.Add(row[0])); break;
                    case "contracts": Assert.True(contracts.Add(row[0])); Assert.Contains(row[2], counterparties); break;
                    case "invoices": Assert.True(invoices.TryAdd(row[0], row)); Assert.Contains(row[2], contracts); Assert.Contains(row[4], counterparties); break;
                    case "acts": Assert.True(acts.TryAdd(row[0], row)); Assert.Equal(invoices[row[2]][10], row[11]); break;
                    case "obligations":
                        Assert.True(obligations.TryAdd(row[0], row)); Assert.Contains(row[5], contracts); Assert.Equal(invoices[row[1]][10], row[11]);
                        Assert.Equal(acts[row[3]][7], row[0]);
                        var amount = decimal.Parse(row[11], CultureInfo.InvariantCulture); var paid = decimal.Parse(row[12], CultureInfo.InvariantCulture);
                        Assert.Equal(amount - paid, decimal.Parse(row[13], CultureInfo.InvariantCulture));
                        totals[row[8]] = totals.GetValueOrDefault(row[8]) + amount; paidTotals[row[8]] = paidTotals.GetValueOrDefault(row[8]) + paid;
                        break;
                    case "bank":
                        Assert.Contains(row[8], obligations.Keys); bank[row[8]] = bank.GetValueOrDefault(row[8]) + decimal.Parse(row[7], CultureInfo.InvariantCulture); break;
                }
        }
        Assert.Equal(manifest.Summary.Obligations, obligations.Count);
        foreach (var (id, row) in obligations) Assert.Equal(decimal.Parse(row[12], CultureInfo.InvariantCulture), bank.GetValueOrDefault(id));
        foreach (var currency in manifest.InvoiceTotals.Keys)
        {
            Assert.Equal(totals.GetValueOrDefault(currency), manifest.InvoiceTotals[currency]);
            Assert.Equal(paidTotals.GetValueOrDefault(currency), manifest.PaymentTotals[currency]);
        }
    }
    [Fact]
    public async Task AppendPreservesInitialDocumentsAndExistingCorpusWithUniqueLinkedRecords()
    {
        var output = Workspace();
        try
        {
            var store = new DemoStore(root, output); var initial = store.Current; var control = Hashes(initial);
            var calendar = File.ReadAllBytes(Path.Combine(initial.Directory, "calendar.csv"));
            var next = await store.ResizeAsync(initial.Id, "add", 1_000_000);
            Preserved(control, next); Verify(next);
            Assert.Equal(initial.Data.Seed, next.Data.Seed); Assert.Equal(calendar, File.ReadAllBytes(Path.Combine(next.Directory, "calendar.csv")));
            var existing = Hashes(next); var before = next.Data.Volume!.TotalBytes;
            var added = await store.ResizeAsync(next.Id, "add", 4_000_000);
            Preserved(existing, added); Verify(added);
            Assert.InRange(added.Data.Volume!.TotalBytes - before, 4_000_000, 104_000_000);
            Assert.False(Directory.Exists(next.Directory));
            Assert.Equal("add", added.Data.Volume.LastChange!.Operation);
            Assert.Equal(before, added.Data.Volume.LastChange.BeforeBytes);
            Assert.Single(Directory.GetDirectories(Path.Combine(output, "generations")));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
    }
    [Fact]
    public async Task RemovalKeepsAPrefixAndControlDataAndAppendAfterRemovalNeverReusesIds()
    {
        var output = Workspace();
        try
        {
            var store = new DemoStore(root, output); var original = await store.RegenerateAsync(81, 40_000_000);
            var hashes = Hashes(original); var originalManifest = DocumentApi.ReadVolume(original)!;
            var removed = await store.ResizeAsync(original.Id, "remove", 10_000_000); Verify(removed);
            var change = original.Data.Volume!.TotalBytes - removed.Data.Volume!.TotalBytes;
            Assert.True(change > 0); Assert.InRange(Math.Abs(change - 10_000_000), 0, BulkVolume.ToleranceBytes);
            var kept = DocumentApi.ReadVolume(removed)!;
            Assert.True(kept.Summary.Obligations < originalManifest.Summary.Obligations);
            foreach (var file in kept.Files) Assert.Equal(hashes[file.Path.Replace('/', Path.DirectorySeparatorChar)], file.Sha256);
            Assert.Equal(100, removed.Data.Documents.Count);
            var oldNextId = originalManifest.NextObligationId;
            var keptHashes = Hashes(removed);
            var added = await store.ResizeAsync(removed.Id, "add", 1_000_000); Verify(added); Preserved(keptHashes, added);
            foreach (var file in DocumentApi.ReadVolume(added)!.Files.Where(f => f.Kind == "obligations" && !kept.Files.Any(k => k.Path == f.Path)))
                foreach (var row in DocumentContentReader.ReadCsv(Path.Combine(added.Directory, file.Path)).Skip(1))
                    Assert.True(long.Parse(row[0][3..], CultureInfo.InvariantCulture) >= oldNextId);
            var restarted = new DemoStore(root, output); Assert.Equal(added.Id, restarted.Current.Id); Verify(restarted.Current);
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
    }
    [Fact]
    public async Task CancellationDuringAppendOrRemovalLeavesCurrentFilesAndPointerUntouched()
    {
        var output = Workspace();
        try
        {
            var store = new DemoStore(root, output); var original = await store.RegenerateAsync(82, 40_000_000);
            var hashes = Hashes(original); var pointer = File.ReadAllBytes(Path.Combine(output, "current.json"));
            foreach (var action in new[] { "add", "remove" })
            {
                using var cancellation = new CancellationTokenSource();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ResizeAsync(original.Id, action, 10_000_000, _ => cancellation.Cancel(), cancellation.Token));
                Assert.Equal(original.Id, store.Current.Id); Preserved(hashes, original);
                Assert.Equal(pointer, File.ReadAllBytes(Path.Combine(output, "current.json")));
                Assert.Single(Directory.GetDirectories(Path.Combine(output, "generations")));
                await Assert.ThrowsAsync<IOException>(() => store.ResizeAsync(original.Id, action, 10_000_000, _ => throw new IOException("Injected resize failure")));
                Assert.Equal(original.Id, store.Current.Id); Preserved(hashes, original);
                Assert.Equal(pointer, File.ReadAllBytes(Path.Combine(output, "current.json")));
                Assert.Single(Directory.GetDirectories(Path.Combine(output, "generations")));
            }
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
    }
    [Fact]
    public async Task InvalidRemovalAndStaleDatasetNeverModifyCurrentData()
    {
        var output = Workspace();
        try
        {
            var store = new DemoStore(root, output);
            await Assert.ThrowsAsync<ArgumentException>(() => store.ResizeAsync("initial", "remove", 1_000_000));
            var next = await store.RegenerateAsync(83, 20_000_000);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ResizeAsync("initial", "add", 1_000_000));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ResizeAsync(next.Id, "remove", 100_000_000));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ResizeAsync(next.Id, "replace", 1_000_000));
            Assert.Equal(next.Id, store.Current.Id); Verify(next);
            var empty = await store.ResizeAsync(next.Id, "remove", BulkVolume.RemovableBytes(DocumentApi.ReadVolume(next)!));
            Verify(empty); Assert.Equal(0, empty.Data.Volume!.Obligations); Assert.Equal(100, empty.Data.Documents.Count);
            var resumed = await store.ResizeAsync(empty.Id, "add", 1_000_000); Verify(resumed);
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
    }
}

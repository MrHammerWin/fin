using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FinPlan;
using Xunit;

namespace FinPlan.Tests;

public sealed class BulkVolumeTests : IClassFixture<DemoFixture>
{
    private readonly string root;
    public BulkVolumeTests(DemoFixture _)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Solution1.sln"))) dir = dir.Parent;
        root = dir!.FullName;
    }
    [Fact]
    public async Task TargetIsMeasuredFromRealFilesAndEveryFinancialLinkAndTotalReconciles()
    {
        var output = Path.Combine(Path.GetTempPath(), "finplan-volume-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DemoStore(root, output); var snapshot = await store.RegenerateAsync(73, 25_000_000);
            var volume = snapshot.Data.Volume!;
            Assert.InRange(volume.TotalBytes, 25_000_000, 125_000_000);
            Assert.Equal(BulkVolume.BytesIn(snapshot.Directory), volume.TotalBytes);
            var manifest = JsonSerializer.Deserialize<VolumeManifest>(File.ReadAllText(Path.Combine(snapshot.Directory, "volume-manifest.json")), JsonFiles.Options)!;
            var invoices = new Dictionary<string, string[]>(); var contracts = new Dictionary<string, string[]>();
            var paid = new Dictionary<string, decimal>(); var totals = new Dictionary<string, decimal>(); var paymentTotals = new Dictionary<string, decimal>();
            long obligations = 0;
            foreach (var file in manifest.Files)
            {
                var path = Path.Combine(snapshot.Directory, file.Path); using var stream = File.OpenRead(path);
                Assert.Equal(file.Bytes, stream.Length); Assert.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(stream)));
                if (file.Kind == "control") continue;
                var rows = BulkRegister.Read(path).Skip(1).ToList(); Assert.Equal(file.Rows, rows.Count);
                foreach (var row in rows)
                    switch (file.Kind)
                    {
                        case "contracts": Assert.True(contracts.TryAdd(row[0], row)); break;
                        case "invoices":
                            Assert.True(invoices.TryAdd(row[0], row)); Assert.True(contracts.ContainsKey(row[2])); Assert.Equal(contracts[row[2]][2], row[4]);
                            totals[row[5]] = totals.GetValueOrDefault(row[5]) + decimal.Parse(row[10], CultureInfo.InvariantCulture); break;
                        case "acts":
                            Assert.True(invoices.ContainsKey(row[2])); Assert.Equal(invoices[row[2]][10], row[11]);
                            var recognized = DateOnly.Parse(row[8], CultureInfo.InvariantCulture);
                            Assert.Equal(recognized.AddDays(int.Parse(contracts[row[4]][6], CultureInfo.InvariantCulture)), DateOnly.Parse(invoices[row[2]][9], CultureInfo.InvariantCulture));
                            Assert.True(DateOnly.Parse(invoices[row[2]][8], CultureInfo.InvariantCulture) <= recognized);
                            break;
                        case "bank":
                            var amount = decimal.Parse(row[6], CultureInfo.InvariantCulture); paid[row[7]] = paid.GetValueOrDefault(row[7]) + amount;
                            paymentTotals[row[3]] = paymentTotals.GetValueOrDefault(row[3]) + amount; break;
                    }
            }
            foreach (var file in manifest.Files.Where(f => f.Kind == "obligations"))
                foreach (var row in BulkRegister.Read(Path.Combine(snapshot.Directory, file.Path)).Skip(1))
                {
                    obligations++; var amount = decimal.Parse(row[11], CultureInfo.InvariantCulture); var payment = decimal.Parse(row[12], CultureInfo.InvariantCulture);
                    Assert.Equal(invoices[row[1]][10], row[11]); Assert.InRange(payment, 0, amount);
                    Assert.Equal(payment, paid.GetValueOrDefault(row[0])); Assert.Equal(amount - payment, decimal.Parse(row[13], CultureInfo.InvariantCulture));
                }
            Assert.Equal(volume.Obligations, obligations);
            foreach (var currency in totals.Keys) { Assert.Equal(totals[currency], manifest.InvoiceTotals[currency]); Assert.Equal(paymentTotals[currency], manifest.PaymentTotals[currency]); }
            Assert.Equal(100, snapshot.Data.Documents.Count); // Core documents stay separate; the corpus contributes through checked additive events.
            var restarted = new DemoStore(root, output).Current;
            Assert.Equal(snapshot.Id, restarted.Id);
            Assert.Equal(BulkVolume.BytesIn(restarted.Directory), restarted.Data.Volume!.TotalBytes);
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
    }
    [Fact]
    public async Task CancelledGenerationLeavesCurrentSnapshotAndPointerUntouched()
    {
        var output = Path.Combine(Path.GetTempPath(), "finplan-volume-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DemoStore(root, output); using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RegenerateAsync(74, 50_000_000,
                _ => cancellation.Cancel(), cancellation.Token));
            Assert.Equal("initial", store.Current.Id); Assert.False(File.Exists(Path.Combine(output, "current.json")));
            Assert.Empty(Directory.GetDirectories(Path.Combine(output, "generations")));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
    }
}

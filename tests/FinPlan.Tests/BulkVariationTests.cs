using System.Globalization;
using System.Text.Json;
using FinPlan;
using Xunit;

namespace FinPlan.Tests;

public sealed class BulkVariationTests(ReaderFixture fixture) : IClassFixture<ReaderFixture>
{
    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(int.MaxValue)]
    public void LargeRegistersHaveDailyPeaksAndVariedPaymentsWithoutBreakingReproducibility(int seed)
    {
        var output = fixture.NewDirectory(); Directory.CreateDirectory(Path.Combine(output, "documents"));
        var manifest = BulkVolume.Generate(output, seed, 30_000_000);
        var data = new Dataset { Seed = seed, Accounts = [new("A-RUB", "RUB", 0), new("A-CHF", "CHF", 0), new("A-GBP", "GBP", 0)] };
        data.Corpus = CorpusAccounting.Load(output, manifest, data).Summary; FinanceCalculator.Calculate(data);
        Assert.True(data.Corpus.Obligations >= 20_000);
        var metrics = new List<object>();
        foreach (var currency in data.Rates.Keys)
        foreach (var direction in new[] { "in", "out" })
        {
            var values = data.Calendar.Skip(1).Select(day => direction == "in" ? day.Currencies[currency].In : day.Currencies[currency].Out).ToArray();
            var total = values.Sum(); var peakShare = values.OrderDescending().Take(5).Sum() / total;
            Assert.True(values.Count(value => value > 0) >= 25, $"Too few settlement days: {seed}/{currency}/{direction}");
            Assert.True(peakShare > .25m, $"Five largest days should carry more than 25%: {seed}/{currency}/{direction}, {peakShare}");
            metrics.Add(new { currency, direction, top_five_days_share = peakShare.ToString("F4", CultureInfo.InvariantCulture), nonzero_days = values.Count(v => v > 0) });
        }
        var byDay = data.Calendar.Skip(1).Select(day => day.Currencies.Sum(kv => (kv.Value.In - kv.Value.Out) * decimal.Parse(data.Rates[kv.Key], CultureInfo.InvariantCulture))).Where(change => change != 0).ToArray();
        var turns = byDay.Zip(byDay.Skip(1)).Count(pair => Math.Sign(pair.First) != Math.Sign(pair.Second));
        Assert.True(turns >= 8, $"The daily net flow should change direction: seed={seed}, changes={turns}");
        var partialRatios = new HashSet<decimal>(); var expectedDelays = new HashSet<int>(); var terms = new HashSet<int>();
        foreach (var file in manifest.Files.Where(f => f.Kind == "contracts"))
            foreach (var row in BulkRegister.Read(Path.Combine(output, file.Path)).Skip(1)) terms.Add(int.Parse(row[6], CultureInfo.InvariantCulture));
        foreach (var file in manifest.Files.Where(f => f.Kind == "obligations"))
            foreach (var row in BulkRegister.Read(Path.Combine(output, file.Path)).Skip(1))
            {
                var amount = decimal.Parse(row[11], CultureInfo.InvariantCulture); var paid = decimal.Parse(row[12], CultureInfo.InvariantCulture);
                if (paid > 0 && paid < amount) partialRatios.Add(decimal.Round(paid / amount, 4));
                expectedDelays.Add(DateOnly.Parse(row[15], CultureInfo.InvariantCulture).DayNumber - DateOnly.Parse(row[14], CultureInfo.InvariantCulture).DayNumber);
            }
        Assert.True(partialRatios.Count > 100); Assert.True(terms.Count >= 5); Assert.True(expectedDelays.Count >= 4);
        Assert.True(data.Corpus.Cash.Select(t => t.Date).Distinct().Count() > 30);
        Assert.Contains(data.Corpus.Cash, t => t.Date >= data.Cutoff);
        var repeat = fixture.NewDirectory(); Directory.CreateDirectory(Path.Combine(repeat, "documents"));
        var repeated = BulkVolume.Generate(repeat, seed, 30_000_000);
        foreach (var kind in new[] { "contracts", "invoices", "acts", "obligations", "bank" })
        {
            var a = manifest.Files.First(f => f.Kind == kind); var b = repeated.Files.First(f => f.Kind == kind);
            Assert.Equal(BulkRegister.Read(Path.Combine(output, a.Path)).Select(r => string.Join(';', r)), BulkRegister.Read(Path.Combine(repeat, b.Path)).Select(r => string.Join(';', r)));
        }
        var evidence = Path.Combine(fixture.Root, ".test-artifacts", $"variation-{seed}.json"); Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        JsonFiles.Write(evidence, new { seed, obligations = data.Corpus.Obligations, net_flow_turns = turns, profiles = metrics, calendar = data.Calendar });
    }
}

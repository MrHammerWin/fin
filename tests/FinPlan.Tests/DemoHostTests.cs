using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinPlan;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FinPlan.Tests;

public sealed class DemoHostTests(DemoFixture fixture) : IClassFixture<DemoFixture>
{
    [Fact]
    public async Task BrowserHasOneGeneratorAndOldDatasetLinksOpenTheReplacement()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Solution1.sln"))) root = root.Parent;
        await using var app = DemoHost.Create(root!.FullName, fixture.Output);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var oldSeed = fixture.Data.Seed;
            var htmlResponse = await client.GetAsync("/");
            var oldPage = htmlResponse.RequestMessage!.RequestUri!;
            var html = await htmlResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain("id=\"regenerate-data\"", html);
            Assert.DoesNotContain("regenerateUrl", html);
            Assert.Contains("#/generation", html);
            var token = Regex.Match(html, "\"token\":\"([A-F0-9]+)\"").Groups[1].Value;
            Assert.NotEmpty(token);
            using var forbidden = await client.PostAsync("/api/regenerate", null);
            Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
            client.DefaultRequestHeaders.Add("X-FinPlan-Token", token);
            using var request = new StringContent("{\"targetBytes\":20000000}", System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/api/jobs/generate", request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
            JsonElement result;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            do
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(20, timeout.Token);
                result = JsonDocument.Parse(await client.GetStringAsync("/api/jobs/" + id, timeout.Token)).RootElement;
            } while (result.GetProperty("state").GetString() is "queued" or "running");
            Assert.Equal("succeeded", result.GetProperty("state").GetString());
            var newPage = result.GetProperty("result").GetProperty("url").GetString()!;
            Assert.NotEqual(oldSeed, JsonDocument.Parse(await client.GetStringAsync("/api/status")).RootElement.GetProperty("seed").GetInt32());
            Assert.NotEqual(oldPage.PathAndQuery, newPage);
            using var newResponse = await client.GetAsync(newPage);
            Assert.Equal(HttpStatusCode.OK, newResponse.StatusCode);
            using var oldResponse = await client.GetAsync(oldPage);
            Assert.Equal(HttpStatusCode.OK, oldResponse.StatusCode);
            Assert.Equal(newResponse.RequestMessage!.RequestUri!.AbsolutePath, oldResponse.RequestMessage!.RequestUri!.AbsolutePath);
            Assert.False(Directory.Exists(Path.Combine(fixture.Output, "documents")));
            var directory = DemoStore.ResolveCurrentDirectory(fixture.Output);
            Assert.Equal(100, Directory.GetFiles(Path.Combine(directory, "documents")).Length);
            var documentUrl = newPage.Split("calendar_view.html")[0] + "documents/ACT-001.pdf";
            using var pdf = await client.GetAsync(documentUrl);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
            var datasetId = JsonDocument.Parse(await client.GetStringAsync("/api/status")).RootElement.GetProperty("id").GetString();
            var api = $"/api/datasets/{datasetId}";
            var corpusPage = JsonDocument.Parse(await client.GetStringAsync(api + "/records?query=BO-000000001"));
            Assert.Single(corpusPage.RootElement.GetProperty("obligations").EnumerateArray());
            var detail = JsonDocument.Parse(await client.GetStringAsync(api + "/obligations/BO-000000001"));
            Assert.Equal("BI-000000001", detail.RootElement.GetProperty("obligation").GetProperty("invoice_id").GetString());
            var bulkManifest = JsonSerializer.Deserialize<VolumeManifest>(File.ReadAllText(Path.Combine(directory, "volume-manifest.json")), JsonFiles.Options)!;
            foreach (var format in BulkRegister.Formats)
            {
                var source = bulkManifest.Files.First(f => f.Kind != "control" && f.Path.EndsWith("." + format, StringComparison.Ordinal));
                using var sourceResponse = await client.GetAsync(newPage.Split("calendar_view.html")[0] + source.Path);
                Assert.Equal(HttpStatusCode.OK, sourceResponse.StatusCode); Assert.Equal(DocumentApi.Mime(source.Path).Split(';')[0], sourceResponse.Content.Headers.ContentType!.MediaType);
            }
            var dataset = JsonSerializer.Deserialize<Dataset>(File.ReadAllText(Path.Combine(directory, "dataset.json")), JsonFiles.Options)!;
            var obligation = dataset.Obligations.First(o => o.Outstanding > 1);
            var manual = new Operation("DEMO-PAY-1", dataset.Cutoff.AddDays(-1), obligation.Account, obligation.Currency, obligation.CounterpartyId, obligation.Counterparty, obligation.Direction, 1, obligation.Id, "Manual test", obligation.Direction == "in" ? "customer_receipts" : "supplier_payments");
            using var pageRequest = new StringContent(JsonSerializer.Serialize(new CorpusApi.RecordQuery(Kind: "cash", Query: "DEMO-PAY-1", Edits: [manual]), JsonFiles.Options), System.Text.Encoding.UTF8, "application/json");
            using var editedPage = await client.PostAsync(api + "/records", pageRequest);
            Assert.Equal(HttpStatusCode.OK, editedPage.StatusCode);
            var edits = JsonDocument.Parse(await editedPage.Content.ReadAsStringAsync()).RootElement.GetProperty("operations");
            Assert.Single(edits.EnumerateArray()); Assert.Equal("1.00", edits[0].GetProperty("amount").GetString());
            Assert.DoesNotContain("DEMO-PAY-1", File.ReadAllText(Path.Combine(directory, "dataset.json")));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/datasets/initial/Program.cs")).StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}

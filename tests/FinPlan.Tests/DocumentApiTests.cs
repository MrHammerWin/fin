using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinPlan;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FinPlan.Tests;

public sealed class DocumentApiTests(ReaderFixture fixture) : IClassFixture<ReaderFixture>
{
    [Fact]
    public async Task UploadedTemplateIsPreviewedThenAddedToRealCalendarOnlyAfterConfirmation()
    {
        var output = fixture.NewDirectory(); await using var app = DemoHost.Create(fixture.Root, output); await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var token = Regex.Match(await client.GetStringAsync("/"), "\"token\":\"([A-F0-9]+)\"").Groups[1].Value;
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/jobs/read", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/conversions", null)).StatusCode);
            client.DefaultRequestHeaders.Add("X-FinPlan-Token", token);
            var initial = JsonSerializer.Deserialize<Dataset>(File.ReadAllText(Path.Combine(output, "dataset.json")), JsonFiles.Options)!;
            List<string[]> rows = [DuplicateChecker.PlanHeader];
            for (var i = 0; i < 20; i++) rows.Add([$"P-UP-{i:000}", "CP-001", initial.Counterparties.Single(c=>c.Id=="CP-001").Name, "A-RUB", "RUB", "out", "9876.54", "2026-11-20", "2026-11-20", "USER FILE PURPOSE __FINANCE__ __DOCUMENT_TOOLS__"]);
            var source = Path.Combine(output, "user-plan.csv"); DocumentFiles.Csv(source, rows); var original = File.ReadAllBytes(source);
            using var form = new MultipartFormDataContent(); form.Add(new StringContent("initial"),"datasetId");form.Add(new ByteArrayContent(original),"files","user-plan.csv");
            using var response=await client.PostAsync("/api/jobs/read",form);Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
            var id=JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
            var completed=await Wait(client,id);Assert.Equal("succeeded",completed.GetProperty("state").GetString());
            var preview=completed.GetProperty("result").GetProperty("preview");Assert.Equal("plan",preview.GetProperty("files")[0].GetProperty("document").GetProperty("type").GetString());
            Assert.Equal(original,File.ReadAllBytes(Path.Combine(output,".jobs",id,"input-000.csv")));
            Assert.Equal("initial",JsonDocument.Parse(await client.GetStringAsync("/api/status")).RootElement.GetProperty("id").GetString());
            using var denied=await client.PostAsync($"/api/jobs/{id}/apply",new StringContent("{\"confirmed\":false}",Encoding.UTF8,"application/json"));Assert.Equal(HttpStatusCode.BadRequest,denied.StatusCode);
            using var apply=await client.PostAsync($"/api/jobs/{id}/apply",new StringContent("{\"confirmed\":true}",Encoding.UTF8,"application/json"));Assert.Equal(HttpStatusCode.Accepted,apply.StatusCode);
            var applyId=JsonDocument.Parse(await apply.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
            var applied=await Wait(client,applyId);Assert.Equal("succeeded",applied.GetProperty("state").GetString());
            var datasetId=applied.GetProperty("result").GetProperty("url").GetString()!.Split('/')[2];
            var data=JsonSerializer.Deserialize<Dataset>(await client.GetStringAsync($"/datasets/{datasetId}/dataset.json"),JsonFiles.Options)!;
            Assert.Equal(440,data.Obligations.Count);Assert.Equal(101,data.Documents.Count);
            var generatedHtml=await client.GetStringAsync($"/datasets/{datasetId}/calendar_view.html");
            var embedded=Regex.Match(generatedHtml,"<script id=\"dataset\"[^>]*>([\\s\\S]*?)</script>").Groups[1].Value;
            using var safeJson=JsonDocument.Parse(embedded);
            Assert.Contains("__FINANCE__",safeJson.RootElement.GetProperty("obligations").EnumerateArray().Single(o=>o.GetProperty("id").GetString()=="P-UP-000").GetProperty("purpose").GetString());
            Assert.All(data.Obligations.Where(o=>o.Id.StartsWith("P-UP-",StringComparison.Ordinal)),o=>Assert.Equal(9876.54m,o.Amount));
            Assert.Equal(initial.Reports!.Currencies["TOTAL"]["operating_profit"],data.Reports!.Currencies["TOTAL"]["operating_profit"]);
            Assert.NotEqual(JsonSerializer.Serialize(initial.Calendar,JsonFiles.Options),JsonSerializer.Serialize(data.Calendar,JsonFiles.Options));
            Assert.Single(Directory.GetDirectories(Path.Combine(output,"generations")));
            Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync($"/datasets/{datasetId}/.jobs/{id}/input-000.csv")).StatusCode);
            using var stale=await client.PostAsync($"/api/jobs/{id}/apply",new StringContent("{\"confirmed\":true}",Encoding.UTF8,"application/json"));Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task InvalidUploadsAndTraversalAreRejectedAndVolumeIsPaginated()
    {
        var output = fixture.NewDirectory(); await using var app = DemoHost.Create(fixture.Root, output); await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var token = Regex.Match(await client.GetStringAsync("/"), "\"token\":\"([A-F0-9]+)\"").Groups[1].Value;
            client.DefaultRequestHeaders.Add("X-FinPlan-Token", token);
            var initialBytes = BulkVolume.BytesIn(output);
            var initialSize = JsonDocument.Parse(await client.GetStringAsync("/api/datasets/initial/size")).RootElement;
            Assert.Equal(initialBytes, initialSize.GetProperty("total_bytes").GetInt64());
            using var bad=new MultipartFormDataContent();bad.Add(new StringContent("initial"),"datasetId");bad.Add(new StringContent("not-pdf"),"files","fake.pdf");
            Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync("/api/jobs/read",bad)).StatusCode);
            using var traversal=new MultipartFormDataContent();traversal.Add(new StringContent("initial"),"datasetId");traversal.Add(new StringContent("../dataset.json"),"source");
            Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync("/api/jobs/read",traversal)).StatusCode);
            using var request = new StringContent("{\"targetBytes\":25000000}", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/api/jobs/generate", request); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
            var result = await Wait(client, id); Assert.Equal("succeeded", result.GetProperty("state").GetString());
            var datasetId = result.GetProperty("result").GetProperty("url").GetString()!.Split('/')[2];
            var datasetDirectory = Path.Combine(output, "generations", datasetId);
            var expectedBytes = BulkVolume.BytesIn(datasetDirectory);
            var size = JsonDocument.Parse(await client.GetStringAsync($"/api/datasets/{datasetId}/size")).RootElement;
            Assert.Equal(expectedBytes, size.GetProperty("total_bytes").GetInt64());
            Assert.Equal(Directory.GetFiles(datasetDirectory, "*", SearchOption.AllDirectories).Length, size.GetProperty("files").GetInt32());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/datasets/initial/size")).StatusCode);
            Assert.False(File.Exists(Path.Combine(output, "dataset.json")));
            // Measure the files again rather than returning the stored size from volume-manifest.json.
            File.AppendAllText(Path.Combine(datasetDirectory, "calendar.csv"), "\r\n");
            size = JsonDocument.Parse(await client.GetStringAsync($"/api/datasets/{datasetId}/size")).RootElement;
            Assert.Equal(expectedBytes + 2, size.GetProperty("total_bytes").GetInt64());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/datasets/unknown/size")).StatusCode);
            var volume = JsonDocument.Parse(await client.GetStringAsync($"/api/datasets/{datasetId}/volume?offset=100&limit=2")).RootElement;
            Assert.Equal(2, volume.GetProperty("files").GetArrayLength()); Assert.True(volume.GetProperty("total").GetInt32() > 100);
            var path = volume.GetProperty("files")[0].GetProperty("path").GetString();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/datasets/{datasetId}/{path}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/datasets/{datasetId}/bulk/unknown.csv")).StatusCode);
            using var retiredPage = await client.GetAsync("/datasets/initial/calendar_view.html");
            Assert.Equal($"/datasets/{datasetId}/calendar_view.html", retiredPage.RequestMessage!.RequestUri!.AbsolutePath);
            Assert.Contains("id=\"dataset-size\"", await retiredPage.Content.ReadAsStringAsync());
            Assert.False(File.Exists(Path.Combine(output, "calendar_view.html")));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/datasets/initial/documents/D-001.docx")).StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task ResizeJobsAuthorizeValidateDatasetAndReportActualBeforeAndAfterSizes()
    {
        var output = fixture.NewDirectory(); await using var app = DemoHost.Create(fixture.Root, output); await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            using var unauthorized = await client.PostAsync("/api/jobs/resize", null);
            Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
            var token = Regex.Match(await client.GetStringAsync("/"), "\"token\":\"([A-F0-9]+)\"").Groups[1].Value;
            client.DefaultRequestHeaders.Add("X-FinPlan-Token", token);
            async Task<HttpResponseMessage> Submit(string dataset, string operation, long bytes) => await client.PostAsync("/api/jobs/resize",
                new StringContent(JsonSerializer.Serialize(new { datasetId = dataset, operation, bytes }), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, (await Submit("initial", "remove", 1_000_000)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Submit("initial", "add", 0)).StatusCode);
            var currentId = "initial";
            foreach (var operation in new[] { "add", "remove" })
            {
                var before = JsonDocument.Parse(await client.GetStringAsync($"/api/datasets/{currentId}/size")).RootElement.GetProperty("total_bytes").GetInt64();
                var bytes = operation == "add" ? 20_000_000L : 10_000_000L;
                using var response = await Submit(currentId, operation, bytes); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
                var completed = await Wait(client, id); Assert.Equal("succeeded", completed.GetProperty("state").GetString());
                var nextId = completed.GetProperty("result").GetProperty("url").GetString()!.Split('/')[2];
                var size = JsonDocument.Parse(await client.GetStringAsync($"/api/datasets/{nextId}/size")).RootElement;
                var after = size.GetProperty("total_bytes").GetInt64();
                Assert.Equal(operation, size.GetProperty("last_change").GetProperty("operation").GetString());
                Assert.Equal(bytes, size.GetProperty("last_change").GetProperty("requested_bytes").GetInt64());
                Assert.Equal(before, size.GetProperty("last_change").GetProperty("before_bytes").GetInt64());
                if (operation == "add") Assert.InRange(after - before, bytes, bytes + BulkVolume.ToleranceBytes);
                else { Assert.True(before > after); Assert.InRange(Math.Abs(before - after - bytes), 0, BulkVolume.ToleranceBytes); }
                Assert.Equal(HttpStatusCode.Conflict, (await Submit(currentId, "add", 1_000_000)).StatusCode);
                Assert.Single(Directory.GetDirectories(Path.Combine(output, "generations")));
                currentId = nextId;
            }
        }
        finally { await app.StopAsync(); }
    }

    private static async Task<JsonElement> Wait(HttpClient client, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            using var response = await client.GetAsync("/api/jobs/" + id, timeout.Token);
            var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token)).RootElement;
            if (result.GetProperty("state").GetString() is not ("queued" or "running")) return result;
            await Task.Delay(20, timeout.Token);
        }
    }
}

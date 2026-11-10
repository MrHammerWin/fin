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

public sealed class DocumentApiTests(ConversionFixture fixture) : IClassFixture<ConversionFixture>
{
    [Fact]
    public async Task UploadAndGeneratedDocumentsUseBackgroundJobsWithPrivateOriginals()
    {
        var output = fixture.NewDirectory();
        await using var app = DemoHost.Create(fixture.Root, output); await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var html = await client.GetStringAsync("/");
            var token = Regex.Match(html, "\"token\":\"([A-F0-9]+)\"").Groups[1].Value;
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/conversions", null)).StatusCode);
            client.DefaultRequestHeaders.Add("X-FinPlan-Token", token);
            using var form = new MultipartFormDataContent();
            var original = Encoding.UTF8.GetBytes("Code;Amount\r\nUPLOADPROBE85;1234.56\r\n");
            form.Add(new ByteArrayContent(original), "file", "user-data.csv"); form.Add(new StringContent("xlsx"), "target");
            using var response = await client.PostAsync("/api/conversions", form); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var accepted = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            var id = accepted.GetProperty("id").GetString()!; var completed = await Wait(client, id);
            Assert.Equal("succeeded", completed.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, completed.GetProperty("result").GetProperty("file_path").ValueKind);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(output, ".jobs", id, "original.csv")));
            using var result = await client.GetAsync(completed.GetProperty("result").GetProperty("url").GetString());
            Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", result.Content.Headers.ContentType!.MediaType);
            Assert.NotNull(result.Content.Headers.ContentDisposition);
            var converted = Path.Combine(output, "download.xlsx"); await File.WriteAllBytesAsync(converted, await result.Content.ReadAsByteArrayAsync());
            Assert.Contains("UPLOADPROBE85", ConversionContentReader.Read(converted).Sections[0].Rows.SelectMany(r => r));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/datasets/initial/.jobs/{id}/original.csv")).StatusCode);

            using var generated = new MultipartFormDataContent();
            generated.Add(new StringContent("initial"), "datasetId"); generated.Add(new StringContent("documents/D-001.docx"), "source"); generated.Add(new StringContent("csv"), "target");
            using var acceptedGenerated = await client.PostAsync("/api/conversions", generated);
            Assert.Equal(HttpStatusCode.Accepted, acceptedGenerated.StatusCode);
            var generatedId = JsonDocument.Parse(await acceptedGenerated.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
            var generatedResult = await Wait(client, generatedId); Assert.Equal("succeeded", generatedResult.GetProperty("state").GetString());
            var csv = await client.GetStringAsync(generatedResult.GetProperty("result").GetProperty("url").GetString());
            Assert.Contains("ДОГОВОР ОКАЗАНИЯ УСЛУГ", csv); Assert.Contains("4. Исполнение подтверждается", csv);
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
            using var bad = new MultipartFormDataContent(); bad.Add(new StringContent("not-pdf"), "file", "fake.pdf"); bad.Add(new StringContent("docx"), "target");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/conversions", bad)).StatusCode);
            using var traversal = new MultipartFormDataContent(); traversal.Add(new StringContent("initial"), "datasetId"); traversal.Add(new StringContent("../dataset.json"), "source"); traversal.Add(new StringContent("pdf"), "target");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/conversions", traversal)).StatusCode);
            using var request = new StringContent("{\"targetBytes\":25000000}", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/api/jobs/generate", request); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
            var result = await Wait(client, id); Assert.Equal("succeeded", result.GetProperty("state").GetString());
            var datasetId = result.GetProperty("result").GetProperty("url").GetString()!.Split('/')[2];
            var volume = JsonDocument.Parse(await client.GetStringAsync($"/api/datasets/{datasetId}/volume?offset=100&limit=2")).RootElement;
            Assert.Equal(2, volume.GetProperty("files").GetArrayLength()); Assert.True(volume.GetProperty("total").GetInt32() > 100);
            var path = volume.GetProperty("files")[0].GetProperty("path").GetString();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/datasets/{datasetId}/{path}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/datasets/{datasetId}/bulk/unknown.csv")).StatusCode);
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

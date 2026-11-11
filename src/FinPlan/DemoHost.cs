using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace FinPlan;

/// <summary>Loopback host for the demo UI and C# regeneration. Not the production distributed API.</summary>
public static partial class DemoHost
{
    public static WebApplication Create(string root, string output)
    {
        var store = new DemoStore(root, output);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = DocumentContentReader.MaxInputBytes + 1_000_000);
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options => options.MultipartBodyLengthLimit = DocumentContentReader.MaxInputBytes + 1_000_000);
        var jobs = new FileJobs(output);
        builder.Services.AddSingleton(jobs);
        builder.Services.AddHostedService(services => services.GetRequiredService<FileJobs>());
        var app = builder.Build();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var tools = new RecognitionTools(root);
        DocumentApi.Map(app, store, jobs, tools, token);
        app.Use(async (context, next) =>
        {
            if (context.Request.Host.Host is not ("127.0.0.1" or "localhost")) { context.Response.StatusCode = 400; return; }
            await next();
        });
        app.MapGet("/", () => Results.Redirect($"/datasets/{store.Current.Id}/calendar_view.html"));
        app.MapGet("/api/status", () => Results.Json(new { seed = store.Current.Data.Seed, id = store.Current.Id }));
        app.MapGet("/datasets/{id}/{**file}", (string id, string? file, HttpContext context) =>
        {
            if (file is null) return Results.NotFound();
            var isDocument = DocumentPath().IsMatch(file);
            var isBulk = file.StartsWith("bulk/", StringComparison.Ordinal);
            if (!isDocument && !isBulk && file is not ("calendar_view.html" or "dataset.json" or "calendar.csv" or "reports.json" or "duplicates.json" or "volume-manifest.json")) return Results.NotFound();
            DemoSnapshot snapshot;
            try { snapshot = store.Load(id); }
            catch (Exception error) when (error is IOException or JsonException)
            {
                return file == "calendar_view.html" && (id == "initial" || Guid.TryParseExact(id, "N", out _))
                    ? Results.Redirect($"/datasets/{store.Current.Id}/calendar_view.html") : Results.NotFound();
            }
            if (isDocument && !snapshot.Data.Documents.Any(d => d.Path == file)) return Results.NotFound();
            if (isBulk && !DocumentApi.VolumeFileAllowed(snapshot, file)) return Results.NotFound();
            var path = Path.Combine(snapshot.Directory, file);
            if (!File.Exists(path)) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (file == "calendar_view.html")
            {
                var config = JsonSerializer.Serialize(new { token });
                var html = new DemoGenerator(root).RenderHtml(snapshot.Data).Replace("<script id=\"dataset\"", $"<script>window.finplanRuntime={config};</script>\n<script id=\"dataset\"");
                return Results.Content(html, "text/html; charset=utf-8");
            }
            var mime = DocumentApi.Mime(file);
            return Results.File(path, mime, enableRangeProcessing: true);
        });
        return app;
    }

    public static async Task RunAsync(string root, string output, bool open)
    {
        await using var app = Create(root, output);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Console.WriteLine("ФинПлан: " + address);
        if (open) Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
        await app.WaitForShutdownAsync();
    }

    [GeneratedRegex(@"^documents/[A-Za-z0-9_-]+\.(pdf|docx|xlsx|csv)$")]
    private static partial Regex DocumentPath();
}

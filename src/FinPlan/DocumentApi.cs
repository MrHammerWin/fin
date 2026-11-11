using System.Text.Json;
using System.Text.RegularExpressions;

namespace FinPlan;

public static partial class DocumentApi
{
    public static void Map(WebApplication app, DemoStore store, FileJobs jobs, RecognitionTools tools, string token)
    {
        app.MapGet("/api/datasets/{id}/size", (string id) =>
        {
            try
            {
                var snapshot = store.Load(id);
                var paths = DatasetFiles.Paths(snapshot.Directory);
                long totalBytes = 0; var files = 0;
                foreach (var path in paths) { totalBytes += new FileInfo(path).Length; files++; }
                var manifest = ReadVolume(snapshot);
                return Results.Json(new { id = snapshot.Id, total_bytes = totalBytes, files, removable_bytes = manifest is null ? 0 : BulkVolume.RemovableBytes(manifest), last_change = manifest?.Summary.LastChange, cleanup_warning = store.CleanupWarning }, JsonFiles.Options);
            }
            catch (Exception e) when (e is IOException or JsonException) { return Results.NotFound(); }
        });
        app.MapPost("/api/jobs/generate", async (HttpContext context) =>
        {
            if (!Authorized(context, token)) return Results.StatusCode(403);
            try
            {
                using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                var target = json.RootElement.GetProperty("targetBytes").GetInt64();
                if (target < 20_000_000) return Results.BadRequest(new { error = "Минимальный целевой объём 20 МБ." });
                var free = new DriveInfo(Path.GetPathRoot(store.Current.Directory)!).AvailableFreeSpace;
                if (target > free - 5_000_000_000) return Results.BadRequest(new { error = "Для временного создания нового набора нужно свободное место на его объём и ещё 5 ГБ." });
                var id = Guid.NewGuid().ToString("N");
                var view = jobs.Enqueue(id, "generation", async (progress, cancellation) =>
                {
                    var snapshot = await store.RegenerateAsync(targetBytes: target, progress: progress, cancellation: cancellation);
                    return new(Url: $"/datasets/{snapshot.Id}/calendar_view.html#/generation", Warnings: store.CleanupWarning is { } warning ? [warning] : [], Bytes: snapshot.Data.Volume!.TotalBytes);
                }, target);
                return Results.Json(view, JsonFiles.Options, statusCode: 202);
            }
            catch (GenerationBusyException e) { return Results.Json(new { error = e.Message }, statusCode: 409); }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            { return Results.BadRequest(new { error = "Укажите целевой объём целым числом байт." }); }
        });
        app.MapPost("/api/jobs/resize", async (HttpContext context) =>
        {
            if (!Authorized(context, token)) return Results.StatusCode(403);
            try
            {
                using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                var datasetId = json.RootElement.GetProperty("datasetId").GetString();
                var operation = json.RootElement.GetProperty("operation").GetString();
                var bytes = json.RootElement.GetProperty("bytes").GetInt64();
                if (bytes < 1_000_000 || bytes > 9_007_199_254_740_991 || operation is not ("add" or "remove"))
                    return Results.BadRequest(new { error = "Выберите добавление или удаление и объём от 1 МБ." });
                if (datasetId != store.Current.Id) return Results.Json(new { error = "Набор уже изменился. Откройте текущий набор." }, statusCode: 409);
                var manifest = ReadVolume(store.Current);
                if (operation == "remove" && (manifest is null || bytes > BulkVolume.RemovableBytes(manifest)))
                    return Results.BadRequest(new { error = "Нельзя удалить такой объём: контрольные документы и справочник контрагентов сохраняются." });
                var id = Guid.NewGuid().ToString("N");
                var view = jobs.Enqueue(id, "generation", async (progress, cancellation) =>
                {
                    var snapshot = await store.ResizeAsync(datasetId!, operation!, bytes, progress, cancellation);
                    return new(Url: $"/datasets/{snapshot.Id}/calendar_view.html#/generation", Warnings: store.CleanupWarning is { } warning ? [warning] : [], Bytes: snapshot.Data.Volume!.TotalBytes);
                }, bytes);
                return Results.Json(view, JsonFiles.Options, statusCode: 202);
            }
            catch (GenerationBusyException e) { return Results.Json(new { error = e.Message }, statusCode: 409); }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            { return Results.BadRequest(new { error = "Укажите действие, текущий набор и объём целым числом байт." }); }
        });
        DocumentIngestionApi.Map(app, store, jobs, tools, token);
        app.MapGet("/api/jobs/{id}", (string id) =>
        {
            try
            {
                var view = jobs.Get(id);
                if (view is null) return Results.NotFound();
                return Results.Json(view, JsonFiles.Options);
            }
            catch (Exception e) when (e is IOException or JsonException) { return Results.NotFound(); }
        });
        app.MapDelete("/api/jobs/{id}", (string id, HttpContext context) =>
            !Authorized(context, token) ? Results.StatusCode(403) : jobs.Cancel(id) ? Results.Accepted() : Results.NotFound());
        app.MapGet("/api/datasets/{id}/volume", (string id, int? offset, int? limit) =>
        {
            try
            {
                var snapshot = store.Load(id); var manifest = ReadVolume(snapshot);
                if (manifest is null) return Results.NotFound();
                var start = Math.Max(0, offset ?? 0); var count = Math.Clamp(limit ?? 25, 1, 100);
                return Results.Json(new { summary = manifest.Summary, scope = manifest.Scope, invoice_totals = manifest.InvoiceTotals, payment_totals = manifest.PaymentTotals, files = manifest.Files.Skip(start).Take(count), total = manifest.Files.Count, offset = start }, JsonFiles.Options);
            }
            catch (Exception e) when (e is IOException or JsonException) { return Results.NotFound(); }
        });
    }
    public static VolumeManifest? ReadVolume(DemoSnapshot snapshot)
    {
        var path = Path.Combine(snapshot.Directory, "volume-manifest.json");
        return !File.Exists(path) ? null : JsonSerializer.Deserialize<VolumeManifest>(File.ReadAllText(path), JsonFiles.Options);
    }
    public static bool VolumeFileAllowed(DemoSnapshot snapshot, string file) => BulkPath().IsMatch(file) && ReadVolume(snapshot)?.Files.Any(f => f.Path == file) == true;
    public static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".csv" => "text/csv; charset=utf-8", _ => "application/json"
    };
    internal static bool Authorized(HttpContext context, string token) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(context.Request.Headers["X-FinPlan-Token"].ToString()), System.Text.Encoding.UTF8.GetBytes(token));
    [GeneratedRegex(@"^bulk/[A-Za-z0-9_-]+\.csv$")]
    private static partial Regex BulkPath();
}

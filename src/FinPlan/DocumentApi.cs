using System.Text.Json;
using System.Text.RegularExpressions;

namespace FinPlan;

public static partial class DocumentApi
{
    public static void Map(WebApplication app, DemoStore store, FileJobs jobs, DocumentConverter converter, ConversionTools tools, string token)
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
        app.MapGet("/api/document-tools", async () =>
        {
            string[] languages;
            try { languages = await tools.LanguagesAsync(); } catch (InvalidOperationException) { languages = []; }
            return Results.Json(new { formats = ConversionContentReader.Formats, maxInputBytes = ConversionContentReader.MaxInputBytes, libreOffice = tools.Office is not null, ocr = tools.Tesseract is not null && tools.PdfToPpm is not null, languages });
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
        app.MapPost("/api/conversions", async (HttpContext context) =>
        {
            if (!Authorized(context, token)) return Results.StatusCode(403);
            string? directory = null;
            try
            {
                if (!context.Request.HasFormContentType) return Results.BadRequest(new { error = "Ожидается форма с файлом или ссылкой на документ набора." });
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                var target = form["target"].ToString().ToLowerInvariant();
                if (!ConversionContentReader.Formats.Contains(target)) return Results.BadRequest(new { error = "Неизвестный выходной формат." });
                var id = Guid.NewGuid().ToString("N"); directory = jobs.DirectoryFor(id); Directory.CreateDirectory(directory);
                var upload = form.Files.GetFile("file"); string originalName; string input;
                if (upload is not null)
                {
                    if (upload.Length == 0 || upload.Length > ConversionContentReader.MaxInputBytes) throw new InvalidDataException("Размер загружаемого файла должен быть от 1 байта до 100 МБ.");
                    originalName = Path.GetFileName(upload.FileName.Replace('\\', '/'));
                    var ext = ConversionContentReader.Format(originalName); input = Path.Combine(directory, "original." + ext);
                    await using var file = new FileStream(input, FileMode.CreateNew, FileAccess.Write);
                    await upload.CopyToAsync(file, context.RequestAborted);
                }
                else
                {
                    var snapshot = store.Load(form["datasetId"].ToString()); var source = form["source"].ToString();
                    var allowed = snapshot.Data.Documents.Any(d => d.Path == source) || VolumeFileAllowed(snapshot, source);
                    if (!allowed) throw new InvalidDataException("Документ отсутствует в выбранном наборе.");
                    var sourcePath = Path.Combine(snapshot.Directory, source);
                    if (new FileInfo(sourcePath).Length > ConversionContentReader.MaxInputBytes) throw new InvalidDataException("Для конвертации выберите файл до 100 МБ.");
                    originalName = Path.GetFileName(source); input = Path.Combine(directory, "original." + ConversionContentReader.Format(source)); File.Copy(sourcePath, input);
                }
                ConversionContentReader.Validate(input);
                if (ConversionContentReader.Format(input) == target) throw new InvalidDataException("Выберите формат, отличный от исходного.");
                File.WriteAllText(Path.Combine(directory, "source-name.txt"), originalName);
                var options = new ConversionOptions(target, form["ocr"].ToString() is { Length: > 0 } ocr ? ocr : "auto", form["languages"].ToString() is { Length: > 0 } lang ? lang : "rus+eng");
                var workDirectory = directory;
                var view = jobs.Enqueue(id, "conversion", async (progress, cancellation) =>
                {
                    var result = await converter.ConvertAsync(input, workDirectory, options, text => progress(new(0, 0, 0, text)), cancellation);
                    var filename = Path.GetFileNameWithoutExtension(originalName) + "." + result.Format;
                    return new(Url: $"/api/jobs/{id}/download", FileName: filename, FilePath: result.Path, Warnings: result.Warnings, OcrPages: result.OcrPages, Bytes: new FileInfo(result.Path).Length);
                });
                return Results.Json(view, JsonFiles.Options, statusCode: 202);
            }
            catch (GenerationBusyException e) { return Results.Json(new { error = e.Message }, statusCode: 409); }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or JsonException or FormatException or System.Xml.XmlException)
            {
                if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true);
                return Results.BadRequest(new { error = e is InvalidDataException ? e.Message : "Не удалось принять документ. Проверьте формат и содержимое файла." });
            }
        });
        app.MapGet("/api/jobs/{id}", (string id) =>
        {
            try
            {
                var view = jobs.Get(id);
                if (view is null) return Results.NotFound();
                // Never publish server-side file paths.
                if (view.Result is { } result) view.Result = result with { FilePath = null };
                return Results.Json(view, JsonFiles.Options);
            }
            catch (Exception e) when (e is IOException or JsonException) { return Results.NotFound(); }
        });
        app.MapDelete("/api/jobs/{id}", (string id, HttpContext context) =>
            !Authorized(context, token) ? Results.StatusCode(403) : jobs.Cancel(id) ? Results.Accepted() : Results.NotFound());
        app.MapGet("/api/jobs/{id}/download", (string id, HttpContext context) =>
        {
            try
            {
                var path = jobs.Download(id); if (path is null) return Results.NotFound();
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(path, Mime(path), jobs.Get(id)!.Result!.FileName, enableRangeProcessing: true);
            }
            catch (Exception e) when (e is IOException or JsonException) { return Results.NotFound(); }
        });
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
    private static bool Authorized(HttpContext context, string token) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(context.Request.Headers["X-FinPlan-Token"].ToString()), System.Text.Encoding.UTF8.GetBytes(token));
    [GeneratedRegex(@"^bulk/[A-Za-z0-9_-]+\.csv$")]
    private static partial Regex BulkPath();
}

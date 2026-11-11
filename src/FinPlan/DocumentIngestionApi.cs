using System.Text.Json;

namespace FinPlan;

public sealed record PreviewFile(string Name, string TargetPath, string[] Replaces, string Sha256, FinancialDocument Document);
public sealed record ImportPreview(string DatasetId, string Fingerprint, List<PreviewFile> Files);

public static class DocumentIngestionApi
{
    public static void Map(WebApplication app, DemoStore store, FileJobs jobs, RecognitionTools tools, string token)
    {
        app.MapGet("/api/document-reader", async () =>
        {
            string[] languages;
            try { languages = await tools.LanguagesAsync(); } catch (InvalidOperationException) { languages = []; }
            return Results.Json(new { formats = DocumentContentReader.Formats, max_input_bytes = DocumentContentReader.MaxInputBytes, ocr = tools.Tesseract is not null && tools.PdfToPpm is not null, languages });
        });
        app.MapPost("/api/jobs/read", async (HttpContext context) =>
        {
            if (!DocumentApi.Authorized(context, token)) return Results.StatusCode(403);
            string? directory = null;
            try
            {
                if (!context.Request.HasFormContentType) throw new InvalidDataException("Ожидается форма с документами.");
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                var snapshot = store.Load(form["datasetId"].ToString());
                var options = new RecognitionOptions(form["ocr"].ToString() is { Length: > 0 } ocr ? ocr : "auto", form["languages"].ToString() is { Length: > 0 } lang ? lang : "eng+rus");
                var id = Guid.NewGuid().ToString("N"); directory = jobs.DirectoryFor(id); Directory.CreateDirectory(directory);
                var names = new List<string>(); var inputs = new List<string>();
                var uploads = form.Files.GetFiles("files");
                if (uploads.Count > 0)
                {
                    if (uploads.Count > 100 || uploads.Sum(f => f.Length) > DocumentContentReader.MaxInputBytes) throw new InvalidDataException("За один раз можно загрузить до 100 файлов общим размером до 100 МБ.");
                    foreach (var upload in uploads)
                    {
                        var name = Path.GetFileName(upload.FileName.Replace('\\', '/')); var format = DocumentContentReader.Format(name);
                        var path = Path.Combine(directory, $"input-{inputs.Count:000}.{format}");
                        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) await upload.CopyToAsync(stream, context.RequestAborted);
                        DocumentContentReader.Validate(path); names.Add(name); inputs.Add(path);
                    }
                }
                else
                {
                    var source = form["source"].ToString();
                    if (!snapshot.Data.Documents.Any(d => d.Path == source)) throw new InvalidDataException("Документ отсутствует в текущем наборе.");
                    var path = Path.Combine(directory, "input-000." + DocumentContentReader.Format(source));
                    File.Copy(Path.Combine(snapshot.Directory, source), path); names.Add(Path.GetFileName(source)); inputs.Add(path);
                }
                var work = directory;
                var view = jobs.Enqueue(id, "reading", async (progress, cancellation) =>
                {
                    var fingerprint = FinancialImport.Fingerprint(snapshot); var files = new List<PreviewFile>();
                    for (var i = 0; i < inputs.Count; i++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        progress(new(i, inputs.Count, 0, $"Читаем документ {i + 1} из {inputs.Count}"));
                        var report = await FinancialTemplateReader.ReadAsync(inputs[i], work, tools, options, text => progress(new(i, inputs.Count, 0, text)), cancellation);
                        var identity = FinancialImport.Identity(report);
                        var matches = identity == "" ? [] : snapshot.Data.Documents.Where(d =>
                        {
                            var hash = FinancialImport.Hash(Path.Combine(snapshot.Directory, d.Path));
                            var extraction = d.Sha256 == hash && d.Extraction?.ReaderVersion == FinancialTemplateReader.Version ? d.Extraction : null;
                            extraction ??= FinancialTemplateReader.Read(Path.Combine(snapshot.Directory, d.Path));
                            return FinancialImport.Identity(extraction) == identity;
                        }).Select(d => d.Path).ToArray();
                        var target = matches.FirstOrDefault(p => Path.GetExtension(p) == "." + report.Format && !p.Contains("_copy", StringComparison.Ordinal))
                            ?? $"documents/import_{id}_{i:000}.{report.Format}";
                        if (files.Any(f => f.TargetPath == target)) target = $"documents/import_{id}_{i:000}.{report.Format}";
                        files.Add(new(names[i], target, matches, FinancialImport.Hash(inputs[i]), report));
                    }
                    return new(Preview: new(snapshot.Id, fingerprint, files), Bytes: inputs.Sum(p => new FileInfo(p).Length));
                }, inputs.Count);
                return Results.Json(view, JsonFiles.Options, statusCode: 202);
            }
            catch (GenerationBusyException e) { return Results.Json(new { error = e.Message }, statusCode: 409); }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or JsonException or System.Xml.XmlException)
            {
                if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true);
                return Results.BadRequest(new { error = e is InvalidDataException ? e.Message : "Не удалось принять документы. Проверьте формат и шаблон." });
            }
        });
        app.MapPost("/api/jobs/{id}/apply", async (string id, HttpContext context) =>
        {
            if (!DocumentApi.Authorized(context, token)) return Results.StatusCode(403);
            try
            {
                using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                if (!json.RootElement.TryGetProperty("confirmed", out var confirmed) || confirmed.ValueKind != JsonValueKind.True)
                    return Results.BadRequest(new { error = "Сначала проверьте извлечённые суммы, даты и реквизиты и подтвердите их." });
                var preview = jobs.Get(id)?.Result?.Preview;
                if (preview is null) return Results.NotFound();
                if (preview.Files.Any(f => !f.Document.CanApply)) return Results.BadRequest(new { error = "Исправьте неоднозначно распознанные поля в исходных файлах и прочитайте документы заново." });
                if (preview.DatasetId != store.Current.Id) return Results.Json(new { error = "Набор уже изменился. Прочитайте документы заново." }, statusCode: 409);
                var applyId = Guid.NewGuid().ToString("N");
                var view = jobs.Enqueue(applyId, "import", async (progress, cancellation) =>
                {
                    var next = await store.ImportAsync(preview, jobs.DirectoryFor(id), progress, cancellation);
                    return new(Url: $"/datasets/{next.Id}/calendar_view.html#/documents", Bytes: DatasetFiles.BytesIn(next.Directory), Warnings: store.CleanupWarning is { } warning ? [warning] : []);
                });
                return Results.Json(view, JsonFiles.Options, statusCode: 202);
            }
            catch (GenerationBusyException e) { return Results.Json(new { error = e.Message }, statusCode: 409); }
            catch (Exception e) when (e is IOException or JsonException or ArgumentException or InvalidOperationException)
            { return Results.BadRequest(new { error = "Не удалось применить результат. Прочитайте документы заново." }); }
        });
    }
}

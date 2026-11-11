using System.Security.Cryptography;
using System.Text.Json;

namespace FinPlan;

public sealed record DemoSnapshot(string Id, string Directory, Dataset Data);
public sealed class GenerationBusyException() : InvalidOperationException("Генерация уже выполняется. Дождитесь завершения.");

/// <summary>Publishes a complete replacement before removing the previous dataset.</summary>
public sealed class DemoStore
{
    internal static readonly string[] MetadataFiles = ["dataset.json", "calendar.csv", "duplicates.json", "reports.json", "calendar_view.html", "volume-manifest.json", "corpus-accounting.json"];
    private readonly string output;
    private readonly DemoGenerator generator;
    private readonly Func<string, int, Dataset> generate;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DemoSnapshot current;
    private sealed record CurrentReference(string Id);
    public DemoSnapshot Current => Volatile.Read(ref current);
    public string? CleanupWarning { get; private set; }

    public DemoStore(string root, string output, Func<string, int, Dataset>? generate = null)
    {
        this.output = Path.GetFullPath(output);
        generator = new(root);
        this.generate = generate ?? generator.Generate;
        System.IO.Directory.CreateDirectory(this.output);
        if (!File.Exists(Path.Combine(this.output, "current.json")) && !File.Exists(Path.Combine(this.output, "dataset.json"))) this.generate(this.output, 42);
        var currentDirectory = ResolveCurrentDirectory(this.output);
        current = ReadSnapshot(currentDirectory == this.output ? "initial" : Path.GetFileName(currentDirectory));
        FinancialImport.Calculate(current.Directory, current.Data);
        generator.Save(current.Directory, current.Data);
        if (current.Data.Volume is not null)
        {
            var manifest = JsonSerializer.Deserialize<VolumeManifest>(File.ReadAllText(Path.Combine(current.Directory, "volume-manifest.json")), JsonFiles.Options)
                ?? throw new InvalidDataException("Не найден манифест большого корпуса.");
            BulkVolume.Finalize(current.Directory, current.Data, manifest, generator);
        }
        else generator.RenderViewer(current.Directory, current.Data);
        CleanupWarning = RemovePreviousDatasets();
    }

    public async Task<DemoSnapshot> RegenerateAsync(int? seed = null, long? targetBytes = null, Action<GenerationProgress>? progress = null, CancellationToken cancellation = default)
    {
        if (!await gate.WaitAsync(0)) throw new GenerationBusyException();
        var directory = Path.Combine(output, "generations", Guid.NewGuid().ToString("N"));
        var pointerTemp = Path.Combine(output, ".current-" + Guid.NewGuid().ToString("N") + ".tmp");
        var published = false;
        try
        {
            if (targetBytes is not null)
            {
                if (targetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(targetBytes));
                var available = new DriveInfo(Path.GetPathRoot(output)!).AvailableFreeSpace;
                if (targetBytes > available - 5_000_000_000) throw new IOException("Для временного создания нового набора нужно свободное место на его объём и ещё 5 ГБ.");
            }
            cancellation.ThrowIfCancellationRequested();
            var nextSeed = seed ?? RandomNumberGenerator.GetInt32(1, int.MaxValue);
            while (seed is null && nextSeed == Current.Data.Seed) nextSeed = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            var data = await Task.Run(() => generate(directory, nextSeed));
            if (targetBytes is not null)
            {
                await Task.Run(() =>
                {
                    var manifest = BulkVolume.Generate(directory, nextSeed, targetBytes.Value, progress, cancellation);
                    BulkVolume.Finalize(directory, data, manifest, generator, progress, cancellation);
                }, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            var next = new DemoSnapshot(Path.GetFileName(directory), directory, data);
            JsonFiles.Write(pointerTemp, new CurrentReference(next.Id));
            File.Move(pointerTemp, Path.Combine(output, "current.json"), true);
            Volatile.Write(ref current, next);
            published = true;
            CleanupWarning = RemovePreviousDatasets();
            return next;
        }
        catch
        {
            if (!published && System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
            if (File.Exists(pointerTemp)) File.Delete(pointerTemp);
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task<DemoSnapshot> ResizeAsync(string expectedId, string operation, long bytes, Action<GenerationProgress>? progress = null, CancellationToken cancellation = default)
    {
        if (!await gate.WaitAsync(0)) throw new GenerationBusyException();
        var directory = Path.Combine(output, "generations", Guid.NewGuid().ToString("N"));
        var pointerTemp = Path.Combine(output, ".current-" + Guid.NewGuid().ToString("N") + ".tmp");
        var published = false;
        try
        {
            if (!string.Equals(expectedId, Current.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Набор уже изменился. Откройте текущий набор и повторите корректировку.");
            if (operation is not ("add" or "remove") || bytes < 1_000_000) throw new ArgumentException("Выберите добавление или удаление и объём от 1 МБ.");
            var previous = Load(expectedId); var before = DatasetFiles.BytesIn(previous.Directory);
            var manifest = DocumentApi.ReadVolume(previous);
            if (operation == "remove" && (manifest is null || bytes > BulkVolume.RemovableBytes(manifest)))
                throw new ArgumentException("Нельзя удалить такой объём: контрольные документы и справочник контрагентов сохраняются.");
            var reserve = operation == "add" ? checked(bytes + 5_000_000_000) : 20_000_000;
            if (new DriveInfo(Path.GetPathRoot(output)!).AvailableFreeSpace < reserve)
                throw new InvalidOperationException("Недостаточно свободного места для корректировки набора.");
            await Task.Run(() => DatasetFiles.CopySnapshot(previous.Directory, directory, reserve, cancellation), cancellation);
            var data = ReadSnapshot(Path.GetFileName(directory)).Data;
            var target = operation == "add" ? checked(before + bytes) : before - bytes;
            await Task.Run(() =>
            {
                if (operation == "add")
                {
                    var oldCount = manifest?.Summary.Obligations ?? 0;
                    void Report(GenerationProgress p) => progress?.Invoke(new(Math.Max(0, p.Bytes - before), bytes, p.Obligations - oldCount, "Догенерация: " + p.Message));
                    manifest = manifest is null ? BulkVolume.Generate(directory, data.Seed, target, Report, cancellation)
                        : BulkVolume.Append(directory, manifest, target, Report, cancellation);
                }
                else BulkVolume.RemoveSuffix(directory, manifest!, bytes, progress, cancellation);
                manifest!.Summary.MeasuredTarget = false;
                manifest.Summary.TargetBytes = target;
                manifest.Summary.LastChange = new(operation, bytes, before);
                BulkVolume.Finalize(directory, data, manifest, generator, progress, cancellation);
                var actual = data.Volume!.TotalBytes;
                if (operation == "add" ? actual < before + bytes : actual >= before || Math.Abs(before - actual - bytes) > BulkVolume.ToleranceBytes)
                    throw new InvalidDataException("Не удалось скорректировать объём в пределах допуска. Текущий набор сохранён.");
            }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var next = new DemoSnapshot(Path.GetFileName(directory), directory, data);
            JsonFiles.Write(pointerTemp, new CurrentReference(next.Id));
            File.Move(pointerTemp, Path.Combine(output, "current.json"), true);
            Volatile.Write(ref current, next); published = true;
            CleanupWarning = RemovePreviousDatasets();
            return next;
        }
        catch
        {
            if (!published && System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
            if (File.Exists(pointerTemp)) File.Delete(pointerTemp);
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task<DemoSnapshot> ImportAsync(ImportPreview preview, string inputs, Action<GenerationProgress>? progress = null, CancellationToken cancellation = default)
    {
        if (!await gate.WaitAsync(0)) throw new GenerationBusyException();
        var directory = Path.Combine(output, "generations", Guid.NewGuid().ToString("N"));
        var pointerTemp = Path.Combine(output, ".current-" + Guid.NewGuid().ToString("N") + ".tmp");
        var published = false;
        try
        {
            if (preview.Files.Any(f => !f.Document.CanApply)) throw new InvalidDataException("Неоднозначные результаты OCR нельзя применять.");
            if (preview.DatasetId != Current.Id || preview.Fingerprint != FinancialImport.Fingerprint(Current)) throw new InvalidOperationException("Набор или его исходные файлы изменились. Прочитайте документы заново перед подтверждением.");
            var previous = Load(preview.DatasetId);
            await Task.Run(() => DatasetFiles.CopySnapshot(previous.Directory, directory, 20_000_000, cancellation), cancellation);
            var data = ReadSnapshot(Path.GetFileName(directory)).Data;
            var removed = preview.Files.SelectMany(f => f.Replaces).ToHashSet(StringComparer.Ordinal);
            if (removed.Any(path => !data.Documents.Any(d => d.Path == path))) throw new InvalidDataException("Некорректная замена исходного документа.");
            var bindings = data.Documents.Where(d => removed.Contains(d.Path)).ToDictionary(d => d.Path, d => d.Obligations);
            foreach (var path in removed) File.Delete(Path.Combine(directory, path));
            data.Documents.RemoveAll(d => removed.Contains(d.Path));
            for (var i = 0; i < preview.Files.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var file = preview.Files[i]; var input = Path.Combine(inputs, $"input-{i:000}.{file.Document.Format}");
                if (FinancialImport.Hash(input) != file.Sha256) throw new InvalidDataException("Загруженный оригинал изменился после проверки. Прочитайте его заново.");
                if (!file.TargetPath.StartsWith("documents/", StringComparison.Ordinal) || file.TargetPath[10..] != Path.GetFileName(file.TargetPath)) throw new InvalidDataException("Некорректный путь документа.");
                File.Copy(input, Path.Combine(directory, file.TargetPath), false);
                if (FinancialImport.Hash(Path.Combine(directory, file.TargetPath)) != file.Sha256) throw new InvalidDataException("Оригинал изменился во время копирования. Прочитайте документ заново.");
                data.Documents.Add(new() { Path = file.TargetPath, Type = file.Document.Type, Sha256 = file.Sha256, Extraction = file.Document, Obligations = file.Replaces.SelectMany(p => bindings[p]).Distinct().ToList() });
                progress?.Invoke(new(i + 1, preview.Files.Count, 0, "Проверяем связи и пересчитываем финансовые данные"));
            }
            await Task.Run(() => FinancialImport.Calculate(directory, data), cancellation);
            generator.Save(directory, data);
            if (data.Volume is not null)
            {
                var manifest = DocumentApi.ReadVolume(new("", directory, data))!;
                manifest.Files.RemoveAll(f => f.Kind == "control");
                manifest.Files.InsertRange(0, data.Documents.Select(d => new VolumeFile(d.Path, "control", new FileInfo(Path.Combine(directory, d.Path)).Length, 0, d.Sha256)));
                manifest.Summary.Files = manifest.Files.Count; manifest.Summary.DocumentBytes = manifest.Files.Sum(f => f.Bytes);
                manifest.Summary.LastChange = null; manifest.Summary.MeasuredTarget = true;
                BulkVolume.Finalize(directory, data, manifest, generator, progress, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            if (FinancialImport.Fingerprint(previous) != preview.Fingerprint) throw new InvalidOperationException("Исходные файлы текущего набора изменились во время обработки. Повторите чтение.");
            var next = new DemoSnapshot(Path.GetFileName(directory), directory, data);
            JsonFiles.Write(pointerTemp, new CurrentReference(next.Id)); File.Move(pointerTemp, Path.Combine(output, "current.json"), true);
            Volatile.Write(ref current, next); published = true; CleanupWarning = RemovePreviousDatasets();
            return next;
        }
        catch
        {
            if (!published && System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
            if (File.Exists(pointerTemp)) File.Delete(pointerTemp);
            throw;
        }
        finally { gate.Release(); }
    }

    public static string ResolveCurrentDirectory(string output)
    {
        output = Path.GetFullPath(output);
        var pointer = Path.Combine(output, "current.json");
        if (!File.Exists(pointer)) return output;
        var id = JsonSerializer.Deserialize<CurrentReference>(File.ReadAllText(pointer), JsonFiles.Options)?.Id;
        if (id is null || !Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Некорректный указатель набора данных.");
        var directory = Path.Combine(output, "generations", id);
        if (!File.Exists(Path.Combine(directory, "dataset.json"))) throw new FileNotFoundException("Текущий набор данных не найден.");
        return directory;
    }

    public DemoSnapshot Load(string id)
    {
        if (!string.Equals(id, Current.Id, StringComparison.OrdinalIgnoreCase)) throw new FileNotFoundException("Этот набор заменён. Откройте текущий набор.");
        return ReadSnapshot(Current.Id);
    }

    private DemoSnapshot ReadSnapshot(string id)
    {
        if (id != "initial" && !Guid.TryParseExact(id, "N", out _)) throw new FileNotFoundException("Неизвестный набор данных.");
        var directory = id == "initial" ? output : Path.Combine(output, "generations", id);
        var path = Path.Combine(directory, "dataset.json");
        if (!File.Exists(path)) throw new FileNotFoundException("Набор данных не найден.");
        var data = JsonSerializer.Deserialize<Dataset>(File.ReadAllText(path), JsonFiles.Options) ?? throw new InvalidDataException("Некорректный набор данных.");
        return new(id, directory, data);
    }

    private string? RemovePreviousDatasets()
    {
        var blocked = false;
        void Remove(Action action)
        {
            try { action(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { blocked = true; }
        }
        void RemoveDirectory(string path)
        {
            if (System.IO.Directory.Exists(path))
                System.IO.Directory.Delete(path, (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0);
        }
        Remove(() =>
        {
            var generations = Path.Combine(output, "generations");
            if (!System.IO.Directory.Exists(generations)) return;
            foreach (var path in System.IO.Directory.EnumerateDirectories(generations))
            {
                var id = Path.GetFileName(path);
                var retired = Path.Combine(generations, ".retired-" + id);
                if (!string.Equals(id, Current.Id, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(id, "N", out _) && (File.Exists(Path.Combine(path, "dataset.json")) || File.Exists(retired)))
                    Remove(() =>
                    {
                        // Keep ownership outside the directory: a partial Windows deletion may remove dataset.json before hitting a locked file.
                        if (!File.Exists(retired)) File.WriteAllText(retired, "");
                        RemoveDirectory(path); File.Delete(retired);
                    });
            }
        });
        if (Current.Id != "initial")
        {
            foreach (var name in new[] { "documents", "bulk" }) Remove(() => RemoveDirectory(Path.Combine(output, name)));
            foreach (var name in MetadataFiles) Remove(() => File.Delete(Path.Combine(output, name)));
        }
        return blocked ? "Новый набор доступен, но часть старых файлов занята или недоступна. Закройте открытые документы и перезапустите приложение для завершения очистки." : null;
    }
}

using System.Security.Cryptography;
using System.Text.Json;

namespace FinPlan;

public sealed record DemoSnapshot(string Id, string Directory, Dataset Data);
public sealed class GenerationBusyException() : InvalidOperationException("Генерация уже выполняется. Дождитесь завершения.");

/// <summary>Publishes a complete replacement before removing the previous dataset.</summary>
public sealed class DemoStore
{
    internal static readonly string[] MetadataFiles = ["dataset.json", "calendar.csv", "duplicates.json", "reports.json", "calendar_view.html", "volume-manifest.json"];
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
                    BulkVolume.Finalize(directory, data, manifest, generator);
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
                manifest!.Summary.TargetBytes = target;
                manifest.Summary.LastChange = new(operation, bytes, before);
                BulkVolume.Finalize(directory, data, manifest, generator);
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
                if (!string.Equals(id, Current.Id, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(id, "N", out _) && File.Exists(Path.Combine(path, "dataset.json")))
                    Remove(() => RemoveDirectory(path));
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

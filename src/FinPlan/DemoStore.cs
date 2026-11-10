using System.Security.Cryptography;
using System.Text.Json;

namespace FinPlan;

public sealed record DemoSnapshot(string Id, string Directory, Dataset Data);
public sealed class GenerationBusyException() : InvalidOperationException("Генерация уже выполняется. Дождитесь завершения.");

/// <summary>Creates immutable datasets and switches the current pointer only after successful generation.</summary>
public sealed class DemoStore
{
    private readonly string output;
    private readonly DemoGenerator generator;
    private readonly Func<string, int, Dataset> generate;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DemoSnapshot current;
    private sealed record CurrentReference(string Id);
    public DemoSnapshot Current => Volatile.Read(ref current);

    public DemoStore(string root, string output, Func<string, int, Dataset>? generate = null)
    {
        this.output = Path.GetFullPath(output);
        generator = new(root);
        this.generate = generate ?? generator.Generate;
        System.IO.Directory.CreateDirectory(this.output);
        if (!File.Exists(Path.Combine(this.output, "dataset.json"))) this.generate(this.output, 42);
        var currentDirectory = ResolveCurrentDirectory(this.output);
        current = Load(currentDirectory == this.output ? "initial" : Path.GetFileName(currentDirectory));
        if (current.Data.Volume is not null)
        {
            var manifest = JsonSerializer.Deserialize<VolumeManifest>(File.ReadAllText(Path.Combine(current.Directory, "volume-manifest.json")), JsonFiles.Options)
                ?? throw new InvalidDataException("Не найден манифест большого корпуса.");
            BulkVolume.Finalize(current.Directory, current.Data, manifest, generator);
        }
        else generator.RenderViewer(current.Directory, current.Data);
    }

    public async Task<DemoSnapshot> RegenerateAsync(int? seed = null, long? targetBytes = null, Action<GenerationProgress>? progress = null, CancellationToken cancellation = default)
    {
        if (!await gate.WaitAsync(0)) throw new GenerationBusyException();
        var directory = Path.Combine(output, "generations", Guid.NewGuid().ToString("N"));
        var pointerTemp = Path.Combine(output, ".current-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (targetBytes is not null)
            {
                if (targetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(targetBytes));
                var available = new DriveInfo(Path.GetPathRoot(output)!).AvailableFreeSpace;
                if (targetBytes > available - 5_000_000_000) throw new IOException("Для выбранного объёма недостаточно места. После генерации должно остаться хотя бы 5 ГБ.");
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
            return next;
        }
        catch
        {
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
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
        if (id != "initial" && !Guid.TryParseExact(id, "N", out _)) throw new FileNotFoundException("Неизвестный набор данных.");
        var directory = id == "initial" ? output : Path.Combine(output, "generations", id);
        var path = Path.Combine(directory, "dataset.json");
        if (!File.Exists(path)) throw new FileNotFoundException("Набор данных не найден.");
        var data = JsonSerializer.Deserialize<Dataset>(File.ReadAllText(path), JsonFiles.Options) ?? throw new InvalidDataException("Некорректный набор данных.");
        return new(id, directory, data);
    }
}

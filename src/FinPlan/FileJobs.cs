using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace FinPlan;

public sealed record FileJobResult(string? Url = null, List<string>? Warnings = null, long Bytes = 0, ImportPreview? Preview = null);
public sealed class FileJobView
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string State { get; set; } = "queued";
    public string Message { get; set; } = "В очереди";
    public string? Error { get; set; }
    public long Bytes { get; set; }
    public long TargetBytes { get; set; }
    public long Obligations { get; set; }
    public FileJobResult? Result { get; set; }
}

/// <summary>One bounded worker keeps file processing outside HTTP requests and records terminal states.</summary>
public sealed class FileJobs : BackgroundService
{
    private sealed record Work(FileJobView View, Func<Action<GenerationProgress>, CancellationToken, Task<FileJobResult>> Run, CancellationTokenSource Cancellation);
    private readonly ConcurrentDictionary<string, Work> jobs = new();
    private readonly Channel<Work> queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly string directory;
    public FileJobs(string output) { directory = Path.Combine(output, ".jobs"); Directory.CreateDirectory(directory); }
    public string DirectoryFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new FileNotFoundException("Задание не найдено.");
        return Path.Combine(directory, id);
    }
    public FileJobView Enqueue(string id, string kind, Func<Action<GenerationProgress>, CancellationToken, Task<FileJobResult>> run, long targetBytes = 0)
    {
        var view = new FileJobView { Id = id, Kind = kind, TargetBytes = targetBytes };
        Directory.CreateDirectory(DirectoryFor(id)); var work = new Work(view, run, new());
        if (!jobs.TryAdd(id, work)) throw new InvalidOperationException("Задание с таким ID уже существует.");
        Save(view);
        if (!queue.Writer.TryWrite(work))
        {
            jobs.TryRemove(id, out _); work.Cancellation.Dispose();
            view.State = "failed"; view.Error = "Очередь заполнена. Дождитесь завершения текущих заданий."; Save(view);
            throw new GenerationBusyException();
        }
        return Get(id)!;
    }
    public FileJobView? Get(string id)
    {
        var path = Path.Combine(DirectoryFor(id), "job.json");
        if (jobs.TryGetValue(id, out var work)) { lock (work.View) return Clone(work.View); }
        if (!File.Exists(path)) return null;
        var view = JsonSerializer.Deserialize<FileJobView>(File.ReadAllText(path), JsonFiles.Options);
        if (view?.State is "queued" or "running")
        {
            view.State = "failed"; view.Error = "Задание прервано перезапуском приложения. Исходный файл сохранён; повторите обработку."; Save(view);
        }
        return view;
    }
    public bool Cancel(string id)
    {
        if (!jobs.TryGetValue(id, out var work)) return false;
        lock (work.View)
        {
            if (work.View.State is not ("queued" or "running")) return false;
            work.Cancellation.Cancel(); return true;
        }
    }
    private static FileJobView Clone(FileJobView view) => new()
    {
        Id = view.Id, Kind = view.Kind, State = view.State, Message = view.Message, Error = view.Error,
        Bytes = view.Bytes, TargetBytes = view.TargetBytes, Obligations = view.Obligations, Result = view.Result
    };
    private void Save(FileJobView view)
    {
        var path = Path.Combine(DirectoryFor(view.Id), "job.json"); var temp = path + ".tmp";
        JsonFiles.Write(temp, view); File.Move(temp, path, true);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var work in queue.Reader.ReadAllAsync(stoppingToken))
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, work.Cancellation.Token);
                lock (work.View) { work.View.State = "running"; work.View.Message = "Обработка"; Save(work.View); }
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var saved = DateTime.UtcNow;
                    var result = await Task.Run(() => work.Run(p =>
                    {
                        lock (work.View)
                        {
                            work.View.Bytes = p.Bytes; work.View.TargetBytes = p.TargetBytes; work.View.Obligations = p.Obligations; work.View.Message = p.Message;
                            if ((DateTime.UtcNow - saved).TotalSeconds >= 1) { Save(work.View); saved = DateTime.UtcNow; }
                        }
                    }, cancellation.Token), cancellation.Token);
                    lock (work.View)
                    {
                        work.View.Result = result; work.View.Bytes = result.Bytes; work.View.State = "succeeded"; work.View.Message = "Готово"; Save(work.View);
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (work.View) { work.View.State = "cancelled"; work.View.Message = "Отменено"; Save(work.View); }
                }
                catch (Exception error)
                {
                    lock (work.View)
                    {
                        work.View.State = "failed";
                        work.View.Error = error is ArgumentException or InvalidDataException or InvalidOperationException ? error.Message : "Не удалось обработать файл. Проверьте формат, зависимости и свободное место.";
                        Save(work.View);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    public override void Dispose()
    {
        foreach (var work in jobs.Values) work.Cancellation.Dispose();
        base.Dispose();
    }
}

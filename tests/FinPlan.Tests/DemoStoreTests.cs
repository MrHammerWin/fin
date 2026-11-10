using System.Text.Json;
using FinPlan;
using Xunit;

namespace FinPlan.Tests;

public sealed class DemoStoreTests(DemoFixture fixture) : IClassFixture<DemoFixture>
{
    private static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Solution1.sln"))) return d.FullName;
        throw new DirectoryNotFoundException();
    }
    private string Workspace()
    {
        var folder = Path.Combine(Path.GetTempPath(), "finplan-store-test-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(fixture.Output, "dataset.json"), Path.Combine(folder, "dataset.json"));
        return folder;
    }
    [Fact]
    public async Task SuccessfulReplacementDeletesPreviousDatasetAndSurvivesRestartWithoutRecreatingInitial()
    {
        var folder = Workspace();
        try
        {
            var store = new DemoStore(Root(), folder);
            var next = await store.RegenerateAsync(12345);
            Assert.Equal(12345, next.Data.Seed); Assert.Equal(100, next.Data.Documents.Count);
            Assert.False(File.Exists(Path.Combine(folder, "dataset.json")));
            Assert.Throws<FileNotFoundException>(() => store.Load("initial"));
            Assert.NotEqual("initial", next.Id);
            var restarted = new DemoStore(Root(), folder, (_, _) => throw new InvalidOperationException("Initial data must not be recreated."));
            Assert.Equal(next.Id, restarted.Current.Id);
            Assert.Equal(100, Directory.GetFiles(Path.Combine(next.Directory, "documents")).Length);
            Assert.Throws<FileNotFoundException>(() => store.Load("../../etc"));
            var replacement = await store.RegenerateAsync(54321);
            Assert.False(Directory.Exists(next.Directory));
            Assert.Equal(replacement.Id, store.Current.Id);
            Assert.Single(Directory.GetDirectories(Path.Combine(folder, "generations")));
            Assert.Null(store.CleanupWarning);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public void StartupRemovesLegacyDatasetsButKeepsSelectedDataAndUnrelatedFiles()
    {
        var folder = Workspace(); var selected = Guid.NewGuid().ToString("N"); var retired = Guid.NewGuid().ToString("N");
        try
        {
            foreach (var id in new[] { selected, retired })
            {
                var path = Path.Combine(folder, "generations", id); Directory.CreateDirectory(path);
                File.Copy(Path.Combine(folder, "dataset.json"), Path.Combine(path, "dataset.json"));
                File.WriteAllText(Path.Combine(path, "owned.csv"), "a;b\n1;2");
            }
            var notes = Path.Combine(folder, "generations", "user-notes"); Directory.CreateDirectory(notes);
            File.WriteAllText(Path.Combine(notes, "keep.txt"), "keep");
            Directory.CreateDirectory(Path.Combine(folder, ".jobs")); File.WriteAllText(Path.Combine(folder, ".jobs", "original.csv"), "keep original");
            File.WriteAllText(Path.Combine(folder, "custom.txt"), "keep custom");
            var selectedId = Directory.Exists(Path.Combine(folder, "generations", selected.ToUpperInvariant())) ? selected.ToUpperInvariant() : selected;
            JsonFiles.Write(Path.Combine(folder, "current.json"), new { Id = selectedId });
            var store = new DemoStore(Root(), folder);
            Assert.Equal(Guid.Parse(selected), Guid.Parse(store.Current.Id));
            Assert.Equal(Guid.Parse(selected), Guid.Parse(store.Load(selected).Id));
            Assert.True(File.Exists(Path.Combine(store.Current.Directory, "owned.csv")));
            Assert.False(Directory.Exists(Path.Combine(folder, "generations", retired)));
            Assert.False(File.Exists(Path.Combine(folder, "dataset.json")));
            Assert.True(File.Exists(Path.Combine(notes, "keep.txt")));
            Assert.True(File.Exists(Path.Combine(folder, ".jobs", "original.csv")));
            Assert.True(File.Exists(Path.Combine(folder, "custom.txt")));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task CleanupFailureNeverDeletesThePublishedReplacementAndStartupRetriesCleanup()
    {
        var folder = Workspace();
        try
        {
            var store = new DemoStore(Root(), folder);
            // A directory at an owned file path produces a cleanup error on both Windows and macOS.
            var obstacle = Path.Combine(folder, "calendar.csv"); Directory.CreateDirectory(obstacle);
            var next = await store.RegenerateAsync(919);
            Assert.Equal(next.Id, store.Current.Id);
            Assert.True(File.Exists(Path.Combine(next.Directory, "dataset.json")));
            Assert.NotNull(store.CleanupWarning);
            Directory.Delete(obstacle);
            var restarted = new DemoStore(Root(), folder);
            Assert.Equal(next.Id, restarted.Current.Id); Assert.Null(restarted.CleanupWarning);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task FailedGenerationDoesNotReplaceCurrentDataAndCleansItsTemporaryFolder()
    {
        var folder = Workspace();
        try
        {
            var store = new DemoStore(Root(), folder, (path, _) => { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "partial"), "partial"); throw new IOException("Test failure"); });
            await Assert.ThrowsAsync<IOException>(() => store.RegenerateAsync());
            Assert.Equal("initial", store.Current.Id);
            Assert.False(File.Exists(Path.Combine(folder, "current.json")));
            Assert.Empty(Directory.GetDirectories(Path.Combine(folder, "generations")));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task ConcurrentRegenerationIsRejectedInsteadOfOverwritingAnotherRun()
    {
        var folder = Workspace(); var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        try
        {
            var generator = new DemoGenerator(Root());
            var store = new DemoStore(Root(), folder, (path, seed) => { entered.SetResult(); release.Task.GetAwaiter().GetResult(); return generator.Generate(path, seed); });
            var first = store.RegenerateAsync(123);
            await entered.Task;
            await Assert.ThrowsAsync<GenerationBusyException>(() => store.RegenerateAsync(456));
            await Assert.ThrowsAsync<GenerationBusyException>(() => store.ResizeAsync("initial", "add", 1_000_000));
            release.SetResult();
            Assert.Equal(123, (await first).Data.Seed);
        }
        finally { release.TrySetResult(); Directory.Delete(folder, true); }
    }
}

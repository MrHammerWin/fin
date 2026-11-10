using System.Runtime.InteropServices;

namespace FinPlan;

/// <summary>Reuse immutable corpus files while the replacement is prepared; metadata is always copied.</summary>
internal static class DatasetFiles
{
    public static IEnumerable<string> Paths(string source) => DemoStore.MetadataFiles.Select(name => Path.Combine(source, name)).Where(File.Exists)
        .Concat(new[] { "documents", "bulk" }.Select(name => Path.Combine(source, name)).Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)));

    public static long BytesIn(string source) => Paths(source).Sum(path => new FileInfo(path).Length);

    public static void CopySnapshot(string source, string target, long reserveBytes, CancellationToken cancellation)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Paths(source))
        {
            cancellation.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Рабочий набор не должен содержать символические ссылки.");
            var relative = Path.GetRelativePath(source, file); var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (relative.StartsWith("bulk" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && TryLink(file, destination)) continue;
            if (new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace < new FileInfo(file).Length + reserveBytes)
                throw new InvalidOperationException("Недостаточно места для безопасной корректировки. Файловая система может требовать временную копию текущих данных.");
            File.Copy(file, destination);
        }
    }

    private static bool TryLink(string source, string target) => OperatingSystem.IsWindows()
        ? CreateHardLink(target, source, IntPtr.Zero) : Link(source, target) == 0;

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string target);
}

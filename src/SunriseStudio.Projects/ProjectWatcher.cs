/** File: Watches a project folder for changes made outside Studio. */
namespace SunriseStudio.Projects;

/** How a file differs from the last scan. */
public enum ProjectFileChangeKind { Added, Modified, Removed }

/** One file of the project folder that differs from the last scan; the path is project-relative with '/' separators. */
public sealed record ProjectFileChange(string RelativePath, ProjectFileChangeKind Kind);

/**
 * Notices edits made to a project folder outside Studio (Solforge Phase 15). Scan() compares the
 * tree (length + last write) with the previous snapshot and is the only source of truth; the
 * FileSystemWatcher merely sets Pending so a caller knows a scan is worth running. The gitignored
 * folders (build, exports, .local), .git and *.tmp files are never reported, so Studio's own atomic
 * writes only show up as the final file, and MarkClean() after a save hides those too.
 */
public sealed class ProjectWatcher : IDisposable
{
    public static readonly IReadOnlyList<string> IgnoredRootFolders = ["build", "exports", ".local"];

    private readonly object gate = new();
    private readonly FileSystemWatcher? watcher;
    private Dictionary<string, (long Length, long WriteTicks)> snapshot;
    private int pending;
    private bool disposed;

    public string DirectoryPath { get; }
    /** True once a file system event arrived since the last Scan or MarkClean. */
    public bool Pending => Volatile.Read(ref pending) != 0;
    /** False when the folder cannot deliver events (network shares, exhausted handles); Scan still works. */
    public bool EventsAvailable => watcher is not null;
    /** Raised on the watcher thread when Pending turns on; callers poll Pending from their own thread instead of acting here. */
    public event Action? Changed;

    public ProjectWatcher(string directory, bool events = true)
    {
        DirectoryPath = Path.GetFullPath(directory);
        if (!Directory.Exists(DirectoryPath)) throw new DirectoryNotFoundException($"Project folder not found: {DirectoryPath}");
        snapshot = Capture();
        if (!events) return;
        FileSystemWatcher? source = null;
        try
        {
            source = new FileSystemWatcher(DirectoryPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };
            source.Changed += OnEvent;
            source.Created += OnEvent;
            source.Deleted += OnEvent;
            source.Renamed += OnEvent;
            source.Error += (_, _) => Signal();
            source.EnableRaisingEvents = true;
            watcher = source;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            source?.Dispose();
            watcher = null;
        }
    }

    /** Files that differ from the last scan, sorted by path; the snapshot then moves to the current tree. */
    public IReadOnlyList<ProjectFileChange> Scan()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Interlocked.Exchange(ref pending, 0);
        var current = Capture();
        var changes = new List<ProjectFileChange>();
        lock (gate)
        {
            foreach (var (path, state) in current)
            {
                if (!snapshot.TryGetValue(path, out var previous)) changes.Add(new(path, ProjectFileChangeKind.Added));
                else if (previous != state) changes.Add(new(path, ProjectFileChangeKind.Modified));
            }
            foreach (string path in snapshot.Keys)
                if (!current.ContainsKey(path)) changes.Add(new(path, ProjectFileChangeKind.Removed));
            snapshot = current;
        }
        changes.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return changes;
    }

    /** Adopts the current tree as the baseline, e.g. right after Studio saved the project itself. */
    public void MarkClean()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Interlocked.Exchange(ref pending, 0);
        var current = Capture();
        lock (gate) snapshot = current;
    }

    /** True for paths the watcher never reports: ignored root folders, any .git folder and *.tmp files. */
    public bool IsIgnored(string fullPath)
    {
        string full = Path.GetFullPath(fullPath);
        if (!full.StartsWith(DirectoryPath, StringComparison.OrdinalIgnoreCase)) return true;
        string relative = Path.GetRelativePath(DirectoryPath, full).Replace('\\', '/');
        return IsIgnoredRelative(relative);
    }

    private static bool IsIgnoredRelative(string relative)
    {
        if (relative.Length == 0 || relative == ".") return true;
        if (relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
        var parts = relative.Split('/');
        if (IgnoredRootFolders.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) return true;
        return parts.Any(part => string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase));
    }

    private Dictionary<string, (long Length, long WriteTicks)> Capture()
    {
        var result = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        var folders = new Stack<string>();
        folders.Push(DirectoryPath);
        while (folders.Count != 0)
        {
            string folder = folders.Pop();
            IEnumerable<string> files, directories;
            try
            {
                files = Directory.EnumerateFiles(folder);
                directories = Directory.EnumerateDirectories(folder);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (string directory in directories)
            {
                string relative = Path.GetRelativePath(DirectoryPath, directory).Replace('\\', '/');
                if (IsIgnoredRelative(relative + "/x")) continue;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                folders.Push(directory);
            }
            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(DirectoryPath, file).Replace('\\', '/');
                if (IsIgnoredRelative(relative)) continue;
                try
                {
                    var info = new FileInfo(file);
                    result[relative] = (info.Length, info.LastWriteTimeUtc.Ticks);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        return result;
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (IsIgnored(e.FullPath) && (e is not RenamedEventArgs renamed || IsIgnored(renamed.OldFullPath))) return;
        Signal();
    }

    private void Signal()
    {
        if (Interlocked.Exchange(ref pending, 1) == 0) Changed?.Invoke();
    }

    /** Stops watching and releases the file system watcher. */
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        watcher?.Dispose();
    }
}

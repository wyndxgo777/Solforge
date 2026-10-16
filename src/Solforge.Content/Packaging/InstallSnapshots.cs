using System.Globalization;

namespace Solforge.Content.Packaging;

public sealed record InstallSnapshot(string ModId, string Stamp, string Path, long Bytes, int Files);

/**
 * Backup and rollback for installed mods (Studio.md Phase 43): before an install replaces
 * mods/<id>, the old folder is copied to <snapshots>/<id>/<stamp>; Restore puts a snapshot back
 * (snapshotting the current copy first, so a restore is itself undoable). Only folders under the
 * snapshot root are ever deleted by Prune.
 */
public sealed class InstallSnapshots(string snapshotRoot)
{
    public string Root { get; } = Path.GetFullPath(snapshotRoot);

    /** Copies modsRoot/<modId>; null when the mod is not installed. */
    public InstallSnapshot? Take(string modsRoot, string modId, DateTimeOffset? now = null)
    {
        RequireId(modId);
        string source = Path.Combine(modsRoot, modId);
        if (!Directory.Exists(source)) return null;
        string stamp = (now ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        string target = Path.Combine(Root, modId, stamp);
        for (int n = 2; Directory.Exists(target); n++) target = Path.Combine(Root, modId, stamp + "-" + n);
        var (bytes, files) = Copy(source, target);
        return new(modId, Path.GetFileName(target), target, bytes, files);
    }

    /** Newest first. */
    public IReadOnlyList<InstallSnapshot> List(string modId)
    {
        RequireId(modId);
        string folder = Path.Combine(Root, modId);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateDirectories(folder).OrderDescending(StringComparer.Ordinal).Select(path =>
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Select(file => new FileInfo(file)).ToList();
            return new InstallSnapshot(modId, Path.GetFileName(path), path, files.Sum(file => file.Length), files.Count);
        }).ToList();
    }

    /** Puts a snapshot back as modsRoot/<modId>; the current copy is snapshotted first. */
    public void Restore(string modsRoot, string modId, string stamp)
    {
        RequireId(modId);
        string snapshot = Path.Combine(Root, modId, stamp);
        if (stamp.Contains('/') || stamp.Contains('\\') || stamp.Contains("..") || !Directory.Exists(snapshot))
            throw new DirectoryNotFoundException($"No snapshot '{stamp}' of {modId}.");
        Take(modsRoot, modId);
        string target = Path.Combine(modsRoot, modId);
        string staging = target + ".restoring";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Copy(snapshot, staging);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.Move(staging, target);
    }

    /** Keeps the newest `keep` snapshots of a mod; returns how many were removed. */
    public int Prune(string modId, int keep)
    {
        int removed = 0;
        foreach (var snapshot in List(modId).Skip(Math.Max(keep, 1)))
        {
            string full = Path.GetFullPath(snapshot.Path);
            if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.Delete(full, recursive: true);
            removed++;
        }
        return removed;
    }

    private static void RequireId(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId) || modId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || modId is "." or "..")
            throw new ArgumentException($"'{modId}' is not a mod folder name.");
    }

    private static (long Bytes, int Files) Copy(string source, string target)
    {
        long bytes = 0; int files = 0;
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            File.Copy(file, destination, overwrite: true);
            bytes += new FileInfo(file).Length; files++;
        }
        return (bytes, files);
    }
}

/** File: Partial ModInstaller: in-place update of an installed mod's scripts with rollback on failure. */
using System.Text.Json;

namespace SunriseStudio.Projects;

/** Partial: script update helpers. */
public static partial class ModInstaller
{
    private sealed record ScriptUpdateFile(string RelativePath, byte[]? Before, byte[]? After);

    /** A child link is refused before enumeration can leave the source folder. */
    private static IEnumerable<string> UnlinkedFiles(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            ProjectPaths.RejectLinks(directory, "Mod sources cannot use linked folders or files.");
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                ProjectPaths.RejectLinks(file, "Mod sources cannot use linked folders or files.");
                yield return file;
            }
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                ProjectPaths.RejectLinks(child, "Mod sources cannot use linked folders or files.");
                pending.Push(child);
            }
        }
    }

    /** Failed file replacement restores earlier writes before the game is asked to reload. */
    private static void ApplyUpdate(string target, ModUpdatePlan plan, IReadOnlyDictionary<string, byte[]> before,
        IEnumerable<string> ownedScripts, byte[]? oldReceipt)
    {
        string receiptPath = ProjectPaths.Resolve(target, SourceReceiptName);
        RequireScriptSnapshot(receiptPath, oldReceipt);
        byte[] nextReceipt = SourceReceipt(ownedScripts);
        ScriptUpdateFile[] receipt = oldReceipt is not null && oldReceipt.AsSpan().SequenceEqual(nextReceipt)
            ? [] : [new ScriptUpdateFile(SourceReceiptName, oldReceipt, nextReceipt)];
        var changes = plan.Writes.Select(file => new ScriptUpdateFile("scripts/" + file.RelativePath,
                before.GetValueOrDefault(file.RelativePath)?.ToArray(), file.Data.ToArray()))
            .Concat(plan.Deletes.Select(path => new ScriptUpdateFile("scripts/" + path, before[path].ToArray(), null)))
            .Concat(receipt).ToArray();
        if (changes.Length == 0) return;
        if (changes.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Length)
            throw new InvalidDataException("A script appears twice in the update.");
        foreach (var file in changes) RequireScriptSnapshot(ScriptPath(target, file.RelativePath), file.Before);
        var existingFolders = changes.Select(file => Path.GetDirectoryName(ScriptPath(target, file.RelativePath))!)
            .Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string recovery = Path.Combine(target, ".script-update-" + Guid.NewGuid().ToString("N"));
        ProjectPaths.RejectLinks(recovery);
        var applied = new List<int>();
        try
        {
            Directory.CreateDirectory(recovery);
            for (int index = 0; index < changes.Length; index++)
            {
                var file = changes[index];
                if (file.Before is not null) ProjectPaths.AtomicWrite(Path.Combine(recovery, $"{index}.before"), file.Before);
                if (file.After is not null) ProjectPaths.AtomicWrite(Path.Combine(recovery, $"{index}.after"), file.After);
            }
            ProjectPaths.AtomicWrite(Path.Combine(recovery, "restore.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 2,
                files = changes.Select((file, index) => new { file.RelativePath,
                    backup = file.Before is null ? null : $"{index}.before" })
            }));
            for (int index = 0; index < changes.Length; index++)
            {
                var file = changes[index]; string path = ScriptPath(target, file.RelativePath);
                RequireScriptSnapshot(path, file.Before);
                if (file.After is null) File.Delete(path);
                else ProjectPaths.AtomicWrite(path, File.ReadAllBytes(Path.Combine(recovery, $"{index}.after")));
                applied.Add(index);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            var errors = new List<Exception> { failure };
            foreach (int index in applied.AsEnumerable().Reverse())
            {
                var file = changes[index];
                try
                {
                    string path = ScriptPath(target, file.RelativePath);
                    RequireScriptSnapshot(path, file.After);
                    if (file.Before is null) File.Delete(path);
                    else ProjectPaths.AtomicWrite(path, File.ReadAllBytes(Path.Combine(recovery, $"{index}.before")));
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException) { errors.Add(rollback); }
            }
            if (errors.Count > 1)
                throw new IOException($"The script update could not be fully restored. Recovery files are in {recovery}.", new AggregateException(errors));
            TryDelete(recovery);
            RemoveEmptyScriptFolders(target, changes.Where(file => !existingFolders.Contains(
                Path.GetDirectoryName(ScriptPath(target, file.RelativePath))!)).Select(file => file.RelativePath));
            throw new IOException("The script update failed. Earlier script changes were restored.", failure);
        }
        TryDelete(recovery);
        RemoveEmptyScriptFolders(target, plan.Deletes.Select(path => "scripts/" + path));
    }

    private static void RequireScriptSnapshot(string path, byte[]? expected)
    {
        if (expected is null ? File.Exists(path) : !File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
            throw new IOException("An installed script changed while its update was being prepared.");
    }

    private static void RemoveEmptyScriptFolders(string root, IEnumerable<string> paths)
    {
        foreach (string relative in paths)
        {
            try
            {
                string folder = Path.GetDirectoryName(ScriptPath(root, relative))!;
                if (!string.Equals(folder, root, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}

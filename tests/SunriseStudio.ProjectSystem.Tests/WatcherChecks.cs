using SunriseStudio.Projects;
using static SunriseStudio.ProjectSystem.Tests.Check;

namespace SunriseStudio.ProjectSystem.Tests;

/** Phase 15: ProjectWatcher reports external adds, edits and removals and ignores generated folders. */
internal static class WatcherChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(string folder, List<string> checks)
    {
        StudioProject.Create(folder, "Watcher fixture");
        using var watcher = new ProjectWatcher(folder);
        Require(watcher.Scan().Count == 0, "A fresh watcher reported changes.");

        string texture = Path.Combine(folder, "assets", "texture.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(texture)!);
        File.WriteAllBytes(texture, [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "notes.tmp"), [9]);
        Directory.CreateDirectory(Path.Combine(folder, "build"));
        File.WriteAllBytes(Path.Combine(folder, "build", "out.pkg"), [7]);
        if (watcher.EventsAvailable)
        {
            for (int wait = 0; wait < 100 && !watcher.Pending; wait++) Thread.Sleep(50);
            Require(watcher.Pending, "The file system watcher did not flag an external edit.");
        }
        var added = watcher.Scan();
        Require(added.Count == 1 && added[0] == new ProjectFileChange("assets/texture.bin", ProjectFileChangeKind.Added),
            "An external add was not reported alone: " + string.Join(", ", added));

        File.WriteAllBytes(texture, [1, 2, 3, 4]);
        var modified = watcher.Scan();
        Require(modified.Count == 1 && modified[0].Kind == ProjectFileChangeKind.Modified, "An external edit was not reported.");

        File.WriteAllBytes(texture, [5, 6]);
        watcher.MarkClean();
        Require(watcher.Scan().Count == 0, "MarkClean did not adopt Studio's own save.");

        File.Delete(texture);
        var removed = watcher.Scan();
        Require(removed.Count == 1 && removed[0].Kind == ProjectFileChangeKind.Removed, "An external delete was not reported.");
        Require(watcher.IsIgnored(Path.Combine(folder, ".local", "prefs.json")) && watcher.IsIgnored(Path.Combine(folder, "x.tmp"))
            && !watcher.IsIgnored(Path.Combine(folder, "deltas", "a.json")), "Ignored paths are wrong.");
        checks.Add("project watcher sees external add/edit/remove, ignores build/.local/*.tmp" + (watcher.EventsAvailable ? " (events on)" : " (scan only)"));
    }
}

using ImGuiNET;
using SunriseStudio.Packages;

namespace SunriseStudio.Editor;

/** Solforge additions kept in their own file so upstream merges of StudioForm stay small. */
public sealed partial class StudioForm
{
    /** Upstream credit shown under Help and on the toolbar version tooltip. */
    private const string AboutCredit = "Built on Sunrise Studio by stan (github.com/stanuwu) and its contributors";

    private string statusFacts = "", statusFactsTip = "";
    private long statusFactsTicks;
    private PackageCatalog? statusKeysCatalog;
    private string statusKeys = "";
    private string? statusFolder;
    private bool statusFolderExists;

    /** Right-aligned profile, package/key/cache health and memory; recomputed at most once a second. */
    private void DrawStatusFacts()
    {
        long now = Environment.TickCount64;
        if (statusFacts.Length == 0 || now - statusFactsTicks >= 1000) { statusFactsTicks = now; RefreshStatusFacts(); }
        float width = ImGui.CalcTextSize(statusFacts).X;
        ImGui.SameLine();
        float x = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width;
        if (x > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(x);
        ImGui.TextDisabled(statusFacts);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(statusFactsTip);
    }

    private void RefreshStatusFacts()
    {
        var catalog = workspace.Catalog;
        // The key source only changes with the installation, so the file check runs once per catalog.
        if (!ReferenceEquals(catalog, statusKeysCatalog))
        {
            statusKeysCatalog = catalog;
            statusKeys = catalog is null ? "" : File.Exists(Path.Combine(catalog.GameRoot, "bin", "x64", "Sunrise",
                "exports", "package_keys.bin")) ? "keys: export" : "keys: exe scan";
        }
        string packages = catalog is null ? "packages: not loaded" : $"packages: {catalog.PackageCount}";
        string cache = catalog is null ? "" : catalog.DiskCache is { Enabled: true }
            ? $"cache: {workspace.Names?.CacheStatus ?? "Not loaded"}" : "cache: off";
        long managed = GC.GetTotalMemory(false) >> 20, working = Environment.WorkingSet >> 20;
        statusFacts = string.Join("  |  ", new[] { "D2 86657", packages, statusKeys, cache, $"mem: {working} MB" }
            .Where(part => part.Length != 0));
        statusFactsTip = "Profile: D2_ARRIVALS_86657 (Destiny 2 build 86657)\n"
            + (catalog is null ? "No installation loaded.\n" : $"Installation: {catalog.GameRoot}\n")
            + "Keys: 'export' reads bin\\x64\\Sunrise\\exports\\package_keys.bin; 'exe scan' falls back to destiny2.exe.\n"
            + $"Memory: {working} MB working set, {managed} MB managed heap.";
    }

    /** Directory.Exists runs once per notice folder, not every frame. */
    private bool StatusFolderExists(string folder)
    {
        if (folder != statusFolder) { statusFolder = folder; statusFolderExists = Directory.Exists(folder); }
        return statusFolderExists;
    }
}

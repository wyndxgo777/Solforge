using ImGuiNET;
using SunriseStudio.Packages;

namespace SunriseStudio.Editor;

/** StudioForm part: disk cache settings and the cache statistics dialog. */
public sealed partial class StudioForm
{
    private bool cacheDialog;
    private StudioCacheStatistics? cacheStats;
    private double cacheStatsAt;

    private void ApplyCacheSettings() => workspace.CacheOptions = new()
    {
        Enabled = settings.DiskCacheEnabled,
        MaximumBytes = settings.DiskCacheLimitMiB * 1024L * 1024
    };

    /** Cache controls never change project data or unload the current scene. */
    private void DrawCacheDialog()
    {
        CenterModal(480);
        if (!ImGui.BeginPopupModal("Disk cache", ImGuiWindowFlags.AlwaysAutoResize)) return;
        bool enabled = settings.DiskCacheEnabled;
        int limit = settings.DiskCacheLimitMiB;
        bool toggled = UiAuto.Checkbox("Cache package loads", ref enabled);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Used the next time Sunrise is opened");
        ImGui.SetNextItemWidth(230 * scale);
        bool moved = UiAuto.SliderInt("Limit", ref limit, 128, 4096, "%d MiB");
        // A drag changes the value every frame; the file is written once, on release.
        bool released = ImGui.IsItemDeactivatedAfterEdit();
        if (toggled || moved)
        {
            settings.DiskCacheEnabled = enabled;
            settings.DiskCacheLimitMiB = limit;
            ApplyCacheSettings();
        }
        if (toggled || released) SaveSettings();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Used the next time Sunrise is opened");
        if (workspace.Catalog?.DiskCache is { } cache)
        {
            if (cacheStats is null || clock.Elapsed.TotalSeconds - cacheStatsAt > 1)
            { cacheStats = cache.Statistics; cacheStatsAt = clock.Elapsed.TotalSeconds; }
            ImGui.TextDisabled($"{FormatBytes(cacheStats.StoredBytes)} stored");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(cache.DirectoryPath);
            if (cacheStats.LastError is { Length: > 0 } error) ImGui.TextWrapped(error);
            if (UiAuto.Button("Clear cache")) workspace.Execute("Clearing cache", () => cache.Clear());
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove local cache files");
        }
        ImGui.Spacing();
        if (UiAuto.Button("Close", new(100 * scale, 0))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}

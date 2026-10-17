using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using Solforge.Mcp;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

/**
 * Several AI agents sharing one Solforge (Solforge.Mcp/MultiAgentTools.cs):
 *  - map-scoped reads: find_object, get_object, the mission slot tools, find_world_objects and get_placement with map=<tag>
 *    answer from a background copy of that map, loaded off the UI thread by its own WorldLoader; workspace.World (the map
 *    the UI shows) is never touched. At most AiBackgroundMapLimit copies are kept, least recently used dropped first.
 *  - the presence list under the AI workspace header: connected MCP sessions (client and shim pid), who opened the map,
 *    and the build, deploy or map load running now with who started it.
 */
public sealed partial class StudioForm
{
    /** One background copy of a map (and mission) for map-scoped reads; MapScope.Current holds it during a call. */
    private sealed class AiBackgroundMap(uint map, uint? mission, int revision)
    {
        public uint Map { get; } = map;
        public uint? Mission { get; } = mission;
        public int Revision { get; } = revision;
        public CancellationTokenSource Cancel { get; } = new();
        public Task<AiCapture> Load { get; set; } = null!;
        public Task<ObjectEntry[]>? Entries;
        public Task<MissionSnapshot?>? MissionSlots;
        public DateTime LastUsed = DateTime.UtcNow;
        public int EntriesRead;
        public string Name = "";
    }

    private const int AiBackgroundMapLimit = 2;
    private static readonly TimeSpan AiBackgroundWait = TimeSpan.FromSeconds(20);
    private readonly List<AiBackgroundMap> aiBackgroundMaps = [];
    private readonly SemaphoreSlim aiBackgroundGate = new(1, 1);
    private (PackageCatalog Catalog, WorldLoader Loader)? aiBackgroundLoader;

    /** ISolforgePresence.PrepareMapScopeAsync: the UI's own map, a ready background copy, or "still loading". */
    private async Task<MapScopeResult> AiPrepareMapScopeAsync(uint map, uint? mission, CancellationToken cancel)
    {
        var state = await OnUi(() => (workspace.World, workspace.Catalog, workspace.Names, workspace.WorldCatalog, workspace.Assets,
            workspace.ProjectRevision, workspace.Project?.DirectoryPath)).WaitAsync(cancel).ConfigureAwait(false);
        var (open, catalog, names, worlds, assets, revision, projectRoot) = state;
        if (open is not null && open.Map.Tag == map && (mission is null || open.MissionTag == mission)) return MapScopeResult.Open();
        if (catalog is null) return MapScopeResult.Failed("No game installation is open (set_settings installation).");
        if (worlds is not null && worlds.Maps.All(row => row.Tag != map))
            return MapScopeResult.Failed($"No map {Hex(map)}; find it with list_maps.");
        if (mission is { } wanted && worlds?.Missions.FirstOrDefault(row => row.Tag == wanted) is { } entry
            && entry.MapTags.Count > 0 && !entry.MapTags.Contains(map))
            return MapScopeResult.Failed($"Mission {entry.Name} loads on {string.Join(", ", entry.MapTags.Select(Hex))}, not {Hex(map)}.");

        AiBackgroundMap view;
        lock (aiBackgroundMaps)
        {
            var found = aiBackgroundMaps.FirstOrDefault(item => item.Map == map && item.Mission == mission);
            // An edit since the copy was read (or a failed read) loads it again.
            if (found is not null && (found.Load.IsFaulted || found.Load.IsCanceled || found.Revision != revision))
            {
                aiBackgroundMaps.Remove(found);
                found.Cancel.Cancel();
                found = null;
            }
            if (found is null)
            {
                found = new AiBackgroundMap(map, mission, revision) { Name = worlds?.Maps.FirstOrDefault(row => row.Tag == map)?.Name ?? Hex(map) };
                var created = found;
                created.Load = Task.Run(() => AiLoadBackgroundMap(created, catalog, names, assets, projectRoot));
                aiBackgroundMaps.Add(found);
                // Keep the newest copies only; a dropped load is cancelled and its memory freed.
                foreach (var old in aiBackgroundMaps.OrderByDescending(item => item.LastUsed).Skip(AiBackgroundMapLimit).ToArray())
                {
                    aiBackgroundMaps.Remove(old);
                    old.Cancel.Cancel();
                }
            }
            found.LastUsed = DateTime.UtcNow;
            view = found;
        }
        try { await view.Load.WaitAsync(AiBackgroundWait, cancel).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            return MapScopeResult.Loading($"Loading {view.Name} in the background ({view.EntriesRead} entries read so far).");
        }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or InvalidOperationException
            or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            return MapScopeResult.Failed($"Could not read {view.Name} in the background: {error.Message}");
        }
        return MapScopeResult.Ready(view, view.Load.Result.Map ?? view.Name);
    }

    /** Reads the map on a worker with a loader of its own (one background read at a time; package reads are thread-safe). */
    private async Task<AiCapture> AiLoadBackgroundMap(AiBackgroundMap view, PackageCatalog catalog, StudioNameIndex? names,
        BrowserAsset[] assets, string? projectRoot)
    {
        var token = view.Cancel.Token;
        await aiBackgroundGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            WorldLoader loader;
            lock (aiBackgroundMaps)
            {
                if (aiBackgroundLoader is not { } cached || !ReferenceEquals(cached.Catalog, catalog))
                    aiBackgroundLoader = cached = (catalog, new WorldLoader(catalog, names));
                loader = cached.Loader;
            }
            loader.Progress = _ => Interlocked.Increment(ref view.EntriesRead);
            // Reads only: no lower detail levels, nothing is drawn from this copy.
            var options = new WorldLoadOptions { IncludeLowerLods = false };
            var world = view.Mission is { } mission ? loader.LoadMission(view.Map, mission, options, token) : loader.LoadMap(view.Map, options, token);
            view.Name = world.Map.Name; // find_object matches rows by this map name (host.CurrentMap).
            return new AiCapture(world, null, new Dictionary<string, string>(), world.Map.Name, catalog, assets, view.Revision,
                [.. world.Scene.AuthoredObjects], projectRoot);
        }
        finally { aiBackgroundGate.Release(); }
    }

    /** find_object rows for a background copy: its objects and mission slots (the install-wide asset rows stay with the UI's map). */
    private async Task<IReadOnlyList<ObjectEntry>> AiScopedObjectsAsync(AiBackgroundMap view, CancellationToken cancel)
    {
        var capture = await view.Load.ConfigureAwait(false);
        var rows = view.Entries ??= Task.Run(() =>
        {
            var objects = capture.World!.Objects;
            var list = new ObjectEntry[objects.Count];
            for (int i = 0; i < list.Length; i++)
            {
                var row = objects[i];
                list[i] = new ObjectEntry(row.Id, AiSafeLabel(capture, row), row.Kind.ToString(), ObjectScope.Tree, capture.Map,
                    AiSafePackage(capture, row.AssetTag), row.AssetTag);
            }
            return list;
        });
        return await rows.WaitAsync(AiReadTimeout, cancel).ConfigureAwait(false);
    }

    private async Task<MissionSnapshot?> AiScopedMissionAsync(AiBackgroundMap view, CancellationToken cancel)
    {
        var capture = await view.Load.ConfigureAwait(false);
        var task = view.MissionSlots ??= Task.Run(() => BuildAiMission(capture));
        return await task.WaitAsync(AiReadTimeout, cancel).ConfigureAwait(false);
    }

    private async Task<JsonObject?> AiScopedObjectDetailsAsync(AiBackgroundMap view, string id, CancellationToken cancel)
    {
        var capture = await view.Load.ConfigureAwait(false);
        if (capture.World?.Find(id) is null)
            return await OnUi(() => AiAssetDetails(SolforgeTools.ParseHash(id.StartsWith("tag:", StringComparison.Ordinal) ? id[4..] : id))).ConfigureAwait(false);
        var mission = view.MissionSlots is { IsCompletedSuccessfully: true } done ? done.Result : null;
        return await Task.Run(() => AiObjectDetails(capture, mission, id, cancel), cancel).WaitAsync(AiReadTimeout, cancel).ConfigureAwait(false);
    }

    /** find_world_objects and get_placement against a background copy. */
    private async Task<JsonObject> AiScopedQueryAsync(AiBackgroundMap view, string tool, JsonObject args, CancellationToken cancel)
    {
        var capture = await view.Load.ConfigureAwait(false);
        if (tool == "find_world_objects")
            return await Task.Run(() => AiFindWorldObjects(capture.World, capture, args), cancel).WaitAsync(AiReadTimeout, cancel).ConfigureAwait(false);
        string id = McpJson.String(args, "id") ?? "";
        if (capture.World?.Find(id) is not { } row || capture.Catalog is not { } catalog)
            return AiError($"No object with that id in {capture.Map}.");
        var info = await Task.Run(() => SceneEdits.Describe(catalog, row.Source, capture.World), cancel).ConfigureAwait(false);
        static JsonArray Vector(Vector3 v) => new(Math.Round(v.X, 3), Math.Round(v.Y, 3), Math.Round(v.Z, 3));
        var result = new JsonObject
        {
            ["id"] = row.Id, ["name"] = AiSafeLabel(capture, row), ["layer"] = AiLayer(row.Source), ["zone"] = row.BubbleName,
            ["canMove"] = info.CanTransform, ["canScale"] = info.CanScale, ["canBounds"] = info.CanBounds,
            ["space"] = info.CoordinateSpace, ["reason"] = info.Reason, ["map"] = capture.Map
        };
        if (info.CanTransform)
        {
            result["position"] = Vector(info.Position);
            result["rotation"] = Vector(QuaternionToDegrees(info.Rotation));
            result["scale"] = Math.Round(info.Scale, 4);
        }
        if (info.CanBounds) { result["minimum"] = Vector(info.Minimum); result["maximum"] = Vector(info.Maximum); }
        result["note"] = "Read from a background copy: edit_placement and edit_bounds act on the map open in the UI, so open this map " +
            "(open_map) before editing it.";
        return result;
    }

    /** The tool host's multi-agent part: presence facts and the map scope. */
    private sealed partial class AiHost : ISolforgePresence
    {
        public uint? OpenMapTag => Owner.workspace.World?.Map.Tag;
        public string? OpenMapName => Owner.workspace.World?.Map.Name;
        public string? BusyWork => Owner.workspace.IsBusy ? Owner.workspace.Status : null;
        public Task<MapScopeResult> PrepareMapScopeAsync(uint map, uint? mission, CancellationToken cancel) =>
            Owner.AiPrepareMapScopeAsync(map, mission, cancel);

        /** The background copy this call reads, or null for the UI's map. */
        private static AiBackgroundMap? Scoped => MapScope.Current as AiBackgroundMap;
    }

    private (DateTime At, IReadOnlyList<(string Text, bool Busy)> Lines) aiPresence = (DateTime.MinValue, []);

    /** Who is using Solforge now: one row per MCP session and per running build, deploy or map load (refreshed each second). */
    private void DrawAiPresence()
    {
        if (aiService?.Sessions is not { } sessions) return;
        if (DateTime.UtcNow - aiPresence.At > TimeSpan.FromSeconds(1))
            aiPresence = (DateTime.UtcNow, sessions.PresenceLines(aiHost));
        var lines = aiPresence.Lines;
        if (lines.Count == 0) return;
        int agents = lines.Count(line => !line.Busy);
        ImGui.TextColored(SolTheme.Dim, agents > 1 ? $"Using Solforge now ({agents} agents):" : "Using Solforge now:");
        foreach (var (text, busy) in lines.Take(6))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, busy ? SolTheme.Sun : SolTheme.Ok);
            ImGui.TextUnformatted((busy ? "  > " : "  - ") + (text.Length > 90 ? text[..90] + "..." : text));
            ImGui.PopStyleColor();
            StudioUi.Tip(text);
        }
        if (lines.Count > 6) { ImGui.TextDisabled($"+{lines.Count - 6} more (get_sessions)"); }
    }
}

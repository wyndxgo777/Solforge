using System.Text.Json.Nodes;
using Solforge.Mcp;
using McpPages = Solforge.Mcp.Pages;
using SunriseStudio.Formats;
using SunriseStudio.Missions;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

/**
 * The AI tools' heavy reads, kept off the UI thread so a huge map (the EDZ) never freezes the window: the UI thread
 * only takes references (the loaded world, Object tree and asset array are replaced whole, not edited in place), and
 * find_object rows, the mission slot snapshot, object details and read_panel pages are built on the thread pool.
 */
public sealed partial class StudioForm
{
    /** References the background readers use, taken on the UI thread in O(named rows) time. */
    private sealed record AiCapture(WorldDocument? World, SceneHierarchy? Tree, IReadOnlyDictionary<string, string> Names,
        string? Map, PackageCatalog? Catalog, BrowserAsset[] Assets, int Revision, AuthoredObjectInfo[] Authored, string? ProjectRoot);

    private static readonly TimeSpan AiReadTimeout = TimeSpan.FromSeconds(90);
    private (WorldDocument? World, SceneHierarchy? Tree, int Revision, Task<ObjectEntry[]> Rows)? aiEntries;
    private (WorldDocument? World, int Revision, Task<MissionSnapshot?> Snapshot)? aiMission;
    private volatile IReadOnlyList<CodeRoot> aiCodeRoots = [];
    private volatile string?[] aiReferenceStarts = [];
    private string? aiCodeRootsKey;

    private AiCapture AiCaptureNow()
    {
        var names = ProjectNames();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        // Project names reach the rows the tree has keyed; the rest keep their tree or scene name.
        if (names.Count > 0 && ReferenceEquals(nameKeyWorld, inspectionWorld))
            foreach (var (row, key) in nameKeys)
                if (names.TryGetValue(key, out var name)) labels[row.Id] = name;
        return new AiCapture(inspectionWorld, sceneTree, labels, workspace.World?.Map.Name, workspace.Catalog, workspace.Assets,
            workspace.ProjectRevision, inspectionWorld?.Scene.AuthoredObjects.ToArray() ?? [], workspace.Project?.DirectoryPath);
    }

    /** The label AiLabel gives, from data that is safe to read off the UI thread. */
    private static string AiSafeLabel(AiCapture capture, WorldObject row) =>
        capture.Names.GetValueOrDefault(row.Id) ?? capture.Tree?.Find(row.Source)?.Name ?? SceneObjectName(row.Source) ?? row.Name;

    private static string? AiSafePackage(AiCapture capture, uint tag)
    {
        try { return tag != 0 && capture.Catalog is { } catalog && catalog.TryGetEntry(tag, out var entry) ? entry.PackageName : null; }
        catch (InvalidOperationException) { return null; }
    }

    /** Runs every publish: starts the indexes for a newly loaded world and refreshes the Lua code folders. */
    private void AiPrewarm()
    {
        if (aiHost is null) return;
        string key = (workspace.Catalog?.GameRoot ?? settings.SunrisePath) + "|" + workspace.Project?.DirectoryPath + "|"
            + missionGraph.ScriptPath + "|" + string.Join(";", settings.AiFolders);
        if (key != aiCodeRootsKey)
        {
            aiCodeRootsKey = key;
            string? game = workspace.Catalog?.GameRoot ?? settings.SunrisePath, project = workspace.Project?.DirectoryPath;
            string?[] folders = [missionGraph.ScriptPath is { } script ? Path.GetDirectoryName(script) : null, .. settings.AiFolders];
            aiReferenceStarts = [project, game, .. folders];
            _ = Task.Run(() => aiCodeRoots = CodeRefs.Locate(game, project, folders));
        }
        if (aiPaused) return;
        if (inspectionWorld is null) return;
        bool missionStale = aiMission is not { } mission || !ReferenceEquals(mission.World, inspectionWorld) || mission.Revision != workspace.ProjectRevision;
        bool entriesStale = aiEntries is not { } entries || !ReferenceEquals(entries.World, inspectionWorld)
            || !ReferenceEquals(entries.Tree, sceneTree) || entries.Revision != workspace.ProjectRevision;
        if (!missionStale && !entriesStale) return;
        var capture = AiCaptureNow();
        if (missionStale) AiMissionTask(capture);
        if (entriesStale) AiEntriesTask(capture);
    }

    private Task<MissionSnapshot?> AiMissionTask(AiCapture capture)
    {
        if (aiMission is { } cached && ReferenceEquals(cached.World, capture.World) && cached.Revision == capture.Revision) return cached.Snapshot;
        var task = Task.Run(() => BuildAiMission(capture));
        aiMission = (capture.World, capture.Revision, task);
        return task;
    }

    private Task<ObjectEntry[]> AiEntriesTask(AiCapture capture)
    {
        if (aiEntries is { } cached && ReferenceEquals(cached.World, capture.World) && ReferenceEquals(cached.Tree, capture.Tree)
            && cached.Revision == capture.Revision) return cached.Rows;
        var task = Task.Run(() =>
        {
            if (capture.World is not { } world) return Array.Empty<ObjectEntry>();
            var objects = world.Objects;
            var rows = new ObjectEntry[objects.Count];
            for (int i = 0; i < rows.Length; i++)
            {
                var row = objects[i];
                rows[i] = new ObjectEntry(row.Id, AiSafeLabel(capture, row), row.Kind.ToString(), ObjectScope.Tree, capture.Map,
                    AiSafePackage(capture, row.AssetTag), row.AssetTag);
            }
            return rows;
        });
        aiEntries = (capture.World, capture.Tree, capture.Revision, task);
        return task;
    }

    /** Slots of every authored prefab, joined to the markers and squads that place them. */
    private static MissionSnapshot? BuildAiMission(AiCapture capture)
    {
        if (capture.World is not { } world) return null;
        var prefabs = new Dictionary<uint, AuthoredObjectInfo>();
        foreach (var authored in capture.Authored) prefabs.TryAdd(authored.RegistryKey, authored);
        var prefabRows = new Dictionary<uint, WorldObject>();
        var slots = new Dictionary<AuthoredSlotIdentity, (string Name, uint Prefab, WorldObject? Row, List<string> Placements)>();
        foreach (var authored in capture.Authored)
            foreach (var slot in authored.Slots)
                slots.TryAdd(slot.Identity, (slot.Name, authored.TagHash, null, new List<string>()));
        foreach (var row in world.Objects)
        {
            if (row.Source is AuthoredObjectInfo prefab) { prefabRows.TryAdd(prefab.RegistryKey, row); continue; }
            var identity = row.Source switch { SceneMarker marker => marker.Slot, AuthoredSquadInfo squad => squad.Slot, _ => null };
            if (identity is null) continue;
            if (!slots.TryGetValue(identity, out var value))
                value = (AiSafeLabel(capture, row), prefabs.GetValueOrDefault(identity.RegistryKey)?.TagHash ?? 0, null, new List<string>());
            if (value.Placements.Count < 16) value.Placements.Add(row.Id);
            if (value.Row is null || (value.Row.Position is null && row.Position is not null)) value.Row = row;
            slots[identity] = value;
        }
        if (slots.Count == 0) return null;
        var rows = slots.Select(pair =>
        {
            var (identity, (name, prefab, row, placements)) = (pair.Key, pair.Value);
            var prefabRow = prefabRows.GetValueOrDefault(identity.RegistryKey);
            return new MissionSlot(MissionSlots.SlotId(identity.RegistryKey, identity.Type, identity.Index),
                name.Length > 0 ? name : $"slot {identity.Index}", identity.RegistryKey, identity.Type, identity.Index, prefab)
            {
                ObjectId = row?.Id, Label = row is null ? null : AiSafeLabel(capture, row), Kind = row?.Kind.ToString(),
                AssetTag = row?.AssetTag ?? 0, Zone = row?.BubbleName,
                Position = row?.Position is { } p ? [p.X, p.Y, p.Z] : null,
                Rotation = row?.Source is SceneMarker marker ? [marker.Rotation.X, marker.Rotation.Y, marker.Rotation.Z, marker.Rotation.W] : null,
                Extents = row?.Extents is { } e ? [e.X, e.Y, e.Z] : null,
                PrefabObjectId = prefabRow?.Id, PrefabName = prefabRow is null ? null : AiSafeLabel(capture, prefabRow),
                Placements = placements
            };
        });
        return new MissionSnapshot(capture.Map, world.MissionTag ?? 0, world.Name, rows);
    }

    /** Slot facts for get_context and the selection: from the snapshot when built, else from the prefab's slot list. */
    private JsonObject? AiSlotFacts(object source)
    {
        if (inspectionWorld is not { } world || world.Find(source) is not { } row) return null;
        if (aiMission is { Snapshot.IsCompletedSuccessfully: true } cached && ReferenceEquals(cached.World, world)
            && cached.Snapshot.Result?.Find(row.Id) is { } known)
            return MissionSlots.Row(known);
        var identity = row.Source switch { SceneMarker marker => marker.Slot, AuthoredSquadInfo squad => squad.Slot, _ => null };
        if (identity is null)
            return row.Source is AuthoredObjectInfo prefab ? new JsonObject
            {
                ["prefab"] = $"0x{prefab.TagHash:X8}", ["registry"] = $"0x{prefab.RegistryKey:X8}", ["slots"] = prefab.Slots.Count,
                ["note"] = "A mission prefab; list_mission_objects filter by its registry lists its slots."
            } : null;
        var owner = world.Scene.AuthoredObjects.FirstOrDefault(item => item.RegistryKey == identity.RegistryKey);
        var info = owner?.Slots.FirstOrDefault(item => item.Identity == identity);
        return MissionSlots.Row(new MissionSlot(MissionSlots.SlotId(identity.RegistryKey, identity.Type, identity.Index),
            info?.Name ?? row.Name, identity.RegistryKey, identity.Type, identity.Index, owner?.TagHash ?? 0)
        {
            ObjectId = row.Id, Kind = row.Kind.ToString(), AssetTag = row.AssetTag, Zone = row.BubbleName,
            Position = row.Position is { } p ? [p.X, p.Y, p.Z] : null
        });
    }

    private async Task<IReadOnlyList<ObjectEntry>> AiObjectsAsync(string query, CancellationToken cancel)
    {
        var (capture, rows, _) = await OnUi(() => { var c = AiCaptureNow(); return (c, AiEntriesTask(c), AiMissionTask(c)); }).ConfigureAwait(false);
        var objects = await rows.WaitAsync(AiReadTimeout, cancel).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var entries = new List<ObjectEntry>(objects);
            // Assets: only rows sharing the query's first number or longest word, so a large index stays cheap.
            var words = query.Split([' ', '[', ']', '-', '/', '_', ',', '.'], StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word.Length >= 3).OrderByDescending(word => word.Any(char.IsDigit)).ThenByDescending(word => word.Length).ToArray();
            if (words.Length == 0) return (IReadOnlyList<ObjectEntry>)entries;
            int added = 0;
            foreach (var asset in capture.Assets)
            {
                cancel.ThrowIfCancellationRequested();
                if (!asset.SearchText.Contains(words[0], StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(new ObjectEntry("tag:" + asset.Entry.TagHash.ToString("X8"), asset.Name, asset.Kind, ObjectScope.Asset, null,
                    asset.Entry.PackageName, asset.Entry.TagHash));
                if (++added >= 5000) break;
            }
            return entries;
        }, cancel).ConfigureAwait(false);
    }

    private async Task<MissionSnapshot?> AiMissionAsync(CancellationToken cancel)
    {
        var task = await OnUi(() => inspectionWorld is null ? Task.FromResult<MissionSnapshot?>(null) : AiMissionTask(AiCaptureNow())).ConfigureAwait(false);
        return await task.WaitAsync(AiReadTimeout, cancel).ConfigureAwait(false);
    }

    /** get_object: inspected off the UI thread without the inspection cache; capped lists; script refs time-boxed. */
    private async Task<JsonObject?> AiObjectDetailsAsync(string id, CancellationToken cancel)
    {
        var capture = await OnUi(AiCaptureNow).ConfigureAwait(false);
        if (capture.World?.Find(id) is null)
            return await OnUi(() => AiAssetDetails(SolforgeTools.ParseHash(id.StartsWith("tag:", StringComparison.Ordinal) ? id[4..] : id))).ConfigureAwait(false);
        var mission = aiMission is { Snapshot.IsCompletedSuccessfully: true } cached && ReferenceEquals(cached.World, capture.World) ? cached.Snapshot.Result : null;
        return await Task.Run(() => AiObjectDetails(capture, mission, id, cancel), cancel).WaitAsync(AiReadTimeout, cancel).ConfigureAwait(false);
    }

    private static JsonObject? AiObjectDetails(AiCapture capture, MissionSnapshot? mission, string id, CancellationToken cancel)
    {
        if (capture.World is not { } world || world.Find(id) is not { } item) return null;
        var details = new JsonObject
        {
            ["id"] = item.Id, ["label"] = AiSafeLabel(capture, item), ["name"] = item.Name, ["kind"] = item.Kind.ToString(),
            ["tag"] = item.AssetTag != 0 ? $"0x{item.AssetTag:X8}" : null, ["package"] = AiSafePackage(capture, item.AssetTag),
            ["zone"] = item.BubbleName, ["state"] = item.StateName,
            ["mission"] = item.MissionTag != 0 ? $"0x{item.MissionTag:X8}" : null,
            ["position"] = item.Position is { } p ? new JsonArray(p.X, p.Y, p.Z) : null,
            ["extents"] = item.Extents is { } e ? new JsonArray(e.X, e.Y, e.Z) : null
        };
        if (mission?.Find(item.Id) is { } slot) details["slot"] = MissionSlots.Row(slot);
        if (capture.Tree?.Find(item.Source) is { } node)
            details["tree"] = new JsonObject { ["node"] = node.Id, ["parent"] = node.Parent?.Id, ["parentName"] = node.Parent?.Name, ["children"] = node.Children.Count };
        cancel.ThrowIfCancellationRequested();
        if (world.Describe(item.Source) is { } inspection)
        {
            var properties = new JsonArray();
            foreach (var property in inspection.Properties.Take(200))
                properties.Add(new JsonObject { ["group"] = property.Group, ["name"] = property.Name, ["value"] = property.Value });
            details["properties"] = properties;
            details["propertyCount"] = inspection.Properties.Count;
            var references = new JsonArray();
            foreach (var reference in inspection.References.Take(200))
                references.Add(new JsonObject { ["name"] = reference.Name, ["tag"] = $"0x{reference.TagHash:X8}", ["package"] = AiSafePackage(capture, reference.TagHash) });
            details["references"] = references;
            var related = new JsonArray();
            foreach (var other in inspection.RelatedObjects.Take(50))
                if (world.Find(other) is { } relatedRow) related.Add(new JsonObject { ["id"] = relatedRow.Id, ["label"] = AiSafeLabel(capture, relatedRow) });
            details["related"] = related;
            details["relatedCount"] = inspection.RelatedObjects.Count;
            var slots = new JsonArray();
            foreach (var each in inspection.Slots.Take(50))
                slots.Add(new JsonObject { ["slotId"] = MissionSlots.SlotId(each.Identity.RegistryKey, each.Identity.Type, each.Identity.Index), ["name"] = each.Name });
            details["slots"] = slots;
            details["slotCount"] = inspection.Slots.Count;
        }
        if (capture.ProjectRoot is { } root && item.AssetTag != 0)
            details["scriptReferences"] = CodeRefs.Search([new CodeRoot("project", root)], [new CodeNeedle($"{item.AssetTag:X8}", false, "tag hash")],
                0, true, 20, TimeSpan.FromSeconds(5), cancel);
        return details;
    }

    /** read_panel: the panel's data is referenced on the UI thread and paged on the thread pool. */
    private async Task<JsonObject> AiReadPanelAsync(string panel, PanelQuery query, CancellationToken cancel)
    {
        Func<JsonObject> read = panel switch
        {
            "hierarchy" => await OnUi(() => { var tree = sceneTree; return (Func<JsonObject>)(() => AiHierarchyPage(tree, query)); }).ConfigureAwait(false),
            "inspector" => await OnUi(() => { var document = workspace.Document; return (Func<JsonObject>)(() => AiInspectorPage(document, query)); }).ConfigureAwait(false),
            "selection" => await OnUi(() =>
            {
                var capture = AiCaptureNow();
                object[] selected = viewport?.SelectedItems.ToArray() ?? [];
                return (Func<JsonObject>)(() => AiSelectionPage(capture, selected, query));
            }).ConfigureAwait(false),
            "assets" => await OnUi(() => { var assets = workspace.Assets; return (Func<JsonObject>)(() => AiAssetsPage(assets, query)); }).ConfigureAwait(false),
            "mission_graph" => await OnUi(() =>
            {
                var graph = missionGraph.Document;
                string? script = missionGraph.ScriptPath is { } path ? AiShownPath(path) : null;
                bool dirty = missionGraph.IsDirty;
                return (Func<JsonObject>)(() => AiGraphPage(graph, script, dirty, query));
            }).ConfigureAwait(false),
            "report" => await OnUi(AiReportPage(query)).ConfigureAwait(false),
            "changes" => await OnUi(() =>
            {
                ChangeRow[] rows = workspace.Project is { } project ? ChangeRows(project) : [];
                return (Func<JsonObject>)(() => McpPages.Page(rows.Where(row => Matches(query.Filter, row.Label, row.Change, row.Value)), query.Offset, query.Limit,
                    row => new JsonObject { ["id"] = row.Id, ["label"] = row.Label, ["change"] = row.Change, ["value"] = row.Value, ["tooltip"] = row.Tooltip }));
            }).ConfigureAwait(false),
            "live" => await OnUi(() => { var state = livePage.AiState(); return (Func<JsonObject>)(() => state); }).ConfigureAwait(false),
            _ => () => new JsonObject { ["error"] = "Unknown panel." }
        };
        return await Task.Run(read, cancel).WaitAsync(AiReadTimeout, cancel).ConfigureAwait(false);
    }

    private static bool Matches(string? filter, params string?[] fields) =>
        filter is not { Length: > 0 } || fields.Any(field => field?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true);

    private static JsonObject AiHierarchyPage(SceneHierarchy? tree, PanelQuery query)
    {
        if (tree is null) return new JsonObject { ["error"] = "No Object tree: open a map or an asset with a scene." };
        static JsonObject Row(SceneHierarchyNode node) => new()
        {
            ["id"] = node.Id, ["name"] = node.Name, ["kind"] = node.Kind.ToString(), ["depth"] = node.Depth,
            ["children"] = node.Children.Count, ["members"] = node.Members.Count, ["visible"] = node.Visible,
            ["objectId"] = node.Representative?.Id, ["parent"] = node.Parent?.Id
        };
        if (query.Path is { Length: > 0 } path)
        {
            if (tree.Find(path) is not { } node) return new JsonObject { ["error"] = "No tree node with that id." };
            var page = McpPages.Page(node.Children.Where(child => Matches(query.Filter, child.Name, child.Kind.ToString())), query.Offset, query.Limit, Row);
            page["node"] = Row(node);
            return page;
        }
        if (query.Filter is { Length: > 0 })
            return McpPages.Page(tree.Nodes.Where(node => Matches(query.Filter, node.Name, node.Kind.ToString(), node.Id)), query.Offset, query.Limit, Row);
        var top = McpPages.Page(tree.Root.Children, query.Offset, query.Limit, Row);
        top["node"] = Row(tree.Root);
        top["nodeCount"] = tree.Nodes.Count;
        return top;
    }

    private static JsonObject AiInspectorPage(StudioDocument? document, PanelQuery query)
    {
        if (document is null) return new JsonObject { ["error"] = "No asset is open." };
        var inspection = document.Inspection;
        JsonObject page;
        if (query.Path is "references")
            page = McpPages.Page(inspection.References.Where(reference => Matches(query.Filter, reference.Name, $"{reference.TagHash:X8}")), query.Offset, query.Limit,
                reference => new JsonObject { ["name"] = reference.Name, ["tag"] = $"0x{reference.TagHash:X8}", ["offset"] = reference.Offset, ["candidate"] = reference.IsCandidate });
        else if (query.Path is "diagnostics")
            page = McpPages.Page(inspection.Diagnostics.Where(line => Matches(query.Filter, line)), query.Offset, query.Limit, line => JsonValue.Create(line));
        else
        {
            var rows = new List<(string Section, AssetProperty Property)>();
            void Walk(IReadOnlyList<AssetNode> nodes, string at)
            {
                foreach (var node in nodes)
                {
                    string section = at.Length == 0 ? node.Name : at + " / " + node.Name;
                    foreach (var property in node.Properties) rows.Add((section, property));
                    if (node.Children is { } children) Walk(children, section);
                }
            }
            Walk(inspection.Sections, "");
            page = McpPages.Page(rows.Where(row => (query.Path is not { Length: > 0 } section || row.Section.StartsWith(section, StringComparison.OrdinalIgnoreCase))
                && Matches(query.Filter, row.Section, row.Property.Name, row.Property.Value)), query.Offset, query.Limit,
                row => new JsonObject
                {
                    ["section"] = row.Section, ["name"] = row.Property.Name, ["value"] = row.Property.Value,
                    ["offset"] = row.Property.Offset >= 0 ? (int?)row.Property.Offset : null, ["editable"] = row.Property.Editable
                });
            page["sections"] = new JsonArray(inspection.Sections.Select(section => (JsonNode?)JsonValue.Create(section.Name)).ToArray());
        }
        page["asset"] = new JsonObject
        {
            ["name"] = document.Name, ["tag"] = $"0x{document.Entry.TagHash:X8}", ["package"] = document.Entry.PackageName,
            ["kind"] = inspection.Kind, ["title"] = inspection.Title, ["references"] = inspection.References.Count, ["diagnostics"] = inspection.Diagnostics.Count
        };
        page["paths"] = "path: a section prefix, 'references' or 'diagnostics'";
        return page;
    }

    private static JsonObject AiSelectionPage(AiCapture capture, object[] selected, PanelQuery query)
    {
        if (capture.World is not { } world) return new JsonObject { ["error"] = "No map or scene is open." };
        var rows = new List<JsonObject>();
        var objects = new JsonArray();
        foreach (var source in selected)
        {
            if (world.Find(source) is not { } item) continue;
            objects.Add(new JsonObject { ["id"] = item.Id, ["label"] = AiSafeLabel(capture, item), ["kind"] = item.Kind.ToString() });
            if (query.Path is { Length: > 0 } only && only != item.Id) continue;
            if (world.Describe(item.Source) is not { } inspection) continue;
            foreach (var property in inspection.Properties)
                rows.Add(new JsonObject { ["object"] = item.Id, ["group"] = property.Group, ["name"] = property.Name, ["value"] = property.Value });
            foreach (var reference in inspection.References)
                rows.Add(new JsonObject { ["object"] = item.Id, ["group"] = "Reference", ["name"] = reference.Name, ["value"] = $"0x{reference.TagHash:X8}" });
        }
        var page = McpPages.Page(rows.Where(row => Matches(query.Filter, row["group"]?.ToString(), row["name"]?.ToString(), row["value"]?.ToString())),
            query.Offset, query.Limit, row => row);
        page["selected"] = objects;
        return page;
    }

    private static JsonObject AiAssetsPage(BrowserAsset[] assets, PanelQuery query)
    {
        string[] words = (query.Filter ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return McpPages.Page(assets.Where(asset => (query.Path is not { Length: > 0 } kind || asset.Kind.Contains(kind, StringComparison.OrdinalIgnoreCase))
            && (words.Length == 0 || words.All(word => asset.SearchText.Contains(word, StringComparison.OrdinalIgnoreCase)))), query.Offset, query.Limit,
            asset => new JsonObject
            {
                ["id"] = "tag:" + asset.Entry.TagHash.ToString("X8"), ["name"] = asset.Name, ["kind"] = asset.Kind,
                ["tag"] = $"0x{asset.Entry.TagHash:X8}", ["package"] = asset.Entry.PackageName, ["entry"] = asset.Entry.EntryIndex
            });
    }

    private static JsonObject AiGraphPage(MissionGraphDocument? graph, string? script, bool dirty, PanelQuery query)
    {
        if (graph is null) return new JsonObject { ["error"] = "No mission script is open in the Mission Graph.", ["script"] = script };
        JsonObject page = query.Path switch
        {
            "edges" => McpPages.Page(graph.Edges.Where(edge => Matches(query.Filter, edge.Source, edge.Target, edge.Kind)), query.Offset, query.Limit,
                edge => new JsonObject { ["source"] = edge.Source, ["target"] = edge.Target, ["kind"] = edge.Kind }),
            "diagnostics" => McpPages.Page(graph.Diagnostics.Where(item => Matches(query.Filter, item.Message, item.Node)), query.Offset, query.Limit,
                item => new JsonObject { ["message"] = item.Message, ["node"] = item.Node }),
            "groups" => McpPages.Page(graph.Groups.Where(group => Matches(query.Filter, group.Title, group.Key, group.Collection)), query.Offset, query.Limit,
                group => new JsonObject { ["key"] = group.Key, ["title"] = group.Title, ["kind"] = group.Kind.ToString(), ["collection"] = group.Collection, ["nodes"] = group.Nodes.Count }),
            _ => McpPages.Page(graph.Nodes.Where(node => Matches(query.Filter, node.Key, node.Id, node.Kind, node.GroupKey)), query.Offset, query.Limit,
                node => new JsonObject
                {
                    ["key"] = node.Key, ["id"] = node.Id, ["group"] = node.GroupKey, ["kind"] = node.Kind,
                    ["dependencies"] = node.Dependencies is { } dependencies ? new JsonArray(dependencies.Select(each => (JsonNode?)JsonValue.Create(each)).ToArray()) : null
                })
        };
        page["script"] = script;
        page["unsaved"] = dirty;
        page["counts"] = new JsonObject { ["groups"] = graph.Groups.Count, ["nodes"] = graph.Nodes.Count, ["edges"] = graph.Edges.Count, ["diagnostics"] = graph.Diagnostics.Count };
        page["paths"] = "path: nodes (default), edges, groups or diagnostics";
        return page;
    }

    /** The Report tab's text, copied on the UI thread (the viewport's lists change while it draws). */
    private Func<Func<JsonObject>> AiReportPage(PanelQuery query) => () =>
    {
        var world = workspace.World;
        var summary = new JsonObject();
        var lines = new List<(string Section, string Text)>();
        if (world is not null)
        {
            summary["meshParts"] = world.Scene.Instances.Count; summary["markers"] = world.Scene.Markers.Count;
            summary["authoredObjects"] = world.Scene.AuthoredObjects.Count; summary["squads"] = world.Scene.Squads.Count;
            summary["lights"] = world.Scene.LightTables.Sum(table => table.Table.Rows.Count);
            lines.AddRange(world.Scene.Diagnostics.Select(line => ("Load report", line)));
        }
        if (workspace.Names is { } names)
        {
            summary["names"] = names.Coverage.ToString();
            lines.AddRange(names.Diagnostics.Select(line => ("Names", line)));
        }
        if (viewport is { } view)
        {
            summary["nativeParts"] = view.NativePartCount; summary["fallbackParts"] = view.NativeFallbackPartCount;
            if (view.NativeError is { } error) lines.Add(("Shader report", error));
            lines.AddRange(view.NativeDiagnostics.Select(line => ("Shader report", line)));
        }
        if (workspace.NativeError.Length != 0) lines.Add(("Shader report", workspace.NativeError));
        var copy = lines.ToArray();
        return () =>
        {
            var page = McpPages.Page(copy.Where(line => (query.Path is not { Length: > 0 } section || line.Section.Equals(section, StringComparison.OrdinalIgnoreCase))
                && Matches(query.Filter, line.Text)), query.Offset, query.Limit, line => new JsonObject { ["section"] = line.Section, ["text"] = line.Text });
            page["summary"] = summary;
            page["paths"] = "path: Load report, Names or Shader report";
            return page;
        };
    };
}

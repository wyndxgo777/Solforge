using System.Globalization;
using System.Numerics;
using System.Text.Json;
using SunriseStudio.Live;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** LivePage part: writes a join report of live rows against the package registry for manual review. */
public sealed partial class LivePage
{
    // Enough rows to review joins by hand without writing the whole registry.
    private const int ReportRows = 200;
    private string reportPath = "";

    /** One snapshot of what the live view observed and applied, so a session can be reviewed later. */
    private void SaveReport(StudioWorkspace workspace, AssetViewport viewport, string? folder = null)
    {
        ulong now = (ulong)Environment.TickCount64;
        var frame = snapshot;
        var placements = connection?.Placements;
        var studioWorld = workspace.World;
        var rows = placements?.Rows ?? [];
        var report = new Dictionary<string, object?>
        {
            ["written"] = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
            ["status"] = status,
            ["bridge"] = connection?.WorldBridgeDiagnostic ?? "",
            ["frame"] = frame is null ? null : new
            {
                process = frame.ProcessId, time = frame.FrameTime, in_world = frame.InWorld,
                player = Vector(frame.Player), slice_available = frame.SliceAvailable, current_slice = frame.CurrentSlice,
                camera = frame.Camera is { } camera ? new
                {
                    position = Vector(camera.Position), forward = Vector(camera.Forward), up = Vector(camera.Up),
                    fov_degrees = camera.HorizontalFieldOfView * 180 / MathF.PI, aspect = camera.Aspect
                } : null
            },
            ["world"] = world is null ? null : new
            {
                available = world.Available, present = world.Present, activity = world.Activity,
                map_definition = Hex(world.MapDefinitionTag), map_root = Hex(world.MapRootTag),
                scenario = Hex(world.ScenarioTag), scenario_name = workspace.DisplayName(world.ScenarioTag),
                current_slice = world.CurrentSlice,
                bubbles = world.Slices.Select(slice => new
                {
                    hash = Hex(slice.BubbleHash), name = workspace.ResolveHash(slice.BubbleHash) ?? "",
                    index = slice.Index, residency = slice.Residency
                }).ToArray()
            },
            ["studio"] = new
            {
                sync_map = syncWorld, sync_bubbles = syncBubbles, sync_placements = syncPlacements, sync_dynamic = syncDynamic,
                sync_status = syncStatus, matches_world = MatchesWorld(),
                map = studioWorld is null ? null : Hex(studioWorld.Map.Tag), mission = studioWorld?.MissionTag is { } mission ? Hex(mission) : null,
                opened_bubbles = studioWorld?.Options.BubbleNameHashes?.Select(Hex).ToArray(),
                loaded_bubbles = loadedBubbles.Count, transforms_applied = transformsApplied, dynamic_applied = dynamicApplied
            },
            ["follow"] = new
            {
                mode = FollowModes[follow], external_camera = viewport.Camera.HasExternalPose,
                camera = Vector(viewport.Camera.Position), pivot = Vector(viewport.Camera.Pivot),
                camera_to_game_camera = frame?.Camera is { } game ? Distance(viewport.Camera.Position, game.Position) : (double?)null,
                pivot_to_player = frame?.Player is { } player ? Distance(viewport.Camera.Pivot, player) : (double?)null
            },
            ["placements"] = placements is null ? null : new
            {
                available = placements.Available, complete = placements.Complete, truncated = placements.Truncated,
                total = placements.Total, overflow = placements.Overflow, excluded = placements.Excluded, rows = rows.Count,
                dynamic_rows = rows.Count(row => row.Dynamic),
                fresh_rows = rows.Count(row => row.TransformAvailable && LiveWorldProtocol.Fresh(row.Time, now)),
                unplaced_or_hidden_rows = rows.Count(row => row.PlacedNotHidden == false), origin_rows = rows.Count(row => row.AtOrigin),
                marker_rows = rows.Count(row => !row.ShowsModel),
                distinct_flags = rows.Select(row => row.DatumFlags).Distinct().Count(),
                matched = binding?.Matched, unmatched = binding?.Unmatched, ambiguous = binding?.Ambiguous,
                inactive = binding?.Inactive, dormant = binding?.Dormant,
                // Placed rows are many and alike; every spawned row is kept.
                sample = rows.Where(row => !row.Dynamic).Take(ReportRows).Concat(rows.Where(row => row.Dynamic)).Select(row => new
                {
                    name = binding?.Find(row)?.Name ?? (row.ClassTag != 0 ? workspace.DisplayName(row.ClassTag) : ""),
                    handle = Hex(row.Handle), generation = row.Generation,
                    identity = row.Identity.ToString("X16", CultureInfo.InvariantCulture), list = Hex(row.ObjectListTag),
                    entry = row.EntryIndex, class_tag = Hex(row.ClassTag), dynamic = row.Dynamic, flags = Hex(row.DatumFlags),
                    placed_not_hidden = row.PlacedNotHidden, at_origin = row.AtOrigin, player_attached = row.PlayerAttached, marker = !row.ShowsModel,
                    fresh = row.TransformAvailable && LiveWorldProtocol.Fresh(row.Time, now), position = Vector(row.Position),
                    orientation = new[] { row.Orientation.X, row.Orientation.Y, row.Orientation.Z, row.Orientation.W }, scale = row.Scale
                }).ToArray()
            },
            ["selected_object"] = InspectedObject is not { } answer ? null : new
            {
                handle = Hex(answer.Handle), generation = answer.Generation, status = answer.Status.ToString(),
                parent = Hex(answer.ParentHandle), children = answer.Children.Select(child => Hex(child.Handle)).ToArray(),
                children_truncated = answer.ChildrenTruncated, models = answer.Models.Select(Hex).ToArray(),
                flags = answer.DatumFlags is { } flags ? Hex(flags) : null, placed_not_hidden = answer.PlacedNotHidden,
                root = Hex(answer.RootHandle), root_flags = Hex(answer.RootFlags), chain_flags = Hex(answer.ChainFlags),
                datum = Convert.ToHexString(answer.Datum)
            },
            ["cui"] = new
            {
                diagnostic = connection?.CuiBridgeDiagnostic ?? "", alive = connection?.CuiAlive ?? false,
                sync = syncCui, sync_status = cuiStatus, following = FollowingCui,
                followed = shownCui is { } shown ? Hex(shown) : null,
                top = connection?.CuiScreens?.Top is { } top ? Hex(top.HierarchyTag) : null,
                screens = connection?.CuiScreens is not { } screens ? null : screens.Rows.Select(screen => new
                {
                    hierarchy = Hex(screen.HierarchyTag), name = workspace.DisplayName(screen.HierarchyTag),
                    screen = Hex(screen.ScreenTag), view = Hex(screen.ViewHash), page = screen.Page,
                    flags = Hex(screen.ScreenFlags), state = Hex(screen.StateFlags), priority = screen.Priority,
                    layer = screen.Layer, ordinal = screen.Ordinal
                }).ToArray(),
                tree = connection?.CuiTree is not { } tree ? null : new
                {
                    hierarchy = Hex(tree.HierarchyTag), status = tree.Status.ToString(), count = tree.Rows.Count,
                    total = tree.Total, revision = tree.Revision, truncated = tree.Truncated, faulted = tree.WalkFaulted
                }
            },
            ["spawned"] = new
            {
                observed = dynamic.ObservedCount, previews = viewport.LiveInstanceCount, prototypes = viewport.LivePrototypeCount,
                status = dynamic.Status,
                notes = dynamic.Errors.Select(pair => new { tag = Hex(pair.Key), reason = pair.Value }).ToArray()
            },
            ["animation"] = new
            {
                enabled = syncAnimation, diagnostic = connection?.PoseDiagnostic ?? "", draws = drawsByComponent.Count,
                owners = drawsByOwner.Count, animated_parts = animated.Count, dropped = mergedDraws?.Dropped,
                rejected = mergedDraws?.Rejected, player_handle = frame?.PlayerHandle is { } handle ? Hex(handle) : null,
                player_owner = playerOwner == uint.MaxValue ? null : Hex(playerOwner), player_parts = playerMeshes.Count,
                hidden_unanimated_parts = unanimated.Count, selected = poseStatus, notes = animationNotes,
                objects = drawsByOwner.Select(pair => (Owner: pair.Key, Draws: pair.Value, Row: RowOf(pair.Key)))
                    .OrderBy(item => frame?.Player is { } player ? Distance(item.Row?.Position ?? item.Draws[0].Anchor, player) : 0)
                    .Take(ReportRows).Select(item => new
                    {
                        owner = Hex(item.Owner), row = item.Row is null ? "none" : item.Row.Dynamic ? "spawned" : "placed",
                        class_tag = item.Row is null ? null : Hex(item.Row.ClassTag), anchor = Vector(item.Draws[0].Anchor),
                        models = item.Draws.Select(draw => new
                        {
                            model = Hex(draw.ModelTag), name = workspace.DisplayName(draw.ModelTag), kind = draw.Kind.ToString(),
                            bones = draw.ModelBones, component = Hex(draw.Component)
                        }).ToArray()
                    }).ToArray()
            }
        };
        folder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sunrise Studio", "live-reports");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"live-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        reportPath = path;
    }

    /** Finite float coordinates need double subtraction and squares to keep their distance finite. */
    private static double Distance(Vector3 first, Vector3 second)
    {
        double x = (double)first.X - second.X, y = (double)first.Y - second.Y, z = (double)first.Z - second.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private static float[]? Vector(Vector3? value) => value is { } v ? [v.X, v.Y, v.Z] : null;
    private static string Hex(uint value) => value.ToString("X8", CultureInfo.InvariantCulture);
}

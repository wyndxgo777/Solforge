using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * One reading of where the player is, from whichever source answered. Positions are game world space (Z up), the
 * same space Solforge draws maps in. Yaw is degrees, atan2(dy, dx), as the recorder measures it.
 */
public sealed record PlayerFix(string Source, Vector3 Position, float? Yaw, DateTimeOffset Time)
{
    /** The Live link's map definition tag. */
    public uint? MapDefinition { get; init; }
    /** The server's room: the activity hash. */
    public uint? ActivityHash { get; init; }
    public string Activity { get; init; } = "";
    public string Bubble { get; init; } = "";
    public string Slice { get; init; } = "";
    public string? Player { get; init; }
}

/** Parsers for the player sources Solforge falls back to when the Live link is not attached. */
public static class PlayerSources
{
    /** The recorder's camera sits about this far above the player's feet. */
    public const float CameraHeight = 1.7f;

    public static float YawFromForward(Vector3 forward) => MathF.Atan2(forward.Y, forward.X) * 180 / MathF.PI;

    public static Vector3 Facing(float yaw) => new(MathF.Cos(yaw * MathF.PI / 180), MathF.Sin(yaw * MathF.PI / 180), 0);

    /** Where test-recorder sessions are written: SOLFORGE_RECORDER, else the client's bin\x64\Sunrise\logs\recorder. */
    public static IEnumerable<string> RecorderRoots(string? gameRoot)
    {
        if (Environment.GetEnvironmentVariable("SOLFORGE_RECORDER") is { Length: > 0 } custom) yield return custom;
        if (string.IsNullOrWhiteSpace(gameRoot)) yield break;
        yield return Path.Combine(gameRoot, "bin", "x64", "Sunrise", "logs", "recorder");
        yield return Path.Combine(gameRoot, "Sunrise", "logs", "recorder");
    }

    /**
     * One recorder snapshot line. pos freezes at spawn or drops out after a load or respawn, so cam (lowered to the
     * feet) wins whenever pos is missing, marked stale or more than 3 m from it. Menus, loads and marks give null.
     */
    public static PlayerFix? ParseRecorder(string line, DateTimeOffset fallbackTime)
    {
        JsonObject? snap;
        try { snap = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
        if (snap is null || snap["mark"] is not null) return null;
        if (snap["ui"]?["in_world"] is JsonValue world && world.TryGetValue(out bool inWorld) && !inWorld) return null;
        Vector3? pos = Vector(snap["pos"]), cam = Vector(snap["cam"]);
        Vector3 at;
        if (cam is { } c && (pos is not { } p0 || snap["pos_stale_ms"] is not null || MathF.Abs(p0.X - c.X) + MathF.Abs(p0.Y - c.Y) > 3))
            at = c - new Vector3(0, 0, CameraHeight);
        else if (pos is { } p) at = p;
        else return null;
        var time = snap["utc"] is JsonValue utc && utc.TryGetValue(out string? stamp)
            && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : fallbackTime;
        var loc = snap["loc"] as JsonObject;
        return new PlayerFix("recorder", at, Number(snap["yaw"]), time)
        {
            Activity = Text(loc?["activity"]), Bubble = Text(loc?["bubble"]), Slice = Text(loc?["slice"])
        };
    }

    /** The newest session's last snapshot; reads only the file's tail. Null when there is none or it is not in a world. */
    public static PlayerFix? LatestRecorder(string root)
    {
        if (!Directory.Exists(root)) return null;
        var session = new DirectoryInfo(root).EnumerateDirectories().OrderByDescending(folder => folder.LastWriteTimeUtc).FirstOrDefault();
        string? file = session is null ? null : Path.Combine(session.FullName, "snapshots.jsonl");
        if (file is null || !File.Exists(file)) return null;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, stream.Length - 16384);
        stream.Seek(start, SeekOrigin.Begin);
        var bytes = new byte[stream.Length - start];
        stream.ReadExactly(bytes);
        var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // The newest snapshot decides: an older position while the player sits in a menu would be stale.
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (!lines[i].StartsWith('{') || lines[i].Contains("\"mark\"", StringComparison.Ordinal)) continue;
            return ParseRecorder(lines[i], File.GetLastWriteTimeUtc(file));
        }
        return null;
    }

    /** The server admin's /coop page: every joined player with name, room (activity hash) and position. */
    public static IReadOnlyList<PlayerFix> ParseCoop(string json, DateTimeOffset time)
    {
        var rows = new List<PlayerFix>();
        JsonObject? page;
        try { page = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return rows; }
        if (page?["players"] is not JsonArray players) return rows;
        foreach (var node in players)
        {
            if (node is not JsonObject row || Number(row["x"]) is not { } x || Number(row["y"]) is not { } y || Number(row["z"]) is not { } z) continue;
            if (x == 0 && y == 0 && z == 0) continue;
            rows.Add(new PlayerFix("server", new Vector3(x, y, z), null, time)
            {
                Player = Text(row["name"]), ActivityHash = SolforgeTools.ParseHash(Text(row["room"])) is { } room and not 0 ? room : null
            });
        }
        return rows;
    }

    /** A JSON array of three numbers, or null. */
    public static Vector3? Vector(JsonNode? node) =>
        node is JsonArray { Count: >= 3 } array && Number(array[0]) is { } x && Number(array[1]) is { } y && Number(array[2]) is { } z
            ? new Vector3(x, y, z) : null;

    public static JsonArray Json(Vector3 value) => new(Round(value.X), Round(value.Y), Round(value.Z));

    public static double Round(float value) => Math.Round(value, 2);

    /** Any JSON number, including whole numbers built in-process (a JsonValue<int> refuses TryGetValue<double>). */
    private static float? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        double number = value.TryGetValue(out double d) ? d : value.TryGetValue(out float f) ? f : value.TryGetValue(out long l) ? l
            : value.TryGetValue(out int i) ? i : double.NaN;
        return double.IsFinite(number) ? (float)number : null;
    }

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : "";
}

/** The breadcrumb trail: points at least `spacing` apart, the oldest dropped past `capacity`, cleared by a teleport. */
public sealed class PlayerTrail(int capacity = 240, float spacing = .75f, float teleport = 150)
{
    private readonly List<Vector3> points = [];
    public IReadOnlyList<Vector3> Points => points;
    public int Revision { get; private set; }

    public void Add(Vector3 point)
    {
        if (points.Count > 0)
        {
            float distance = Vector3.Distance(points[^1], point);
            if (distance < spacing) return;
            if (distance > teleport) points.Clear();
        }
        points.Add(point);
        if (points.Count > capacity) points.RemoveAt(0);
        Revision++;
    }

    public void Clear() { if (points.Count == 0) return; points.Clear(); Revision++; }
}

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Solforge.Mcp.LocalServer;

/** One listening socket of the server process, from ss. */
public sealed record ServerSocket(string Protocol, string Address, int Port);

/** A session member that appears twice in one census session (a killed client's ghost: events stop spawning). */
public sealed record GhostMember(string Session, string Member, int Count);

/** Player census: sessions, members, ghost members and members split across sessions. */
public sealed record CensusSummary(int Sessions, int Members, IReadOnlyList<GhostMember> Ghosts, IReadOnlyList<string> SplitMembers)
{
    public static readonly CensusSummary Empty = new(0, 0, [], []);
}

/** Snapshot of the local server: process, sockets, ports answering, census and problems. */
public sealed record LocalServerStatus(
    bool Running, int? Pid, long? UptimeSeconds, string? WorkingDirectory, bool ForeignInstance,
    IReadOnlyList<ServerSocket> Sockets, bool BapUdp, bool BapTcp, bool Web, bool Admin,
    int? Players, IReadOnlyList<string> PlayerNames, CensusSummary Census, string? Error)
{
    public string Summary => !Running ? "stopped"
        : $"running pid {Pid}, up {LocalServerParse.Duration(UptimeSeconds ?? 0)}, {Players?.ToString(CultureInfo.InvariantCulture) ?? "?"} player(s)"
          + (Census.Ghosts.Count > 0 ? $", {Census.Ghosts.Count} GHOST member(s): restart before testing events" : "");

    /** JSON form of the status. */
    public JsonObject ToJson()
    {
        var sockets = new JsonArray();
        foreach (var socket in Sockets) sockets.Add($"{socket.Protocol} {socket.Address}:{socket.Port}");
        var ghosts = new JsonArray();
        foreach (var ghost in Census.Ghosts)
            ghosts.Add(new JsonObject { ["session"] = ghost.Session, ["member"] = ghost.Member, ["count"] = ghost.Count });
        var names = new JsonArray();
        foreach (string name in PlayerNames) names.Add(name);
        var split = new JsonArray();
        foreach (string member in Census.SplitMembers) split.Add(member);
        return new JsonObject
        {
            ["running"] = Running, ["pid"] = Pid, ["uptime_s"] = UptimeSeconds, ["cwd"] = WorkingDirectory,
            ["foreign_instance"] = ForeignInstance, ["summary"] = Summary,
            ["ports"] = new JsonObject { ["bap_udp"] = BapUdp, ["bap_tcp"] = BapTcp, ["web_http"] = Web, ["admin_http"] = Admin },
            ["sockets"] = sockets, ["players"] = Players, ["player_names"] = names,
            ["census"] = new JsonObject
            {
                ["sessions"] = Census.Sessions, ["members"] = Census.Members, ["ghosts"] = ghosts, ["members_in_several_sessions"] = split,
                ["ghost_check"] = Census.Ghosts.Count == 0 ? "ok" : "ghost member found: restart the server before judging spawns",
            },
            ["error"] = Error,
        };
    }
}

/** Pure parsers for the status script, census lines and the log; unit tested without WSL. */
public static partial class LocalServerParse
{
    public const string SocketsMark = "---SOCKETS", AdminMark = "---ADMIN", PlayersMark = "---PLAYERS", CensusMark = "---CENSUS", EndMark = "---END";

    /** Parses the sectioned output of the status script. */
    public static LocalServerStatus Status(string output, LocalServerOptions options)
    {
        var sections = Sections(output);
        var head = sections.GetValueOrDefault("") ?? [];
        string? Value(string key) => head.FirstOrDefault(line => line.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..].Trim();
        int? pid = int.TryParse(Value("PID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p > 0 ? p : null;
        long? uptime = long.TryParse(Value("ETIME"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long u) ? u : null;
        string? cwd = Value("CWD") is { Length: > 0 } c ? c : null;
        var sockets = (sections.GetValueOrDefault(SocketsMark) ?? []).Select(Socket).OfType<ServerSocket>().ToArray();
        bool Has(string protocol, int port) => sockets.Any(s => s.Port == port && s.Protocol == protocol);
        int? players = null;
        string? error = null;
        string adminText = string.Join("", sections.GetValueOrDefault(AdminMark) ?? []).Trim();
        if (pid is not null && adminText.Length > 0)
        {
            try
            {
                if (JsonNode.Parse(adminText) as JsonObject is { } admin && admin["players"] is JsonValue count && count.TryGetValue(out int n)) players = n;
            }
            catch (System.Text.Json.JsonException) { error = "admin /status was not JSON: " + Clip(adminText, 120); }
        }
        var names = new List<string>();
        string playersText = string.Join("", sections.GetValueOrDefault(PlayersMark) ?? []).Trim();
        if (playersText.Length > 0)
        {
            try
            {
                if ((JsonNode.Parse(playersText) as JsonObject)?["players"] is JsonArray rows)
                    foreach (var row in rows.OfType<JsonObject>())
                        if ((row["name"] ?? row["username"] ?? row["persona"])?.ToString() is { Length: > 0 } name) names.Add(name);
            }
            catch (System.Text.Json.JsonException) { }
        }
        var census = Census(sections.GetValueOrDefault(CensusMark) ?? []);
        bool foreign = pid is not null && cwd is not null && cwd != options.RunDirectory;
        return new LocalServerStatus(pid is not null, pid, uptime, cwd, foreign, sockets,
            Has("udp", options.BapPort), Has("tcp", options.BapPort), Has("tcp", options.WebPort), Has("tcp", options.AdminPort),
            players, names, census, error);
    }

    private static Dictionary<string, List<string>> Sections(string output)
    {
        var sections = new Dictionary<string, List<string>> { [""] = [] };
        var current = sections[""];
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("---", StringComparison.Ordinal) && line.Length > 3 && line.Trim() == line)
            {
                sections[line] = current = [];
                continue;
            }
            current.Add(line);
        }
        return sections;
    }

    [GeneratedRegex(@"^(?<proto>udp|tcp)\s+\S+\s+\d+\s+\d+\s+(?<local>\S+):(?<port>\d+)\s", RegexOptions.CultureInvariant)]
    private static partial Regex SocketLine();

    /** "udp UNCONN 0 0 0.0.0.0:30974 0.0.0.0:* users:((...))" from ss -H -tulpn. */
    public static ServerSocket? Socket(string line)
    {
        var match = SocketLine().Match(line.Trim() + " ");
        return match.Success ? new ServerSocket(match.Groups["proto"].Value, match.Groups["local"].Value,
            int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture)) : null;
    }

    [GeneratedRegex(@"\bt=(?<t>\d+)\b.*\bstage=census\b.*\bsession=(?<session>\S+).*\bmembers=(?<members>\S*)", RegexOptions.CultureInvariant)]
    private static partial Regex CensusLine();

    /**
     * The newest census round (lines within 2 s of the last census line): sessions, distinct members, and any member listed
     * twice in one session. A taskkilled client leaves such a ghost and public events then place squads in an unowned bubble.
     */
    public static CensusSummary Census(IEnumerable<string> lines)
    {
        var rows = new List<(long T, string Session, string[] Members)>();
        foreach (string line in lines)
        {
            var match = CensusLine().Match(line);
            if (!match.Success || !long.TryParse(match.Groups["t"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long t)) continue;
            rows.Add((t, match.Groups["session"].Value,
                match.Groups["members"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        }
        if (rows.Count == 0) return CensusSummary.Empty;
        long last = rows[^1].T;
        var latest = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var row in rows.Where(row => row.T >= last - 2000)) latest[row.Session] = row.Members;
        var ghosts = new List<GhostMember>();
        var sessionsOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (session, members) in latest)
        {
            foreach (var group in members.GroupBy(member => member, StringComparer.Ordinal))
            {
                if (group.Count() > 1) ghosts.Add(new GhostMember(session, group.Key, group.Count()));
                sessionsOf[group.Key] = sessionsOf.GetValueOrDefault(group.Key) + 1;
            }
        }
        return new CensusSummary(latest.Count, sessionsOf.Count, ghosts,
            sessionsOf.Where(pair => pair.Value > 1).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray());
    }

    /**
     * Log filter: space-separated terms that must all appear (case-insensitive), "-term" excludes, and "re:" makes the rest
     * one regular expression. Empty matches everything.
     */
    public static Func<string, bool> Filter(string? filter)
    {
        filter = filter?.Trim() ?? "";
        if (filter.Length == 0) return _ => true;
        if (filter.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
        {
            Regex regex;
            try { regex = new Regex(filter[3..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
            catch (ArgumentException) { return line => line.Contains(filter[3..], StringComparison.OrdinalIgnoreCase); }
            return line => { try { return regex.IsMatch(line); } catch (RegexMatchTimeoutException) { return false; } };
        }
        var terms = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var include = terms.Where(term => !term.StartsWith('-') || term.Length == 1).ToArray();
        var exclude = terms.Where(term => term.StartsWith('-') && term.Length > 1).Select(term => term[1..]).ToArray();
        return line => include.All(term => line.Contains(term, StringComparison.OrdinalIgnoreCase))
            && !exclude.Any(term => line.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /** Formats a number of seconds as a short duration string. */
    public static string Duration(long seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h" : span.TotalHours >= 1 ? $"{span.Hours}h {span.Minutes}m" : $"{span.Minutes}m {span.Seconds}s";
    }

    /** Truncates text to length characters, marking the cut. */
    public static string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "...";
}

/** Joins log chunks read by byte offset into whole lines; a partial last line waits for the next chunk. */
public sealed class LogFollower
{
    private string pending = "";
    public long Offset { get; private set; } = -1;

    /**
     * Adds a log chunk and returns the complete lines; a partial last line is held back until the next
     * chunk.
     */
    public IReadOnlyList<string> Accept(string chunk, long nextOffset, bool reset)
    {
        if (reset) pending = "";
        Offset = nextOffset;
        string text = pending + chunk.Replace("\r", "", StringComparison.Ordinal);
        int end = text.LastIndexOf('\n');
        if (end < 0) { pending = text; return []; }
        pending = text[(end + 1)..];
        return text[..end].Split('\n');
    }
}

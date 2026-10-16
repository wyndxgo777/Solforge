using System.Text.Json.Nodes;

namespace Solforge.Mcp.LocalServer;

/** What one part of the sol-server does, which original service it replaces, and how to see it is up. */
public sealed record ServerSubsystem(string Name, string Replaces, string Role, string Endpoint, string Check, string Source)
{
    /** JSON form, with whether the subsystem is up when a status is known. */
    public JsonObject ToJson(LocalServerStatus? status) => new()
    {
        ["name"] = Name, ["replaces"] = Replaces, ["role"] = Role, ["endpoint"] = Endpoint, ["check"] = Check, ["source"] = Source,
        ["up"] = status is null ? null : ServerSubsystems.IsUp(this, status),
    };
}

/**
 * The server's subsystems as the architecture docs describe them (08-docs master.md §3 and §10, Studio.md §6, the
 * Demonware research PDF §1.2 and §4.2), mapped to what the local sol-server actually listens on. Demonware (Activision)
 * did identity, lobbies, NAT relay and title storage and no gameplay; Bungie's Activity Host and Physics Host ran the game.
 * Sol replaces both halves in one process.
 */
public static class ServerSubsystems
{
    public static readonly IReadOnlyList<ServerSubsystem> All =
    [
        new("Sign-in and identity", "Demonware bdAuth + Bungie.net membership",
            "Validates the player (Steam ticket or Sol account), resolves the account and loads character saves.",
            "HTTP 8098 /api/login, /api/session, /api/steam", "web", "Demonware PDF §1.2, §4.2 step 2 and 4; master.md §10.1"),
        new("Lobbies, fireteams and matchmaking", "Demonware bdLobby / LSG",
            "Fireteam joins, invites, presence and Crucible matchmaking; no gameplay.",
            "HTTP 8098 /api/join, /api/invite, /api/presence; admin /lobbies /fireteam", "web", "Demonware PDF §1.2, §2.1"),
        new("NAT relay", "Demonware bdRelay / Steam Datagram Relay",
            "Single-port relay so players behind strict NAT reach each other through the server (log: ev=single_port stage=traffic).",
            "UDP 30974 (shared with BAP)", "bap_udp", "Demonware PDF §2.1"),
        new("Title storage and news", "Demonware title files / MOTD",
            "News posts, content gating (Forsaken/Shadowkeep locks), server flags.",
            "HTTP 8098 /api/news; Sunrise/data/content_gating.txt", "web", "Demonware PDF §1.2"),
        new("Activity host (BAP)", "Bungie Activity Host",
            "Authoritative game logic: activity lifecycle (Bungie Activity Protocol), player join and team reservation, combatants, " +
            "Lua mission scripts, objectives and milestones. Census lines (stage=census) list each session's members.",
            "UDP+TCP 30974 (bap_port)", "bap", "master.md §3, §3.1; Studio.md §6"),
        new("Bubbles and physics", "Bungie Physics Host / bubble authority",
            "Open worlds are split into bubbles; the activity host owns script state while a bubble authority arbitrates local " +
            "physics. A ghost member (same account twice in one census session, left by a killed client) holds a bubble, so " +
            "public-event squads go to an unowned bubble and never appear: restart before testing spawns.",
            "embedded gameplay topology (settings server.gameplay)", "census", "master.md §3.2; research PDF (Activity Host & Physics Host split)"),
        new("Investment and inventory", "Bungie.net inventory / manifest",
            "Item definitions from build_data.bin, player inventory and currencies (investment.sqlite3), loot pools, vendors, pursuits.",
            "Sunrise/cache/build_data.bin; admin /sol/inventory", "admin", "Studio.md §6 (Investment Service); plan.md spike B"),
        new("Admin API", "(Sol only)",
            "Loopback operator API: status, players, server commands (give, clear, activity, goto), inventory edits.",
            "HTTP 127.0.0.1:8099", "admin", "master.md §10.1"),
    ];

    /** Whether the subsystem is answering; null when the check cannot be judged from the status. */
    public static bool? IsUp(ServerSubsystem subsystem, LocalServerStatus status) => !status.Running ? false : subsystem.Check switch
    {
        "web" => status.Web,
        "bap" => status.BapUdp && status.BapTcp,
        "bap_udp" => status.BapUdp,
        "admin" => status.Admin,
        "census" => status.Census.Ghosts.Count == 0,
        _ => null,
    };

    /** All subsystems as a JSON array. */
    public static JsonArray ToJson(LocalServerStatus? status)
    {
        var array = new JsonArray();
        foreach (var subsystem in All) array.Add(subsystem.ToJson(status));
        return array;
    }
}

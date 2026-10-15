using System.Text.Json.Nodes;

namespace SunriseStudio.Editor;

/** Solforge Phase 47B: what get_live_state reports about the in-game Live link (Studio.md §3.7). */
public sealed partial class LivePage
{
    public JsonObject AiState()
    {
        var state = new JsonObject { ["attached"] = connection is not null, ["snapshot"] = snapshot is not null };
        if (world is not null) state["world"] = world.ToString();
        if (snapshot is not null) state["detail"] = snapshot.ToString();
        if (connection is null) state["note"] = "Attach the Live link (Live page) to read the player position and nearby in-game objects.";
        return state;
    }
}

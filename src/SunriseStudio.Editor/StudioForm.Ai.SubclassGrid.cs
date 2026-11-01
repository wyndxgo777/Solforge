/**
 * File: StudioForm partial: the fork_subclass_grid MCP action (runs on the UI thread after the approve click).
 */
using System.Text.Json.Nodes;
using Solforge.Mcp;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** fork_subclass_grid: gives a subclass item its own socket-entry list and pools (the talent-grid writer). */
    private string AiForkSubclassGrid(JsonObject args)
    {
        string name = (McpJson.String(args, "name") ?? "").Trim();
        if (workspace.LastSubclassGrid is { } last && last.Name == name)
            return $"Forked as {name}: item {Hex(last.ItemTag)} now names list {last.NewList} (was {last.OldList}); "
                + $"list definition {Hex(last.DefinitionTag)}; copies: "
                + string.Join(", ", last.Tags.Select(pair => $"{Hex(pair.Key)} -> {Hex(pair.Value)}"))
                + ". It is in Project changes with undo.";
        uint item = SolforgeTools.ParseHash(McpJson.String(args, "item") ?? "")
            ?? throw new InvalidDataException("Give the subclass item tag (0x...).");
        ushort package = workspace.CustomPerkPackage() ?? throw new InvalidDataException("No project package can take the copies.");
        workspace.Run(() => workspace.ForkSubclassGrid(item, name, package));
        return workspace.Error.Length > 0 ? workspace.Error
            : $"Forking {Hex(item)}'s socket-entry list into package {package:X4}; call fork_subclass_grid again with the same "
              + "name for the new list once get_build_status is idle.";
    }
}

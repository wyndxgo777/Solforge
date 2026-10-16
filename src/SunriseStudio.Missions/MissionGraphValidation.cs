/** File: Partial MissionGraphDocument: checks node fields against the flow library contract. */
using System.Text;

namespace SunriseStudio.Missions;

/** Partial: validation rules. */
public sealed partial class MissionGraphDocument
{
    /** The runtime stores flow state under keys of at most 63 UTF-8 bytes. */
    private const int MaxStateKeyBytes = 63;

    private static bool StateKeyTooLong(string prefix, string kind, string id) =>
        Encoding.UTF8.GetByteCount($"flow.{prefix}.{kind}.{id}") > MaxStateKeyBytes;

    /** Literal fields are checked against flow.lua; computed Lua values remain outside these checks. */
    private void ValidateFlowFields(List<MissionGraphDiagnostic> diagnostics)
    {
        var bindings = new MissionScriptBindings(Script);
        foreach (var group in Groups.Where(group => group.IsFlowSteps))
        {
            if (group.Nodes.Count == 0) diagnostics.Add(new("A flow needs at least one step."));
            var factNodes = NodesIn(group, "facts");
            var facts = factNodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            bool completeFacts = group.Definition?.Field("facts") is null or { Table: not null };
            string? prefix = group.Definition?.Field("key")?.StringValue;
            if (prefix is not null)
            {
                if (StateKeyTooLong(prefix, "state", "checkpoint"))
                    diagnostics.Add(new("Flow key exceeds the 63-byte state-key limit."));
                foreach (var fact in factNodes)
                    if (fact.Table?.Field("id")?.StringValue is { } id && StateKeyTooLong(prefix, "fact", id))
                        diagnostics.Add(new("Flow fact exceeds the 63-byte state-key limit.", fact.Key));
            }
            foreach (var node in group.Nodes)
            {
                if (node.Table is not { } table) continue;
                if (node.Dependencies is { } after && after.Distinct(StringComparer.Ordinal).Count() != after.Count)
                    diagnostics.Add(new("Flow dependencies contain duplicate steps.", node.Key));
                if (table.Field("after")?.StringValue is not null)
                    diagnostics.Add(new("Flow dependencies must be a list.", node.Key));
                if (table.Field("checkpoint") is { } boolean && boolean.Value.Read(Source) is not ("true" or "false" or "nil")
                    && IsLiteral(boolean)) diagnostics.Add(new("Flow checkpoint must be a boolean.", node.Key));
                if (table.Field("run") is { } run && run.Value.Read(Source) != "nil" && IsLiteral(run))
                    diagnostics.Add(new("Flow action must be a function.", node.Key));
                foreach (string field in new[] { "when", "await" })
                {
                    try
                    {
                        var condition = MissionConditions.Read(table.Field(field)?.Value.Read(Source), bindings);
                        if (completeFacts)
                            foreach (string fact in MissionConditions.Facts(condition).Where(fact => !facts.Contains(fact)).Distinct())
                                diagnostics.Add(new("Unknown flow fact: " + fact, node.Key));
                    }
                    catch (InvalidDataException error) { diagnostics.Add(new(error.Message, node.Key)); }
                }
                if (table.Field("reset_facts") is { Table: { } reset })
                {
                    if (reset.Fields.Count > 64) diagnostics.Add(new("A checkpoint resets at most 64 facts.", node.Key));
                    string? checkpoint = table.Field("checkpoint")?.Value.Read(Source);
                    if (reset.Fields.Count > 0 && checkpoint is null or "false" or "nil")
                        diagnostics.Add(new("Only a checkpoint can reset facts.", node.Key));
                    if (completeFacts)
                        foreach (var field in reset.Fields)
                            if (field.StringValue is { } fact && !facts.Contains(fact))
                                diagnostics.Add(new("Unknown reset fact: " + fact, node.Key));
                }
                if (prefix is not null && table.Field("id")?.StringValue is { } id && StateKeyTooLong(prefix, "step", id))
                    diagnostics.Add(new("Flow step exceeds the 63-byte state-key limit.", node.Key));
            }
        }
    }

    /** A value whose first token is a literal; `1 + x` counts, `-1` does not. */
    private static bool IsLiteral(LuaField field) => field.Table is not null || field.StringValue is not null
        || LuaScript.First(field.Expression) is LuaLiteral literal && (literal.Kind is LuaLiteralKind.True or LuaLiteralKind.False
            or LuaLiteralKind.Nil || literal.Kind == LuaLiteralKind.Number && char.IsDigit(literal.Text[0]));
}

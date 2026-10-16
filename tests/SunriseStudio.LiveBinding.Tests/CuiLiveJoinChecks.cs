using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Live;

/** Checks the join of live CUI rows to authored nodes by id and name hash, including hidden parents and nested rows. */
internal static class CuiLiveJoinChecks
{
    private const uint Hierarchy = 0x80B00001, Nested = 0x80B00002;
    private const uint Visible = LiveCuiProtocol.StateVisible;

    /** The live CUI overlay joins rows by node id and name, and a hidden parent hides its children. */
    internal static int Run()
    {
        CuiComposedNode[] nodes = [Node(0, 0), Node(1, 0xA1), Node(2, 0xA2), Node(3, 0xA3), Node(4, 0xA4)];
        LiveCuiWidget[] rows =
        [
            Row(0, 0, null, Visible),
            Row(1, 1, 0, 0, 0xA1),
            Row(2, 2, 1, Visible, 0xA2),
            Row(3, 3, 0, Visible, 0xFF),
            Row(4, 1, 0, Visible, 0xB1, Nested),
        ];
        var join = CuiLiveJoin.Build(nodes, Hierarchy, Tree(rows));
        Check(join.Matched == 3 && join.Row(2)?.Row == 2, "Rows did not join by node id.");
        Check(join.Mismatched == 1 && join.Row(3) is null, "A name hash mismatch was joined or not counted.");
        Check(join.OtherHierarchy == 1 && join.Row(1)?.HierarchyTag == Hierarchy, "A nested hierarchy row was joined.");
        Check(join.State(0) == CuiLiveState.Shown && join.State(1) == CuiLiveState.Hidden
            && join.State(2) == CuiLiveState.HiddenByParent && !join.ShownOrUnknown(2), "A hidden parent did not hide its child.");
        Check(join.State(4) == CuiLiveState.NotBuilt && join.ShownOrUnknown(4), "An unbuilt node was hidden.");
        foreach (var empty in new[] { Tree([]), Tree(rows) with { Status = LiveCuiStatus.Absent }, null })
        {
            var none = CuiLiveJoin.Build(nodes, Hierarchy, empty);
            Check(none.Matched == 0 && none.Mismatched == 0 && none.State(1) == CuiLiveState.NotBuilt && none.ShownOrUnknown(1),
                "An empty or missing tree joined rows.");
        }
        // A CUI object that is not a widget has no state word, so it must not hide its widget child.
        LiveCuiWidget[] objects = [Row(0, 0, null, Visible), Row(1, 1, 0, 0, 0xA1) with { RowFlags = 0 }, Row(2, 2, 1, Visible, 0xA2)];
        var mixed = CuiLiveJoin.Build(nodes, Hierarchy, Tree(objects));
        Check(mixed.State(1) == CuiLiveState.Shown && mixed.State(2) == CuiLiveState.Shown && mixed.ShownOrUnknown(2),
            "A node that is not a widget hid itself or its widget child.");
        // A walk cut short at its row or depth cap did not reach every node.
        var partial = CuiLiveJoin.Build(nodes, Hierarchy, Tree(rows) with { Truncated = true });
        Check(partial.Partial && partial.State(4) == CuiLiveState.Unknown && partial.ShownOrUnknown(4)
            && join.State(4) == CuiLiveState.NotBuilt, "A partial tree reported an unreached node as not built.");
        Console.WriteLine("cui_live_join joined=3 mismatched=1 nested=1 non_widget=shown partial=unknown");
        return 1;
    }

    private static CuiComposedNode Node(int id, uint name) =>
        new() { Id = id, Definition = new((ushort)id, name, 0, 0, $"Node {id}", -1) };

    private static LiveCuiWidget Row(int row, ushort node, int? parent, uint state, uint name = 0, uint hierarchy = Hierarchy) =>
        new(row, 0, hierarchy, name, node, parent, state, LiveCuiProtocol.RowWidget, 0);

    private static LiveCuiTree Tree(IReadOnlyList<LiveCuiWidget> rows) =>
        new(1, LiveCuiStatus.Complete, 0, 1, Hierarchy, 0, 0, (uint)rows.Count, false, rows);

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}

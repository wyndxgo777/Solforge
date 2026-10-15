namespace SunriseStudio.Editor;

/** Where a removed area sat, so it can go back beside the same neighbour. */
public sealed record StudioLayoutSlot(int SiblingId, StudioSplitAxis Axis, bool First, float Ratio);

/** Solforge Phase 03B: named layout presets (Studio.md §3.9) built from the same split tree. */
public sealed partial class StudioLayout
{
    public const string SolforgePreset = "Solforge";
    public const string ScenePreset = "Solforge (scene)";
    public const string ClassicPreset = "Solforge (classic)";
    public const string SunrisePreset = "Sunrise Studio";
    public const string AiPreset = "Solforge (AI)";
    public static IReadOnlyList<string> PresetNames { get; } = [SolforgePreset, AiPreset, ScenePreset, ClassicPreset, SunrisePreset];

    /** An unknown name gives the upstream Sunrise Studio layout. */
    public static StudioLayout Preset(string name) => name switch
    {
        SolforgePreset => Solforge(),
        AiPreset => Ai(),
        ScenePreset => Scene(),
        ClassicPreset => Classic(),
        _ => Default()
    };

    /** Solforge's default (Seth's, 2026-10-15): Asset browser, Workspace and AI, over Project changes and the Mission Graph. */
    private static StudioLayout Solforge() => new(new()
    {
        Id = 1, Axis = StudioSplitAxis.Rows, Ratio = .74f,
        First = new()
        {
            Id = 2, Axis = StudioSplitAxis.Columns, Ratio = .16f,
            First = new() { Id = 3, Panel = StudioPanelKind.Browser },
            Second = new()
            {
                Id = 4, Axis = StudioSplitAxis.Columns, Ratio = .54f,
                First = new() { Id = 5, Panel = StudioPanelKind.Workspace },
                Second = new() { Id = 6, Panel = StudioPanelKind.AI }
            }
        },
        Second = new()
        {
            Id = 7, Axis = StudioSplitAxis.Columns, Ratio = .5f,
            First = new() { Id = 8, Panel = StudioPanelKind.Changes },
            Second = new() { Id = 9, Panel = StudioPanelKind.MissionGraph }
        }
    });

    /** AI first: the AI workspace in the middle, the Asset browser and Mission Graph as side panels that fold away while empty. */
    private static StudioLayout Ai() => new(new()
    {
        Id = 1, Axis = StudioSplitAxis.Columns, Ratio = .2f,
        First = new() { Id = 2, Panel = StudioPanelKind.Browser },
        Second = new()
        {
            Id = 3, Axis = StudioSplitAxis.Columns, Ratio = .7f,
            First = new() { Id = 4, Panel = StudioPanelKind.AI },
            Second = new() { Id = 5, Panel = StudioPanelKind.MissionGraph }
        }
    });

    /** Scene editing: Asset browser left, Workspace centre, Object tree over Inspector right, and a bottom row. */
    private static StudioLayout Scene() => new(new()
    {
        Id = 1, Axis = StudioSplitAxis.Rows, Ratio = .74f,
        First = new()
        {
            Id = 2, Axis = StudioSplitAxis.Columns, Ratio = .16f,
            First = new() { Id = 3, Panel = StudioPanelKind.Browser },
            Second = new()
            {
                Id = 4, Axis = StudioSplitAxis.Columns, Ratio = .79f,
                First = new() { Id = 5, Panel = StudioPanelKind.Workspace },
                Second = new()
                {
                    Id = 6, Axis = StudioSplitAxis.Rows, Ratio = .42f,
                    First = new() { Id = 7, Panel = StudioPanelKind.ObjectTree },
                    Second = new() { Id = 8, Panel = StudioPanelKind.Inspector }
                }
            }
        },
        // One editor per area: the bottom row splits Project changes and the Mission Graph.
        Second = new()
        {
            Id = 9, Axis = StudioSplitAxis.Columns, Ratio = .5f,
            First = new() { Id = 10, Panel = StudioPanelKind.Changes },
            Second = new() { Id = 11, Panel = StudioPanelKind.MissionGraph }
        }
    });

    /** The classic Solforge layout (Seth's, 2026-10-15): Asset browser and Workspace, then Inspector over Project changes. */
    private static StudioLayout Classic() => new(new()
    {
        Id = 1, Axis = StudioSplitAxis.Columns, Ratio = .765f,
        First = new()
        {
            Id = 2, Axis = StudioSplitAxis.Columns, Ratio = .31f,
            First = new() { Id = 3, Panel = StudioPanelKind.Browser },
            Second = new() { Id = 4, Panel = StudioPanelKind.Workspace }
        },
        Second = new()
        {
            Id = 5, Axis = StudioSplitAxis.Rows, Ratio = .5f,
            First = new() { Id = 6, Panel = StudioPanelKind.Inspector },
            Second = new() { Id = 7, Panel = StudioPanelKind.Changes }
        }
    });

    /**
     * Takes an area out and lets its neighbour fill the space.
     * @return Where it was, or null when the editor is not docked or is the only area.
     */
    public StudioLayoutSlot? Remove(StudioPanelKind kind)
    {
        if (kind == StudioPanelKind.Empty || Find(kind) is not { } node || node == Root) return null;
        var parent = Nodes(Root).First(item => item.First == node || item.Second == node);
        var sibling = parent.First == node ? parent.Second! : parent.First!;
        var slot = new StudioLayoutSlot(sibling.Id, parent.Axis, parent.First == node, parent.Ratio);
        return Close(node.Id) ? slot : null;
    }

    /**
     * Puts a removed area back beside the neighbour it had.
     * @return False when the editor is already docked, the neighbour is gone or the slot is malformed.
     */
    public bool Reinsert(StudioPanelKind kind, StudioLayoutSlot slot)
    {
        if (!Enum.IsDefined(kind) || kind == StudioPanelKind.Empty || Find(kind) is not null || Panels.Count >= MaximumPanels
            || !Enum.IsDefined(slot.Axis) || !float.IsFinite(slot.Ratio) || slot.Ratio is <= 0 or >= 1
            || Find(slot.SiblingId) is not { } sibling) return false;
        var parent = sibling == Root ? null : Nodes(Root).First(item => item.First == sibling || item.Second == sibling);
        var panel = new StudioLayoutNode { Id = nextId++, Panel = kind };
        var split = new StudioLayoutNode
        {
            Id = nextId++, Axis = slot.Axis, Ratio = slot.Ratio,
            First = slot.First ? panel : sibling,
            Second = slot.First ? sibling : panel
        };
        if (parent is null) Root = split;
        else if (parent.First == sibling) parent.First = split;
        else parent.Second = split;
        return true;
    }
}

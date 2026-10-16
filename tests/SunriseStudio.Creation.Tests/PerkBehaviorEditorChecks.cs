using SunriseStudio.Editor;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;

namespace SunriseStudio.Creation.Tests;

/** Checks the perk behavior editor: blank and scratch graphs, and that edited values survive native compilation. */
internal static class PerkBehaviorEditorChecks
{
    /** Draft edits obey native phase kinds, preserve undo and compile without a source perk. */
    public static void Run()
    {
        var editor = new PerkBehaviorEditor();
        Check(editor.Problems.Count >= 2 && !editor.IsDirty, "A blank graph was ready to compile.");
        string condition = editor.AddNode(PerkBehaviorPhase.Activation, "always");
        string effect = editor.AddNode(PerkBehaviorPhase.Effects, "property");
        editor.SetProperty(effect, "hash", 0x12345678);
        editor.SetProperty(effect, "value", 1.25);
        editor.SetProperty(effect, "operation", 3);
        Check(editor.Problems.Count == 0 && editor.Document.OriginalBytes.Length == 0,
            "A scratch graph required an installed effect.");
        byte[] compiled = editor.Compile();
        var decoded = NativePerkBehavior.Decompile(compiled);
        Check(decoded.Activation.Single().Primitive == "always" && decoded.Effects.Single().Primitive == "property"
            && decoded.Effects[0].Properties.Single(row => row.Key == "value").Value == 1.25,
            "The edited values did not survive native compilation.");
        editor.MarkSaved();
        editor.SetProperty(effect, "value", 1.5);
        Check(editor.IsDirty && editor.CanUndo, "A parameter change did not enter undo.");
        editor.Undo();
        Check(!editor.IsDirty && editor.Compile().SequenceEqual(compiled), "Undo did not restore the saved graph.");
        editor.Redo();
        Check(editor.IsDirty && Value(editor, effect, "value") == 1.5, "Redo did not restore the parameter.");
        byte[] before = editor.Compile();
        Reject(() => editor.SetProperty(effect, "operation", 4));
        Reject(() => editor.SetProperty(effect, "operation", .5));
        Reject(() => editor.SetProperty(effect, "hash", 0));
        Reject(() => editor.SetProperty(effect, "value", double.NaN));
        Reject(() => editor.AddNode(PerkBehaviorPhase.Activation, "property"));
        Reject(() => editor.AddNode(PerkBehaviorPhase.Effects, "timer"));
        Check(editor.Compile().SequenceEqual(before), "A rejected edit changed the graph.");
        string delayed = editor.AddNode(PerkBehaviorPhase.Activation, "timer");
        editor.SetProperty(delayed, "seconds", 2);
        editor.MoveNode(delayed, -1);
        Check(editor.Document.Activation[0].Id == delayed, "Earlier did not change native array order.");
        editor.RemoveNode(delayed);
        Check(editor.Document.Activation.Single().Id == condition, "Removing a node changed another node.");
        editor.Undo();
        Check(editor.Document.Activation.Count == 2, "Undo did not restore the removed node.");

        editor.Load(decoded);
        Check(!editor.IsDirty && !editor.CanUndo, "Opening another graph retained draft history.");
        var locked = NativePerkBehavior.Decompile(compiled);
        locked.Effects[0].Editable = false;
        editor.Load(locked);
        string lockedId = editor.Document.Effects[0].Id;
        Reject(() => editor.SetProperty(lockedId, "value", 3));
        Reject(() => editor.RemoveNode(lockedId));
        Reject(() => editor.MoveNode(lockedId, -1));
        Check(!editor.IsDirty, "A read-only edit changed the draft.");
        Console.WriteLine("perk_editor scratch=pass native_roundtrip=pass ranges=reject phases=reject undo=pass readonly=pass");
        Kinds();
        Copy();
    }

    /** A copied program reopens and recompiles to the same bytes; bytes Studio cannot read are refused. */
    private static void Copy()
    {
        var editor = new PerkBehaviorEditor();
        editor.AddNode(PerkBehaviorPhase.Activation, "always");
        string effect = editor.AddNode(PerkBehaviorPhase.Effects, "property");
        editor.SetProperty(effect, "hash", 0x12345678);
        editor.SetProperty(effect, "value", 1.25);
        editor.AddNode(PerkBehaviorPhase.Deactivation, "timer");
        byte[] source = editor.Compile();
        var graph = PerkBehaviorWorkflow.OpenProgram(source, 0x80A01234, out string problem);
        Check(graph is not null && problem.Length == 0, "A compiled program did not reopen: " + problem);
        var copy = new PerkBehaviorEditor();
        copy.Load(graph!);
        Check(copy.Problems.Count == 0 && copy.Compile().SequenceEqual(source), "An unchanged copy did not compile to the source bytes.");
        Check(PerkBehaviorWorkflow.OpenProgram(new byte[16], 0x80A01234, out problem) is null
            && problem == PerkBehaviorWorkflow.Unsupported, "Unreadable program bytes were accepted.");
        SelfReference();
        foreach (var mode in new[] { CreationMode.New, CreationMode.Clone })
        {
            var type = CreationTypes.For("Perks", mode);
            Check(type is { Available: true, Factory: not null } && type.Summary.Contains("No item uses it yet"),
                $"Perks {mode} is not an available workflow.");
        }
        Console.WriteLine("perk_editor copy_roundtrip=identical unsupported=refused perks_new_clone=available");
    }

    /** A copy's damage-resist self reference names the new entry, and the source tag is gone. */
    private static void SelfReference()
    {
        const uint sourceTag = 0x80A01234, newTag = 0x80A05678;
        var editor = new PerkBehaviorEditor();
        editor.AddNode(PerkBehaviorPhase.Activation, "always");
        editor.AddNode(PerkBehaviorPhase.Effects, "damage-resist");
        editor.Document.ProgramTag = sourceTag;
        byte[] source = editor.Compile();
        Check(Contains(source, sourceTag), "A damage-resist effect did not store its program's tag.");
        var graph = PerkBehaviorWorkflow.OpenProgram(source, sourceTag, out string problem);
        Check(graph is not null && problem.Length == 0, "A damage-resist program did not reopen: " + problem);
        var copy = new PerkBehaviorEditor();
        copy.Load(graph!);
        byte[] moved = PerkBehaviorWorkflow.CompileCopy(copy, newTag, sourceTag);
        Check(Contains(moved, newTag) && !Contains(moved, sourceTag), "A copy kept the source's self reference.");
        Check(copy.Document.ProgramTag == sourceTag, "Compiling a copy changed the editor's program tag.");
        Console.WriteLine("perk_editor self_reference=retargeted");
    }

    private static bool Contains(byte[] bytes, uint tag) => bytes.AsSpan().IndexOf(BitConverter.GetBytes(tag)) >= 0;

    /** Every writable kind can be added from the palette; rows, inner conditions and pickers keep their rules. */
    private static void Kinds()
    {
        var editor = new PerkBehaviorEditor();
        foreach (var primitive in NativePerkBehavior.Primitives.Where(row => row.CanCreate))
            editor.AddNode(primitive.Kind == PerkBehaviorNodeKind.Effect ? PerkBehaviorPhase.Effects : PerkBehaviorPhase.Activation,
                primitive.Key);
        Check(editor.Problems.Count != 0, "Unset picker values were ready to compile.");
        foreach (var node in editor.Document.Effects.Where(node => node.Primitive is "spawn" or "driven-spawn"))
            editor.SetProperty(node.Id, "definition", 0x80C197BA);
        editor.SetProperty(editor.Document.Effects.Single(node => node.Primitive == "transmat").Id, "key", 0xF8EFC9A9);
        editor.SetProperty(editor.Document.Effects.Single(node => node.Primitive == "property").Id, "hash", 0x12345678);
        foreach (var node in editor.Document.Activation.Where(node => node.Primitive is "event" or "state-event"))
            editor.SetProperty(node.Primitive == "event" ? node.Id : node.Children[0].Id, "event", 0xA6288DD1);
        // Table-driven kinds: installed-value pickers, labels and free name hashes need a value before compiling.
        string gatedPickup = editor.AddNode(PerkBehaviorPhase.Activation, "state-ammo-pickup");
        foreach (var node in editor.Document.Activation.Concat(editor.Document.Effects).ToArray())
            foreach (var property in node.Properties.Where(row => row.Picker.Length != 0 || row.Kind == PerkBehaviorValueKind.Label
                || row.Key is "flag" or "tag").ToArray())
                editor.SetProperty(node.Id, property.Key, property.Kind switch
                {
                    PerkBehaviorValueKind.Label => 303 * 4294967296.0 + 0x6DC29533,
                    PerkBehaviorValueKind.Tag => 0x80C197BA,
                    _ => property.Choices.Count != 0 && property.Key != "event" ? property.Choices[0].Value : 0x12345678
                });
        var groups = editor.Document.Activation.Single(node => node.Primitive == "all-groups");
        string group = editor.AddChild(groups.Id, "group");
        editor.SetProperty(group, "window", 2);
        editor.AddChild(group, "kill");
        Reject(() => editor.AddChild(groups.Id, "kill"));
        var extend = editor.Document.Effects.Single(node => node.Primitive == "extend-timers");
        string extendKill = editor.AddChild(extend.Id, "kill");
        Reject(() => editor.AddChild(extend.Id, "while-state"));
        Check(editor.Problems.Any(problem => problem.Contains("program's tag")), "A damage-resist effect compiled without a tag.");
        editor.Document.ProgramTag = 0x80BBC7D0;
        Check(editor.Document.Activation.Single(node => node.Id == gatedPickup).Children.Single().Primitive == "ammo-pickup",
            "A state-gated node lost its inner condition.");
        var counter = editor.Document.Activation.Single(node => node.Primitive == "counter");
        Reject(() => editor.AddChild(counter.Id, "always"));
        string first = editor.AddChild(counter.Id, "kill");
        string second = editor.AddChild(counter.Id, "state-damage");
        editor.SetProperty(second, "row_value", 3);
        editor.MoveNode(second, -1);
        Check(counter.Children[0].Id == second && counter.Children[1].Id == first, "Earlier did not reorder counter rows.");
        var gated = editor.Document.Activation.Single(node => node.Primitive == "state-kill");
        Reject(() => editor.RemoveNode(gated.Children[0].Id));
        Reject(() => editor.SetProperty(gated.Id, "property", 0x12345678));
        Check(editor.Problems.Count == 0, "A palette-built graph did not validate: " + string.Join("; ", editor.Problems));
        var decoded = NativePerkBehavior.Decompile(editor.Compile());
        Check(decoded.Activation.Select(node => node.Primitive).SequenceEqual(editor.Document.Activation.Select(node => node.Primitive))
            && decoded.Effects.Select(node => node.Primitive).SequenceEqual(editor.Document.Effects.Select(node => node.Primitive))
            && decoded.Activation.Single(node => node.Primitive == "counter").Children.Count == 2
            && decoded.Activation.Single(node => node.Primitive == "all-groups").Children.Single().Children.Single().Primitive == "kill"
            && decoded.Effects.Single(node => node.Primitive == "extend-timers").Children.Single().NativeBytes[7] == 0xFF
            && editor.Document.Effects.Single(node => node.Primitive == "extend-timers").Children.Single().Id == extendKill,
            "Palette nodes did not survive compilation.");

        // Installed rows keep their layout; their values stay editable.
        editor.Load(decoded);
        var installed = editor.Document.Activation.Single(node => node.Primitive == "counter");
        Reject(() => editor.AddChild(installed.Id, "kill"));
        Reject(() => editor.RemoveNode(installed.Children[0].Id));
        editor.SetProperty(installed.Children[0].Id, "row_value", 4);
        Check(editor.Problems.Count == 0, "An installed row value edit did not validate.");
        editor.RemoveNode(installed.Id);
        Check(editor.Document.Activation.All(node => node.Primitive != "counter"), "An installed counter could not be removed.");
        editor.Undo();

        // With a census loaded, picker values must come from the installed lists.
        var before = PerkBehaviorEditor.InstalledKinds;
        try
        {
            PerkBehaviorEditor.InstalledKinds = new PerkCensus([])
            {
                EventHashes = [new(0xA6288DD1, "event")], TransmatKeys = [new(0xF8EFC9A9, "key")],
                ObjectDefinitions = [new(0x80C197BA, "definition")]
            };
            var eventNode = editor.Document.Activation.Single(node => node.Primitive == "event");
            Reject(() => editor.SetProperty(eventNode.Id, "event", 0x12345678));
            editor.SetProperty(eventNode.Id, "event", 0xA6288DD1);
            var spawn = editor.Document.Effects.First(node => node.Primitive == "spawn");
            Reject(() => editor.SetProperty(spawn.Id, "definition", 0x80000001));
        }
        finally { PerkBehaviorEditor.InstalledKinds = before; }
        Labels();
        Hud();
        Console.WriteLine($"perk_editor kinds={NativePerkBehavior.Primitives.Count(row => row.CanCreate)} "
            + "rows=pass groups=pass timer_lists=pass inner=fixed installed_rows=fixed pickers=installed");
    }

    /** Label lists take only table labels and groups, survive undo, and compile to the native form. */
    private static void Labels()
    {
        var before = PerkLabelTable.Installed;
        try
        {
            byte[] groupMask = new byte[40];
            groupMask[0] = 0x06;
            PerkLabelTable.Installed = new PerkLabelTable([0x11111111, 0x22222222], [(0x33333333, groupMask)]);
            var editor = new PerkBehaviorEditor();
            editor.AddNode(PerkBehaviorPhase.Activation, "always");
            string kill = editor.AddNode(PerkBehaviorPhase.Activation, "kill");
            editor.AddNode(PerkBehaviorPhase.Effects, "perk-counter");
            Reject(() => editor.AddLabel(kill, "victim", 0, 0xDEADBEEF));
            Reject(() => editor.AddLabel(kill, "victim", 4, 0x11111111));
            editor.AddLabel(kill, "victim", 0, 0x11111111);
            editor.AddLabel(kill, "victim", 0, 0x33333333);
            Reject(() => editor.AddLabel(kill, "victim", 0, 0x33333333));
            editor.AddLabel(kill, "source", 1, 0x22222222);
            editor.RemoveLabel(kill, "source", 1, 0);
            editor.Undo();
            var node = editor.Document.Activation.Single(row => row.Id == kill);
            Check(node.LabelFilters[0].AnyOf.SequenceEqual([0x11111111u, 0x33333333u]) && node.LabelFilters[1].AllOf.Single() == 0x22222222,
                "Label list edits or undo went wrong.");
            var decoded = NativePerkBehavior.Decompile(editor.Compile()).Activation[1];
            Check(decoded.LabelFilters[0].AnyOf.SequenceEqual([0x11111111u, 0x33333333u]) && decoded.LabelFilters[1].AllOf.Single() == 0x22222222,
                "Label lists did not survive compilation.");
        }
        finally { PerkLabelTable.Installed = before; }
        Console.WriteLine("perk_editor labels=pass unknown=reject duplicate=reject undo=pass");
    }

    /** A saved HUD buff reopens with its settings, changes in place and can be removed. */
    private static void Hud()
    {
        Check(NativePerkBehavior.Primitives.Where(row => NativePerkBehavior.IsTested(row.Key)).Select(row => row.Key)
            .Order().SequenceEqual(["always", "ammo-drop", "timer"]), "The tested node kinds changed.");
        var editor = new PerkBehaviorEditor();
        editor.AddNode(PerkBehaviorPhase.Activation, "always");
        editor.AddNode(PerkBehaviorPhase.Effects, "perk-counter");
        editor.AddNode(PerkBehaviorPhase.Deactivation, "timer");
        uint label = (uint)NativePerkBehavior.HudLabels[0].Value;
        editor.SetHud(new() { Label = label, ShowTimer = true });
        byte[] saved = editor.Compile();
        editor.Load(NativePerkBehavior.Decompile(saved));
        Check(editor.Document.Hud is { ShowTimer: true } hud && hud.Label == label && editor.Problems.Count == 0
            && editor.Document.Diagnostics.Count == 0, "A saved HUD buff did not reopen.");
        Check(editor.Compile().SequenceEqual(saved), "An unchanged HUD buff did not round trip.");
        editor.SetHud(new() { Label = label, ShowTimer = false });
        byte[] changed = editor.Compile();
        Check(changed.Length == saved.Length && NativePerkBehavior.Decompile(changed).Hud is { ShowTimer: false },
            "A HUD change did not rewrite its row in place.");
        editor.SetHud(null);
        var removed = NativePerkBehavior.Decompile(editor.Compile());
        Check(removed.Hud is null && removed.Diagnostics.Count == 0, "A removed HUD buff stayed.");
        Console.WriteLine("perk_editor hud=reopen change=in_place remove=pass tested_kinds=3");
    }

    private static double Value(PerkBehaviorEditor editor, string id, string key) => editor.Document.Effects
        .Single(node => node.Id == id).Properties.Single(property => property.Key == key).Value;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("The editor accepted an unsupported draft edit.");
    }
}

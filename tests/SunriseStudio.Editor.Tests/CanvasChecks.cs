using System.Numerics;
using System.Reflection;
using ImGuiNET;
using SunriseStudio.Editor;
using SunriseStudio.Missions;
using SunriseStudio.Viewport;

internal static class CanvasChecks
{
    public static unsafe void Run(string root)
    {
        var layout = new MissionLayoutStore(Path.Combine(root, "graph-layouts"));
        string script = Path.Combine(root, "test.lua");
        layout.Save(script, new Dictionary<string, Vector2> { ["node"] = new(14, 32) });
        string layoutFile = Directory.GetFiles(Path.Combine(root, "graph-layouts")).Single();
        foreach (string broken in new[] { "{\"Positions\":null}", "{\"Positions\":{\"node\":null}}", "{\"Positions\":{\"node\":[1]}}" })
        {
            File.WriteAllText(layoutFile, broken);
            Check(layout.Load(script).Count == 0, "A damaged saved graph layout did not recover to automatic layout.");
        }
        var context = ImGui.CreateContext();
        var oldMap = ShortcutInput.Map;
        bool oldSuspended = ShortcutInput.Suspended;
        try
        {
            var io = ImGui.GetIO(); io.NativePtr->IniFilename = null;
            io.DisplaySize = new(1200, 800); io.DeltaTime = 1f / 60;
            io.Fonts.AddFontDefault(); io.Fonts.GetTexDataAsRGBA32(out IntPtr _, out int _, out int _);
            ImGui.GetStyle().WindowPadding = Vector2.Zero;
            ShortcutInput.Map = new(); ShortcutInput.Suspended = false;
            var canvas = new NodeCanvas();
            CanvasNode[] nodes = [new("a", "A", ""), new("b", "B", ""), new("c", "C", "")];
            List<CanvasEdge> edges = [new("a", "b", true), new("b", "c", true)];
            string selected = ""; int connected = 0, disconnected = 0, reconnected = 0;
            var dragSource = MissionGraphDocument.Parse("local flow = require('lib.flow')\nreturn flow.new{key='drag',steps={"
                + "{id='a'}, {id='b',after={'a'}}, {id='c',after={'b'}}}}\n");
            var dragKeys = dragSource.Groups.Single(group => group.Collection == "steps").Nodes
                .ToDictionary(node => node.Id, node => node.Key);
            const BindingFlags access = BindingFlags.Instance | BindingFlags.NonPublic;
            var owner = new MissionGraphPage();
            typeof(MissionGraphPage).GetField("graph", access)!.SetValue(owner, dragSource);
            ((Solforge.Graphs.GraphSourceSync)typeof(MissionGraphPage).GetField("source", access)!.GetValue(owner)!).Reset(dragSource.Source);
            Vector2 origin = default, region = default;
            var actions = new CanvasActions
            {
                Connect = (source, target) =>
                {
                    Func<MissionGraphDocument, MissionGraphDocument> change = document => document.Connect(dragKeys[source], dragKeys[target]);
                    Check((bool)typeof(MissionGraphPage).GetMethod("Change", access)!.Invoke(owner, [change])!, "Completed connection was refused.");
                    connected++;
                },
                Disconnect = (_, _) => disconnected++,
                Reconnect = (_, _, _) => reconnected++
            };
            void Frame(Vector2 mouse, bool? down = null, int button = 0, bool active = true)
            {
                io.AddMousePosEvent(mouse.X, mouse.Y);
                if (down is { } held) io.AddMouseButtonEvent(button, held);
                ImGui.NewFrame(); ShortcutInput.BeginFrame(active);
                ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(io.DisplaySize);
                ImGui.Begin("canvas-check", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove);
                origin = ImGui.GetCursorScreenPos(); region = ImGui.GetContentRegionAvail();
                canvas.Draw("fixture", nodes, edges, true, ref selected, actions);
                ImGui.End(); ImGui.Render();
            }
            Frame(new(-100)); Frame(new(-100));
            var minimum = canvas.Positions.Values.Aggregate(Vector2.Min);
            var maximum = canvas.Positions.Values.Aggregate(Vector2.Max) + new Vector2(230, 48);
            var pan = (region - (maximum - minimum)) / 2 - minimum;
            Vector2 Node(string key, Vector2 offset) => origin + pan + canvas.Positions[key] + offset;
            var start = Node("a", new(70, 24)); var before = canvas.Positions["a"];
            Frame(start); Frame(start, true);
            var moved = start + new Vector2(300, -140);
            Frame(moved);
            Check(selected == "a" && Vector2.Distance(canvas.Positions["a"], before + new Vector2(300, -140)) < .01f,
                "A fast drag dropped its node when the pointer left the old rectangle.");
            Frame(moved, false);
            var output = Node("a", new(230, 24)); var input = Node("c", new(0, 24));
            Frame(output); Frame(output, true); Frame(input); Frame(input, false);
            Check(connected == 1, "The moved node's output port did not follow its rectangle.");
            string linked = owner.Document!.Source;
            Check(owner.CanUndo && owner.Document.Edges.Count == 3, "A completed link did not create its model edit.");
            owner.Undo();
            Check(owner.Document!.Source == dragSource.Source && !owner.CanUndo, "One link gesture created more than one undo step.");
            owner.Redo();
            Check(owner.Document!.Source == linked, "Link redo did not restore its model edit.");
            input = Node("b", new(0, 24));
            Frame(input); Frame(input, true); Frame(new(50, 50)); Frame(new(50, 50), false);
            Check(disconnected == 1, "Dropping a detached link on empty canvas did not disconnect it.");
            Frame(input); Frame(input, true); Frame(Node("c", new(0, 24))); Frame(Node("c", new(0, 24)), false);
            Check(reconnected == 1 && disconnected == 1 && connected == 1,
                "Moving a link published a separate disconnect before reconnecting.");
            foreach (bool capture in new[] { false, true })
            {
                Frame(input); Frame(input, true);
                ShortcutInput.Suspended = capture;
                Frame(new(50, 50), active: capture); Frame(new(50, 50), false, active: capture);
                ShortcutInput.Suspended = false; Frame(new(50, 50));
                Check(reconnected == 1 && disconnected == 1 && connected == 1,
                    "Blur or shortcut capture committed an unfinished link gesture.");
                Check(owner.Document!.Source == linked, "Cancellation changed the mission model.");
                var nodePoint = Node("b", new(70, 24));
                Frame(nodePoint); Frame(nodePoint, true);
                var position = canvas.Positions["b"];
                ShortcutInput.Suspended = capture;
                Frame(nodePoint + new Vector2(200), active: capture);
                Frame(nodePoint + new Vector2(200), false, active: capture);
                ShortcutInput.Suspended = false; Frame(nodePoint);
                Check(canvas.Positions["b"] == position, "Blur or capture moved a retained node drag.");
            }

            var source = MissionGraphDocument.Parse("local flow = require('lib.flow')\nreturn flow.new{key='test',steps={"
                + "{id='a'}, {id='b',after={'a'}}, {id='c',after={'b'}}}}\n");
            var group = source.Groups.Single(value => value.Collection == "steps");
            string a = group.Nodes[0].Key, b = group.Nodes[1].Key, c = group.Nodes[2].Key;
            var document = source;
            try { document = document.Connect(b, c, false).Connect(b, a); }
            catch (InvalidDataException) { }
            Check(ReferenceEquals(source, document) && document.Edges.Count == 2,
                "An invalid reconnect discarded the original graph dependency.");
            Console.WriteLine("mission_canvas fast_drag=pass moved_ports=pass detach=pass reconnect=atomic blur=cancelled capture=cancelled");
        }
        finally { ShortcutInput.Map = oldMap; ShortcutInput.Suspended = oldSuspended; ImGui.DestroyContext(context); }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}

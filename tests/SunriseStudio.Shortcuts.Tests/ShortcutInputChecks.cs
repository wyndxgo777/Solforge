using System.Numerics;
using System.Reflection;
using ImGuiNET;
using SunriseStudio.Editor;
using SunriseStudio.Rendering;
using SunriseStudio.Viewport;

/** Checks shortcut input routing: inactive windows and mouse input must not trigger shortcuts or canvas actions. */
internal static class ShortcutInputChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /** Queued inputs must not cross inactivity, capture or an externally controlled camera. */
    public static unsafe void Run()
    {
        var oldMap = ShortcutInput.Map; bool oldSuspended = ShortcutInput.Suspended;
        nint context = ImGui.CreateContext();
        string root = Path.Combine(Path.GetTempPath(), "sunrise-shortcut-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var io = ImGui.GetIO(); io.NativePtr->IniFilename = null;
            io.DisplaySize = new(800, 600); io.DeltaTime = 1f / 60;
            io.ConfigInputTrickleEventQueue = false;
            io.Fonts.AddFontDefault(); io.Fonts.GetTexDataAsRGBA32(out IntPtr _, out int _, out int _);
            ShortcutInput.Map = new(); ShortcutInput.Suspended = false;
            Frame(() => ShortcutInput.Hover(ShortcutSection.View2D));
            io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.S, true);
            io.AddMouseButtonEvent(2, true); io.AddMousePosEvent(100, 100); io.AddMouseWheelEvent(0, 1);
            Frame(() =>
            {
                Check(!ShortcutInput.Pressed(ShortcutActions.SaveProject) && ShortcutInput.Canvas(true) == default
                    && ShortcutInput.Mouse(ShortcutSection.View3D, ShortcutButton.Middle).Count == 0
                    && ShortcutInput.Wheel(ShortcutSection.View3D, ShortcutActions.Zoom3D) == 0,
                    "Inactive input reached a shortcut or canvas.");
                Check(ShortcutInput.Capture(ShortcutActions.Find(ShortcutActions.SaveProject)!, out bool cancelled) is null && !cancelled,
                    "An inactive window captured a shortcut.");
            }, false);
            Release(); Frame(() => { });
            ShortcutInput.Suspended = true;
            io.AddMouseButtonEvent(0, true); io.AddMousePosEvent(150, 150); io.AddMouseWheelEvent(0, 1);
            Frame(() =>
            {
                Check(ShortcutInput.Mouse(ShortcutSection.View3D, ShortcutButton.Right).Count == 0
                    && !ShortcutInput.Clicked(ShortcutSection.View3D, ShortcutActions.Select3D, out _)
                    && ShortcutInput.Canvas(true) == default, "Shortcut capture allowed mouse input through.");
            });
            ShortcutInput.Suspended = false; Release(); Frame(() => { });
            io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.S, true);
            Frame(() =>
            {
                Check(ShortcutInput.Pressed(ShortcutActions.SaveProject), "Active Ctrl+S failed.");
                io.WantTextInput = true;
                Check(!ShortcutInput.Pressed(ShortcutActions.SaveProject), "A focused text field received a menu shortcut.");
                io.WantTextInput = false;
                SaveMission(root);
            });
            Release(); Frame(() => { });
            CuiCancellationChecks();
            CameraChecks();
            ControllerInactiveChecks();
            Console.WriteLine("shortcut_input inactive=blocked capture=blocked text=blocked mission_save=pass follow=preserved");

            void Frame(Action check, bool active = true)
            { ImGui.NewFrame(); ShortcutInput.BeginFrame(active); check(); ImGui.Render(); }
            void Release()
            {
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.S, false);
                for (int button = 0; button < 3; button++) io.AddMouseButtonEvent(button, false);
            }
        }
        finally
        {
            ShortcutInput.Map = oldMap; ShortcutInput.Suspended = oldSuspended;
            ImGui.DestroyContext(context);
            Directory.Delete(root, true);
        }
    }

    /** Ctrl+S must use the same admission as File > Save when no project is open. */
    private static void SaveMission(string root)
    {
        string folder = Path.Combine(root, "bin", "x64", "Sunrise", "scripts", "shortcut");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "shortcut.lua"), before = "return {name='before'}\n", after = "return {name='after'}\n";
        File.WriteAllText(path, before);
        using var form = new StudioForm(new(Smoke: true, SettingsPath: Path.Combine(root, "settings.json")));
        var page = (MissionGraphPage)typeof(StudioForm).GetField("missionGraph", Private)!.GetValue(form)!;
        page.OpenScript(path, root);
        ((Solforge.Graphs.GraphSourceSync)typeof(MissionGraphPage).GetField("source", Private)!.GetValue(page)!).Edit(after, double.MaxValue);
        typeof(StudioForm).GetField("missionGraphActive", Private)!.SetValue(form, true);
        Check(form.Workspace.Project is null && page.CanSave, "Mission save fixture was not editable without a project.");
        typeof(StudioForm).GetMethod("HandleShortcuts", Private)!.Invoke(form, null);
        Check(File.ReadAllText(path) == after && !page.IsDirty, "Ctrl+S did not save the installed-script fixture.");
    }

    /** Follow mode may accept selection, but manual camera gestures must not replace its pose. */
    private static void CameraChecks()
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, 64, 64, true);
        using var viewport = new AssetViewport(renderer);
        var input = typeof(AssetViewport).GetMethod("ProcessInput", Private)!;
        var io = ImGui.GetIO();
        void Draw() => input.Invoke(viewport, [true, Vector2.Zero, new Vector2(800, 600)]);
        void Frame() { ImGui.NewFrame(); ShortcutInput.BeginFrame(); Draw(); ImGui.Render(); }
        viewport.Camera.SetExternalPose(new(5, 7, 9), Vector3.UnitX, Vector3.UnitZ, 1);
        var view = viewport.Camera.ViewProjection(4f / 3);
        io.AddMouseWheelEvent(0, 2); io.AddMouseButtonEvent(1, true); io.AddMousePosEvent(100, 100); Frame();
        io.AddMousePosEvent(200, 160); Frame();
        Check(viewport.Camera.ViewProjection(4f / 3) == view, "First-person follow accepted camera navigation.");
        io.AddMouseButtonEvent(1, false); Frame(); viewport.Camera.ClearExternalPose();
        viewport.Camera.PivotFollowed = true;
        var pivot = viewport.Camera.Pivot; float distance = viewport.Camera.Distance;
        io.AddMouseButtonEvent(2, true); Frame(); io.AddMousePosEvent(260, 190); Frame();
        viewport.FocusSelection(); viewport.SetCursorToSelection(); viewport.Frame();
        Check(viewport.Camera.Pivot == pivot, "Player-cursor follow accepted a manual pivot move.");
        io.AddMouseButtonEvent(2, false); io.AddMouseWheelEvent(0, 1); Frame();
        Check(viewport.Camera.Distance != distance && viewport.Camera.Pivot == pivot, "Player-cursor follow lost permitted zoom.");
        viewport.Camera.PivotFollowed = false;
        io.AddMouseButtonEvent(1, true); Frame(); io.AddMousePosEvent(300, 220); Frame();
        float yaw = viewport.Camera.Yaw;
        ShortcutInput.Suspended = true; io.AddMousePosEvent(340, 250); Frame();
        Check(viewport.Camera.Yaw == yaw, "A retained drag crossed shortcut capture.");
        ShortcutInput.Suspended = false; io.AddMouseButtonEvent(1, false); Frame();
    }

    /** Losing input ownership must not turn a mouse-up into a stored CUI edit. */
    private static void CuiCancellationChecks()
    {
        using var workspace = new StudioWorkspace();
        using var page = new CuiPage();
        var node = new SunriseStudio.Formats.CuiNode(1, 0, (uint)SunriseStudio.Formats.CuiTypeRegistry.TextType, 0, "Fixture", 0);
        var component = new SunriseStudio.Formats.CuiComponent(1, 0, []);
        var before = new Vector4(10, 20, 100, 50);
        var rectangle = new SunriseStudio.Formats.CuiLayoutRectangle(node, component, before, []);
        Set("rectangles", new[] { rectangle }); Set("selected", 0); Set("fit", false);
        var io = ImGui.GetIO();
        foreach (bool capture in new[] { false, true })
        {
            io.AddMouseButtonEvent(0, true);
            ImGui.NewFrame(); ShortcutInput.BeginFrame(); ImGui.Render();
            Set("edited", before with { X = 120 }); Set("dragRectangle", before); Set("dragging", true);
            ShortcutInput.Suspended = capture; io.AddMouseButtonEvent(0, false);
            ImGui.NewFrame(); ShortcutInput.BeginFrame(capture);
            ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(500, 400)); ImGui.Begin("cui-cancel");
            typeof(CuiPage).GetMethod("DrawCanvas", Private)!.Invoke(page, [new StudioPageContext(workspace, null!, _ => { })]);
            ImGui.End(); ImGui.Render();
            Check((Vector4)typeof(CuiPage).GetField("edited", Private)!.GetValue(page)! == before
                && !(bool)typeof(CuiPage).GetField("dragging", Private)!.GetValue(page)!
                && workspace.Error.Length == 0, "Inactive CUI drag was retained or published.");
            ShortcutInput.Suspended = false;
        }
        void Set(string name, object value) => typeof(CuiPage).GetField(name, Private)!.SetValue(page, value);
    }

    /** Inactive window events must not enter ImGui's key, character, button or wheel queues. */
    private static void ControllerInactiveChecks()
    {
        nint previous = ImGui.GetCurrentContext();
        try
        {
            using var window = new Form();
            using var renderer = new Dx11Renderer(IntPtr.Zero, 64, 64, true);
            using var controller = new ImGuiController(renderer, window);
            Check(!controller.IsInputActive, "Hidden input fixture unexpectedly owns focus.");
            typeof(ImGuiController).GetMethod("SendKey", Private)!.Invoke(controller, [Keys.F, true]);
            typeof(ImGuiController).GetMethod("SendButton", Private)!.Invoke(controller, [MouseButtons.Right, true]);
            typeof(ImGuiController).GetMethod("OnKeyPress", Private)!.Invoke(controller, [window, new KeyPressEventArgs('x')]);
            typeof(ImGuiController).GetMethod("OnMouseWheel", Private)!.Invoke(controller,
                [window, new MouseEventArgs(MouseButtons.None, 0, 0, 0, 120)]);
            controller.NewFrame(1f / 60); ShortcutInput.BeginFrame(controller.IsInputActive);
            Check(!ImGui.IsKeyDown(ImGuiKey.F) && !ImGui.IsMouseDown(ImGuiMouseButton.Right)
                && ImGui.GetIO().MouseWheel == 0 && ImGui.GetIO().InputQueueCharacters.Size == 0,
                "Inactive window events reached ImGui.");
            controller.Render();
        }
        finally { ImGui.SetCurrentContext(previous); }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

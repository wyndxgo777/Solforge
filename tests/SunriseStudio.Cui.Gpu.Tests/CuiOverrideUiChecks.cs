using System.Numerics;
using System.Reflection;
using ImGuiNET;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Viewport;

/** Checks the CUI override panel: new-property drafts persist across same-selection frames and per-profile selection, and read-only drawing changes nothing. */
internal static class CuiOverrideUiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /** Shared native offsets still belong to distinct profile selections and distinct UI drafts. */
    public static void Run()
    {
        using var window = new Form { ClientSize = new Size(640, 360) };
        using var renderer = new Dx11Renderer(IntPtr.Zero, 640, 360, true);
        using var gui = new ImGuiController(renderer, window);
        using var viewport = new AssetViewport(renderer);
        using var workspace = new StudioWorkspace();
        using var page = new CuiPage();
        var context = new StudioPageContext(workspace, viewport, _ => { });
        var node = new CuiNode(1, 0, (uint)CuiTypeRegistry.TextType, 0, "Shared text", 344);
        var one = new CuiComponent(1, 400, new List<CuiProperty>());
        var two = new CuiComponent(1, 400, new List<CuiProperty>());
        var document = new CuiDocument { TagHash = 1, ClassHash = 0x80804825, Nodes = [node],
            Objects = [new(0, 360, [one]), new(1, 424, [two])] };
        Set("document", document);
        var first = new CuiLayoutRectangle(node, one, new(0, 0, 360, 240), []);
        var second = new CuiLayoutRectangle(node, two, first.Rectangle, []);
        Frame(first);
        var alignment = CuiVisualProperties.Missing(node, one).Single(choice => choice.Kind == CuiVisualPropertyKind.Alignment);
        Set("addingOverride", alignment); Set("overrideValue", 2u);
        Frame(first);
        Check(Get("addingOverride") is not null && (uint)Get("overrideValue")! == 2, "A same-selection frame discarded the new-property draft.");
        Check(!workspace.CanEdit && workspace.Project is null && workspace.Document is null, "Read-only drawing changed workspace ownership.");
        Frame(second);
        Check(Get("addingOverride") is null, "Shared-offset profile selection retained another profile's new-property draft.");
        Set("addingOverride", alignment);
        typeof(CuiPage).GetMethod("ResetContent", Private)!.Invoke(page, null);
        Check(Get("addingOverride") is null, "Reset content retained an uncommitted override.");
        Set("rectangles", new CuiLayoutRectangle[] { first, second }); Set("selected", 1);
        var reloadOne = new CuiComponent(1, 400, new List<CuiProperty>());
        var reloadTwo = new CuiComponent(1, 400, new List<CuiProperty>());
        var reload = Asset(reloadOne, reloadTwo);
        page.Prepare(reload, renderer);
        Check((int)Get("selected")! == 1, "Reload selected the first shared-offset profile.");
        page.Prepare(Asset(reloadOne, new(1, 600, new List<CuiProperty>())), renderer);
        Check((int)Get("selected")! == 1, "Detaching a shared array lost the selected profile.");
        Set("addingOverride", alignment);
        page.Prepare(null, renderer);
        Check(Get("addingOverride") is null, "Navigation retained an uncommitted override.");
        Console.WriteLine("cui_override_ui read_only=pass same_selection=retained shared_profile=draft_cleared reload=retained detached_reload=retained reset=pass navigation=pass");

        void Frame(CuiLayoutRectangle rectangle)
        {
            renderer.BeginFrame(new(0, 0, 0, 1)); gui.NewFrame(1f / 60);
            ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(640, 360));
            ImGui.Begin("CUI override controls", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
            typeof(CuiPage).GetMethod("DrawVisualProperties", Private)!.Invoke(page, [context, rectangle, new CuiWidgetContent()]);
            ImGui.End(); gui.Render();
        }
        object? Get(string name) => typeof(CuiPage).GetField(name, Private)!.GetValue(page);
        void Set(string name, object? value) => typeof(CuiPage).GetField(name, Private)!.SetValue(page, value);
        StudioDocument Asset(CuiComponent a, CuiComponent b)
        {
            var parsed = new CuiDocument { TagHash = 1, ClassHash = 0x80804825, Nodes = [node],
                Objects = [new(0, 360, [a]), new(1, 424, [b])] };
            var preview = new CuiPreviewData();
            typeof(CuiPreviewData).GetProperty(nameof(CuiPreviewData.Document))!.SetValue(preview, parsed);
            typeof(CuiPreviewData).GetProperty(nameof(CuiPreviewData.Layouts))!.SetValue(preview,
                new CuiLayoutRectangle[] { new(node, a, first.Rectangle, []), new(node, b, first.Rectangle, []) });
            return new(new PackageEntry(1, 0x80804825, 8, 0, 344, "fixture", 0, 0), "CUI fixture", new byte[344],
                new("CUI", "CUI fixture", [], [], []), null, null, "", Cui: preview);
        }
    }

    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}

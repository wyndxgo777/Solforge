using System.Text.Json;
using ImGuiNET;
using SunriseStudio.Viewport;
using static SunriseStudio.Viewport.ShortcutActions;
using static SunriseStudio.Viewport.ShortcutButton;
using static SunriseStudio.Viewport.ShortcutModifiers;

var checks = new List<string>();
try
{
    PresetCoverage();
    checks.Add("every preset lists every action once, bound or unbound, with accepted kinds and at most two bindings");
    SunriseDefaults();
    checks.Add("the Sunrise Studio preset keeps the bindings and modifier behavior Studio had before presets");
    PresetConventions();
    checks.Add("Blender, Unity, Maya and Unreal keep their navigation and edit conventions");
    Conflicts();
    checks.Add("conflicts are marked within a section and between menus and views, and never dropped");
    RoundTrip();
    checks.Add("preset and overrides survive save and load, including inside a larger settings file");
    DamagedFiles();
    checks.Add("damaged files and entries fall back to the preset without throwing");
    SettingsFile();
    checks.Add("shortcuts save with the local preferences, and a damaged section keeps the other preferences");
    BindingText();
    checks.Add("every key and gesture round trips through its text form; malformed text is refused");
    HelpPage();
    checks.Add("Help > Keybinds & Tips lists every action in a known area with the map's keys, F1 opens it in every preset, and search filters");
    ShortcutInputChecks.Run();
    checks.Add("inactive and captured input is suppressed; mission save needs no project; follow mode keeps its camera");
    foreach (string check in checks) Console.WriteLine("PASS " + check);
    Console.WriteLine($"PASS checks={checks.Count}");
}
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }

static void HelpPage()
{
    foreach (var preset in ShortcutPresets.All)
    {
        var map = new ShortcutMap();
        map.UsePreset(preset.Name);
        var keys = HelpCatalog.Keys(map);
        Require(keys.Count(key => key.Rebindable) == ShortcutActions.All.Count, $"{preset.Name}: Help misses actions.");
        Require(keys.All(key => HelpCatalog.Areas.Contains(key.Area)), $"{preset.Name}: an action has no Help area.");
        Require(keys.Single(key => key.Id == OpenHelp).Keys.Contains("F1", StringComparison.Ordinal), $"{preset.Name}: F1 does not open Help.");
    }
    var sunrise = new ShortcutMap();
    Require(HelpCatalog.Search(sunrise, "help").Any(key => key.Id == OpenHelp), "Search does not find the Help action.");
    Require(HelpCatalog.Search(sunrise, null, HelpCatalog.Viewport).All(key => key.Area == HelpCatalog.Viewport), "The area filter leaks.");
    Require(HelpCatalog.AreaOf(ShortcutActions.All.Single(action => action.Id == OpenAiWorkspace)) == HelpCatalog.Ai, "AI workspace is not in AI / MCP.");
    Require(HelpCatalog.Search(sunrise, "terminal copy").Any(key => key.Keys == "Ctrl+Shift+C" && key.Area == HelpCatalog.Ai && !key.Rebindable)
        && HelpCatalog.Search(sunrise, "terminal paste").Any(key => key.Keys.Contains("Ctrl+Shift+V", StringComparison.Ordinal))
        && HelpCatalog.Search(sunrise, "terminal stop").Any(key => key.Keys.Contains("Ctrl+C twice", StringComparison.Ordinal))
        && HelpCatalog.Search(sunrise, "terminal menu").Any(key => key.Keys == "Right-click"), "F1 Help misses the AI terminal's keys.");
    Require(HelpCatalog.Keys(sunrise).GroupBy(key => key.Id).All(group => group.Count() == 1), "Two Help rows share an id.");
    Require(HelpCatalog.SearchTips("mcp").Count >= 3 && HelpCatalog.Tips.All(tip => tip.Text.Length > 0), "Tips are missing MCP setup.");
}

static void PresetCoverage()
{
    var problems = ShortcutPresets.Problems();
    Require(problems.Count == 0, "Preset tables: " + string.Join("; ", problems));
    Require(ShortcutPresets.All.Select(preset => preset.Name).SequenceEqual(
        [ShortcutPresets.SunriseStudio, ShortcutPresets.Blender, ShortcutPresets.Unity, ShortcutPresets.Maya, ShortcutPresets.Unreal]),
        "Preset names or order changed.");
    Require(ShortcutPresets.Default.Name == ShortcutPresets.SunriseStudio, "Sunrise Studio is not the default preset.");
    var ids = ShortcutActions.All.Select(action => action.Id).ToHashSet(StringComparer.Ordinal);
    Require(ids.Count == ShortcutActions.All.Count, "Two actions share an id.");
    foreach (var action in ShortcutActions.All)
    {
        string prefix = action.Section switch { ShortcutSection.Menus => "menu.", ShortcutSection.View2D => "2d.", _ => "3d." };
        Require(action.Id.StartsWith(prefix, StringComparison.Ordinal) && action.Accepts.Count > 0,
            $"{action.Id} has the wrong section prefix or accepts nothing.");
        // A menu action may take a click, such as a mouse side button, but never a drag or the wheel.
        Require(action.Section != ShortcutSection.Menus || action.Accepts.Contains(ShortcutKind.Key)
            && !action.Accepts.Contains(ShortcutKind.Drag) && !action.Accepts.Contains(ShortcutKind.Wheel),
            $"Menu action {action.Id} accepts a drag or the wheel.");
    }
    foreach (var preset in ShortcutPresets.All)
    {
        Require(preset.Bindings.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(ids), $"{preset.Name} does not list exactly the catalog.");
        foreach (var action in ShortcutActions.All)
        {
            var bindings = preset.For(action.Id);
            Require(bindings.Count <= ShortcutPresets.MaximumBindings && bindings.All(binding => action.Allows(binding.Kind))
                && bindings.Distinct().Count() == bindings.Count, $"{preset.Name} {action.Id} has a refused or repeated binding.");
        }
    }
}

/** Studio's bindings before presets existed, including how extra modifiers behaved. */
static void SunriseDefaults()
{
    var map = new ShortcutMap();
    Require(map.Title == ShortcutPresets.SunriseStudio && !map.IsCustom, "A new map is not the Sunrise Studio preset.");
    var expected = new Dictionary<string, ShortcutBinding[]>
    {
        [NewProject] = [Key("N", Ctrl)],
        [OpenProject] = [Key("O", Ctrl)],
        [SaveProject] = [Key("S", Ctrl)],
        [Undo] = [Key("Z", Ctrl)],
        [Redo] = [Key("Y", Ctrl)],
        [BuildPackages] = [Key("B", Ctrl | Shift)],
        [NavigateBack] = [ShortcutBinding.Click(Back), Key("Left Arrow", Alt)],
        [NavigateForward] = [ShortcutBinding.Click(Forward), Key("Right Arrow", Alt)],
        [Pan2D] = [ShortcutBinding.Drag(Middle), ShortcutBinding.Drag(Right)],
        [Zoom2D] = [ShortcutBinding.Wheel()],
        [FrameSelection2D] = [Key("F"), Key("Numpad .")],
        [Select2D] = [ShortcutBinding.Click(Left)],
        [Orbit3D] = [ShortcutBinding.Drag(Right), ShortcutBinding.Drag(Left, Alt)],
        [Pan3D] = [ShortcutBinding.Drag(Middle), ShortcutBinding.Drag(Right, Shift)],
        [Zoom3D] = [ShortcutBinding.Wheel()],
        [FrameSelection3D] = [Key("F"), Key("Numpad .")],
        [Select3D] = [ShortcutBinding.Click(Left)],
        // Phase 03B: UE5 tool keys and the Asset browser drawer.
        [ToggleBrowser] = [Key("Space", Ctrl)],
        [SelectTool3D] = [Key("Q")],
        [MoveTool3D] = [Key("W")],
        [RotateTool3D] = [Key("E")],
        [ScaleTool3D] = [Key("R")],
        // Phase 47B: the AI workspace.
        [OpenAiWorkspace] = [Key("A", Ctrl | Shift)],
        // The 3D viewport render switch and the Settings window.
        [ToggleViewportRendering] = [Key("R", Ctrl | Shift)],
        [OpenSettings] = [Key(",", Ctrl)]
    };
    foreach (var action in ShortcutActions.All)
        Same(map.Bindings(action.Id), expected.GetValueOrDefault(action.Id) ?? [], $"Sunrise Studio {action.Id}");

    Resolves(map.ResolveMouse(ShortcutSection.View3D, Right, None), "3D right drag", Orbit3D);
    Resolves(map.ResolveMouse(ShortcutSection.View3D, Right, Shift), "3D shift right drag", Pan3D);
    Resolves(map.ResolveMouse(ShortcutSection.View3D, Right, Ctrl), "3D ctrl right drag", Orbit3D);
    Resolves(map.ResolveMouse(ShortcutSection.View3D, Middle, None), "3D middle drag", Pan3D);
    Resolves(map.ResolveMouse(ShortcutSection.View3D, Middle, Ctrl | Alt), "3D middle drag with modifiers", Pan3D);
    Resolves(map.ResolveMouse(ShortcutSection.View3D, Left, Alt), "3D alt left press", Orbit3D);
    Resolves(map.ResolveMouse(ShortcutSection.View3D, Left, None), "3D left press", Select3D);
    Resolves(map.ResolveMouse(ShortcutSection.View3D, Left, Ctrl), "3D ctrl left press", Select3D);
    Resolves(map.ResolveWheel(ShortcutSection.View3D, Ctrl), "3D ctrl wheel", Zoom3D);
    Resolves(map.ResolveMouse(ShortcutSection.View2D, Right, None), "2D right drag", Pan2D);
    Resolves(map.ResolveMouse(ShortcutSection.View2D, Middle, None), "2D middle drag", Pan2D);
    Resolves(map.ResolveMouse(ShortcutSection.View2D, Left, None), "2D left press", Select2D);
    Resolves(map.ResolveWheel(ShortcutSection.View2D, None), "2D wheel", Zoom2D);

    Resolves(map.ResolveMenuClick(Back, None), "Back click", NavigateBack);
    Resolves(map.ResolveMenuClick(Forward, Ctrl), "Ctrl+Forward click", NavigateForward);
    Resolves(map.ResolveMenuClick(Left, None), "Left click outside a view");
    Resolves(map.ResolveKey("Left Arrow", Alt, null), "Alt+Left Arrow", NavigateBack);
    Resolves(map.ResolveKey("Z", Ctrl, null), "Ctrl+Z", Undo);
    Resolves(map.ResolveKey("Z", Ctrl | Shift, null), "Ctrl+Shift+Z", Undo);
    Resolves(map.ResolveKey("S", Ctrl | Shift, null), "Ctrl+Shift+S", SaveProject);
    Resolves(map.ResolveKey("Z", None, null), "Z alone");
    Resolves(map.ResolveKey("R", Ctrl | Shift, ShortcutSection.View3D), "Ctrl+Shift+R over the 3D view", ToggleViewportRendering);
    Resolves(map.ResolveKey("R", None, ShortcutSection.View3D), "R over the 3D view", ScaleTool3D);
    Resolves(map.ResolveKey(",", Ctrl, null), "Ctrl+,", OpenSettings);
    Resolves(map.ResolveKey("B", Ctrl, null), "Ctrl+B");
    Resolves(map.ResolveKey("B", Ctrl | Shift, null), "Ctrl+Shift+B", BuildPackages);
    Resolves(map.ResolveKey("F", None, ShortcutSection.View3D), "F over the 3D view", FrameSelection3D);
    Resolves(map.ResolveKey("F", Ctrl, ShortcutSection.View3D), "Ctrl+F over the 3D view", FrameSelection3D);
    Resolves(map.ResolveKey("Numpad .", None, ShortcutSection.View3D), "Numpad . over the 3D view", FrameSelection3D);
    Resolves(map.ResolveKey("F", None, ShortcutSection.View2D), "F over a 2D view", FrameSelection2D);
    Resolves(map.ResolveKey("F", None, null), "F over no view");
    Require(map.Text(Orbit3D) == "Right drag or Alt+Left drag" && map.Text(BuildPackages) == "Ctrl+Shift+B"
        && map.Text(BuildAndInstall).Length == 0, "Menu and tooltip text changed.");
}

static void PresetConventions()
{
    var map = new ShortcutMap();
    map.UsePreset(ShortcutPresets.Blender);
    Same(map.Bindings(Orbit3D), [ShortcutBinding.Drag(Middle)], "Blender orbit");
    Same(map.Bindings(Pan3D), [ShortcutBinding.Drag(Middle, Shift)], "Blender pan");
    Same(map.Bindings(Zoom3D), [ShortcutBinding.Wheel(), ShortcutBinding.Drag(Middle, Ctrl)], "Blender zoom");
    Same(map.Bindings(FrameSelection3D), [Key("Numpad .")], "Blender frame selection");
    Same(map.Bindings(FrameAll3D), [Key("Home")], "Blender frame all");
    Same(map.Bindings(Redo), [Key("Z", Ctrl | Shift)], "Blender redo");
    Same(map.Bindings(Delete2D), [Key("X"), Key("Delete")], "Blender delete");
    Same(map.Bindings(Duplicate2D), [Key("D", Shift)], "Blender duplicate");
    Resolves(map.ResolveKey("Z", Ctrl | Shift, null), "Blender Ctrl+Shift+Z", Redo);
    Resolves(map.ResolveKey("Z", Ctrl, null), "Blender Ctrl+Z", Undo);
    Resolves(map.ResolveMouse(ShortcutSection.View3D, Middle, Shift), "Blender shift middle", Pan3D);
    Resolves(map.ResolveMouse(ShortcutSection.View3D, Middle, Ctrl), "Blender ctrl middle", Zoom3D);
    Resolves(map.ResolveMouse(ShortcutSection.View3D, Middle, None), "Blender middle", Orbit3D);
    Require(map.Bindings(BuildPackages).Count == 0 && map.Bindings(SetCursor3D).Count == 0, "Blender invented a binding.");

    map.UsePreset(ShortcutPresets.Unity);
    Same(map.Bindings(Orbit3D), [ShortcutBinding.Drag(Left, Alt)], "Unity orbit");
    Same(map.Bindings(Pan3D), [ShortcutBinding.Drag(Middle)], "Unity pan");
    Same(map.Bindings(Zoom3D), [ShortcutBinding.Wheel(), ShortcutBinding.Drag(Right, Alt)], "Unity zoom");
    Same(map.Bindings(FrameSelection3D), [Key("F")], "Unity frame");
    Same(map.Bindings(Redo), [Key("Y", Ctrl)], "Unity redo");
    Same(map.Bindings(Duplicate2D), [Key("D", Ctrl)], "Unity duplicate");
    Resolves(map.ResolveMouse(ShortcutSection.View3D, Left, Alt), "Unity alt left", Orbit3D);
    Resolves(map.ResolveMouse(ShortcutSection.View3D, Left, None), "Unity left", Select3D);

    map.UsePreset(ShortcutPresets.Maya);
    Same(map.Bindings(Pan3D), [ShortcutBinding.Drag(Middle, Alt)], "Maya pan");
    Same(map.Bindings(Zoom3D), [ShortcutBinding.Drag(Right, Alt), ShortcutBinding.Wheel()], "Maya zoom");
    Same(map.Bindings(FrameAll3D), [Key("A")], "Maya frame all");
    Same(map.Bindings(Pan2D), [ShortcutBinding.Drag(Middle, Alt)], "Maya 2D pan");

    map.UsePreset(ShortcutPresets.Unreal);
    Same(map.Bindings(Pan2D), [ShortcutBinding.Drag(Right)], "Unreal 2D pan");
    Same(map.Bindings(FrameAll2D), [Key("Home")], "Unreal 2D frame all");
    Same(map.Bindings(FrameSelection3D), [Key("F")], "Unreal frame");
    Require(map.Bindings(FrameAll3D).Count == 0, "Unreal invented a frame all binding.");
}

static void Conflicts()
{
    var map = new ShortcutMap();
    foreach (var preset in ShortcutPresets.All)
    {
        map.UsePreset(preset.Name);
        Require(map.ConflictCount() == (preset.Name is ShortcutPresets.Maya or ShortcutPresets.Blender ? 2 : 0),
            $"{preset.Name} has unexpected conflicting bindings.");
    }
    map.UsePreset(ShortcutPresets.SunriseStudio);
    Require(map.Set(SaveProject, 0, Key("F")), "Save did not take F.");
    Require(map.Title == "Custom (from Sunrise Studio)", "An edit did not mark the preset as custom.");
    var withF = map.ConflictsWith(SaveProject, Key("F")).Select(action => action.Id).ToHashSet();
    Require(withF.SetEquals([FrameSelection2D, FrameSelection3D]), "A menu key must conflict with 2D and 3D uses of the key.");
    Require(map.ConflictsWith(FrameSelection3D, Key("F")).Any(action => action.Id == SaveProject), "The view side missed the menu conflict.");
    Require(map.ConflictCount() == 3, $"Expected three conflicting bindings, found {map.ConflictCount()}.");
    Same(map.Bindings(SaveProject), [Key("F")], "A conflicting binding was dropped");
    Resolves(map.ResolveKey("F", None, ShortcutSection.View3D), "Conflicting F refuses both actions");
    map.Reset(SaveProject);
    Require(map.ConflictCount() == 0 && !map.IsCustom && map.Title == ShortcutPresets.SunriseStudio, "Reset did not restore the preset.");

    map.Set(HideSelection3D, 0, Key("H"));
    map.Set(Delete2D, 0, Key("H"));
    Require(map.ConflictCount() == 0, "2D and 3D bindings must not conflict with each other.");
    map.Set(Pan2D, 0, ShortcutBinding.Drag(Left));
    Require(map.ConflictsWith(Pan2D, ShortcutBinding.Drag(Left)).Any(action => action.Id == Select2D),
        "A drag and a click on one button must conflict.");
    Require(!map.Set(SaveProject, 0, ShortcutBinding.Click(Left)), "A menu action accepted a mouse binding.");
    Require(!map.Set(Orbit3D, 0, Key("O")), "A drag action accepted a key.");
    Require(!map.Set(Orbit3D, 2, ShortcutBinding.Drag(Left)), "A third slot was accepted.");
    Require(!map.Set("menu.unknown", 0, Key("Q")), "An unknown action was accepted.");

    Require(!ShortcutMap.Collides(Key("Z", Ctrl), Key("Z", Ctrl | Shift)), "Different modifiers must not collide.");
    Require(!ShortcutMap.Collides(ShortcutBinding.Wheel(), ShortcutBinding.Wheel(Ctrl)), "Wheel modifiers must not collide.");
    Require(ShortcutMap.Collides(ShortcutBinding.Wheel(Ctrl), ShortcutBinding.Wheel(Ctrl)), "Equal wheel bindings must collide.");
    Require(!ShortcutMap.Collides(Key("F"), ShortcutBinding.Click(Left)), "A key and a click must not collide.");
    Require(!ShortcutMap.Collides(ShortcutBinding.Drag(Left), ShortcutBinding.Drag(Right)), "Different buttons must not collide.");
    map.UsePreset(ShortcutPresets.Blender);
    Resolves(map.ResolveMouse(ShortcutSection.View3D, Middle, Ctrl | Shift), "Blender ambiguous pan/zoom");
    Resolves(map.ResolveMouse(ShortcutSection.View3D, Middle, Shift), "Blender pan remains available", Pan3D);
    Resolves(map.ResolveMouse(ShortcutSection.View3D, Middle, Ctrl), "Blender zoom remains available", Zoom3D);
    map.UsePreset(ShortcutPresets.Maya);
    Resolves(map.ResolveKey("Z", Ctrl | Shift, null), "Maya ambiguous undo/redo");
    Resolves(map.ResolveKey("Z", Ctrl, null), "Maya undo", Undo);
    Resolves(map.ResolveKey("Z", Shift, null), "Maya redo", Redo);
    Require(map.ConflictsWith(Undo, Key("Z", Ctrl)).Any(action => action.Id == Redo), "Equal-rank modifier overlap was not marked.");
    map.Set(Redo, 1, Key("Z", Ctrl | Shift));
    Resolves(map.ResolveKey("Z", Ctrl | Shift, null), "More-specific redo wins", Redo);
    Require(map.ConflictCount() == 0, "A more-specific binding did not remove the unreachable tie.");
    map.UsePreset(ShortcutPresets.SunriseStudio);
    map.Set(HideSelection3D, 0, Key("H", Ctrl)); map.Set(ShowAll3D, 0, Key("H", Shift));
    Resolves(map.ResolveKey("H", Ctrl | Shift, ShortcutSection.View3D), "Ambiguous 3D key");
    Resolves(map.ResolveKey("H", Ctrl | Shift, ShortcutSection.View2D), "3D tie cannot run in 2D");
    map.Set(FrameSelection3D, 0, Key("H", Ctrl | Shift));
    Resolves(map.ResolveKey("H", Ctrl | Shift, ShortcutSection.View3D), "Specific 3D chord", FrameSelection3D);
    Require(map.ConflictCount() == 0, "A higher-rank 3D command left a false conflict.");
}

static void RoundTrip()
{
    var original = new ShortcutMap();
    original.UsePreset(ShortcutPresets.Blender);
    original.Set(Orbit3D, 0, ShortcutBinding.Drag(Right, Alt));
    original.Clear(Delete2D, 1);
    original.Set(SaveProject, 1, Key("F12", Ctrl | Shift | Alt));
    original.Set(Zoom3D, 0, ShortcutBinding.Wheel(Ctrl));
    original.Clear(Undo, 0);
    Require(original.Title == "Custom (from Blender)", "The edited map is not custom.");
    Compare(ShortcutMap.FromJson(original.ToJson()), original, "text");
    Compare(ShortcutMap.FromElement(original.ToElement()), original, "element");
    string settings = JsonSerializer.Serialize(new { SunrisePath = "C:\\Games", Shortcuts = (JsonElement?)original.ToElement() });
    using (var document = JsonDocument.Parse(settings))
        Compare(ShortcutMap.FromElement(document.RootElement.GetProperty("Shortcuts")), original, "settings file");

    var restored = new ShortcutMap();
    restored.UsePreset(ShortcutPresets.Blender);
    restored.Set(Orbit3D, 0, ShortcutBinding.Drag(Right));
    restored.Set(Orbit3D, 0, ShortcutBinding.Drag(Middle));
    Require(!restored.IsCustom && restored.OverriddenIds.Count == 0, "Editing back to the preset kept an override.");
    Require(!ShortcutMap.FromJson(restored.ToJson()).IsCustom, "A plain preset loaded as custom.");

    static void Compare(ShortcutMap loaded, ShortcutMap expected, string path)
    {
        Require(loaded.Title == expected.Title && loaded.Preset.Name == expected.Preset.Name, $"Title changed through the {path} form.");
        Require(loaded.OverriddenIds.ToHashSet().SetEquals(expected.OverriddenIds), $"Overrides changed through the {path} form.");
        foreach (var action in ShortcutActions.All)
            Same(loaded.Bindings(action.Id), expected.Bindings(action.Id), $"{action.Id} through the {path} form");
    }
}

static void DamagedFiles()
{
    string?[] broken =
    [
        null, "", "{ not json", "[]", "5", "null",
        "{\"Version\":2,\"Preset\":\"Blender\"}",
        "{\"Version\":\"1\",\"Preset\":\"Blender\"}",
        "{\"Preset\":\"Blender\"}"
    ];
    foreach (string? text in broken)
    {
        var map = ShortcutMap.FromJson(text);
        Require(map.Preset.Name == ShortcutPresets.SunriseStudio && !map.IsCustom, $"Damaged text did not fall back: {text}");
    }
    Require(ShortcutMap.FromElement(null).Title == ShortcutPresets.SunriseStudio, "A missing section did not fall back.");

    string mixed = """
        {
          "Version": 1,
          "Preset": "Maya",
          "Overrides": {
            "menu.fly_mode": ["Ctrl+Q"],
            "3d.orbit": ["Ctrl+Banana"],
            "3d.pan": "Middle drag",
            "3d.zoom": ["F"],
            "3d.select": ["Left click", "Right click", "Middle click"],
            "2d.pan": [5],
            "menu.undo": ["Ctrl+U"],
            "2d.frame_all": []
          }
        }
        """;
    var partial = ShortcutMap.FromJson(mixed);
    var maya = ShortcutPresets.Find(ShortcutPresets.Maya)!;
    Require(partial.Preset.Name == ShortcutPresets.Maya, "A damaged entry discarded the preset.");
    foreach (string id in new[] { Orbit3D, Pan3D, Zoom3D, Select3D, Pan2D })
        Same(partial.Bindings(id), maya.For(id), $"Damaged {id} did not fall back to the preset");
    Same(partial.Bindings(Undo), [Key("U", Ctrl)], "A valid override beside damaged ones");
    Require(partial.Bindings(FrameAll2D).Count == 0, "An explicit empty override was lost.");
    Require(partial.OverriddenIds.ToHashSet().SetEquals([Undo, FrameAll2D]), "Damaged entries became overrides.");

    var unknown = ShortcutMap.FromJson("{\"Version\":1,\"Preset\":\"Houdini\",\"Overrides\":{\"menu.undo\":[\"Ctrl+U\"]}}");
    Require(unknown.Preset.Name == ShortcutPresets.SunriseStudio, "An unknown preset did not fall back.");
    Same(unknown.Bindings(Undo), [Key("U", Ctrl)], "Overrides under an unknown preset");
    var shapeless = ShortcutMap.FromJson("{\"Version\":1,\"Preset\":\"Unity\",\"Overrides\":5}");
    Require(shapeless.Preset.Name == ShortcutPresets.Unity && !shapeless.IsCustom, "Malformed overrides changed the preset.");
}

static void SettingsFile()
{
    string root = Path.Combine(Path.GetTempPath(), "sunrise-shortcuts-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        string path = Path.Combine(root, "settings.json");
        var map = new ShortcutMap();
        map.UsePreset(ShortcutPresets.Unreal);
        map.Set(Undo, 1, Key("Z", Ctrl | Shift));
        new SunriseStudio.Editor.StudioSettings { SunrisePath = "C:\\Games\\Sunrise", Shortcuts = map.ToElement() }.Save(path);
        var loaded = SunriseStudio.Editor.StudioSettings.Load(path);
        Require(loaded.SunrisePath == "C:\\Games\\Sunrise", "Saving shortcuts lost the installation path.");
        var again = ShortcutMap.FromElement(loaded.Shortcuts);
        Require(again.Title == "Custom (from Unreal)", "The saved preset did not load.");
        Same(again.Bindings(Undo), map.Bindings(Undo), "Undo through the settings file");
        string[] sections = ["5", "\"text\"", "[1, 2]", "null", "{\"Version\":1,\"Preset\":\"Maya\",\"Overrides\":{\"3d.orbit\":7}}"];
        foreach (string section in sections)
        {
            File.WriteAllText(path, "{\"SunrisePath\":\"kept\",\"Shortcuts\":" + section + "}");
            var damaged = SunriseStudio.Editor.StudioSettings.Load(path);
            Require(damaged.SunrisePath == "kept", $"A damaged shortcut section reset the other preferences: {section}");
            var fallback = ShortcutMap.FromElement(damaged.Shortcuts);
            Require(!fallback.IsCustom, $"A damaged shortcut section produced overrides: {section}");
        }
    }
    finally { Directory.Delete(root, true); }
}

static void BindingText()
{
    ShortcutModifiers[] modifierSets = [None, Ctrl, Shift | Alt, Ctrl | Shift | Alt];
    foreach (string name in ShortcutKeys.Names)
        foreach (var modifiers in modifierSets)
            RoundTrips(Key(name, modifiers));
    foreach (var button in new[] { Left, Right, Middle })
        foreach (var modifiers in modifierSets)
        {
            RoundTrips(ShortcutBinding.Click(button, modifiers));
            RoundTrips(ShortcutBinding.Drag(button, modifiers));
        }
    foreach (var modifiers in modifierSets) RoundTrips(ShortcutBinding.Wheel(modifiers));
    Require(ShortcutBinding.TryParse("ctrl+shift+z", out var lower) && lower == Key("Z", Ctrl | Shift), "Parsing must ignore case.");
    Require(ShortcutBinding.TryParse("Alt+Ctrl+Left drag", out var reordered) && reordered.ToString() == "Ctrl+Alt+Left drag",
        "Modifier order must not matter when reading.");
    Require(ShortcutBinding.Drag(Left, Alt).ToString() == "Alt+Left drag" && Key("B", Ctrl | Shift).ToString() == "Ctrl+Shift+B"
        && Key("Numpad .").ToString() == "Numpad ." && ShortcutBinding.Wheel().ToString() == "Wheel", "Binding text changed.");
    string[] malformed = ["", " ", "Ctrl+", "Ctrl+Ctrl+A", "Hyper+A", "Left", "Left hold", "Escape", "Ctrl", "F13", "1 click",
        "Left Arrow drag", "A or B", new string('A', 41)];
    foreach (string text in malformed)
        Require(!ShortcutBinding.TryParse(text, out _), $"Accepted malformed binding '{text}'.");
    var mapped = ShortcutKeys.Names.Select(ShortcutInput.ToImGui).ToArray();
    Require(mapped.All(key => key != ImGuiKey.None) && mapped.Distinct().Count() == mapped.Length,
        "A bindable key has no ImGui key, or two names share one.");

    static void RoundTrips(ShortcutBinding binding) =>
        Require(ShortcutBinding.TryParse(binding.ToString(), out var parsed) && parsed == binding, $"Text did not round trip: {binding}");
}

static ShortcutBinding Key(string key, ShortcutModifiers modifiers = None) => ShortcutBinding.Keyboard(key, modifiers);

static void Same(IReadOnlyList<ShortcutBinding> actual, IReadOnlyList<ShortcutBinding> expected, string what) =>
    Require(actual.SequenceEqual(expected), $"{what}: expected '{string.Join(" or ", expected)}', got '{string.Join(" or ", actual)}'.");

static void Resolves(IReadOnlyList<string> actual, string what, params string[] expected) =>
    Require(actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)),
        $"{what}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");

static void Require(bool value, string message) { if (!value) throw new Exception(message); }

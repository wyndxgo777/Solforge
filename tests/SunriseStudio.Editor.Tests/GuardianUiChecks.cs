using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

/**
 * The Guardians category opens a class body with the Guardian page, gear, a weapon, colours, Back and two exports.
 * Empty armor slots draw the class's gear-preview placeholders, and the hands always draw.
 */
internal static class GuardianUiChecks
{
    private const uint Helmet = 0x8131934E, Weapon = 0x81319AF9;
    // Table 0x81327CE3 by class byte, in helmet, gauntlets, chest, legs, class item order.
    private static readonly uint[][] Placeholders =
    [
        [0x81319353, 0x81319350, 0x81319351, 0x81319354, 0x81319352],
        [0x8131934E, 0x8131934B, 0x8131934C, 0x8131934F, 0x8131934D],
        [0x81319358, 0x81319355, 0x81319356, 0x81319359, 0x81319357]
    ];
    private static readonly string[] ArmorLabels = ["Helmet", "Gauntlets", "Chest", "Legs", "Class item"];

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string install, string output)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Frames(install, output); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    private static void Frames(string install, string output)
    {
        Directory.CreateDirectory(output);
        string settings = Path.Combine(output, "settings.json");
        // The disk cache keeps the full-install open inside the five-minute smoke limit.
        new StudioSettings().Save(settings);
        string export = Path.Combine(output, "export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(export);
        using var form = new StudioForm(new(Game: install, Warp: true, Smoke: true, Frames: 40000, SettingsPath: settings));
        const BindingFlags access = BindingFlags.Instance | BindingFlags.NonPublic;
        var gear = GuardianLoadout.Default with { Helmet = Helmet, Weapon = Weapon, RaceGender = 1 };
        var head = GuardianLoadout.Default with { Helmet = Helmet, ShowHelmet = false };
        // The default Dye06 is the last of its list, so step down.
        var dyed = head with { Dye06 = head.Dye06 - 1 };
        var exoFemale = GuardianLoadout.Default with { RaceGender = 5, ShowHelmet = false };
        var noHelmet = GuardianPage.Pick(GuardianLoadout.Default, 0, null);
        CheckPickers();
        string femaleExport = Path.Combine(output, "export-female-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(femaleExport);
        int phase = 0, frames = 0;
        form.FrameRendered += () =>
        {
            var workspace = form.Workspace;
            if (!form.EditorReady || workspace.IsBusy || workspace.Catalog is null) return;
            Check(workspace.Error.Length == 0, workspace.Error);
            switch (phase)
            {
                case 0:
                    Check(StudioCategories.Find(StudioCategories.GuardiansName) is { ModelPane: true, Group: StudioCategories.WorldGroup }
                        && workspace.CategoryRowsOf(StudioCategories.GuardiansName).Select(row => row.Kind).Order()
                            .SequenceEqual(["Guardians / Hunter", "Guardians / Titan", "Guardians / Warlock"]),
                        "The Guardians category does not list the three class bodies.");
                    workspace.Open(GuardianClasses.Titan); Next(); return;
                case 1:
                {
                    if (!Settled(5)) return;
                    var document = Guardian(GuardianClasses.Titan, GuardianLoadout.Default);
                    var category = StudioCategories.ForKind(document.Kind)!;
                    string[] tabs = [.. StudioCategories.Tabs(category, form.Pages.Pages, document, true).Select(page => page.Id)];
                    Check(document.Kind == "Guardians / Titan" && tabs.Take(4).SequenceEqual(
                        [StudioCategories.GuardianPageId, "sunrise.scene", "sunrise.materials", "sunrise.related"]),
                        "The Titan body did not open the guardian viewer with its tabs: " + string.Join(", ", tabs));
                    var guardian = document.Guardian!;
                    CheckPlaceholders(guardian.Parts, GuardianClasses.All[0], 0);
                    CheckDefaults(workspace);
                    Check(document.Scene is { Instances.Count: > 0 } && guardian.Animation is { Rig.Bones.Count: 72 }
                        && guardian.PoseClipTag == 0 && document.Connections?.SelectedClipTag == 0,
                        $"The unarmed Titan has no scene or rig, or does not open at bind (pose {guardian.PoseClipTag:X8}).");
                    form.ShowAssetPage(StudioCategories.GuardianPageId);
                    AssertView(document);
                    Capture("titan-default");
                    Next(); return;
                }
                case 2:
                {
                    Check(form.AssetPreviewViewport!.Animation!.Clip is null, "The unarmed Titan is posed.");
                    workspace.OpenGuardian(GuardianClasses.Titan, gear); Next(); return;
                }
                case 3:
                {
                    var animation = form.AssetPreviewViewport!.Animation!;
                    if (!Settled(5) || animation.IsLoading) return;
                    var guardian = Guardian(GuardianClasses.Titan, gear).Guardian!;
                    Check(guardian.PoseClipTag != 0 && animation.Clip?.TagHash == guardian.PoseClipTag && !animation.Playing
                        && animation.Error is null, $"The armed Titan does not open held in its hold pose 0x{guardian.PoseClipTag:X8}: "
                        + animation.Error);
                    var parts = guardian.Parts.Parts;
                    Check(parts.Any(part => part.Label == "Helmet" && part.ItemTag == Helmet)
                        && parts.Count(part => part.ItemTag == Weapon) > 0 && guardian.Parts.WeaponEntity != 0
                        && guardian.Animation?.AttachedSkins.Count > 0 && !parts.Any(part => part.Selector > 0),
                        "Helmet, weapon or grip attachment is missing on the female Titan.");
                    Check(guardian.Scene.Instances.Any(instance => instance.Name.StartsWith("Helmet:", StringComparison.Ordinal)
                        && instance.InitiallyVisible != false),
                        "The female Titan does not draw its helmet.");
                    Console.WriteLine($"guardian_parts female parts={parts.Count} instances={guardian.Scene.Instances.Count} "
                        + $"attached={guardian.Animation!.AttachedSkins.Count} notes={guardian.Diagnostics.Count}");
                    Capture("titan-female-gear");
                    workspace.OpenGuardian(GuardianClasses.Titan, gear with { RaceGender = 0 }); Next(); return;
                }
                case 4:
                {
                    if (!Settled(5)) return;
                    var document = Guardian(GuardianClasses.Titan, gear with { RaceGender = 0 });
                    Check(document.Scene!.Instances.Any(instance => instance.Name.StartsWith("Helmet:", StringComparison.Ordinal))
                        && document.Scene.Instances.Any(instance => instance.Name.StartsWith("Weapon:", StringComparison.Ordinal)),
                        "The male Titan does not draw its helmet and weapon.");
                    AssertView(document);
                    Capture("titan-male-gear");
                    workspace.OpenGuardian(GuardianClasses.Hunter, gear with { RaceGender = 0 }); Next(); return;
                }
                case 5:
                {
                    if (!Settled(5)) return;
                    var document = Guardian(GuardianClasses.Hunter, gear with { RaceGender = 0 });
                    Check(document.Kind == "Guardians / Hunter", "The Hunter did not open as a guardian.");
                    // Same loadout as the Titan before; the class change alone swaps the placeholders.
                    CheckPlaceholders(document.Guardian!.Parts, GuardianClasses.All[1], 1);
                    AssertView(document);
                    Capture("hunter-male-gear");
                    workspace.Run(() => workspace.ExportAssetBundle(export)); Next(); return;
                }
                case 6:
                {
                    var result = workspace.LastAssetExport;
                    var hunter = Guardian(GuardianClasses.Hunter, gear with { RaceGender = 0 }).Guardian!;
                    int clips = workspace.GuardianExportAllClips ? hunter.Animation!.Clips.Select(clip => clip.ClipTag).Distinct().Count() : 1;
                    // The scene glTF holds every action, the hold pose first; no action has its own file.
                    Check(result is { Manifest: var manifest } && manifest.Meshes.Count > 0 && manifest.ScenePath is not null
                        && manifest.Animations.Count == clips && manifest.Animations.All(clip => clip.PosePath is null && clip.Error is null)
                        && manifest.Animations[0] is { Name: "Weapon hold pose" } hold && hold.ClipTag == hunter.PoseClipTag
                        && manifest.Context?.Guardian == gear with { RaceGender = 0 },
                        "The guardian export lacks meshes, its loadout, a clip or its hold pose as the first action.");
                    Console.WriteLine($"guardian_export meshes={result!.Manifest.Meshes.Count} "
                        + $"animations={result.Manifest.Animations.Count} path={result.ManifestPath}");
                    workspace.OpenGuardian(GuardianClasses.Titan, head); Next(); return;
                }
                case 7:
                {
                    if (!Settled(8)) return;
                    AssertView(Guardian(GuardianClasses.Titan, head));
                    Capture("titan-head");
                    workspace.OpenGuardian(GuardianClasses.Titan, dyed); Next(); return;
                }
                case 8:
                {
                    // A colour change must redraw; a reused atlas tag would keep the old picture.
                    if (!Settled(8)) return;
                    AssertView(Guardian(GuardianClasses.Titan, dyed));
                    Capture("titan-dye");
                    Check(!File.ReadAllBytes(Path.Combine(output, "titan-head.png"))
                        .SequenceEqual(File.ReadAllBytes(Path.Combine(output, "titan-dye.png"))), "A colour change did not change the picture.");
                    workspace.NavigateBack(); Next(); return;
                }
                case 9:
                {
                    if (!Settled(5)) return;
                    Check(workspace.Document?.OpenContext?.Guardian == head, "Back did not restore the previous loadout.");
                    workspace.OpenGuardian(GuardianClasses.Titan, exoFemale); Next(); return;
                }
                case 10:
                {
                    if (!Settled(5)) return;
                    var parts = Guardian(GuardianClasses.Titan, exoFemale).Guardian!.Parts.Parts;
                    Check(parts.Any(part => part.Selector == GuardianAppearance.HeadSelector)
                        && !parts.Any(part => part.Selector == GuardianAppearance.ThirdSelector),
                        "The Exo female does not draw a head without selector 17.");
                    workspace.OpenGuardian(GuardianClasses.Titan, gear); Next(); return;
                }
                case 11:
                {
                    if (!Settled(5)) return;
                    Guardian(GuardianClasses.Titan, gear);
                    workspace.Run(() => workspace.ExportAssetBundle(femaleExport)); Next(); return;
                }
                case 12:
                {
                    var result = workspace.LastAssetExport;
                    Check(result is { Manifest: var manifest } && manifest.Meshes.Count > 0 && manifest.Context?.Guardian == gear,
                        "The female guardian export lacks meshes or its loadout.");
                    Console.WriteLine($"guardian_export_female meshes={result!.Manifest.Meshes.Count} path={result.ManifestPath}");
                    workspace.OpenGuardian(GuardianClasses.Titan, noHelmet); Next(); return;
                }
                case 13:
                {
                    // Helmet None draws the head and the four other placeholders.
                    if (!Settled(8)) return;
                    var document = Guardian(GuardianClasses.Titan, noHelmet);
                    var parts = document.Guardian!.Parts.Parts;
                    AssertView(document);
                    CheckPlaceholders(document.Guardian.Parts, GuardianClasses.All[0], 1);
                    Check(!parts.Any(part => part.Label == "Helmet") && parts.Any(part => part.Selector == GuardianAppearance.HeadSelector),
                        "Helmet None still draws a helmet or hides the head.");
                    Capture("titan-no-helmet");
                    workspace.OpenGuardian(GuardianClasses.Warlock, GuardianLoadout.Default with { RaceGender = 1 }); Next(); return;
                }
                case 14:
                {
                    if (!Settled(8)) return;
                    var document = Guardian(GuardianClasses.Warlock, GuardianLoadout.Default with { RaceGender = 1 });
                    AssertView(document);
                    CheckPlaceholders(document.Guardian!.Parts, GuardianClasses.All[2], 0);
                    Capture("warlock-female-default");
                    phase++; form.Close(); return;
                }
            }
        };
        Application.Run(form);
        if (form.Failure is { } error) throw new InvalidDataException(error);
        Check(phase == 15, $"The guardian check stopped at phase {phase}.");
        Console.WriteLine("editor_guardian category=pass viewer=pass hold=pass gear=pass hunter=pass export=pass "
            + "colour=pass back=pass exo_female=pass female_export=pass placeholders=pass pickers=pass no_helmet=pass hands=pass");

        void Next() { phase++; frames = 0; }
        // Frames the whole scene two frames before the capture; the view keeps the old framing on a reopen.
        bool Settled(int wait)
        {
            if (++frames == wait - 2) form.AssetPreviewViewport?.Frame();
            return frames >= wait;
        }

        StudioDocument Guardian(uint body, GuardianLoadout loadout)
        {
            var document = form.Workspace.Document;
            Check(document is { Guardian: not null } && document.Entry.TagHash == body && document.OpenContext?.Guardian == loadout,
                $"The guardian {body:X8} did not open with its loadout: {document?.PreviewError}");
            return document!;
        }

        void AssertView(StudioDocument document)
        {
            var viewport = form.AssetPreviewViewport!;
            Check(ReferenceEquals(viewport.Scene, document.Scene) && viewport.VisibleObjectCount > 0
                && viewport.Animation?.Source == document.Animation,
                "The model pane does not show the guardian scene with its rig.");
        }

        void Capture(string name)
        {
            var renderer = (Dx11Renderer)typeof(StudioForm).GetField("renderer", access)!.GetValue(form)!;
            string path = Path.Combine(output, name + ".png");
            renderer.CapturePng(path);
            Console.WriteLine("guardian_capture path=" + path);
        }
    }

    /** The hands once, and the class placeholder in each armor slot from fromSlot on. */
    private static void CheckPlaceholders(GuardianParts parts, GuardianClass guardianClass, int fromSlot)
    {
        Check(parts.Parts.Count(part => part is { Kind: GuardianPartKind.Body, Label: "Hands" }
                && part.EntityTag == guardianClass.BodyTag) == 1, $"The {guardianClass.Name} does not add its hands once.");
        for (int slot = fromSlot; slot < ArmorLabels.Length; slot++)
        {
            uint expected = Placeholders[guardianClass.ClassByte][slot];
            uint[] items = [.. parts.Parts.Where(part => part.Label == ArmorLabels[slot]).Select(part => part.ItemTag).Distinct()];
            Check(items.SequenceEqual([expected]), $"The {guardianClass.Name} {ArmorLabels[slot]} is "
                + $"[{string.Join(", ", items.Select(item => item.ToString("X8")))}], not placeholder {expected:X8}.");
        }
    }

    /** Every class on every race/gender row resolves the five table items in their slots. */
    private static void CheckDefaults(StudioWorkspace workspace)
    {
        var tables = workspace.GuardianAppearance!;
        var catalog = workspace.Catalog!;
        foreach (var guardianClass in GuardianClasses.All)
        {
            for (int slot = 0; slot < ArmorLabels.Length; slot++)
                Check(tables.PreviewDefault(guardianClass.ClassByte, slot) == Placeholders[guardianClass.ClassByte][slot],
                    $"The {guardianClass.Name} {ArmorLabels[slot]} placeholder is not read from table 0x81327CE3.");
            foreach (var row in tables.Rows)
                CheckPlaceholders(tables.Resolve(catalog, guardianClass.BodyTag, GuardianLoadout.Default with { RaceGender = row.Row }),
                    guardianClass, 0);
        }
        Console.WriteLine($"guardian_placeholders classes={GuardianClasses.All.Count} rows={tables.Rows.Count}");
    }

    /** Armor pickers lead with Default; only the helmet has None, which takes the helmet off. */
    private static void CheckPickers()
    {
        for (int slot = 0; slot < ArmorLabels.Length; slot++)
        {
            var rows = GuardianPage.LeadingRows(slot);
            Check(rows[0] is { Label: "Default", Value: 0 }, $"The {ArmorLabels[slot]} picker does not start with Default.");
            Check(rows.Any(row => row.Label == "None") == (slot == 0), $"The {ArmorLabels[slot]} picker has the wrong None row.");
        }
        Check(GuardianPage.LeadingRows(ArmorLabels.Length) is [{ Label: "None", Value: 0 }], "The weapon picker lost its None row.");
        var none = GuardianPage.Pick(GuardianLoadout.Default with { Helmet = Helmet }, 0, null);
        Check(!none.ShowHelmet && none.Helmet == Helmet, "Helmet None does not take the helmet off.");
        Check(GuardianPage.Pick(none, 0, 0) is { ShowHelmet: true, Helmet: 0 }
            && GuardianPage.Pick(none, 0, Helmet) is { ShowHelmet: true, Helmet: Helmet },
            "Picking Default or a helmet does not put the helmet back on.");
        Check(GuardianPage.Pick(GuardianLoadout.Default, 2, 0) == GuardianLoadout.Default, "Chest Default changed the loadout.");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}

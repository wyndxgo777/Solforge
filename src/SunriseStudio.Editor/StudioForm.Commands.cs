/**
 * File: Commands, dialogs and file pickers of StudioForm: menu actions, the project dialog, open/save helpers and
 * install-safety checks.
 */
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Projects;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private bool addDialog;
    private string addPath = "", addName = "", addPackageId = "0AA0", addReference = "80800000";
    private int addKind, addType = 8, addSubType;
    private StudioDocument? cloneSource;

    /**
     * Title bar groups in bar order. The menus come first; groups that do not fit at the window's width move into
     * the "»" menu from the right end, so nothing on the row ever overlaps.
     */
    internal enum TitleGroup
    {
        File, Edit, Assets, Build, View, Window, Help,
        Relay, Navigation, Project, Render, Shaders, Quality, Detail, Lighting, Settings
    }

    private static readonly TitleGroup[] TitleGroups = Enum.GetValues<TitleGroup>();

    // Width each group took when last drawn on the row (0 until then), used to decide what fits.
    private readonly float[] titleWidths = new float[TitleGroups.Length];
    private readonly HashSet<string> titleReachable = [];
    private readonly List<string> titleOverflow = [];

    /** Groups (and "WindowButtons") the last frame offered, on the row or in the "»" menu. */
    internal IReadOnlyCollection<string> TitleBarReachable => titleReachable;

    /** Groups the last frame moved into the "»" menu. */
    internal IReadOnlyList<string> TitleBarOverflow => titleOverflow;

    /**
     * The title bar: brand, the menus, the toolbar and the window buttons on one row, all in one window's menu bar
     * (no overlay window). Wherever no control sits it is the window's caption.
     */
    private void DrawTitleBar(float height)
    {
        var style = ImGui.GetStyle();
        float normalPad = style.FramePadding.Y;
        // The menu bar is as tall as the frame padding at Begin and BeginMenuBar makes it; controls use the normal padding.
        float barPad = Math.Max(normalPad, (height - ImGui.GetFontSize()) / 2);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X, barPad));
        ImGui.PushStyleColor(ImGuiCol.MenuBarBg, Vector4.Zero);
        BeginPanel("Toolbar", Vector2.Zero, new(ClientSize.Width, height),
            ImGuiWindowFlags.MenuBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        // A frame whose bar fails to open offers nothing, so the layout checks see it.
        titleReachable.Clear();
        titleOverflow.Clear();
        bool bar = ImGui.BeginMenuBar();
        ImGui.PopStyleVar();
        if (!bar)
        {
            ImGui.End(); ImGui.PopStyleColor(); ImGui.PopStyleVar();
            return;
        }
        // BeginMenuBar aligns the row's text to the bar padding; text items start this much higher to sit centred.
        float row = (height - ImGui.GetFrameHeight()) / 2;
        float textTop = row + normalPad - barPad;
        float x = DrawTitleBrand(height);
        float right = ClientSize.Width - chrome.ButtonsWidth - 8 * scale;

        // Which groups fit, from last frame's widths; the rest go to the "»" menu, the right-most first.
        float overflowWidth = ImGui.CalcTextSize("»").X + style.ItemSpacing.X * 2;
        int fit = TitleGroups.Length;
        if (x + TitleGroups.Sum(TitleWidth) > right)
        {
            float used = x + overflowWidth;
            fit = 0;
            while (fit < TitleGroups.Length && used + TitleWidth(TitleGroups[fit]) <= right) used += TitleWidth(TitleGroups[fit++]);
        }
        for (int i = 0; i < fit; i++)
        {
            var group = TitleGroups[i];
            float start = x;
            x += TitleLead(group);
            ImGui.SetCursorPos(new(x, IsMenu(group) ? textTop : row));
            DrawTitleGroup(group, row, textTop);
            // On a menu bar row each item's ItemSize steps the cursor past it (and the item spacing).
            x = ImGui.GetCursorPosX();
            titleWidths[(int)group] = x - start;
            titleReachable.Add(group.ToString());
        }
        if (fit < TitleGroups.Length)
        {
            ImGui.SetCursorPos(new(x, textTop));
            if (UiAuto.BeginMenu("»##title-overflow"))
            {
                for (int i = fit; i < TitleGroups.Length; i++) DrawTitleOverflowItem(TitleGroups[i]);
                UiAuto.EndMenu();
            }
            x = ImGui.GetCursorPosX();
            for (int i = fit; i < TitleGroups.Length; i++)
            {
                titleReachable.Add(TitleGroups[i].ToString());
                titleOverflow.Add(TitleGroups[i].ToString());
            }
        }
        if (workspace.IsBusy && x + ImGui.CalcTextSize("Working").X <= right)
        {
            ImGui.SetCursorPos(new(x, textTop));
            ImGui.TextDisabled("Working");
        }
        chrome.DrawButtons(height);
        titleReachable.Add("WindowButtons");
        ImGui.EndMenuBar();
        ImGui.End(); ImGui.PopStyleColor(); ImGui.PopStyleVar();
    }

    private static bool IsMenu(TitleGroup group) => group <= TitleGroup.Help;

    // Extra room before a group: the toolbar starts apart from the menus, and the project apart from navigation.
    private float TitleLead(TitleGroup group) => group switch
    {
        TitleGroup.Relay => 12 * scale,
        TitleGroup.Project => 16 * scale,
        _ => 0
    };

    /** A group's width when last drawn, or a guess before its first frame on the row. */
    private float TitleWidth(TitleGroup group)
    {
        float width = titleWidths[(int)group];
        if (width > 0) return width;
        var style = ImGui.GetStyle();
        return IsMenu(group) ? ImGui.CalcTextSize(group.ToString()).X + style.ItemSpacing.X * 2
            : group is TitleGroup.Quality or TitleGroup.Lighting ? 150 * scale
            : group is TitleGroup.Project ? 200 * scale
            : ImGui.GetFrameHeight() * 2 + style.ItemSpacing.X + TitleLead(group);
    }

    /** Logo, SOLFORGE and the version, drawn without clickable items so the brand stays part of the drag caption. */
    private float DrawTitleBrand(float height)
    {
        var draw = ImGui.GetWindowDrawList();
        Vector2 origin = ImGui.GetWindowPos();
        float x = ImGui.GetStyle().WindowPadding.X;
        ImGui.PushFont(gui!.TitleFont);
        int mark = (int)MathF.Round(ImGui.GetTextLineHeight() * 1.2f);
        if (toolbarLogo?.Width != mark && renderer is not null)
        {
            toolbarLogo?.Dispose();
            toolbarLogo = StudioBrand.CreateTexture(renderer, mark);
        }
        if (toolbarLogo is { } logo)
        {
            // Whole pixels keep the 1:1 logo from being resampled again.
            ImGui.SetCursorScreenPos(Vector2.Round(origin + new Vector2(x, (height - mark) / 2)));
            ImGui.Image(logo.ImGuiId, new(mark));
            x += mark + 8 * scale;
        }
        float titleTop = (height - ImGui.GetTextLineHeight()) / 2;
        float baseline = titleTop + FontAscent();
        draw.AddText(origin + new Vector2(x, titleTop), ImGui.GetColorU32(StudioTheme.Accent), "SOL");
        x += ImGui.CalcTextSize("SOL").X;
        draw.AddText(origin + new Vector2(x, titleTop), ImGui.GetColorU32(ImGuiCol.Text), "FORGE");
        x += ImGui.CalcTextSize("FORGE").X;
        ImGui.PopFont();
        x += 6 * scale;
        var version = ImGui.CalcTextSize(StudioVersion.Text);
        ImGui.SetCursorPos(new(x, baseline - FontAscent()));
        ImGui.Dummy(version);
        draw.AddText(ImGui.GetItemRectMin(), ImGui.GetColorU32(ImGuiCol.TextDisabled), StudioVersion.Text);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(AboutCredit);
        return x + version.X + 10 * scale;
    }

    /** One group on the row; the cursor is at its start, its top at row (textTop for text). */
    private void DrawTitleGroup(TitleGroup group, float row, float textTop)
    {
        switch (group)
        {
            case TitleGroup.File: DrawFileMenu(); break;
            case TitleGroup.Edit: DrawEditMenu(); break;
            case TitleGroup.Assets: DrawAssetsMenu(); break;
            case TitleGroup.Build: DrawBuildMenu(); break;
            case TitleGroup.View: DrawViewMenu(); break;
            case TitleGroup.Window: DrawWindowMenu(); break;
            case TitleGroup.Help: DrawHelpMenu(); break;
            case TitleGroup.Relay: DrawScreenRelayButton(row); break;
            case TitleGroup.Navigation: DrawNavigationButtons(row); break;
            case TitleGroup.Project:
                ImGui.SetCursorPosY(textTop);
                ImGui.TextUnformatted(workspace.Project?.Name ?? "View only");
                ToolbarNext(row);
                if (workspace.Project is null)
                {
                    if (ImGuiIcons.Button("tb-new", StudioIcon.Plus, "New project")) projectDialog = true;
                }
                else
                {
                    ImGui.BeginDisabled(!workspace.CanEdit);
                    if (ImGuiIcons.Button("tb-add", StudioIcon.Plus, "Add asset")) addDialog = true;
                    ToolbarNext(row);
                    string build = ShortcutInput.Text(ShortcutActions.BuildPackages);
                    // Solforge: the primary action uses the sun-grad button (SolTheme).
                    if (SolTheme.PrimaryButton("Build##tb-build")) workspace.Run(() => workspace.Build());
                    StudioUi.Tip(build.Length == 0 ? "Build packages" : $"Build packages ({build})");
                    ImGui.EndDisabled();
                }
                break;
            case TitleGroup.Render: DrawRenderingToolbarButton(); break;
            case TitleGroup.Shaders:
            {
                bool shading = settings.GameShaders;
                string? shaderProblem = GameShaderProblem();
                if (ImGuiIcons.Toggle("tb-shaders", StudioIcon.Shader,
                    "Game shaders: the game's materials and lighting (Quality High or Full)" + (shaderProblem is null ? "" : "\n" + shaderProblem),
                    ref shading))
                    SetGameShaders(shading);
                break;
            }
            case TitleGroup.Quality: DrawQualityToolbarCombo(); break;
            case TitleGroup.Detail:
            {
                bool fullDetail = settings.FullDetail;
                if (ImGuiIcons.Toggle("tb-detail", StudioIcon.Detail,
                    "Full detail: draw static objects at full detail at every distance, with no far cull.\nOff switches detail and culls with distance, as the game does."
                    + "\nIn the map viewport this applies at Quality High; Full always draws full detail, Low and Medium never do.",
                    ref fullDetail))
                    SetFullDetail(fullDetail);
                break;
            }
            case TitleGroup.Lighting:
            {
                int lighting = Math.Clamp((int)settings.LightingMode, 0, LightingNames.Length - 1);
                // The preview names the setting so a bare "Studio" doesn't read as the app or layout name.
                ImGui.SetNextItemWidth(ImGui.CalcTextSize("Lighting: Ambient").X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2);
                if (UiAuto.BeginCombo("##lighting", "Lighting: " + LightingNames[lighting]))
                {
                    for (int i = 0; i < LightingNames.Length; i++)
                        if (UiAuto.Selectable(LightingNames[i], i == lighting) && i != lighting) SetLighting(i);
                    UiAuto.EndCombo();
                }
                string? shaderProblem = GameShaderProblem();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip((lighting == 1
                    ? "Lighting: atmosphere preview\nLocal probes and shadows are not drawn"
                    : "Lighting: editor lights with game materials") + (shaderProblem is null ? "" : "\n" + shaderProblem));
                break;
            }
            case TitleGroup.Settings:
            {
                string settingsKeys = ShortcutInput.Text(ShortcutActions.OpenSettings);
                if (ImGuiIcons.Button("tb-settings", StudioIcon.Gear, settingsKeys.Length > 0 ? $"Settings ({settingsKeys})" : "Settings"))
                    preferencesOpen = true;
                break;
            }
        }
    }

    /** One group inside the "»" menu: menus become submenus, buttons become menu items. */
    private void DrawTitleOverflowItem(TitleGroup group)
    {
        switch (group)
        {
            case TitleGroup.File: DrawFileMenu(); break;
            case TitleGroup.Edit: DrawEditMenu(); break;
            case TitleGroup.Assets: DrawAssetsMenu(); break;
            case TitleGroup.Build: DrawBuildMenu(); break;
            case TitleGroup.View: DrawViewMenu(); break;
            case TitleGroup.Window: DrawWindowMenu(); break;
            case TitleGroup.Help: DrawHelpMenu(); break;
            case TitleGroup.Relay:
                if (UiAuto.BeginMenu("Screen relay")) { DrawScreenRelayItems(); UiAuto.EndMenu(); }
                break;
            case TitleGroup.Navigation:
                if (UiAuto.MenuItem("Back", ShortcutInput.Text(ShortcutActions.NavigateBack), false, workspace.CanNavigateBack)) NavigateBack();
                if (UiAuto.MenuItem("Forward", ShortcutInput.Text(ShortcutActions.NavigateForward), false, workspace.CanNavigateForward))
                    NavigateForward();
                break;
            case TitleGroup.Project:
                ImGui.Separator();
                ImGui.TextDisabled(workspace.Project?.Name ?? "View only");
                if (workspace.Project is null)
                {
                    if (UiAuto.MenuItem("New project")) projectDialog = true;
                }
                else
                {
                    if (UiAuto.MenuItem("Add asset", "", false, workspace.CanEdit)) addDialog = true;
                    if (UiAuto.MenuItem("Build packages", ShortcutInput.Text(ShortcutActions.BuildPackages), false, workspace.CanEdit))
                        workspace.Run(() => workspace.Build());
                }
                break;
            case TitleGroup.Render: DrawRenderingMenuItem(); break;
            case TitleGroup.Shaders:
            {
                bool shading = settings.GameShaders;
                if (UiAuto.MenuItem("Game shaders", "", ref shading)) SetGameShaders(shading);
                break;
            }
            case TitleGroup.Quality:
                if (UiAuto.BeginMenu("Quality"))
                {
                    for (int i = 0; i < QualityNames.Length; i++)
                        if (UiAuto.MenuItem(QualityNames[i], "", i == (int)settings.ViewportQuality)) SetViewportQuality((ViewportQuality)i);
                    UiAuto.EndMenu();
                }
                break;
            case TitleGroup.Detail:
            {
                bool fullDetail = settings.FullDetail;
                if (UiAuto.MenuItem("Full detail", "", ref fullDetail)) SetFullDetail(fullDetail);
                break;
            }
            case TitleGroup.Lighting:
                if (UiAuto.BeginMenu("Lighting"))
                {
                    int lighting = Math.Clamp((int)settings.LightingMode, 0, LightingNames.Length - 1);
                    for (int i = 0; i < LightingNames.Length; i++)
                        if (UiAuto.MenuItem(LightingNames[i], "", i == lighting) && i != lighting) SetLighting(i);
                    UiAuto.EndMenu();
                }
                break;
            case TitleGroup.Settings:
                if (UiAuto.MenuItem("Settings...", ShortcutInput.Text(ShortcutActions.OpenSettings))) preferencesOpen = true;
                break;
        }
    }

    private void SetFullDetail(bool fullDetail)
    {
        settings.FullDetail = fullDetail;
        if (viewport is not null) viewport.FullDetail = fullDetail;
        if (assetViewport is not null) assetViewport.FullDetail = fullDetail;
        if (fullDetail && settings.ViewportQuality < ViewportQuality.High) SetViewportQuality(ViewportQuality.High);
        else SaveSettings();
    }

    private void SetLighting(int lighting)
    {
        settings.LightingMode = (SunriseStudio.Rendering.NativeLightingMode)lighting;
        if (viewport?.NativeRenderer is { } scene) scene.LightingMode = settings.LightingMode;
        if (assetViewport?.NativeRenderer is { } asset) asset.LightingMode = settings.LightingMode;
        // Lighting is part of game shading; picking a mode turns it on so the choice shows.
        if (!settings.GameShaders || settings.ViewportQuality < ViewportQuality.High) SetGameShaders(true);
        else SaveSettings();
    }

    /** Pixels from a text line's top to its baseline in the current font. */
    private static float FontAscent() => ImGui.GetFont().Ascent * ImGui.GetFontSize() / ImGui.GetFont().FontSize;

    /** The next toolbar control on this row, its top at row (the centre line less half a frame). */
    private static void ToolbarNext(float row)
    {
        ImGui.SameLine();
        ImGui.SetCursorPosY(row);
    }

    // The title bar menus; each item is disabled while its command cannot run. The » menu reuses them as submenus.

    private void DrawFileMenu()
    {
        if (!UiAuto.BeginMenu("File")) return;
        if (UiAuto.MenuItem("New project", ShortcutInput.Text(ShortcutActions.NewProject), false, !workspace.IsBusy)) projectDialog = true;
        if (UiAuto.MenuItem("Open project...", ShortcutInput.Text(ShortcutActions.OpenProject), false, !workspace.IsBusy)) OpenProjectDialog();
        if (UiAuto.BeginMenu("Recent projects", settings.RecentProjects.Count != 0 && !workspace.IsBusy))
        {
            foreach (string path in settings.RecentProjects.ToArray())
            {
                ImGui.PushID(path);
                // A moved or deleted project stays listed but cannot be opened.
                bool exists = IconChecks.Exists(path);
                if (UiAuto.MenuItem(Path.GetFileName(Path.GetDirectoryName(path)) ?? path, "", false, exists)) OpenProject(path);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(exists ? path : path + " (missing)");
                ImGui.PopID();
            }
            UiAuto.EndMenu();
        }
        if (UiAuto.MenuItem("New mission script...", ShortcutInput.Text(ShortcutActions.CreateMissionScript), false,
            workspace.Catalog is not null && !workspace.IsBusy))
            NewMissionScript();
        if (UiAuto.MenuItem(missionGraphActive ? "Save script" : "Save project",
            ShortcutInput.Text(ShortcutActions.SaveProject), false, CanSaveActive))
            SaveActiveDocument();
        if (UiAuto.MenuItem("Close project", "", false, workspace.CanEdit)) workspace.Run(() =>
        { if (ConfirmMissionDraft()) workspace.CloseProject(); });
        if (UiAuto.MenuItem("Close world", "", false, workspace.World is not null && !workspace.IsBusy)) workspace.Run(workspace.CloseWorld);
        ImGui.Separator();
        if (UiAuto.MenuItem("Sunrise location...", "", false, !workspace.IsBusy)) settingsDialog = true;
        if (UiAuto.MenuItem("Cache...", "", false, !workspace.IsBusy)) cacheDialog = true;
        if (UiAuto.MenuItem("Settings...", ShortcutInput.Text(ShortcutActions.OpenSettings))) preferencesOpen = true;
        ImGui.Separator();
        if (UiAuto.MenuItem("Exit")) BeginInvoke(Close);
        UiAuto.EndMenu();
    }

    private void DrawEditMenu()
    {
        if (!UiAuto.BeginMenu("Edit")) return;
        if (UiAuto.MenuItem("Undo", ShortcutInput.Text(ShortcutActions.Undo), false, CanUndoActive)) UndoActive();
        if (UiAuto.MenuItem("Redo", ShortcutInput.Text(ShortcutActions.Redo), false, CanRedoActive)) RedoActive();
        ImGui.Separator();
        if (UiAuto.MenuItem("Convert placement deltas to object edits", "", false,
            workspace.CanEdit && workspace.Project?.Changes.Count > 0))
            workspace.Run(() => workspace.ConvertPlacementDeltas());
        ImGui.Separator();
        if (UiAuto.MenuItem("Shortcuts...")) shortcutsDialog = true;
        if (UiAuto.MenuItem("Settings...", ShortcutInput.Text(ShortcutActions.OpenSettings))) preferencesOpen = true;
        UiAuto.EndMenu();
    }

    private void DrawAssetsMenu()
    {
        if (!UiAuto.BeginMenu("Assets")) return;
        if (UiAuto.MenuItem("Export full asset to Blender...", "", false, CanExportBundle)) ExportFullAsset();
        MapBundleTooltip();
        if (UiAuto.MenuItem("Export world to Blender...", "", false,
            !workspace.IsBusy && workspace.World is not null)) ExportWorldScene();
        if (UiAuto.MenuItem("Import Blender asset...", "", false, workspace.CanEdit)) BeginBundleImport();
        if (UiAuto.MenuItem("Import skinned model...", "", false, CanReplaceSkin)) BeginSkinImport();
        ImGui.Separator();
        if (UiAuto.MenuItem("Export...", "", false, workspace.Document is not null && !workspace.IsBusy)) ExportSelected();
        if (UiAuto.MenuItem("Export animation...", "", false,
            AssetPreviewViewport?.Animation is { Source.Binding: not null, Clip: not null })) ExportAnimation();
        if (UiAuto.MenuItem("Export editable animation...", "", false, SelectedClipTag() is not null
            && !workspace.IsBusy)) ExportAnimationControls();
        if (UiAuto.MenuItem("Import animation controls...", "", false, SelectedClipTag() is not null
            && workspace.CanEdit)) ImportAnimationControls();
        if (UiAuto.MenuItem("Export material to Blender...", "", false,
            workspace.Document?.Entry.Reference == 0x808071E8 && !workspace.IsBusy)) ExportMaterialBundle();
        if (UiAuto.MenuItem("Export collision...", "", false, CanExportCollision)) ExportCollision();
        if (UiAuto.MenuItem("Import collision...", "", false, workspace.CanEdit && CanExportCollision)) ImportCollision();
        if (UiAuto.MenuItem("Import as delta...", "", false, workspace.CanEdit && workspace.Document is not null)) ImportDelta();
        if (UiAuto.MenuItem("Add asset...", "", false, workspace.CanEdit)) addDialog = true;
        if (UiAuto.MenuItem("Edit perk behavior...", "", false,
            workspace.Catalog is not null && !workspace.IsBusy)) OpenPerkBehaviorPicker();
        ImGui.Separator();
        if (UiAuto.MenuItem("Resolve asset names", "", false, workspace.Catalog is not null && !workspace.IsBusy))
            workspace.Run(workspace.IndexNames);
        UiAuto.EndMenu();
    }

    private void DrawBuildMenu()
    {
        if (!UiAuto.BeginMenu("Build")) return;
        if (UiAuto.MenuItem("Build packages", ShortcutInput.Text(ShortcutActions.BuildPackages), false, workspace.CanEdit))
            workspace.Run(() => workspace.Build());
        if (UiAuto.MenuItem("Build and install", ShortcutInput.Text(ShortcutActions.BuildAndInstall), false, workspace.CanEdit)) InstallMod();
        if (UiAuto.MenuItem("Update running game", ShortcutInput.Text(ShortcutActions.UpdateRunningGame), false, workspace.CanEdit))
            UpdateRunningGame();
        if (UiAuto.MenuItem("Build to folder...", "", false, workspace.CanEdit))
            PickFolder("Build output", folder => workspace.Build(folder));
        ImGui.Separator();
        if (UiAuto.MenuItem("Mod details...", "", false, workspace.CanEdit)) workspace.Run(OpenModDialog);
        DrawPackagingMenuItems();
        UiAuto.EndMenu();
    }

    private void DrawViewMenu()
    {
        if (!UiAuto.BeginMenu("View")) return;
        if (UiAuto.MenuItem("Graph")) focusMissionGraph = true;
        if (UiAuto.MenuItem("Live")) focusLive = true;
        if (UiAuto.MenuItem("Local Server")) focusLocalServer = true;
        if (UiAuto.MenuItem("Items", "", false, workspace.Catalog is not null)) ShowItems();
        if (UiAuto.MenuItem("Data explorer (decomp, tags, strings)")) dataExplorerOpen = true;
        ImGui.Separator();
        if (UiAuto.MenuItem("Frame selection", ShortcutInput.Text(ShortcutActions.FrameSelection3D), false,
            viewport?.Camera.PivotFree == true)) viewport?.FocusSelection();
        if (UiAuto.MenuItem("Set cursor to selection", ShortcutInput.Text(ShortcutActions.SetCursor3D), false,
            (viewport?.SelectedObject is not null || viewport?.SelectedCollision >= 0) && viewport!.Camera.PivotFree))
            viewport?.SetCursorToSelection();
        bool wire = viewport?.Wireframe ?? false, grid = viewport?.ShowGrid ?? true;
        if (UiAuto.MenuItem("Wireframe", "", ref wire)) viewport!.Wireframe = wire;
        if (UiAuto.MenuItem("Grid", "", ref grid)) viewport!.ShowGrid = grid;
        ImGui.Separator();
        DrawRenderingMenuItem();
        DrawPhoneStreamMenuItems();
        UiAuto.EndMenu();
    }

    private void DrawHelpMenu()
    {
        if (!UiAuto.BeginMenu("Help")) return;
        DrawHelpMenuItems();
        if (UiAuto.MenuItem("Third-party notices")) noticesDialog = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Licenses of the libraries and code Studio includes");
        // About: Solforge is a fork; the upstream author keeps the credit.
        ImGui.Separator();
        ImGui.TextDisabled($"Solforge {StudioVersion.Text}");
        ImGui.TextDisabled(AboutCredit);
        UiAuto.EndMenu();
    }

    private static readonly string[] LightingNames = ["Studio", "Sky", "Ambient"];

    /** Draws whichever modal dialogs are open. */
    private void DrawDialogs()
    {
        DrawWideHashes();
        DrawTagDiff();
        DrawConfigBindings();
        if (setup || settingsDialog) ImGui.OpenPopup("Sunrise location");
        if (projectDialog) { ImGui.OpenPopup("New project"); projectDialog = false; }
        if (addDialog) { ImGui.OpenPopup("Add asset"); addDialog = false; }
        if (cacheDialog) { ImGui.OpenPopup("Disk cache"); cacheDialog = false; cacheStats = null; }
        if (modDialog) { ImGui.OpenPopup("Mod details"); modDialog = false; }
        if (shortcutsDialog) { ImGui.OpenPopup("Shortcuts"); shortcutsDialog = false; }
        if (noticesDialog) { ImGui.OpenPopup("Third-party notices"); noticesDialog = false; }
        DrawLocationDialog(); DrawProjectDialog(); DrawAddDialog(); DrawCacheDialog(); DrawBundleDialogs(); DrawModDialog();
        DrawShortcutsDialog(); DrawNoticesDialog(); DrawHelpDialog(); DrawPreferences(); DrawDataExplorer(); DrawPhoneStreamWindow();
        DrawContentEditors();
        DrawCreation();
    }

    /** Centers the next modal window with the given width. */
    private void CenterModal(float width)
    {
        ImGui.SetNextWindowPos(new(ClientSize.Width / 2f, ClientSize.Height / 2f),
            ImGuiCond.Appearing, new(.5f));
        ImGui.SetNextWindowSize(new(width * scale, 0), ImGuiCond.Appearing);
    }

    /** The installation folder and key source; setup, which has nothing to go back to, has no Cancel. */
    private void DrawLocationDialog()
    {
        CenterModal(600);
        if (!ImGui.BeginPopupModal("Sunrise location", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted("Installation folder");
        ImGui.SetNextItemWidth(465 * scale);
        UiAuto.InputText("##install", ref location, 4096);
        ImGui.SameLine();
        if (UiAuto.Button("Browse")) PickFolder("Sunrise installation", path => location = path);
        if (ImGui.CollapsingHeader("Package key source"))
        {
            ImGui.SetNextItemWidth(465 * scale);
            UiAuto.InputTextWithHint("##keySource", "Automatic", ref keySource, 4096);
            StudioUi.Tip("Empty: read the keys from the installation");
            ImGui.SameLine();
            if (UiAuto.Button("Browse##keys"))
                PickFile("Package key export|*.bin|Executable|*.exe|All files|*.*", path => keySource = path);
        }
        if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
        ImGui.Spacing();
        if (UiAuto.Button("Open Sunrise", new(150 * scale, 0))) workspace.Run(() =>
        {
            string root = StudioSettings.ResolveGameRoot(location);
            if (!ConfirmMissionDraft()) return;
            workspace.OpenInstallation(root, keySource);
            settings.SunrisePath = root; settings.KeySource = keySource;
            SaveSettings();
            setup = settingsDialog = false;
            ImGui.CloseCurrentPopup();
        });
        if (!setup)
        {
            ImGui.SameLine();
            if (UiAuto.Button("Cancel")) { settingsDialog = false; ImGui.CloseCurrentPopup(); }
        }
        ImGui.EndPopup();
    }

    /** Draws the new/open project dialog. */
    private void DrawProjectDialog()
    {
        CenterModal(600);
        if (!ImGui.BeginPopupModal("New project", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted("Name"); ImGui.SetNextItemWidth(-1);
        UiAuto.InputText("##projectName", ref projectName, 128);
        ImGui.TextUnformatted("Parent folder");
        ImGui.SetNextItemWidth(465 * scale); UiAuto.InputText("##projectFolder", ref projectFolder, 4096);
        ImGui.SameLine();
        if (UiAuto.Button("Browse")) PickFolder("Projects folder", path => projectFolder = path);
        if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(projectFolder)
            || workspace.IsBusy);
        if (UiAuto.Button("Create project", new(150 * scale, 0))) workspace.Run(() =>
        {
            string name = projectName.Trim();
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
                throw new IOException("Choose a valid project name.");
            string path = Path.Combine(Path.GetFullPath(projectFolder), name);
            EnsureOutsideInstall(path);
            if (!ConfirmMissionDraft()) return;
            workspace.CreateProject(path, name);
            RememberProject();
            ImGui.CloseCurrentPopup();
        });
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (UiAuto.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /** Adds a file as a source asset or a native entry; with a clone source it copies that asset instead. */
    private void DrawAddDialog()
    {
        CenterModal(600);
        if (!ImGui.BeginPopupModal("Add asset", ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (cloneSource is { } source) { ImGui.TextDisabled("Source"); ImGui.TextWrapped(source.Name); }
        else
        {
            ImGui.TextUnformatted("File");
            ImGui.SetNextItemWidth(465 * scale); UiAuto.InputText("##addPath", ref addPath, 4096);
            ImGui.SameLine();
            if (UiAuto.Button("Browse")) PickFile("All assets|*.*", path =>
            { addPath = path; addName = Path.GetFileName(path); });
        }
        ImGui.TextUnformatted("Name"); ImGui.SetNextItemWidth(-1);
        UiAuto.InputText("##addName", ref addName, 128);
        ImGui.SetNextItemWidth(-1);
        if (cloneSource is null) UiAuto.Combo("##assetKind", ref addKind, ["Source asset", "Native package entry"], 2);
        if (addKind == 1)
        {
            ImGui.SetNextItemWidth(140 * scale); UiAuto.InputText("Package ID", ref addPackageId, 4, ImGuiInputTextFlags.CharsHexadecimal);
            if (cloneSource is null)
            {
                ImGui.SetNextItemWidth(140 * scale); UiAuto.InputText("Class / reference", ref addReference, 8, ImGuiInputTextFlags.CharsHexadecimal);
                ImGui.SetNextItemWidth(140 * scale); UiAuto.InputInt("Type", ref addType);
                ImGui.SetNextItemWidth(140 * scale); UiAuto.InputInt("Subtype", ref addSubType);
            }
            else ImGui.TextDisabled("Stored as a delta from the original");
        }
        else ImGui.TextDisabled("Source file; excluded from package builds");
        if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
        ImGui.BeginDisabled(!workspace.CanEdit || cloneSource is null && !IconChecks.Exists(addPath) || string.IsNullOrWhiteSpace(addName));
        if (UiAuto.Button(cloneSource is null ? "Add asset" : "Create copy", new(150 * scale, 0))) workspace.Run(() =>
        {
            int kind = addKind;
            string path = addPath, name = addName;
            var original = cloneSource;
            ushort packageId = kind == 0 ? (ushort)0 : ushort.Parse(addPackageId, NumberStyles.HexNumber);
            NativeAssetMetadata? metadata = null;
            if (kind == 1)
            {
                if (addType is < 0 or > 127 || addSubType is < 0 or > 7)
                    throw new InvalidDataException("Type or subtype is out of range.");
                if (workspace.Catalog!.Entries.Any(entry => entry.PackageId == packageId))
                    throw new InvalidDataException("Choose an unused package ID.");
                metadata = new(packageId, original?.Entry.Reference ?? uint.Parse(addReference, NumberStyles.HexNumber),
                    original?.Entry.TypeInfo ?? (uint)((addType << 9) | (addSubType << 6)), name);
            }
            workspace.ImportAsset((project, catalog, token) => new([original is not null
                ? new(name, original.Bytes, metadata!, original.Entry.TagHash)
                : new ProjectAssetAddition(name, File.ReadAllBytes(path), metadata)]));
            cloneSource = null;
            ImGui.CloseCurrentPopup();
        });
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (UiAuto.Button("Cancel")) { cloneSource = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    /** Starts duplicating the selected object. */
    private void BeginDuplicate()
    {
        if (workspace.Document is not { AddedAssetId: null } source) return;
        // A texture's header names its pixel buffer, so a byte copy would share it.
        if (source.Entry.Type == 32) { workspace.Error = "A copy would share its pixels."; return; }
        cloneSource = source;
        addName = source.Name + " copy";
        addKind = 1;
        addDialog = true;
    }

    /** Asks for a project file and opens it. */
    private void OpenProjectDialog() => PickFile("Sunrise project|sunrise.project.json", OpenProject);

    /** Opens a project file and loads it into the workspace. */
    private void OpenProject(string path) => workspace.Run(() =>
    {
        if (!ConfirmMissionDraft()) return;
        workspace.OpenProject(path);
        RememberProject();
    });

    /** Context changes finish the draft decision before switching its owning project. */
    private bool ConfirmMissionDraft()
    {
        if (!missionGraph.IsDirty || options.Smoke) return true;
        bool previous = nativeDialog;
        nativeDialog = true;
        try
        {
            var answer = MessageBox.Show(this,
                missionGraph.CanSave ? "Save mission changes before continuing?" : "Discard the unsaved mission draft?",
                "Mission Graph", missionGraph.CanSave ? MessageBoxButtons.YesNoCancel : MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.Yes)
            {
                missionGraph.SaveCurrent(workspace);
                if (!missionGraph.IsDirty) return true;
                focusMissionGraph = true; return false;
            }
            missionGraph.DiscardDraft();
            return true;
        }
        finally { nativeDialog = previous; }
    }

    /** Remembers the open project in the recent-project list. */
    private void RememberProject()
    {
        settings.RememberProject(workspace.Project!.ManifestPath);
        SaveSettings();
        Text = $"{workspace.Project.Name} - Solforge";
    }

    /** Shows a folder picker and calls back with the chosen folder. */
    private void PickFolder(string title, Action<string> selected)
    {
        nativeDialog = true;
        try
        {
            using var dialog = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) workspace.Run(() => selected(dialog.SelectedPath));
        }
        finally { nativeDialog = false; }
    }

    /** Shows an open-file picker and calls back with the chosen file. */
    private void PickFile(string filter, Action<string> selected)
    {
        nativeDialog = true;
        try
        {
            using var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) workspace.Run(() => selected(dialog.FileName));
        }
        finally { nativeDialog = false; }
    }

    /** Shows a save-file picker and calls back with the chosen path. */
    private void SaveFile(string filter, string name, Action<string> save)
    {
        nativeDialog = true;
        try
        {
            var invalid = Path.GetInvalidFileNameChars().ToHashSet();
            name = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
            using var dialog = new SaveFileDialog { Filter = filter, FileName = name, AddExtension = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) workspace.Run(() =>
            {
                EnsureExternalExport(dialog.FileName);
                save(dialog.FileName);
            });
        }
        finally { nativeDialog = false; }
    }

    /** Exported originals stay outside delta projects and the installed game. */
    private void EnsureExternalExport(string path)
    {
        EnsureOutsideInstall(path);
        if (workspace.Project is { } project && IsWithin(path, Path.GetDirectoryName(project.ManifestPath)!))
            throw new IOException("Export to a working folder outside the project.");
    }

    /** Throws when a path is inside the game install (Solforge never writes there). */
    private void EnsureOutsideInstall(string path)
    {
        RejectLinks(path);
        if (workspace.Catalog is { } catalog && IsWithin(path, catalog.GameRoot))
            throw new IOException("Choose a folder outside the Sunrise installation.");
    }

    /** Throws when the path is a symbolic link or junction. */
    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Choose a path without links or junctions.");
    }

    /** Whether a path lies inside a folder. */
    private static bool IsWithin(string path, string folder)
    {
        string full = Path.GetFullPath(path), root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /** Opens a folder in the file explorer. */
    private static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo { FileName = Path.GetFullPath(path), UseShellExecute = true });
}

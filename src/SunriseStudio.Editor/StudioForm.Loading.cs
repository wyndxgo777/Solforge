using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private LoadingBackdrop? backdrop;
    private bool loadingCaptured;

    /** Resource upload callbacks may present loading frames only outside an active ImGui frame. */
    private void ReportPreparation(string stage, int completed, int total)
    {
        presentationProgress = new(stage, completed, total);
        EditorReady = false;
        if (closed || renderer is null || gui is null || guiFrameActive || nativeDialog
            || clock.Elapsed.TotalSeconds - lastLoadingFrame < 1d / 30) return;
        lastLoadingFrame = clock.Elapsed.TotalSeconds;
        renderer.Resize(ClientSize.Width, ClientSize.Height);
        guiFrameActive = true;
        try
        {
            gui.NewFrame((float)Math.Clamp(lastLoadingFrame - lastFrame, .001, .1));
            lastFrame = lastLoadingFrame;
            renderer.BeginFrame(StudioTheme.Canvas);
            DrawLoading();
            gui.Render();
            CaptureLoadingFrame();
            renderer.EndFrame(false);
        }
        finally { guiFrameActive = false; }
    }

    /** Capture a counted loading stage without changing the worker's completion barrier. */
    private void CaptureLoadingFrame()
    {
        if (loadingCaptured || EditorReady || options.CaptureLoading is not { } path
            || (presentationProgress ?? workspace.LoadingProgress) is not { Fraction: >= .15f and <= .9f }) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        renderer!.CapturePng(path);
        loadingCaptured = true;
    }

    /** Editor controls stay absent until the scene and its graphics resources are ready. */
    private void DrawLoading()
    {
        var client = new Vector2(ClientSize.Width, ClientSize.Height);
        BeginPanel("Loading", Vector2.Zero, client, ImGuiWindowFlags.NoScrollbar);
        double now = clock.Elapsed.TotalSeconds;
        var draw = ImGui.GetWindowDrawList();
        backdrop?.Draw(draw, client, scale, now);
        // Without a menu bar the top strip is the caption, so the window can move and close while loading.
        chrome.Scale = scale;
        chrome.CaptionHeight = 32 * scale;
        chrome.DrawButtons(32 * scale);
        DrawLoadingCard(draw, client, now);
        ImGui.End();
        if (setup || settingsDialog) DrawDialogs();
    }

    /** The launcher's hero side: the Sol mark and wordmark, the stage and count and the bar, in a card left of the emblem. */
    private void DrawLoadingCard(ImDrawListPtr draw, Vector2 client, double now)
    {
        bool failed = workspace.Error.Length != 0, working = !setup && !failed;
        var progress = presentationProgress ?? (workspace.IsBusy ? workspace.LoadingProgress
            : new StudioLoadProgress(viewport?.Animation?.IsLoading == true || assetViewport?.Animation?.IsLoading == true
                ? "Loading animation" : browserPreparing ? "Preparing asset browser" : "Preparing editor"));
        float pad = 24 * scale, width = Math.Max(200 * scale, Math.Min(400 * scale, client.X - 128 * scale - pad * 2));
        bool font = SolTheme.PushFont(SolTheme.TitleFont);
        float title = ImGui.GetTextLineHeight(), tracking = ImGui.GetFontSize() * .2f;
        if (font) ImGui.PopFont();
        // Centred on the emblem's line for the usual card; an error grows it downwards.
        float height = title + 14 * scale + ImGui.GetTextLineHeight() * 2 + 4 * scale;
        var origin = new Vector2(64 * scale + pad, MathF.Round((client.Y - height) / 2));
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(origin);
        ImGui.BeginGroup();
        float markEdge = backdrop is null ? 0 : MathF.Round(title);
        backdrop?.DrawMark(draw, origin, markEdge);
        font = SolTheme.PushFont(SolTheme.TitleFont);
        SolTheme.ShimmerText(draw, origin + new Vector2(markEdge > 0 ? markEdge + 10 * scale : 0, 0), "SOLFORGE", tracking,
            LoadingBackdrop.ReducedMotion ? 0 : (float)(now % 6 / 6));
        if (font) ImGui.PopFont();
        ImGui.Dummy(new Vector2(width, title + 10 * scale));
        if (failed) ImGui.TextColored(SolTheme.Bad, "Unable to load");
        else ImGui.TextUnformatted(setup ? "Sunrise location" : progress.Stage);
        if (working)
        {
            string count = progress.Total > 0 ? $"{progress.Completed:N0} / {progress.Total:N0}"
                : progress.Completed > 0 ? $"{progress.Completed:N0}" : "";
            if (count.Length > 0)
            {
                float countWidth = ImGui.CalcTextSize(count).X;
                ImGui.SameLine(); ImGui.SetCursorScreenPos(new(origin.X + width - countWidth, ImGui.GetCursorScreenPos().Y));
                ImGui.TextDisabled(count);
            }
            ImGui.Dummy(new Vector2(0, 4 * scale));
            DrawProgressBar(progress, width, 6 * scale);
        }
        if (failed)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextWrapped(workspace.Error);
            ImGui.PopTextWrapPos();
            if (!workspace.IsBusy && SolTheme.PrimaryButton("Choose Sunrise location")) setup = true;
        }
        ImGui.EndGroup();
        draw.ChannelsSetCurrent(0);
        SolTheme.ChamferFrame(draw, ImGui.GetItemRectMin() - new Vector2(pad), ImGui.GetItemRectMax() + new Vector2(pad),
            SolTheme.Cut, SolTheme.U32(SolTheme.Alpha(SolTheme.Panel, .72f)), SolTheme.U32(SolTheme.Line));
        draw.ChannelsMerge();
    }

    /** The launcher's .dl-progress: a sun-grad pill on a faint track with a sheen sweeping it; an unknown total moves a short segment. */
    private void DrawProgressBar(StudioLoadProgress progress, float width, float height)
    {
        var bar = ImGui.GetCursorScreenPos();
        if (height < ImGui.GetTextLineHeight()) bar.Y += (ImGui.GetTextLineHeight() - height) / 2;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(bar, bar + new Vector2(width, height), SolTheme.U32(SolTheme.Surface2), height / 2);
        bool still = LoadingBackdrop.ReducedMotion;
        double now = clock.Elapsed.TotalSeconds;
        if (progress.Fraction is { } fraction)
            SolTheme.ProgressFill(draw, bar, bar + new Vector2(width * Math.Clamp(fraction, 0, 1), height),
                still ? -1 : (float)(now % 2.4 / 2.4));
        else
        {
            float segment = width * .22f;
            float start = still ? (width - segment) / 2 : (float)(now % 1.6 / 1.6) * (width + segment) - segment;
            SolTheme.ProgressFill(draw, bar + new Vector2(Math.Max(0, start), 0), bar + new Vector2(Math.Min(width, start + segment), height));
        }
        ImGui.Dummy(new Vector2(width, MathF.Max(height, ImGui.GetTextLineHeight())));
    }
}

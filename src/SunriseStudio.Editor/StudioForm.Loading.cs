using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private static readonly Vector4 LogoColor = SolTheme.Sun;
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
        BeginPanel("Loading", Vector2.Zero, new(ClientSize.Width, ClientSize.Height), ImGuiWindowFlags.NoScrollbar);
        // Without a menu bar the top strip is the caption, so the window can move and close while loading.
        chrome.Scale = scale;
        chrome.CaptionHeight = 32 * scale;
        chrome.DrawButtons(32 * scale);
        float width = Math.Min(440 * scale, ClientSize.Width - 64 * scale);
        var origin = new Vector2((ClientSize.Width - width) / 2, ClientSize.Height * .42f);
        ImGui.SetCursorScreenPos(origin);
        ImGui.BeginGroup();
        DrawSunSpinner(origin, 32 * scale, !setup && workspace.Error.Length == 0);
        ImGui.Dummy(new Vector2(32, 32) * scale);
        ImGui.SameLine(0, 12 * scale);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6 * scale);
        ImGui.TextColored(LogoColor, "SOLFORGE");
        ImGui.SetCursorScreenPos(origin + new Vector2(0, 50) * scale);
        var progress = presentationProgress ?? (workspace.IsBusy ? workspace.LoadingProgress
            : new StudioLoadProgress(viewport?.Animation?.IsLoading == true || assetViewport?.Animation?.IsLoading == true
                ? "Loading animation" : browserPreparing ? "Preparing asset browser" : "Preparing editor"));
        ImGui.TextUnformatted(setup ? "Sunrise location" : workspace.Error.Length != 0 ? "Unable to load" : progress.Stage);
        if (!setup && workspace.Error.Length == 0)
        {
            string count = progress.Total > 0 ? $"{progress.Completed:N0} / {progress.Total:N0}"
                : progress.Completed > 0 ? $"{progress.Completed:N0}" : "";
            if (count.Length > 0)
            {
                float countWidth = ImGui.CalcTextSize(count).X;
                ImGui.SameLine(); ImGui.SetCursorScreenPos(new(origin.X + width - countWidth, ImGui.GetCursorScreenPos().Y));
                ImGui.TextDisabled(count);
            }
            ImGui.Spacing();
            DrawProgressBar(progress, width, 7 * scale);
        }
        if (workspace.Error.Length != 0)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextWrapped(workspace.Error);
            ImGui.PopTextWrapPos();
            // A drive that dropped out comes back, so the same location can open again.
            bool retry = !workspace.IsBusy && workspace.Catalog is null && location.Length != 0;
            if (retry && ImGui.Button("Retry"))
                workspace.Run(() => workspace.OpenInstallation(location, keySource));
            if (retry) ImGui.SameLine();
            if (!workspace.IsBusy && ImGui.Button("Choose Sunrise location")) setup = true;
        }
        ImGui.EndGroup();
        ImGui.End();
        if (setup || settingsDialog) DrawDialogs();
    }

    /** An unknown total draws a moving segment instead of a fill. */
    private void DrawProgressBar(StudioLoadProgress progress, float width, float height)
    {
        var bar = ImGui.GetCursorScreenPos();
        if (height < ImGui.GetTextLineHeight()) bar.Y += (ImGui.GetTextLineHeight() - height) / 2;
        var size = new Vector2(width, height);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(bar, bar + size, ImGui.GetColorU32(ImGuiCol.FrameBg), 3 * scale);
        if (progress.Fraction is { } fraction)
            SolTheme.ChamferGradient(draw, bar, bar + new Vector2(size.X * fraction, size.Y), 0);
        else
        {
            float offset = (float)(clock.Elapsed.TotalSeconds % 1.6 / 1.6) * (width + width * .22f) - width * .22f;
            draw.PushClipRect(bar, bar + size, true);
            draw.AddRectFilled(bar + new Vector2(offset, 0), bar + new Vector2(offset + width * .22f, size.Y),
                ImGui.GetColorU32(LogoColor), 3 * scale);
            draw.PopClipRect();
        }
        ImGui.Dummy(new Vector2(width, MathF.Max(height, ImGui.GetTextLineHeight())));
    }

    /** Rotation transforms only the displayed quad; the supplied artwork remains unchanged. */
    private void DrawSunSpinner(Vector2 origin, float size, bool spin)
    {
        if (loadingLogo is null) return;
        var center = origin + new Vector2(size / 2);
        float angle = spin ? (float)(clock.Elapsed.TotalSeconds % 6 / 6 * Math.Tau) : 0;
        float sine = MathF.Sin(angle), cosine = MathF.Cos(angle);
        Vector2 Corner(float x, float y) => center + new Vector2(x * cosine - y * sine, x * sine + y * cosine) * (size / 2);
        ImGui.GetWindowDrawList().AddImageQuad(loadingLogo.ImGuiId,
            Corner(-1, -1), Corner(1, -1), Corner(1, 1), Corner(-1, 1),
            Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY, uint.MaxValue);
    }
}

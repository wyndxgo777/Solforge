using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace SunriseStudio.Viewport;

/**
 * Sol design language (Studio.md §3.10): the token set shared with the Sol website and launcher
 * (sol-launcher/src/sol.css), the bundled Chakra Petch / Inter faces and the draw helpers built on them.
 * Pages take colours from here instead of writing their own.
 */
public static unsafe class SolTheme
{
    // Tokens from the Project Sol website (sol-website/assets/site.css on the cyberpulse night theme), named as
    // there; the launcher (sol-launcher) uses the same values. Neutral greys, no blue cast.
    /** --bg: hsl(0 0% 5%). */
    public static readonly Vector4 Bg = Hex(0x0d0d0d);
    /** --bg2: night background-100, hsl(0 0% 10%). */
    public static readonly Vector4 Bg2 = Hex(0x1a1a1a);
    /** --panel: hsl(0 0% 9%). */
    public static readonly Vector4 Panel = Hex(0x171717);
    public static readonly Vector4 PanelTop = Hex(0x181818);
    public static readonly Vector4 PanelBottom = Hex(0x141414);
    /** The site's scrolled top bar, hsla(0 0% 7% / .96). */
    public static readonly Vector4 Bar = Hex(0x121212);
    /** Form field fill, hsla(0 0% 100% / .05); hovered and focused fields go to .08. */
    public static readonly Vector4 Surface = White(.05f);
    public static readonly Vector4 Surface2 = White(.08f);
    /** --line and --line-strong. */
    public static readonly Vector4 Line = White(.10f);
    public static readonly Vector4 Line2 = White(.24f);
    /** --text (night contrast-100, 98%). */
    public static readonly Vector4 Text = Hex(0xfafafa);
    /** --muted (night contrast-300, 75%): secondary text and resting icons. */
    public static readonly Vector4 Muted = Hex(0xbfbfbf);
    /** --faint (52%): hints and disabled text. */
    public static readonly Vector4 Dim = Hex(0x858585);
    /** One step under --faint, for rules and placeholder glyphs. */
    public static readonly Vector4 Faint = Hex(0x5c5c5c);
    /** --accent (--cpl-color-brand) and --accent-hi. */
    public static readonly Vector4 Sun = Hex(0xffb648);
    public static readonly Vector4 SunHi = Hex(0xffcb7a);
    public static readonly Vector4 Sun2 = Hex(0xff7a2f);
    public static readonly Vector4 SunGradStart = Hex(0xffd88a);
    public static readonly Vector4 SunGradEnd = Hex(0xff9a45);
    /** The light ends of sun-shimmer (#ffe0a0 -> sun -> sun-2 -> sun -> #ffe0a0). */
    public static readonly Vector4 SunShimmerEdge = Hex(0xffe0a0);
    /** --accent-ink: text on a sun fill. */
    public static readonly Vector4 AccentInk = Hex(0x141006);
    public static readonly Vector4 Ok = Hex(0x5fd38a);
    public static readonly Vector4 Bad = Hex(0xf07a7a);

    // Type scale and shapes, in pixels at 96 DPI. Body is Inter (the site's --font-body), headings and buttons
    // Chakra Petch (--font-head). Cuts are the site's --cut, --cut-sm and --cut-xs chamfers; corners are never rounded.
    public const float TitleSize = 17, HeadingSize = 14, BodySize = 13, HintSize = 12, LabelSize = 11, ButtonSize = 12.5f;
    public const float Cut = 14, CutSmall = 8, CutTiny = 5, ControlHeight = 36, ControlSmall = 28;

    /** The DPI scale from the last Apply. */
    public static float Scale { get; private set; } = 1;
    /** Chakra Petch 600 at body size: panel titles, tabs, mode buttons. */
    public static ImFontPtr HeadingFont { get; private set; }
    /** Chakra Petch 600 at button size: primary and secondary buttons. */
    public static ImFontPtr ButtonFont { get; private set; }
    /** Chakra Petch 700 at label size: uppercase section headers. */
    public static ImFontPtr LabelFont { get; private set; }
    /** Chakra Petch 700 at title size: the SOLFORGE wordmark and dialog titles. */
    public static ImFontPtr TitleFont { get; private set; }
    /** Inter 600 at body size: emphasised body text. */
    public static ImFontPtr StrongFont { get; private set; }

    private static readonly string[] FaceNames = ["Inter-Regular", "Inter-SemiBold", "ChakraPetch-SemiBold", "ChakraPetch-Bold"];
    // Native copies of the embedded faces. The atlas reads them on every rebuild and never frees them.
    private static (IntPtr Data, int Length)[]? faces;
    private static bool facesTried;
    private static readonly string[] Glyphs = Enumerable.Range(0, 128).Select(code => ((char)code).ToString()).ToArray();

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex { public Vector2 Position; public Vector2 Uv; public uint Color; }

    private static Vector4 Hex(uint rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1);
    private static Vector4 White(float alpha) => new(1, 1, 1, alpha);
    public static Vector4 Alpha(Vector4 color, float alpha) => color with { W = alpha };
    public static uint U32(Vector4 color) => ImGui.GetColorU32(color);

    /**
     * Installs the bundled faces for ImGuiController.HostFonts. The controller's body size is 16 px at
     * 96 DPI, so its ratio to 16 is the DPI scale. Returns null, adding nothing, when a face is missing.
     */
    public static ImFontPtr? AddFonts(ImFontAtlasPtr fonts, float controllerBodySize)
    {
        HeadingFont = default; ButtonFont = default; LabelFont = default; TitleFont = default; StrongFont = default;
        if (!LoadFaces() || faces is not { } loaded) return null;
        float scale = Math.Max(1, controllerBodySize / 16f);
        AddFace(fonts, loaded[0], BodySize * scale); // first face: ImGui's default font
        StrongFont = AddFace(fonts, loaded[1], BodySize * scale);
        HeadingFont = AddFace(fonts, loaded[2], HeadingSize * scale);
        ButtonFont = AddFace(fonts, loaded[2], ButtonSize * scale);
        LabelFont = AddFace(fonts, loaded[3], LabelSize * scale);
        TitleFont = AddFace(fonts, loaded[3], TitleSize * scale);
        return TitleFont;
    }

    private static bool LoadFaces()
    {
        if (facesTried) return faces is not null;
        facesTried = true;
        var loaded = new (IntPtr Data, int Length)[FaceNames.Length];
        for (int i = 0; i < FaceNames.Length; i++)
        {
            using var stream = typeof(SolTheme).Assembly.GetManifestResourceStream("Solforge.Fonts." + FaceNames[i]);
            if (stream is null || stream.Length is <= 0 or > 4 << 20)
            {
                for (int j = 0; j < i; j++) Marshal.FreeHGlobal(loaded[j].Data);
                return false;
            }
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var data = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, data, bytes.Length);
            loaded[i] = (data, bytes.Length);
        }
        faces = loaded;
        return true;
    }

    private static ImFontPtr AddFace(ImFontAtlasPtr fonts, (IntPtr Data, int Length) face, float pixels)
    {
        ImFontConfigPtr config = ImGuiNative.ImFontConfig_ImFontConfig();
        config.FontDataOwnedByAtlas = false;
        var font = fonts.AddFontFromMemoryTTF(face.Data, face.Length, MathF.Round(pixels), config);
        config.Destroy();
        return font;
    }

    /** Pushes a bundled face when it is loaded; pop only when this returns true. */
    public static bool PushFont(ImFontPtr font)
    {
        if (font.NativePtr == null) return false;
        ImGui.PushFont(font);
        return true;
    }

    /** Sol colours, shapes and sizes on the current context; safe to call again on a DPI change. */
    public static void Apply(float scale)
    {
        Scale = Math.Max(1, scale);
        var style = ImGui.GetStyle();
        // Start from ImGui's defaults, so a second call does not scale the sizes twice.
        var defaults = ImGuiNative.ImGuiStyle_ImGuiStyle();
        *style.NativePtr = *defaults;
        ImGuiNative.ImGuiStyle_destroy(defaults);
        ImGui.StyleColorsDark();
        style.WindowPadding = new(12, 10);
        // Body text plus this padding gives the launcher's small control height.
        style.FramePadding = new(10, (ControlSmall - BodySize) / 2);
        style.ItemSpacing = new(8, 6);
        style.ItemInnerSpacing = new(6, 4);
        // Square corners everywhere; chamfers are drawn by the helpers below.
        style.WindowRounding = 0;
        style.ChildRounding = 0;
        style.PopupRounding = 0;
        style.FrameRounding = 0;
        style.GrabRounding = 0;
        style.TabRounding = 0;
        style.ScrollbarRounding = 0;
        style.WindowBorderSize = 0;
        style.ChildBorderSize = 1;
        style.PopupBorderSize = 1;
        style.FrameBorderSize = 1;
        style.TabBorderSize = 0;
        style.TabBarBorderSize = 1;
        style.TabBarOverlineSize = 2;
        style.ScrollbarSize = 10;
        style.GrabMinSize = 10;
        style.SeparatorTextBorderSize = 1;
        style.SeparatorTextPadding = new(0, 4);
        var colors = style.Colors;
        void Set(ImGuiCol slot, Vector4 value) => colors[(int)slot] = value;
        var clear = Vector4.Zero;
        Set(ImGuiCol.Text, Text);
        Set(ImGuiCol.TextDisabled, Dim);
        Set(ImGuiCol.WindowBg, Panel);
        Set(ImGuiCol.ChildBg, Panel);
        // The site's dropdown option fill.
        Set(ImGuiCol.PopupBg, Alpha(Hex(0x151515), .98f));
        // Thin --line borders, as the site's cards; focus and selection add the sun.
        Set(ImGuiCol.Border, Line);
        Set(ImGuiCol.BorderShadow, clear);
        Set(ImGuiCol.FrameBg, Surface);
        Set(ImGuiCol.FrameBgHovered, Surface2);
        Set(ImGuiCol.FrameBgActive, Alpha(Sun, .14f));
        Set(ImGuiCol.TitleBg, Bar);
        Set(ImGuiCol.TitleBgActive, Bar);
        Set(ImGuiCol.TitleBgCollapsed, Bar);
        Set(ImGuiCol.MenuBarBg, Bar);
        Set(ImGuiCol.ScrollbarBg, clear);
        Set(ImGuiCol.ScrollbarGrab, Line2);
        Set(ImGuiCol.ScrollbarGrabHovered, White(.22f));
        Set(ImGuiCol.ScrollbarGrabActive, Sun);
        Set(ImGuiCol.CheckMark, Sun);
        Set(ImGuiCol.SliderGrab, Sun);
        Set(ImGuiCol.SliderGrabActive, Sun2);
        Set(ImGuiCol.Button, Surface);
        Set(ImGuiCol.ButtonHovered, White(.12f));
        Set(ImGuiCol.ButtonActive, Alpha(Sun, .24f));
        // Selection is a faint sun wash; helpers add the sun bar or underline.
        Set(ImGuiCol.Header, Alpha(Sun, .12f));
        Set(ImGuiCol.HeaderHovered, Surface2);
        Set(ImGuiCol.HeaderActive, Alpha(Sun, .2f));
        Set(ImGuiCol.Separator, Line);
        Set(ImGuiCol.SeparatorHovered, Sun);
        Set(ImGuiCol.SeparatorActive, Sun);
        Set(ImGuiCol.ResizeGrip, clear);
        Set(ImGuiCol.ResizeGripHovered, Alpha(Sun, .5f));
        Set(ImGuiCol.ResizeGripActive, Sun);
        Set(ImGuiCol.Tab, clear);
        Set(ImGuiCol.TabHovered, Surface2);
        Set(ImGuiCol.TabSelected, Surface);
        Set(ImGuiCol.TabSelectedOverline, Sun);
        Set(ImGuiCol.TabDimmed, clear);
        Set(ImGuiCol.TabDimmedSelected, Surface);
        Set(ImGuiCol.TabDimmedSelectedOverline, Alpha(Sun, .5f));
        Set(ImGuiCol.PlotLines, Dim);
        Set(ImGuiCol.PlotLinesHovered, Sun);
        Set(ImGuiCol.PlotHistogram, Sun);
        Set(ImGuiCol.PlotHistogramHovered, Sun2);
        Set(ImGuiCol.TableHeaderBg, Bg2);
        Set(ImGuiCol.TableBorderStrong, Line2);
        Set(ImGuiCol.TableBorderLight, Line);
        Set(ImGuiCol.TableRowBg, clear);
        Set(ImGuiCol.TableRowBgAlt, White(.02f));
        Set(ImGuiCol.TextLink, Sun);
        Set(ImGuiCol.TextSelectedBg, Alpha(Sun, .28f));
        Set(ImGuiCol.DragDropTarget, Sun);
        Set(ImGuiCol.NavCursor, Sun);
        Set(ImGuiCol.NavWindowingHighlight, Sun);
        Set(ImGuiCol.NavWindowingDimBg, Alpha(Bg, .6f));
        Set(ImGuiCol.ModalWindowDimBg, Alpha(Bg, .6f));
        style.ScaleAllSizes(Scale);
        ImGui.GetIO().FontGlobalScale = 1;
    }

    // ---- Draw helpers ----

    /** The launcher's --chamfer polygon: top-left and bottom-right corners cut. */
    public static void ChamferPath(ImDrawListPtr draw, Vector2 min, Vector2 max, float cut)
    {
        cut = Math.Clamp(cut, 0, Math.Min(max.X - min.X, max.Y - min.Y) * .5f);
        if (cut <= 0) { draw.PathRect(min, max); return; }
        draw.PathLineTo(new(min.X + cut, min.Y));
        draw.PathLineTo(new(max.X, min.Y));
        draw.PathLineTo(new(max.X, max.Y - cut));
        draw.PathLineTo(new(max.X - cut, max.Y));
        draw.PathLineTo(new(min.X, max.Y));
        draw.PathLineTo(new(min.X, min.Y + cut));
    }

    /** A chamfered box; a zero colour skips the fill or the border. Cut is in unscaled pixels. */
    public static void ChamferFrame(ImDrawListPtr draw, Vector2 min, Vector2 max, float cut, uint fill, uint border = 0)
    {
        if (max.X - min.X < 1 || max.Y - min.Y < 1) return;
        if (fill != 0) { ChamferPath(draw, min, max, cut * Scale); draw.PathFillConvex(fill); }
        if (border != 0) { ChamferPath(draw, min, max, cut * Scale); draw.PathStroke(border, ImDrawFlags.Closed, 1); }
    }

    /** A chamfered box filled with sun-grad (#ffd88a -> #ffb648 -> #ff9a45, left to right). */
    public static void ChamferGradient(ImDrawListPtr draw, Vector2 min, Vector2 max, float cut, float alpha = 1)
    {
        if (max.X - min.X < 1 || max.Y - min.Y < 1) return;
        int first = draw.VtxBuffer.Size;
        ChamferPath(draw, min, max, cut * Scale);
        uint opaque = (uint)Math.Clamp(alpha * 255, 0, 255) << 24 | 0x00FFFFFF;
        draw.PathFillConvex(opaque);
        // Recolour the fill's vertices (and its anti-aliased fringe) by x, keeping each vertex's alpha.
        var vertices = (Vertex*)(void*)draw.VtxBuffer.Data;
        float width = max.X - min.X;
        for (int i = first; i < draw.VtxBuffer.Size; i++)
        {
            float t = Math.Clamp((vertices[i].Position.X - min.X) / width, 0, 1);
            var color = t < .5f ? Vector4.Lerp(SunGradStart, Sun, t * 2) : Vector4.Lerp(Sun, SunGradEnd, (t - .5f) * 2);
            uint rgb = ImGui.ColorConvertFloat4ToU32(color with { W = 1 }) & 0x00FFFFFF;
            vertices[i].Color = (vertices[i].Color & 0xFF000000) | rgb;
        }
    }

    /**
     * The launcher's .dl-bar: a pill filled with #ffd88a -> sun -> sun-2 along x, drawn as slices so the middle
     * stop holds. A sheen in [0, 1) sweeps a pale band across it; a negative sheen leaves it still.
     */
    public static void ProgressFill(ImDrawListPtr draw, Vector2 min, Vector2 max, float sheen = -1)
    {
        float width = max.X - min.X, height = max.Y - min.Y, radius = height / 2;
        if (width < 1 || height < 1) return;
        if (width <= height) { draw.AddRectFilled(min, max, U32(SunGradStart), radius); return; }
        float band = Math.Max(height * 8, width * .18f), center = min.X - band + (width + band * 2) * sheen;
        Vector4 At(float x)
        {
            float t = (x - min.X) / width, u = (x - center) / band;
            var color = t < .5f ? Vector4.Lerp(SunGradStart, Sun, t * 2) : Vector4.Lerp(Sun, Sun2, (t - .5f) * 2);
            return sheen < 0 || u <= -1 || u >= 1 ? color : Vector4.Lerp(color, Text, (1 - u * u) * (1 - u * u) * .45f);
        }
        float left = min.X + radius, right = max.X - radius, y = min.Y + radius;
        draw.AddCircleFilled(new(left, y), radius, U32(At(left)), 12);
        draw.AddCircleFilled(new(right, y), radius, U32(At(right)), 12);
        int slices = Math.Clamp((int)((right - left) / (3 * Scale)), 1, 128);
        for (int i = 0; i < slices; i++)
        {
            float x0 = left + (right - left) * i / slices, x1 = left + (right - left) * (i + 1) / slices;
            uint a = U32(At(x0)), b = U32(At(x1));
            draw.AddRectFilledMultiColor(new(x0, min.Y), new(x1, max.Y), a, b, b, a);
        }
    }

    /**
     * The panel gradient (#13151d -> #0d0e14, top to bottom) behind the current window's content.
     * A window that pushed its own background (a splitter, an overlay) keeps it.
     */
    public static void PanelGradient()
    {
        if (ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg] != Panel) return;
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        uint top = U32(PanelTop), bottom = U32(PanelBottom);
        ImGui.GetWindowDrawList().AddRectFilledMultiColor(min, max, top, top, bottom, bottom);
    }

    /** Flat bg-2 with a top hairline: the status bar. */
    public static void StatusBackground()
    {
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, U32(Bg2));
        draw.AddLine(min, new(max.X, min.Y), U32(Line), 1);
    }

    /** Draws text with letter spacing, as the launcher's uppercase labels; returns its width. */
    public static float TrackedText(ImDrawListPtr draw, Vector2 position, uint color, string text, float tracking)
    {
        float x = position.X;
        for (int i = 0; i < text.Length; i++)
        {
            string glyph = text[i] < Glyphs.Length ? Glyphs[text[i]] : text[i].ToString();
            draw.AddText(new(x, position.Y), color, glyph);
            x += ImGui.CalcTextSize(glyph).X + (i + 1 < text.Length ? tracking : 0);
        }
        return x - position.X;
    }

    /** sun-shimmer at t; the gradient tiles, so moving t sweeps it as the launcher's 6 s shimmer does. */
    public static Vector4 Shimmer(float t)
    {
        t = (t % 1 + 1) % 1 * 4;
        int stop = Math.Min(3, (int)t);
        var (from, to) = stop switch { 0 => (SunShimmerEdge, Sun), 1 => (Sun, Sun2), 2 => (Sun2, Sun), _ => (Sun, SunShimmerEdge) };
        return Vector4.Lerp(from, to, t - stop);
    }

    /**
     * Tracked text filled with sun-shimmer, as the launcher's h1 span: the text spans half the gradient
     * (background-size 200%) and phase in [0, 1) is the sweep. Returns its width.
     */
    public static float ShimmerText(ImDrawListPtr draw, Vector2 position, string text, float tracking, float phase)
    {
        int first = draw.VtxBuffer.Size;
        float width = Math.Max(1, TrackedText(draw, position, uint.MaxValue, text, tracking));
        var vertices = (Vertex*)(void*)draw.VtxBuffer.Data;
        for (int i = first; i < draw.VtxBuffer.Size; i++)
        {
            float t = Math.Clamp((vertices[i].Position.X - position.X) / width, 0, 1);
            uint rgb = ImGui.ColorConvertFloat4ToU32(Shimmer(t * .5f - phase) with { W = 1 }) & 0x00FFFFFF;
            vertices[i].Color = (vertices[i].Color & 0xFF000000) | rgb;
        }
        return width;
    }

    public static float TrackedWidth(string text, float tracking)
    {
        float width = 0;
        for (int i = 0; i < text.Length; i++)
            width += ImGui.CalcTextSize(text[i] < Glyphs.Length ? Glyphs[text[i]] : text[i].ToString()).X;
        return width + tracking * Math.Max(0, text.Length - 1);
    }

    /** Uppercase Chakra Petch label in sun over a hairline rule, as the launcher's group headers. */
    public static void SectionHeader(string text, string tooltip = "")
    {
        bool font = PushFont(LabelFont);
        string label = text.ToUpperInvariant();
        float tracking = ImGui.GetFontSize() * .2f;
        var draw = ImGui.GetWindowDrawList();
        ImGui.Dummy(new(0, 4 * Scale));
        var start = ImGui.GetCursorScreenPos();
        float width = Math.Max(1, ImGui.GetContentRegionAvail().X);
        float height = ImGui.GetTextLineHeight() + 5 * Scale;
        ImGui.Dummy(new(width, height));
        TrackedText(draw, start, U32(Sun), label, tracking);
        draw.AddLine(new(start.X, start.Y + height - 1), new(start.X + width, start.Y + height - 1), U32(Line), 1);
        if (font) ImGui.PopFont();
        if (tooltip.Length != 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
    }

    /** A collapsing group drawn as a section header with a fold arrow; the label's id is kept. */
    public static bool CollapsingSection(string label, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
    {
        int split = label.IndexOf("##", StringComparison.Ordinal);
        string shown = (split < 0 ? label : label[..split]).ToUpperInvariant();
        bool font = PushFont(LabelFont);
        ImGui.PushStyleColor(ImGuiCol.Header, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Surface);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Surface2);
        ImGui.PushStyleColor(ImGuiCol.Text, Sun);
        // The visible text is drawn tracked below; the whole original label stays in the id, so ids stay unique.
        bool open = ImGui.CollapsingHeader("##" + label, flags);
        ImGui.PopStyleColor(4);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var draw = ImGui.GetWindowDrawList();
        float arrow = ImGui.GetFontSize() + ImGui.GetStyle().FramePadding.X * 2;
        float textTop = min.Y + (max.Y - min.Y - ImGui.GetTextLineHeight()) / 2;
        TrackedText(draw, new(min.X + arrow, textTop), U32(Sun), shown, ImGui.GetFontSize() * .2f);
        draw.AddLine(new(min.X, max.Y - 1), new(max.X, max.Y - 1), U32(Line), 1);
        if (font) ImGui.PopFont();
        return open;
    }

    /** The site's primary button: chamfered brand fill, accent-ink uppercase Chakra Petch label. */
    public static bool PrimaryButton(string label, Vector2 size = default)
    {
        int split = label.IndexOf("##", StringComparison.Ordinal);
        string shown = (split < 0 ? label : label[..split]).ToUpperInvariant();
        bool font = PushFont(ButtonFont);
        float tracking = ImGui.GetFontSize() * .1f;
        float textWidth = TrackedWidth(shown, tracking);
        if (size.X <= 0) size.X = textWidth + 32 * Scale;
        if (size.Y <= 0) size.Y = ImGui.GetFrameHeight();
        bool clicked = ImGui.InvisibleButton(label, Vector2.Max(size, Vector2.One));
        bool hovered = ImGui.IsItemHovered(), held = ImGui.IsItemActive();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        if (held) { min.Y += 1; max.Y += 1; }
        var draw = ImGui.GetWindowDrawList();
        // The site's .btn.primary: flat brand fill, inverted to --text on hover, --accent-hi while pressed.
        var fill = held ? SunHi : hovered ? Text : Sun;
        ChamferFrame(draw, min, max, CutSmall, U32(fill));
        var textPosition = new Vector2(min.X + (max.X - min.X - textWidth) / 2, min.Y + (max.Y - min.Y - ImGui.GetTextLineHeight()) / 2);
        TrackedText(draw, textPosition, U32(AccentInk), shown, tracking);
        if (font) ImGui.PopFont();
        return clicked;
    }

    /** The width PrimaryButton gives the label, for laying out a row before drawing it. */
    public static float PrimaryButtonWidth(string label)
    {
        int split = label.IndexOf("##", StringComparison.Ordinal);
        string shown = (split < 0 ? label : label[..split]).ToUpperInvariant();
        bool font = PushFont(ButtonFont);
        float width = TrackedWidth(shown, ImGui.GetFontSize() * .1f) + 32 * Scale;
        if (font) ImGui.PopFont();
        return width;
    }

    /** A small mode button (Object / Asset): no fill when active, a sun underline instead. */
    public static bool ModeButton(string label, bool active)
    {
        bool font = PushFont(HeadingFont);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, active ? Text : Dim);
        bool clicked = UiAuto.SmallButton(label);
        ImGui.PopStyleColor(2);
        if (font) ImGui.PopFont();
        if (active) Underline();
        return clicked;
    }

    /** A 2 px sun line under the last item: the active tab or mode. */
    public static void Underline()
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddRectFilled(new(min.X, max.Y - 2 * Scale), max, U32(Sun));
    }

    /** A 3 px sun bar on the left of a row: the selected list row. */
    public static void LeftBar(ImDrawListPtr draw, Vector2 min, Vector2 max)
        => draw.AddRectFilled(min, new(min.X + 3 * Scale, max.Y), U32(Sun));

    /** The launcher's title notch: a small sun-grad tab with its bottom-left corner cut, before a panel title. */
    public static void TitleNotch(ImDrawListPtr draw, Vector2 min, float height)
    {
        float width = 6 * Scale;
        int first = draw.VtxBuffer.Size;
        draw.PathLineTo(min);
        draw.PathLineTo(new(min.X + width, min.Y));
        draw.PathLineTo(new(min.X + width, min.Y + height * .7f));
        draw.PathLineTo(new(min.X + width * .4f, min.Y + height));
        draw.PathLineTo(new(min.X, min.Y + height));
        draw.PathFillConvex(U32(Sun));
        var vertices = (Vertex*)(void*)draw.VtxBuffer.Data;
        for (int i = first; i < draw.VtxBuffer.Size; i++)
        {
            float t = Math.Clamp((vertices[i].Position.Y - min.Y) / Math.Max(1, height), 0, 1);
            uint rgb = ImGui.ColorConvertFloat4ToU32(Vector4.Lerp(SunGradStart, SunGradEnd, t) with { W = 1 }) & 0x00FFFFFF;
            vertices[i].Color = (vertices[i].Color & 0xFF000000) | rgb;
        }
    }

    /** Space a panel header leaves for its title notch. */
    public static float HeaderIndent => 12 * Scale;

    /**
     * A panel header in the launcher's drawer style: hairline below the header window, sun notch, and the
     * uppercase Chakra Petch title drawn where the next item (an editor combo with an empty preview) sits.
     */
    public static void PanelHeader(string title, float itemWidth)
    {
        var draw = ImGui.GetWindowDrawList();
        var windowMin = ImGui.GetWindowPos();
        var windowMax = windowMin + ImGui.GetWindowSize();
        draw.AddLine(new(windowMin.X, windowMax.Y - 1), new(windowMax.X, windowMax.Y - 1), U32(Line), 1);
        var at = ImGui.GetCursorScreenPos();
        float frame = ImGui.GetFrameHeight();
        TitleNotch(draw, new(at.X, at.Y + frame * .22f), frame * .56f);
        bool font = PushFont(HeadingFont);
        var textAt = new Vector2(at.X + HeaderIndent + ImGui.GetStyle().FramePadding.X, at.Y + (frame - ImGui.GetTextLineHeight()) / 2);
        var clipMax = new Vector2(Math.Max(textAt.X, at.X + HeaderIndent + itemWidth - frame), at.Y + frame);
        draw.PushClipRect(at, clipMax, true);
        TrackedText(draw, textAt, U32(Text), title.ToUpperInvariant(), ImGui.GetFontSize() * .14f);
        draw.PopClipRect();
        if (font) ImGui.PopFont();
        ImGui.SetCursorScreenPos(new(at.X + HeaderIndent, at.Y));
    }

    /** Colours for a header combo that reads as a title; pop 3 after the combo (and its EndCombo). */
    public static void PushHeaderCombo()
    {
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
    }

    /** A viewport label chip: panel fill, line-2 border, sun border when the pointer is over it. */
    public static void Chip(ImDrawListPtr draw, Vector2 min, Vector2 max, bool hovered)
        => ChamferFrame(draw, min, max, CutSmall * .5f, U32(Alpha(Panel, .92f)), U32(hovered ? Sun : Line2));

    /** Icon tint: muted at rest, text on hover, sun when active. */
    public static uint IconTint(bool active, bool hovered) => U32(active ? Sun : hovered ? Text : Muted);
}

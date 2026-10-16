using System.Numerics;
using ImGuiNET;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using System.Buffers.Binary;

/** Console entry for the GPU CUI tests; flags select the game root, output folder and the audit or inspection modes. */
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            CuiMutationUiChecks.Run();
            CuiComparisonDraftChecks.Run();
            if (args is ["--mutation-audit"]) return 0;
            if (args is ["--scan", var scanRoot]) { CuiWrappedInspection.Run(scanRoot); return 0; }
            if (args is ["--authoring", var game, var capture]) { CuiAuthoringUiChecks.Run(game, capture); return 0; }
            if (args is ["--bindings", var bindingGame, var bindingCapture]) { CuiBindingUiChecks.Run(bindingGame, bindingCapture); return 0; }
            string? gameRoot = null;
            string output = Path.Combine(Path.GetTempPath(), "SunriseStudio-cui-fonts");
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || args[i] is not ("--game-root" or "--output"))
                {
                    Console.Error.WriteLine("usage: [--game-root <dir>] [--output <folder>] | --mutation-audit"
                        + " | --scan <dir> | --authoring <dir> <png> | --bindings <dir> <png>");
                    return 2;
                }
                if (args[i] == "--game-root") gameRoot = args[i + 1];
                else output = args[i + 1];
            }
            if (gameRoot is null)
            {
                CuiOverrideUiChecks.Run();
                Console.WriteLine("cui_font installed=skipped reason=no_game_root");
                return 0;
            }
            using var catalog = PackageCatalog.Open(gameRoot, cacheOptions: new() { Enabled = false });
            var preview = CuiPreviewData.Read(catalog, 0x80BC722A, 0x80804825, catalog.Read(0x80BC722A),
                hash => hash == 0xF9C88800 ? "Waiting for Other Players" : null)!;
            var owner = preview.Document!.Components.First(component => component.Key == 0x543);
            var content = preview.LocalContent[owner.Offset];
            var style = content.NativeText ?? throw new Exception(content.Error);
            Check(style.Size == 22 && style.Casing == 1 && style.AutoWidth, "Native waiting style differs.");
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(output);
            var hardware = Run(style, content.Text, Path.Combine(output, "native-font-hardware.png"), false);
            var warp = Run(style, content.Text, Path.Combine(output, "native-font-warp.png"), true);
            Check(hardware == warp, "Hardware and WARP native font bounds differ.");
            FontInputChecks(style, output);
            CuiWrappedChecks.Run(catalog, output);
            CuiOverrideUiChecks.Run();
            Console.WriteLine($"cui_font hardware=pass warp=pass source={style.Typeface} size={style.Size} bounds={hardware}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void FontInputChecks(CuiTextAppearance installed, string output)
    {
        using var window = new Form { ClientSize = new Size(320, 150) };
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 150, true);
        using var gui = new ImGuiController(renderer, window);
        byte[] original = OwnedFontFixture.Create();
        var added = Appearance(original, "A");
        using (var atlas = new CuiFontAtlas(renderer, [added]))
        {
            Check(Math.Abs(atlas.Layout(added, "AAA", 100).Lines[0].Positions[2] - 26) < .01f, "Added font did not use rounded native advances.");
            renderer.BeginFrame(new(0, 0, 0, 1)); gui.NewFrame(1f / 60);
            ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(320, 150));
            ImGui.Begin("Owned font", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground);
            atlas.Draw(ImGui.GetWindowDrawList(), new(20, 20), 280, 3, added, "AAA"); ImGui.End(); gui.Render();
            string path = Path.Combine(output, "added-font.png"); renderer.CapturePng(path);
            using var image = new Bitmap(path);
            Check(Bounds(image, new(0, 0, 320, 150), color => color.R > 128).Width > 110, "Added font outlines were not rasterized.");
        }
        byte[] edited = original.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(edited.AsSpan(OwnedFontFixture.Table(edited, "hmtx") + 4), 900);
        OwnedFontFixture.RepairChecksums(edited);
        var updated = Appearance(edited, "A");
        using (var atlas = new CuiFontAtlas(renderer, [updated]))
            Check(Math.Abs(atlas.Layout(updated, "AAA", 100).Lines[0].Positions[2] - 40) < .01f, "Edited font retained its previous metrics.");
        var kerned = Appearance(OwnedFontFixture.Create(true), "A");
        using (var atlas = new CuiFontAtlas(renderer, [kerned]))
        {
            Check(atlas.Layout(kerned, "AAA", 100).Lines[0].Positions[2] == 22, "Native low-em kerning differs.");
            Check(atlas.Layout(kerned, "A\u2060A", 100).Lines[0].Positions[2] == 11, "Word joiner reset pair kerning.");
            var larger = kerned with { Size = 30 };
            Check(atlas.Layout(larger, "AAA", 100).Lines[0].Positions[2] == 30, "Native full-em kerning differs.");
            Check(atlas.Layout(larger, "AAAA", 20).Lines.Count == 4, "A long word did not make bounded progress.");
            Reject(() => atlas.Layout(kerned, new('A', 16385), 100));
        }
        byte[] badCff = installed.Fonts[0].Face.Font.Bytes.ToArray();
        badCff[OwnedFontFixture.Table(badCff, "CFF ")] = 0xFF;
        OwnedFontFixture.RepairChecksums(badCff);
        Reject(() => { using var atlas = new CuiFontAtlas(renderer, [Appearance(badCff, "A")]); });
        byte[] largeGlyph = original.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(largeGlyph.AsSpan(OwnedFontFixture.Table(largeGlyph, "head") + 18), 16);
        OwnedFontFixture.RepairChecksums(largeGlyph);
        Reject(() => { using var atlas = new CuiFontAtlas(renderer, [Appearance(largeGlyph, "A")]); });
        Reject(() => { using var atlas = new CuiFontAtlas(renderer, [Appearance(original, new(Enumerable.Range(1, 4097).Select(value => (char)value).ToArray()))]); });
        Console.WriteLine("cui_font added=pass edited=pass malformed_outline=rejected glyph_budget=bounded");

        static CuiTextAppearance Appearance(byte[] bytes, string characters)
        {
            var face = new CuiFontFace(1, 2, "Owned fixture", new(bytes));
            return new(0, 0, 22, 0, 0, Vector4.One, [new(face, characters, 0)], "") { FontSetKey = "fixture" };
        }
    }

    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; }
        throw new InvalidOperationException("Invalid font input reached the preview.");
    }

    private static unsafe Rectangle Run(CuiTextAppearance style, string text, string output, bool warp)
    {
        using var window = new Form { ClientSize = new Size(900, 230) };
        using var renderer = new Dx11Renderer(IntPtr.Zero, 900, 230, warp);
        using var gui = new ImGuiController(renderer, window);
        nint editorAtlas = (nint)ImGui.GetIO().Fonts.NativePtr;
        using (var atlas = new CuiFontAtlas(renderer, [style]))
        {
            Check((nint)ImGui.GetIO().Fonts.NativePtr == editorAtlas, "CUI replaced the editor font atlas.");
            float metricWidth = atlas.Measure(style, text);
            Frame(() =>
            {
                var draw = ImGui.GetWindowDrawList();
                atlas.Draw(draw, new(20, 20), 400, 1, style, text);
                atlas.Draw(draw, new(20, 80), 400, 1, style with { Alignment = 2, Color = new(1, .25f, .125f, .5f) }, text);
                atlas.Draw(draw, new(440, 20), 440, 1, style with { Alignment = 1, Color = new(.1f, .8f, .2f, 1) }, text);
            });
            renderer.CapturePng(output);
            using var bitmap = new Bitmap(output);
            var white = Bounds(bitmap, new(0, 0, 430, 70), color => color.R > 180 && color.G > 180 && color.B > 180);
            var red = Bounds(bitmap, new(0, 70, 430, 70), color => color.R > 75 && color.G is > 8 and < 50 && color.B < 30);
            var green = Bounds(bitmap, new(430, 0, 470, 70), color => color.G > 120 && color.R < 60 && color.B < 90);
            Check(white.Width >= metricWidth - 4 && white.Width <= metricWidth + 2 && white.Height > 10,
                $"Native glyph scale differs: ink={white.Width}, advance={metricWidth:F3}.");
            Check(red.Left > white.Left + 40 && Math.Abs(red.Width - white.Width) <= 2, "Right alignment changed glyph scale or failed to move text.");
            Check(green.Left > 470 && green.Right < 860, "Center alignment failed.");
            Check(red.Width > 320, "RGBA alpha/tint rendering failed.");
            atlas.Dispose();
            Check((nint)ImGui.GetIO().Fonts.NativePtr == editorAtlas, "Disposing CUI fonts changed the editor atlas.");
            Frame(() => ImGui.TextUnformatted("Editor font remains valid"));
            return white;
        }

        void Frame(Action draw)
        {
            renderer.BeginFrame(new(0, 0, 0, 1)); gui.NewFrame(1f / 60);
            ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(900, 230));
            ImGui.Begin("CUI font fixture", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings);
            draw(); ImGui.End(); gui.Render();
        }
    }

    private static Rectangle Bounds(Bitmap bitmap, Rectangle area, Func<Color, bool> accept)
    {
        int left = area.Right, right = area.Left, top = area.Bottom, bottom = area.Top;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if (accept(bitmap.GetPixel(x, y)))
                { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}

using System.Numerics;
using ImGuiNET;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;

/** GPU check of wrapped CUI text: narrow widths wrap and grow automatic height, blank lines advance the baseline, lines stay within width. */
internal static class CuiWrappedChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(PackageCatalog catalog, string output)
    {
        var strings = LocalizedStrings.LoadBanks(catalog, catalog.Entries.Where(entry => entry.Reference == 0x80809A88
            && entry.PackageName.StartsWith("w64_ui", StringComparison.Ordinal)).Select(entry => entry.TagHash));
        const uint tag = 0x80BC669D;
        var preview = CuiPreviewData.Read(catalog, tag, 0x80804825, catalog.Read(tag), strings.Get)!;
        var component = preview.Document!.Components.First(component => component.Key == 0x9F);
        var content = preview.LocalContent[component.Offset];
        var style = content.NativeText ?? throw new Exception(content.Error);
        var rectangle = preview.Layouts.First(layout => layout.Component.Offset == component.Offset).Rectangle;
        Check(style.AutoHeight && rectangle.Z == 792 && content.Text.Contains("\n\n"), "Owned multiline fixture changed.");
        foreach (bool warp in new[] { false, true })
        {
            using var window = new Form { ClientSize = new Size(1040, 520) };
            using var renderer = new Dx11Renderer(IntPtr.Zero, 1040, 520, warp);
            using var gui = new ImGuiController(renderer, window);
            using var atlas = new CuiFontAtlas(renderer, [style]);
            var wide = atlas.Layout(style, content.Text, rectangle.Z);
            var narrow = atlas.Layout(style, content.Text, 340);
            Check(wide.Lines.Count >= 4 && narrow.Lines.Count > wide.Lines.Count && narrow.Size.Y > wide.Size.Y,
                "Narrow text did not wrap and grow its automatic height.");
            Check(wide.Lines[1].Text.Length == 0 && wide.Lines[2].Baseline > wide.Lines[1].Baseline,
                "An explicit blank line lost its baseline advance.");
            Check(narrow.Lines.All(line => line.Width <= 341), "A wrapped line leaves its width.");
            Check(string.Concat(wide.Lines.Select(line => line.Text)).Replace(" ", "", StringComparison.Ordinal)
                == style.Format(content.Text).Replace("\n", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal),
                "Word wrapping lost visible text.");
            renderer.BeginFrame(new(0, 0, 0, 1)); gui.NewFrame(1f / 60);
            ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(1040, 520));
            ImGui.Begin("Native text layout", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground);
            var draw = ImGui.GetWindowDrawList();
            atlas.Draw(draw, new(20, 20), rectangle.Z, 1, style, content.Text);
            atlas.Draw(draw, new(20, 210), 340, 1, style, content.Text);
            atlas.Draw(draw, new(420, 210), 340, 1, style with { Alignment = 2, Color = new(.5f, .8f, 1, 1) }, content.Text);
            ImGui.End(); gui.Render();
            renderer.CapturePng(Path.Combine(output, $"native-wrapped-{(warp ? "warp" : "hardware")}.png"));
            Console.WriteLine($"cui_wrap adapter={(warp ? "warp" : "hardware")} tag={tag:X8} owner=9F wide_lines={wide.Lines.Count} narrow_lines={narrow.Lines.Count} wide_height={wide.Size.Y:F2} narrow_height={narrow.Size.Y:F2}");
        }
    }

    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}

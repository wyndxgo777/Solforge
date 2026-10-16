using System.Numerics;
using ImGuiNET;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;
using Vortice.DXGI;

namespace SunriseStudio.Editor;

/** A page that needs GPU resources (thumbnails, textures) uploaded between frames before it draws. */
public interface IPreparedStudioPage : IStudioPage, IDisposable
{
    void Prepare(StudioDocument? document, Dx11Renderer renderer);
}

/** Thumbnails are uploaded before the document appears and released between frames. */
public sealed class MaterialsPage : IPreparedStudioPage
{
    private readonly Dictionary<uint, GpuTexture> thumbnails = [];
    private readonly Dictionary<uint, string> errors = [];
    private uint[] additionalTextures = [];
    private uint selectedMaterial;
    private MeshInstance? previousPart;
    public string Id => "sunrise.materials";
    public string Title => "Materials";
    public int TexturePreviewCount => thumbnails.Count;
    /** Accepts documents whose surface has materials. */
    public bool CanOpen(StudioDocument document) => document.Surface?.Materials.Count > 0;

    /** Upload before ImGui can borrow the document's texture views. */
    public void Prepare(StudioDocument? document, Dx11Renderer renderer)
    {
        Dispose();
        selectedMaterial = 0; previousPart = null;
        if (document?.Surface is not { } surface) return;
        var boundTextures = surface.Materials.SelectMany(material => material.Material.Textures)
            .Select(binding => binding.TextureTag).ToHashSet();
        additionalTextures = surface.Textures.Keys.Where(tag => !boundTextures.Contains(tag)).ToArray();
        foreach (var (tag, texture) in surface.Textures)
        {
            var resource = texture.Subresources.Where(part => part.ArraySlice == 0)
                .OrderBy(part => part.MipLevel).FirstOrDefault(part => part.Width <= 256 && part.Height <= 256)
                ?? texture.Subresources[0];
            try
            {
                thumbnails.Add(tag, renderer.CreateTexture(resource.Width, resource.Height, (Format)texture.Format,
                    resource.Data.AsSpan(0, resource.SlicePitch), resource.RowPitch));
            }
            catch (Exception error) when (error is not OutOfMemoryException) { errors[tag] = error.Message; }
        }
    }

    /** Texture labels retain shader registers without guessing material roles. */
    public void Draw(StudioPageContext context)
    {
        if (context.Workspace.Document?.Surface is not { Materials.Count: > 0 } surface) return;
        if (!ReferenceEquals(previousPart, context.SelectedMesh))
        {
            previousPart = context.SelectedMesh;
            if (context.SelectedMesh is { } part) selectedMaterial = part.Mesh.MaterialTag;
        }
        var material = surface.Materials.FirstOrDefault(value => value.TagHash == selectedMaterial) ?? surface.Materials[0];
        selectedMaterial = material.TagHash;
        ImGui.SetNextItemWidth(-1);
        if (UiAuto.BeginCombo("##material", material.Name))
        {
            foreach (var value in surface.Materials)
            {
                ImGui.PushID(unchecked((int)value.TagHash));
                if (UiAuto.Selectable(value.Name, value.TagHash == selectedMaterial)) selectedMaterial = value.TagHash;
                ImGui.PopID();
            }
            UiAuto.EndCombo();
        }
        ImGui.BeginChild("material-details");
        if (UiAuto.SmallButton("Open material")) context.Workspace.Select(material.TagHash);
        string profiles = string.Join("  ", new[] { material.Material.VertexShader, material.Material.PixelShader, material.Material.ComputeShader }
            .Where(shader => shader is not null).Select(shader => $"{StageShort(shader!.Stage.ToString())} {shader.Profile}"));
        if (profiles.Length != 0) { ImGui.SameLine(); ImGui.TextDisabled(profiles); }
        ImGui.Spacing();
        if (material.Material.Textures.Count == 0) ImGui.TextDisabled("No textures");
        else if (ImGui.BeginTable("texture-bindings", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Texture", ImGuiTableColumnFlags.WidthFixed, 108);
            ImGui.TableSetupColumn("Binding", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Format", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();
            foreach (var binding in material.Material.Textures)
            {
                ImGui.PushID($"{binding.Stage}:{binding.Slot}:{binding.TextureTag}");
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (thumbnails.TryGetValue(binding.TextureTag, out var image))
                {
                    float fit = 96f / Math.Max(image.Width, image.Height);
                    ImGui.Image(image.ImGuiId, new Vector2(image.Width, image.Height) * fit);
                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        context.Workspace.Select(binding.TextureTag);
                }
                else ImGui.TextDisabled("Unavailable");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{binding.Stage} / t{binding.Slot}");
                string name = surface.TextureNames.GetValueOrDefault(binding.TextureTag)
                    ?? context.Workspace.DisplayName(binding.TextureTag);
                if (UiAuto.Selectable(name)) context.Workspace.Select(binding.TextureTag);
                if (!binding.DeclaredByShader) ImGui.TextDisabled("Undeclared binding");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{binding.Width} x {binding.Height}");
                ImGui.TextUnformatted(binding.Format.ToString());
                if (binding.ArraySize > 1) ImGui.TextDisabled($"{binding.ArraySize} faces / layers");
                if (binding.Depth > 1) ImGui.TextDisabled($"Depth {binding.Depth}");
                string? error = errors.GetValueOrDefault(binding.TextureTag) ?? surface.TextureErrors.GetValueOrDefault(binding.TextureTag);
                if (error is not null) ErrorMark(error);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (additionalTextures.Length > 0 && ImGui.CollapsingHeader("Additional textures"))
            foreach (uint tag in additionalTextures)
            {
                ImGui.PushID(unchecked((int)tag));
                if (thumbnails.TryGetValue(tag, out var image))
                {
                    float fit = 96f / Math.Max(image.Width, image.Height);
                    ImGui.Image(image.ImGuiId, new Vector2(image.Width, image.Height) * fit);
                }
                if (UiAuto.Selectable(surface.TextureNames.GetValueOrDefault(tag) ?? context.Workspace.DisplayName(tag)))
                    context.Workspace.Select(tag);
                if (errors.TryGetValue(tag, out string? error)) ErrorMark(error);
                ImGui.PopID();
            }
        if (material.Execution is { } execution)
            foreach (var stage in new[] { execution.Vertex, execution.Pixel, execution.Compute })
            {
                if (stage.Shader is null && stage.ConstantTemplate.Length == 0 && stage.TfxConstants.Length == 0) continue;
                if (!ImGui.CollapsingHeader($"{stage.Stage} constants")) continue;
                ImGui.PushID($"{stage.Stage}");
                DrawVectors("Material", stage.ConstantTemplate);
                DrawVectors("Expressions", stage.TfxConstants);
                ImGui.TextDisabled($"{stage.TfxBytecode.Length:N0} TFX bytes / {stage.Samplers.Count} samplers");
                for (int i = 0; i < stage.Samplers.Count; i++)
                {
                    var sampler = stage.Samplers[i].Description;
                    ImGui.TextUnformatted($"s{i}  {sampler.Filter}  {sampler.AddressU} / {sampler.AddressV} / {sampler.AddressW}");
                }
                ImGui.PopID();
            }
        if (material.Error is not null)
        {
            ImGui.TextColored(StudioTheme.Accent, "Constants not read");
            StudioUi.Tip(material.Error);
        }
        if (surface.Diagnostics.Count > 0 && ImGui.CollapsingHeader($"Load report ({surface.Diagnostics.Count})###Load report"))
            StudioUi.Facts("surface-load-report", surface.Diagnostics);
        ImGui.EndChild();
    }

    private static string StageShort(string stage) => stage switch
    {
        "Vertex" => "VS", "Pixel" => "PS", "Compute" => "CS", "Geometry" => "GS", _ => stage
    };

    /** An accent "Error" with the message on hover. */
    private static void ErrorMark(string message)
    {
        ImGui.TextColored(StudioTheme.Accent, "Error");
        StudioUi.Tip(message);
    }

    /** Constants retain their authored vector order. */
    private static void DrawVectors(string title, Vector4[] values)
    {
        if (values.Length == 0 || !ImGui.TreeNode($"{title} ({values.Length})###{title}")) return;
        for (int i = 0; i < values.Length; i++)
        {
            var value = values[i];
            ImGui.TextUnformatted($"{i}:  {value.X:G6}, {value.Y:G6}, {value.Z:G6}, {value.W:G6}");
        }
        ImGui.TreePop();
    }

    /** Release views only after ImGui has consumed the current frame. */
    public void Dispose()
    {
        foreach (var texture in thumbnails.Values) texture.Dispose();
        thumbnails.Clear(); errors.Clear(); additionalTextures = [];
    }
}

/** Scrolling reuses decoded shader lines without reading packages. */
public sealed class ShadersPage : IStudioPage
{
    private uint selectedShader;
    private ShaderSurface? previous;
    private string[] lines = [], header = [];
    public string Id => "sunrise.shaders";
    public string Title => "Shaders";
    /** Accepts documents whose surface has shaders. */
    public bool CanOpen(StudioDocument document) => document.Surface?.Shaders.Count > 0;

    /** Shader headers retain their native stage and bytecode identity. */
    public unsafe void Draw(StudioPageContext context)
    {
        if (context.Workspace.Document?.Surface is not { Shaders.Count: > 0 } surface) return;
        var selected = surface.Shaders.FirstOrDefault(shader => shader.TagHash == selectedShader) ?? surface.Shaders[0];
        ImGui.SetNextItemWidth(-1);
        if (UiAuto.BeginCombo("##shader", $"{selected.Stage} / {selected.Name}"))
        {
            foreach (var shader in surface.Shaders)
            {
                ImGui.PushID(unchecked((int)shader.TagHash));
                if (UiAuto.Selectable($"{shader.Stage} / {shader.Name}", shader.TagHash == selected.TagHash)) selectedShader = shader.TagHash;
                ImGui.PopID();
            }
            UiAuto.EndCombo();
        }
        ImGui.TextUnformatted($"{selected.Profile} / {selected.Bytecode.Length:N0} bytes");
        ImGui.SameLine();
        if (UiAuto.SmallButton("Open bytecode")) context.Workspace.Select(selected.Shader.BytecodeTag);
        if (ImGui.CollapsingHeader("Declarations"))
            foreach (string declaration in selected.Declarations) ImGui.TextUnformatted(declaration);
        if (!ReferenceEquals(previous, selected))
        {
            previous = selected;
            var all = selected.Disassembly.Split('\n');
            int start = 0;
            // The leading comment block is the disassembler header; the listing starts after it.
            while (start < all.Length && (all[start].TrimStart().StartsWith("//", StringComparison.Ordinal) || all[start].Trim().Length == 0)) start++;
            header = all[..start]; lines = all[start..];
        }
        if (header.Length > 0 && ImGui.TreeNode($"Header ({header.Length} lines)###shader-header"))
        {
            if (context.MonospaceFont.NativePtr != null) ImGui.PushFont(context.MonospaceFont);
            foreach (string line in header) ImGui.TextUnformatted(line.TrimEnd('\r'));
            if (context.MonospaceFont.NativePtr != null) ImGui.PopFont();
            ImGui.TreePop();
        }
        ImGui.BeginChild("shader-code", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
        if (context.MonospaceFont.NativePtr != null) ImGui.PushFont(context.MonospaceFont);
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(lines.Length);
        while (clipper.Step())
            for (int index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                ImGui.TextUnformatted(lines[index].TrimEnd('\r'));
        clipper.End(); clipper.Destroy();
        if (context.MonospaceFont.NativePtr != null) ImGui.PopFont();
        ImGui.EndChild();
    }
}

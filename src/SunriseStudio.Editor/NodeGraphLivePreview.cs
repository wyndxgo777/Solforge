using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Solforge.Graphs;
using Solforge.Mcp;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * Live preview of a material graph. Each frame it compiles the graph, with fields still being typed, and after a short
 * debounce puts the result in the shared MaterialOverrideSet; the world, asset and preview viewports draw it from their
 * next frame. Nothing is written to a tag: Apply still does that. Clear puts every viewport back on the authored
 * material. The preview mesh is a part that uses the target material (the selection first), drawn in its own small
 * viewport with the same overrides.
 */
internal sealed class NodeGraphLivePreview(MaterialOverrideSet overrides, Dx11Renderer renderer, Func<PackageCatalog?> catalog,
    Func<IReadOnlyList<AssetViewport>> viewports) : IDisposable
{
    private readonly LiveDebounce debounce = new();
    private readonly ConcurrentQueue<(uint Tag, TextureData? Data)> decoded = new();
    // Decoded override textures by tag; null marks one that could not be read.
    private readonly Dictionary<uint, TextureData?> textures = [];
    private readonly HashSet<uint> decoding = [];
    private int texturesRevision;
    private string compiledKey = "";
    private MaterialCompileResult? compiled;
    // After a revert, the revision whose graph is not previewed until the next edit.
    private int? heldRevision;
    private uint? target;
    private string status = "";
    private AssetViewport? preview;
    private uint previewMaterial, previewShown;
    private Task<(uint Material, SceneDocument Scene, NativeScenePlan Plan)>? loading;
    private CancellationTokenSource? loadCancel;
    private string previewError = "";
    private double retryAt;

    /** Off clears the overrides; on previews the graph again from the next frame. */
    public bool Enabled { get; set; } = true;
    /** The material the viewports draw with the graph's values; null when none. */
    public uint? Target => target;

    /** Call every frame the graph is shown. */
    public void Update(GraphDocument document, int revision, IReadOnlyDictionary<string, string> drafts, double now)
    {
        while (decoded.TryDequeue(out var item))
        {
            decoding.Remove(item.Tag);
            textures[item.Tag] = item.Data;
            texturesRevision++;
        }
        if (!Enabled || document.Kind != GraphKind.Material) { Clear(); return; }
        if (heldRevision is { } held)
        {
            if (held == revision && drafts.Count == 0) return;
            heldRevision = null;
        }
        string key = revision.ToString(CultureInfo.InvariantCulture) + "\n" + MaterialLivePreview.DraftKey(drafts);
        if (compiled is null || key != compiledKey)
        {
            compiledKey = key;
            compiled = MaterialGraphCompiler.Compile(MaterialLivePreview.WithDrafts(document, drafts));
        }
        string signature = MaterialLivePreview.Signature(compiled) + "#" + texturesRevision.ToString(CultureInfo.InvariantCulture);
        if (debounce.Update(signature, now)) Send(compiled);
    }

    /** Puts every viewport back on the authored material and forgets the compiled graph. */
    public void Clear()
    {
        if (target is { } tag) overrides.Remove(tag);
        target = null;
        compiled = null;
        compiledKey = "";
        heldRevision = null;
        status = "";
        debounce.Reset();
        if (textures.Count != 0) textures.Clear();
        ClearPreviewMesh();
    }

    /** Clear, then keep the viewports on the authored material until the graph changes again (Revert). */
    public void Hold(int revision)
    {
        Clear();
        heldRevision = revision;
    }

    /** One line on what the viewports show, with the writes they left out. */
    public string Summary()
    {
        if (!Enabled) return "Live preview is off; Apply writes the graph to the material.";
        if (heldRevision is not null) return "Reverted: the viewports show the material as it is. Edit the graph to preview again.";
        if (target is not { } tag) return status;
        var report = overrides.Summary(tag);
        string line = report.Parts > 0 ? $"Live on {report.Parts} part(s) using 0x{tag:X8}; Apply writes it to the tag."
            : report.Materials > 0 ? $"Live on 0x{tag:X8}; Apply writes it to the tag."
            : $"Live: nothing loaded draws 0x{tag:X8} yet.";
        var failed = textures.Where(item => item.Value is null).Select(item => $"0x{item.Key:X8}").ToArray();
        if (failed.Length != 0) line += "\nCould not read texture " + string.Join(", ", failed) + ".";
        foreach (string skipped in report.Skipped.Take(4)) line += "\n" + skipped;
        if (status.Length != 0) line += "\n" + status;
        return line;
    }

    /** The material of the first selected part in the world or asset viewport; null when nothing with one is selected. */
    public uint? SelectedMaterial()
    {
        foreach (var view in viewports())
            foreach (var part in view.SelectedMeshParts)
            {
                uint tag = MaterialOf(part);
                if (tag != 0) return tag;
            }
        return null;
    }

    private void Send(MaterialCompileResult result)
    {
        // An error keeps the last good preview; the Problems list says what to fix.
        if (GraphValidation.HasErrors(result.Diagnostics)) { status = "Paused until the graph's errors are fixed."; return; }
        if (SolforgeTools.ParseHash(result.Target) is not { } tag)
        {
            if (target is { } old) overrides.Remove(old);
            target = null;
            status = "Set the material on Material Output (or use From selection) to preview it live.";
            return;
        }
        if (target is { } previous && previous != tag) overrides.Remove(previous);
        target = tag;
        status = "";
        var constants = new List<MaterialConstantOverride>();
        foreach (var write in result.Constants)
        {
            if (!Enum.TryParse<GameShaderStage>(write.Stage, out var stage)) continue;
            var bank = write.Bank == NodeLibrary.Banks[1] ? MaterialConstantBank.Expression : MaterialConstantBank.Template;
            constants.Add(new(stage, bank, write.Vector, MaterialOverrideSet.ParseMask(write.Mask),
                new Vector4(Lane(write.Values, 0), Lane(write.Values, 1), Lane(write.Values, 2), Lane(write.Values, 3))));
        }
        var slots = new List<MaterialTextureOverride>();
        foreach (var write in result.Textures)
        {
            if (!Enum.TryParse<GameShaderStage>(write.Stage, out var stage) || write.Slot < 0
                || SolforgeTools.ParseHash(write.Texture) is not { } texture) continue;
            // A renderer that has the texture uses its own upload; the payload is for those that do not.
            if (!textures.TryGetValue(texture, out var data)) Decode(texture);
            slots.Add(new(stage, (uint)write.Slot, texture, data));
        }
        overrides.Set(tag, new MaterialOverride(constants, slots));
    }

    private static float Lane(double[] values, int index) => index < values.Length ? (float)values[index] : 0;

    private static uint MaterialOf(MeshInstance part)
    {
        uint native = part.Mesh.Native?.MaterialTag ?? 0;
        return native != 0 ? native : part.Mesh.MaterialTag;
    }

    /** Reads a texture on a worker; Update picks it up and sends the override again with it. */
    private void Decode(uint tag)
    {
        if (catalog() is not { } source || !decoding.Add(tag)) return;
        _ = Task.Run(() =>
        {
            TextureData? data = null;
            // Any failure leaves the slot on its authored texture; Summary names the texture.
            try { data = new TextureDataReader(source).Read(tag); }
            catch (Exception error) when (error is not OutOfMemoryException) { data = null; }
            decoded.Enqueue((tag, data));
        });
    }

    /**
     * Draws the preview mesh in the given size; false when there is none to draw yet, so the caller shows the swatch.
     */
    public bool DrawPreviewMesh(Vector2 size)
    {
        AdoptPreview();
        if (target is not { } tag) return false;
        double now = ImGui.GetTime();
        if (tag != previewMaterial || previewShown != tag && loading is null && now > retryAt)
        {
            retryAt = now + 2;
            StartPreview(tag);
        }
        if (preview is null || previewShown != tag || !preview.NativeSceneReady)
        {
            if (loading is not null) ImGui.TextDisabled("Loading a preview mesh...");
            else if (previewError.Length != 0) ImGui.TextWrapped(previewError);
            return false;
        }
        preview.DrawImGui(size);
        return true;
    }

    private void StartPreview(uint tag)
    {
        loadCancel?.Cancel();
        loadCancel = null;
        loading = null;
        previewMaterial = tag;
        if (catalog() is not { } source) { previewError = "Open a project to load a preview mesh."; return; }
        if (FindPart(tag) is not { } part)
        {
            previewError = $"No loaded part uses 0x{tag:X8}. Select one in the viewport, or open a map or asset that uses it.";
            return;
        }
        var scene = new SceneDocument { Name = "Material preview" };
        scene.Instances.Add(part);
        var cancel = loadCancel = new CancellationTokenSource();
        previewError = "";
        loading = Task.Run(() => (Material: tag, Scene: scene,
            Plan: AssetViewport.PrepareNativeScene(source, scene, cancel.Token, assetPreview: true)), cancel.Token);
    }

    /** The first selected part that uses the material, else the first static part of a loaded scene, else any part. */
    private MeshInstance? FindPart(uint tag)
    {
        var views = viewports();
        foreach (var view in views)
            foreach (var part in view.SelectedMeshParts)
                if (MaterialOf(part) == tag && part.Mesh.Native is not null) return part;
        MeshInstance? any = null;
        foreach (var view in views)
        {
            if (view.Scene is not { } scene) continue;
            foreach (var part in scene.Instances)
            {
                if (part.Mesh.Native is not { } native || MaterialOf(part) != tag) continue;
                if (native.Kind == NativeMeshKind.Static) return part;
                any ??= part;
            }
        }
        return any;
    }

    /** Takes a finished load into the preview viewport, on this (the render) thread. */
    private void AdoptPreview()
    {
        if (loading is not { IsCompleted: true } task) return;
        loading = null;
        if (task.IsCanceled) return;
        if (task.Exception is { } failure)
        {
            // A part that cannot be prepared is not tried again until the target changes.
            retryAt = double.PositiveInfinity;
            previewError = "Preview mesh: " + (failure.InnerException ?? failure).Message;
            return;
        }
        var (material, scene, plan) = task.Result;
        if (material != previewMaterial) return;
        try
        {
            preview ??= new AssetViewport(renderer)
            {
                ShowToolbar = false, ShowGrid = false, ShowCursor = false, ShowCursorToggle = false, ShowGameShaderToggle = false,
                ShowAxisGizmo = false, MaterialOverrides = overrides
            };
            preview.Clear();
            preview.SetScene(scene);
            preview.SetNativeScene(plan);
            preview.Frame();
            previewShown = material;
            previewError = "";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            retryAt = double.PositiveInfinity;
            previewShown = 0;
            previewError = "Preview mesh: " + error.Message;
            preview?.Clear();
        }
    }

    private void ClearPreviewMesh()
    {
        loadCancel?.Cancel();
        loadCancel = null;
        loading = null;
        if (previewShown != 0) preview?.Clear();
        previewMaterial = previewShown = 0;
        previewError = "";
        retryAt = 0;
    }

    public void Dispose()
    {
        Clear();
        preview?.Dispose();
        preview = null;
    }
}

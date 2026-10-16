using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using SunriseStudio.Rendering;

namespace SunriseStudio.Viewport;

/** Material TFX of one frame: planned in group order, evaluated in parallel, bound in group order. */
public sealed partial class NativeSceneRenderer
{
    /** The GroupKeys values the shared inputs held when one material was due; Present has a bit per key. */
    private struct GroupState
    {
        public int Present;
        public GroupValues Values;
    }

    /** A due material and its stage requests; -1 marks a stage without TFX. */
    private readonly record struct MaterialJob(DrawGroup Group, int Vertex, int Pixel);

    /** One evaluation: the stage, the group state its inputs take and its object channel table. */
    private readonly record struct TfxRequest(MaterialStageData Stage, int State, IReadOnlyList<Vector4>? Channels);

    private readonly List<MaterialJob> materialJobs = [];
    private readonly List<TfxRequest> tfxRequests = [];
    private readonly List<GroupState> groupStates = [];
    private readonly Dictionary<SharedKey, int> requestsByKey = [];
    private TfxEvaluation?[] tfxResults = [];
    private Exception?[] tfxErrors = [];
    // Empty worker input copies; each keeps its dictionary capacity for the next frame.
    private readonly Stack<TfxInputs> inputCopies = new();
    // Below this many evaluations they run on the calling thread.
    private const int MinimumParallelEvaluations = 32;

    /**
     * Applies each active group's inputs in group order and lists the materials UpdateMaterial would evaluate,
     * with the group state each one sees. Clears failedGroups for active groups.
     */
    private void PlanMaterialUpdates(TfxInputs inputs, double seconds, float exposureEv)
    {
        materialJobs.Clear(); tfxRequests.Clear(); groupStates.Clear(); requestsByKey.Clear();
        if (!ReferenceEquals(inputs, sharedInputs)) { sharedEvaluations.Clear(); sharedInputs = inputs; }
        var state = new GroupState();
        // Bits of the GroupKeys written since state last read them.
        int dirty = (1 << GroupKeys.Length) - 1;
        Vector4[]? written72 = null;
        foreach (var group in activeGroups)
        {
            failedGroups[group.Order] = false;
            var material = group.Material;
            if (material.WaterObject is { } water)
            {
                NativeWater.ApplyObject(inputs, water.Position, water.Scale);
                dirty |= 0b11;
            }
            if (material.Decorator is { } decorator)
            {
                // Rows already in the inputs are not written again; only this loop writes extern 72.
                if (!ReferenceEquals(decorator.Payload.Extern72, written72))
                {
                    NativeDecorators.ApplyExtern72(inputs, decorator.Payload.Extern72);
                    written72 = decorator.Payload.Extern72;
                    dirty |= 0xFF0;
                }
                if (decorator.Type.Ground)
                {
                    NativeDecorators.ApplyExtern47(inputs, groundWinds.GetValueOrDefault(decorator.Type.Tag));
                    dirty |= 0b1000;
                }
            }
            // The test UpdateMaterial makes; a material shared by several groups is due once per time.
            if (material.Static || material.LastTime == seconds && !material.UsesCamera
                && (!material.UsesExposure || material.LastExposureEv == exposureEv)) continue;
            NativeGearDye.Apply(inputs, material.Source.GearDyeBit);
            dirty |= 0b100;
            for (int slot = 0; dirty != 0; slot++, dirty >>= 1) if ((dirty & 1) != 0) Capture(inputs, ref state, slot);
            int index = groupStates.Count;
            groupStates.Add(state);
            materialJobs.Add(new(group, Request(material.Execution.Vertex, material.VertexShare, material, index),
                Request(material.Execution.Pixel, material.PixelShare, material, index)));
            material.LastTime = seconds;
            material.LastExposureEv = exposureEv;
        }
    }

    /** Copies one GroupKeys value from the inputs; an absent key reads as zero, as SharedEvaluation reads it. */
    private static void Capture(TfxInputs inputs, ref GroupState state, int slot)
    {
        bool present;
        if (slot == 0) { present = inputs.Floats.TryGetValue(GroupKeys[0], out float scalar); state.Values[0] = new(scalar, 0, 0, 0); }
        else present = inputs.Vectors.TryGetValue(GroupKeys[slot], out state.Values[slot]);
        state.Present = present ? state.Present | 1 << slot : state.Present & ~(1 << slot);
    }

    /** The request index for one stage; requests SharedEvaluation would share collapse into one. */
    private int Request(MaterialStageData stage, TfxShare share, MaterialGpu material, int state)
    {
        if (stage.TfxBytecode.Length == 0) return -1;
        bool shared = share.Program >= 0 && !(programUsers[share.Program] == 1 && !material.UsesCamera);
        var key = new SharedKey { Program = share.Program, Channels = (share.Reads & ChannelsRead) != 0 ? material.ChannelTable : 0 };
        if (shared)
        {
            var values = groupStates[state];
            for (int slot = 0; slot < GroupKeys.Length; slot++)
            {
                if ((share.Reads & 1 << slot) == 0) continue;
                key.Values[slot] = values.Values[slot];
                key.Present |= values.Present & 1 << slot;
            }
            if (requestsByKey.TryGetValue(key, out int existing)) return existing;
        }
        tfxRequests.Add(new(stage, state, material.Source.ObjectChannels));
        if (shared) requestsByKey.Add(key, tfxRequests.Count - 1);
        return tfxRequests.Count - 1;
    }

    /**
     * Evaluates every planned request, then binds the results in group order.
     * @return Materials evaluated; a failed one marks its group in failedGroups.
     */
    private int RunMaterialUpdates(TfxInputs inputs)
    {
        int count = tfxRequests.Count;
        // The last frame's results stay in place; Evaluate reuses their buffers.
        if (tfxResults.Length < count) { Array.Resize(ref tfxResults, count); tfxErrors = new Exception?[count]; }
        Array.Clear(tfxErrors, 0, count);
        // Evaluation writes group state into its inputs, so it never runs on the frame's own; the shadow pass reads them.
        if (count >= MinimumParallelEvaluations)
            Parallel.For(0, count, () => Copy(inputs), (i, _, local) => { Evaluate(i, local); return local; }, Release);
        else if (count != 0)
        {
            var local = Copy(inputs);
            for (int i = 0; i < count; i++) Evaluate(i, local);
            Release(local);
        }
        foreach (var (key, index) in requestsByKey)
            if (tfxErrors[index] is null) sharedEvaluations[key] = tfxResults[index]!;
        int evaluations = 0;
        foreach (var job in CollectionsMarshal.AsSpan(materialJobs))
        {
            var material = job.Group.Material;
            try
            {
                Bind(material, material.Execution.Vertex, job.Vertex);
                Bind(material, material.Execution.Pixel, job.Pixel);
                evaluations++;
            }
            catch (Exception error) when (Recoverable(error) || error is InvalidOperationException)
            {
                failedGroups[job.Group.Order] = true;
                // The next frame evaluates it again.
                material.LastTime = double.NaN;
                AddDiagnostic($"Material 0x{job.Group.Source.Mesh.MaterialTag:X8}: {error.Message}");
            }
        }
        return evaluations;
    }

    /** Runs one request with its group state written into inputs, which then holds that state. */
    private void Evaluate(int index, TfxInputs inputs)
    {
        var request = tfxRequests[index];
        var state = groupStates[request.State];
        for (int slot = 0; slot < GroupKeys.Length; slot++)
        {
            bool present = (state.Present & 1 << slot) != 0;
            if (slot == 0)
            {
                if (present) inputs.Floats[GroupKeys[0]] = state.Values[0].X;
                else inputs.Floats.Remove(GroupKeys[0]);
            }
            else if (present) inputs.Vectors[GroupKeys[slot]] = state.Values[slot];
            else inputs.Vectors.Remove(GroupKeys[slot]);
        }
        inputs.ObjectChannels = request.Channels;
        // A result's constants and samplers are read only while it is bound, so this frame may overwrite them.
        var spare = tfxResults[index];
        tfxResults[index] = null;
        try { tfxResults[index] = TfxEvaluator.Evaluate(request.Stage, inputs, spare); }
        catch (Exception error) { tfxErrors[index] = error; }
    }

    /** Binds one material stage; rethrows an error captured while preparing its TFX request. */
    private void Bind(MaterialGpu material, MaterialStageData stage, int request)
    {
        if (request < 0) return;
        if (tfxErrors[request] is { } error) ExceptionDispatchInfo.Throw(error);
        ApplyEvaluation(material, stage, tfxResults[request]!);
    }

    /** A private copy for one worker; TFX only reads its inputs. Release hands it back. */
    private TfxInputs Copy(TfxInputs source)
    {
        TfxInputs copy;
        lock (inputCopies) copy = inputCopies.Count > 0 ? inputCopies.Pop() : new TfxInputs();
        copy.Parameters = source.Parameters; copy.ObjectChannels = source.ObjectChannels;
        copy.ObjectChannelDefault = source.ObjectChannelDefault;
        foreach (var item in source.Floats) copy.Floats.Add(item.Key, item.Value);
        foreach (var item in source.FloatTerms) copy.FloatTerms.Add(item.Key, item.Value);
        foreach (var item in source.Vectors) copy.Vectors.Add(item.Key, item.Value);
        foreach (var item in source.Matrices) copy.Matrices.Add(item.Key, item.Value);
        foreach (var item in source.Resources) copy.Resources.Add(item.Key, item.Value);
        return copy;
    }

    /** Empties a worker copy, so it holds no frame state, and keeps it for the next Copy. */
    private void Release(TfxInputs copy)
    {
        copy.Floats.Clear(); copy.FloatTerms.Clear(); copy.Vectors.Clear(); copy.Matrices.Clear(); copy.Resources.Clear();
        copy.Parameters = null; copy.ObjectChannels = null; copy.ObjectChannelDefault = null;
        lock (inputCopies) inputCopies.Push(copy);
    }
}

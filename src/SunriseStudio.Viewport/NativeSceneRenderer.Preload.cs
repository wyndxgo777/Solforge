using System.Runtime.ExceptionServices;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using Vortice.DXGI;

namespace SunriseStudio.Viewport;

/** Preload half of NativeSceneRenderer: background texture and geometry creation for a plan. */
public sealed partial class NativeSceneRenderer
{
    /**
     * Creates a plan's textures, then its shared vertex and index buffers, on a worker.
     * Only ID3D11Device calls run there; the render thread takes each result and does all binding.
     * Call Finish on every path; it releases the results nobody took.
     */
    private sealed class GpuPreload
    {
        private readonly object gate = new();
        private readonly uint[] textureTags;
        private readonly TextureData[] textureData;
        private readonly (GpuTexture? Resource, Exception? Error, bool Taken)[] textureResults;
        private readonly NativeSceneGroup[] geometrySources;
        private readonly (NativeGpuGeometry? Resource, Exception? Error, bool Taken)[] geometryResults;
        private readonly Dictionary<uint, int> textureIndex = [];
        private readonly Dictionary<string, int> geometryIndex = new(StringComparer.Ordinal);
        private readonly Task work;
        private int texturesDone, geometryDone;
        private volatile bool stopped;
        private bool finished;

        public GpuPreload(Dx11Renderer renderer, NativeScenePlan plan)
        {
            textureTags = plan.Textures.Keys.ToArray();
            textureData = plan.Textures.Values.ToArray();
            textureResults = new (GpuTexture?, Exception?, bool)[textureTags.Length];
            for (int index = 0; index < textureTags.Length; index++) textureIndex[textureTags[index]] = index;
            geometrySources = plan.Groups.DistinctBy(group => group.GeometryKey).ToArray();
            geometryResults = new (NativeGpuGeometry?, Exception?, bool)[geometrySources.Length];
            for (int index = 0; index < geometrySources.Length; index++) geometryIndex[geometrySources[index].GeometryKey] = index;
            work = Task.Run(() => Create(renderer));
        }

        private void Create(Dx11Renderer renderer)
        {
            for (int index = 0; index < textureData.Length && !stopped; index++)
            {
                var t = textureData[index];
                try
                {
                    textureResults[index].Resource = renderer.CreateTextureResource(t.Width, t.Height, t.Depth, t.ArraySize,
                        t.MipCount, (Format)t.Format, t.IsCube, t.IsVolume, t.Subresources.Select(s =>
                            new TextureSubresourceUpload(s.MipLevel, s.ArraySlice, s.Data, s.RowPitch, s.SlicePitch)).ToArray());
                }
                catch (Exception error) { textureResults[index].Error = error; }
                lock (gate) { texturesDone = index + 1; Monitor.PulseAll(gate); }
            }
            for (int index = 0; index < geometrySources.Length && !stopped; index++)
            {
                try { geometryResults[index].Resource = CreateGeometry(renderer, geometrySources[index]); }
                catch (Exception error) { geometryResults[index].Error = error; }
                lock (gate) { geometryDone = index + 1; Monitor.PulseAll(gate); }
            }
            lock (gate) { texturesDone = textureTags.Length; geometryDone = geometrySources.Length; Monitor.PulseAll(gate); }
        }

        /** Waits for every texture; progress runs on the caller's thread between waits. */
        public void WaitTextures(Action<string, int, int>? progress)
        {
            // Progress presents a loading frame, so report at most every 50 ms.
            const int reportMilliseconds = 50;
            lock (gate)
                while (texturesDone < textureTags.Length)
                {
                    Monitor.Wait(gate, reportMilliseconds);
                    progress?.Invoke("Uploading textures", texturesDone, textureTags.Length);
                }
        }

        /** The created texture, or null when the plan had no such tag; rethrows the creation error. */
        public GpuTexture? TakeTexture(uint tag)
        {
            if (!textureIndex.TryGetValue(tag, out int index)) return null;
            lock (gate) while (texturesDone <= index) Monitor.Wait(gate);
            ref var result = ref textureResults[index];
            if (result.Taken) return null;
            result.Taken = true;
            if (result.Error is { } error) ExceptionDispatchInfo.Capture(error).Throw();
            return result.Resource;
        }

        /** The created buffers, or null when the key was not preloaded; rethrows the creation error. */
        public NativeGpuGeometry? TakeGeometry(string key)
        {
            if (!geometryIndex.TryGetValue(key, out int index)) return null;
            lock (gate) while (geometryDone <= index) Monitor.Wait(gate);
            ref var result = ref geometryResults[index];
            if (result.Taken) return null;
            result.Taken = true;
            if (result.Error is { } error) ExceptionDispatchInfo.Capture(error).Throw();
            return result.Resource;
        }

        /** Stops the worker, waits for it, and releases every result nobody took; later calls do nothing. */
        public void Finish()
        {
            if (finished) return;
            finished = true;
            stopped = true;
            try { work.Wait(); }
            catch (AggregateException) { }
            foreach (var result in textureResults) if (!result.Taken) result.Resource?.Dispose();
            foreach (var result in geometryResults) if (!result.Taken) result.Resource?.Dispose();
        }
    }
}

using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Performance and equivalence checks for frame visibility and line buffer modes; optimized paths must match the reference draw order. */
internal static class FramePerformance
{
    private sealed record CameraView(string Name, Vector3 Eye, Vector3 Pivot);

    /** Each comparison replays the same camera path after shaders and exposure have warmed. */
    public static void Run(string[] args)
    {
        string Option(string key, string fallback = "") => Array.IndexOf(args, key) is >= 0 and var index ? args[index + 1] : fallback;
        string output = Path.GetFullPath(Option("--frame-profile"));
        Directory.CreateDirectory(output);
        using var catalog = PackageCatalog.Open(Option("--game"));
        var loader = new WorldLoader(catalog, StudioNameIndex.Create(catalog));
        Console.WriteLine("frame_profile phase=load");
        var world = loader.LoadMission(0x80EDC19B, 0x80B48062, new() { IncludeLowerLods = !args.Contains("--no-lower-lods") });
        using var renderer = new Dx11Renderer(IntPtr.Zero, 906, 533, args.Contains("--warp"));
        using var viewport = new AssetViewport(renderer) { ShowGrid = true, MeasureFrameTimes = true };
        viewport.SetScene(world.Scene);
        viewport.Visibility.HiddenOverlayLayers.UnionWith([SceneLayer.Transitions, SceneLayer.Squads,
            SceneLayer.Effects, SceneLayer.Lights]);
        viewport.Frame();
        var exterior = new CameraView("exterior", new(3624.356f, -8483.99f, 3124.3152f),
            new(-955.83057f, -1631.5587f, 115.675964f));
        var spawn = world.Scene.Markers.Where(x => x.Layer == SceneLayer.Spawns)
            .OrderByDescending(x => world.AllStates.Any(state => state.PackageEnabled && state.Ordinal == x.StateOrdinal
                && state.BubbleNameHash == world.CanonicalBubble(x.BubbleHash))).First();
        var forward = Vector3.Transform(Vector3.UnitX, spawn.Rotation); forward.Z = 0;
        forward = forward.LengthSquared() > .0001f ? Vector3.Normalize(forward) : Vector3.UnitX;
        var eye = spawn.Position + Vector3.UnitZ * 1.7f;
        var interior = new CameraView("interior", eye, eye + forward * 10);
        var views = new List<CameraView> { exterior, interior };
        foreach (var (name, tag, offset) in new[] { ("entrance", 0x80C4293Cu, 0x600), ("courtyard", 0x80C42FD2u, 0x30) })
        {
            var entranceSpawn = world.Scene.Markers.FirstOrDefault(x => x.Layer == SceneLayer.Spawns &&
                x.SourceTag == tag && x.SourceOffset == offset);
            if (entranceSpawn is null) continue;
            var direction = Vector3.Transform(Vector3.UnitX, entranceSpawn.Rotation); direction.Z = 0;
            direction = direction.LengthSquared() > .0001f ? Vector3.Normalize(direction) : Vector3.UnitX;
            var location = entranceSpawn.Position + Vector3.UnitZ * 1.7f;
            views.Add(new(name, location, location + direction * 10));
        }
        var plan = AssetViewport.PrepareNativeScene(catalog, world.Scene, textureBudgetBytes: long.MaxValue);
        viewport.SetNativeScene(plan);
        var native = viewport.NativeRenderer!;
        string requestedLighting = Option("--profile-lighting");
        if (requestedLighting.Length != 0) native.LightingMode = Enum.Parse<NativeLightingMode>(requestedLighting, true);
        var referenceBounds = viewport.Selection.Objects.Select(viewport.Selection.GetBounds).ToArray();
        var referenceGrid = new SceneGrid(referenceBounds.Select(x => x.Center).ToArray(), referenceBounds.Select(x => x.Radius).ToArray());
        void CheckVisibility(int width, int height)
        {
            // The reference has no distance levels, so the check renders at full detail.
            bool fullDetail = viewport.FullDetail;
            viewport.FullDetail = true;
            viewport.RenderImage(width, height, .4);
            viewport.FullDetail = fullDetail;
            var expected = new List<int>();
            referenceGrid.Query(SceneGrid.FrustumPlanes(viewport.Camera.ViewProjection((float)width / height)),
                viewport.Camera.Position, height / (2 * MathF.Tan(ViewerCamera.FieldOfView / 2)), viewport.Camera.Orthographic, 0,
                index => viewport.Visibility.IsVisible(viewport.Selection.Objects[index]), expected);
            if (!expected.SequenceEqual(viewport.VisibleIndices)) throw new InvalidOperationException("Optimized visibility changed the reference draw order or membership.");
        }
        Console.WriteLine($"frame_profile phase=ready adapter={renderer.AdapterName.Replace(' ', '_')} objects={world.Scene.Instances.Count} native={native.ReadyPartCount}");
        foreach (var view in (args.Contains("--no-captures") ? [] : views)
            .Where(x => Option("--profile-view").Length == 0 || Option("--profile-view").Split(',').Contains(x.Name)))
        {
            viewport.Camera.LookFrom(view.Eye, view.Pivot);
            native.RequestExposureWarmup();
            for (int i = 0; i < 24; i++) viewport.RenderImage(906, 533, i / 60d);
            var image = viewport.RenderImage(906, 533, .4);
            CheckVisibility(906, 533);
            if (!args.Contains("--no-captures")) NativeShadingInspection.Capture(renderer, viewport, image, Path.Combine(output, view.Name));
            Console.WriteLine($"frame_profile capture={view.Name} eye={view.Eye} pivot={view.Pivot} backdrop={viewport.Backdrop.ActiveBubbleHash:X8} atmosphere={native.AtmosphereIndex}");
        }
        if (args.Contains("--capture-only")) return;
        string cacheOptions = Option("--profile-caches");
        bool pairedCaches = cacheOptions.Length != 0;
        string[] cacheModes = pairedCaches ? cacheOptions.Split(',', StringSplitOptions.RemoveEmptyEntries) : ["current"];
        if (cacheModes.Length == 0 || cacheModes.Distinct().Count() != cacheModes.Length
            || pairedCaches && cacheModes.Any(mode => mode is not ("off" or "spatial")))
            throw new ArgumentException("Cache modes must be distinct off or spatial values.");
        int count = int.Parse(Option("--profile-frames", pairedCaches ? "60" : "90"));
        if (count <= 0) throw new ArgumentOutOfRangeException("--profile-frames");
        string[] motions = pairedCaches && Option("--profile-motion").Length == 0 ? ["idle", "orbit"] : ["idle", "orbit", "pan"];
        string[] lineModes = Option("--profile-lines", "current").Split(',');
        if (lineModes.Any(mode => mode is not ("current" or "direct" or "cached")))
            throw new ArgumentException("Line buffer modes must be current, direct, or cached.");
        bool checkParity = pairedCaches || lineModes.Length > 1;
        renderer.MeasureDiagnosticUploads = true;
        var exposures = new Dictionary<(string View, int Width, int Height, bool Sky, string Motion), float>();
        var parity = new Dictionary<(string View, int Width, int Height, bool Sky, string Motion), (string Pixels, int[] Visible)>();
        var points = new Dictionary<(int Width, int Height), (int X, int Y)[]>();
        var results = new List<object>();
        foreach (var view in views.Where(x => Option("--profile-view").Length == 0 || Option("--profile-view").Split(',').Contains(x.Name))
            .OrderBy(x => x.Name == "interior" ? 0 : x.Name == "entrance" ? 1 : 2))
            foreach (var (width, height) in new[] { (800, 500), (906, 533) })
                foreach (bool sky in requestedLighting.Length == 0 ? new[] { false, true } : new[] { false })
                    foreach (string motion in motions)
                    foreach (string cacheMode in cacheModes)
                    foreach (string lineMode in lineModes)
                    {
                        if (Option("--profile-size").Length != 0 && Option("--profile-size") != width.ToString()) continue;
                        if (Option("--profile-motion").Length != 0 && !Option("--profile-motion").Split(',').Contains(motion)) continue;
                        if (Option("--profile-sky").Length != 0 && bool.Parse(Option("--profile-sky")) != sky) continue;
                        native.CacheOpaqueDraws = true;
                        if (lineMode != "current") viewport.CacheOverlayLines = lineMode == "cached";
                        if (cacheMode != "current")
                        {
                            viewport.CacheSpatialCandidates = cacheMode == "spatial";
                        }
                        viewport.Camera.LookFrom(view.Eye, view.Pivot);
                        float yaw = viewport.Camera.Yaw, pitch = viewport.Camera.Pitch;
                        var right = viewport.Camera.Right; var up = viewport.Camera.Up;
                        if (requestedLighting.Length == 0) native.SkyLightingEnabled = sky;
                        var key = (view.Name, width, height, sky, motion);
                        if (checkParity)
                        {
                            if (!exposures.TryGetValue(key, out float exposure))
                            {
                                native.AutoExposure = true;
                                native.RequestExposureWarmup();
                                viewport.RenderImage(width, height, 0);
                                exposures.Add(key, exposure = native.AppliedViewerStops);
                            }
                            native.ViewerStops = exposure;
                            native.AutoExposure = false;
                        }
                        native.RequestExposureWarmup();
                        using var gpu = new GpuFrameTimer(renderer.Device,
                            ["Scene", "Mask", "Composite", "Collision", "Grid", "Volumes", "Markers", "Points", "Cursor"]);
                        viewport.OverlayGpuMark = gpu.Mark;
                        try
                        {
                        void Move(int frame)
                        {
                            if (motion == "orbit") viewport.Camera.SetView(yaw + MathF.Sin(frame * .05f) * .08f,
                                pitch + MathF.Sin(frame * .037f) * .02f);
                            if (motion == "pan") viewport.Camera.MovePivot(view.Pivot + right * (MathF.Sin(frame * .05f) * 1.5f)
                                + up * (MathF.Sin(frame * .035f) * .4f));
                        }
                        for (int frame = 0; frame < 20; frame++)
                        {
                            Move(frame); gpu.Begin(); viewport.RenderImage(width, height, frame / 60d); gpu.Mark(8); gpu.End(false);
                        }
                        gpu.Reset();
                        var cpu = new double[count];
                        var phases = new double[9];
                        var overlays = new double[8];
                        double lineMap = renderer.DiagnosticLineMapMilliseconds;
                        double lineUpload = renderer.DiagnosticLineUploadMilliseconds;
                        double lineConstants = renderer.DiagnosticLineConstantsMilliseconds;
                        double meshConstants = renderer.DiagnosticMeshConstantsMilliseconds;
                        long mappedVertices = renderer.DiagnosticLineMappedVertices;
                        long uploadedVertices = renderer.DiagnosticLineUploadedVertices;
                        int overlayLineUploads = viewport.OverlayLineUploads;
                        double record = native.CpuMilliseconds[0], submit = native.CpuMilliseconds[1], tfx = native.CpuMilliseconds[2];
                        double readback = native.ExposureReadbackMilliseconds;
                        int reads = native.ExposureReadbackCount, generations = native.SkyReflectionGenerations;
                        int busyReadbacks = native.ExposureReadbackBusyCount;
                        int skippedMeasurements = native.ExposureSkippedMeasurements;
                        int opaqueRecordings = native.OpaqueRecordings;
                        int spatialQueries = viewport.SceneCullQueryCount;
                        var packageReads = catalog.ReadStatistics;
                        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
                        long allocated = GC.GetAllocatedBytesForCurrentThread();
                        int drawStart = renderer.DrawCalls;
                        var whole = Stopwatch.StartNew();
                        GpuTexture image = null!;
                        for (int frame = 0; frame < count; frame++)
                        {
                            Move(frame + 20); gpu.Begin();
                            long began = Stopwatch.GetTimestamp();
                            image = viewport.RenderImage(width, height, (frame + 20) / 60d);
                            cpu[frame] = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                            gpu.Mark(8); gpu.End(true);
                            var timing = viewport.LastFrameTimings;
                            phases[0] += timing.VisibilityMilliseconds; phases[1] += timing.NativeMilliseconds;
                            phases[2] += timing.DisplayMilliseconds; phases[3] += timing.FallbackMilliseconds;
                            phases[4] += timing.OverlayMilliseconds;
                            phases[5] += timing.BackdropMilliseconds; phases[6] += timing.CullMilliseconds;
                            phases[7] += timing.VisibleListMilliseconds; phases[8] += timing.VisibilityAllocationBytes;
                            var overlay = viewport.LastOverlayTimings;
                            overlays[0] += overlay.MaskMilliseconds; overlays[1] += overlay.CompositeMilliseconds;
                            overlays[2] += overlay.CollisionMilliseconds; overlays[3] += overlay.GridMilliseconds;
                            overlays[4] += overlay.VolumeMilliseconds; overlays[5] += overlay.MarkerMilliseconds;
                            overlays[6] += overlay.PointMilliseconds; overlays[7] += overlay.CursorMilliseconds;
                        }
                        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                        viewport.OverlayGpuMark = null;
                        var measuredFrame = native.LastFrame;
                        var measuredReads = catalog.ReadStatistics;
                        double measuredRecord = native.CpuMilliseconds[0] - record;
                        double measuredSubmit = native.CpuMilliseconds[1] - submit;
                        double measuredTfx = native.CpuMilliseconds[2] - tfx;
                        double measuredReadback = native.ExposureReadbackMilliseconds - readback;
                        int measuredReadbacks = native.ExposureReadbackCount - reads;
                        int measuredBusy = native.ExposureReadbackBusyCount - busyReadbacks;
                        int measuredSkipped = native.ExposureSkippedMeasurements - skippedMeasurements;
                        int measuredGenerations = native.SkyReflectionGenerations - generations;
                        int measuredOpaque = native.OpaqueRecordings - opaqueRecordings;
                        int measuredSpatial = viewport.SceneCullQueryCount - spatialQueries;
                        int measuredDraws = renderer.DrawCalls - drawStart;
                        int measuredLineUploads = viewport.OverlayLineUploads - overlayLineUploads;
                        long measuredMappedVertices = renderer.DiagnosticLineMappedVertices - mappedVertices;
                        long measuredUploadedVertices = renderer.DiagnosticLineUploadedVertices - uploadedVertices;
                        double measuredLineMap = renderer.DiagnosticLineMapMilliseconds - lineMap;
                        double measuredLineUpload = renderer.DiagnosticLineUploadMilliseconds - lineUpload;
                        double measuredLineConstants = renderer.DiagnosticLineConstantsMilliseconds - lineConstants;
                        double measuredMeshConstants = renderer.DiagnosticMeshConstantsMilliseconds - meshConstants;
                        int measuredGen0 = GC.CollectionCount(0) - gen0;
                        int measuredGen1 = GC.CollectionCount(1) - gen1;
                        int measuredGen2 = GC.CollectionCount(2) - gen2;
                        long drainBegan = Stopwatch.GetTimestamp();
                        renderer.ReadTexturePixel(image, 0, 0);
                        double drain = Stopwatch.GetElapsedTime(drainBegan).TotalMilliseconds;
                        whole.Stop(); Array.Sort(cpu);
                        CheckVisibility(width, height);
                        string? pixelsHash = null;
                        if (checkParity)
                        {
                            var measuredVisible = viewport.VisibleIndices.ToArray();
                            native.RequestExposureWarmup();
                            image = viewport.RenderImage(width, height, (count + 19) / 60d);
                            if (!viewport.VisibleIndices.SequenceEqual(measuredVisible))
                                throw new InvalidOperationException("Settling exposure changed the measured visibility order.");
                            if (!points.TryGetValue((width, height), out var pixelPoints))
                                points[(width, height)] = pixelPoints = Enumerable.Range(0, height)
                                    .SelectMany(y => Enumerable.Range(0, width).Select(x => (x, y))).ToArray();
                            var pixels = renderer.ReadTexturePixels(image, pixelPoints);
                            pixelsHash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(pixels.AsSpan())));
                            if (parity.TryGetValue(key, out var expected))
                            {
                                if (pixelsHash != expected.Pixels || !viewport.VisibleIndices.SequenceEqual(expected.Visible))
                                    throw new InvalidOperationException($"Cache mode {cacheMode}/{lineMode} changed pixels or ordered visibility for {view.Name}/{motion}.");
                            }
                            else parity.Add(key, (pixelsHash, viewport.VisibleIndices.ToArray()));
                        }
                        var volumes = viewport.VisibleIndices.Select(index => viewport.Selection.Objects[index])
                            .OfType<MeshInstance>().Where(instance => instance.Layer == SceneLayer.Volumes).ToArray();
                        var result = new
                        {
                            Camera = view.Name, Eye = new[] { view.Eye.X, view.Eye.Y, view.Eye.Z },
                            Pivot = new[] { view.Pivot.X, view.Pivot.Y, view.Pivot.Z },
                            Width = width, Height = height, Sky = sky, Lighting = native.LightingMode.ToString(), Motion = motion,
                            Caches = cacheMode, viewport.CacheSpatialCandidates,
                            Lines = lineMode, viewport.CacheOverlayLines,
                            native.CacheOpaqueDraws,
                            FixedExposure = checkParity, ExposureStops = native.AppliedViewerStops,
                            PixelComparison = checkParity ? "settled-current-camera" : null,
                            PixelSha256 = pixelsHash, ParityReference = checkParity ? cacheModes[0] + "/" + lineModes[0] : null,
                            MedianMs = cpu[count / 2], P95Ms = cpu[Math.Min(count - 1, (int)(count * .95))],
                            MeanMs = cpu.Average(), ThroughputMs = whole.Elapsed.TotalMilliseconds / count, DrainMs = drain,
                            GpuMs = gpu.Frames > 0 ? gpu.Totals.Sum() / gpu.Frames : 0, GpuSamples = gpu.Frames,
                            GpuSections = gpu.Sections.Select((name, index) => new
                            { Name = name, Milliseconds = gpu.Frames > 0 ? gpu.Totals[index] / gpu.Frames : 0 }).ToArray(),
                            AllocationPerFrame = bytes / count, Gen0 = measuredGen0,
                            Gen1 = measuredGen1, Gen2 = measuredGen2,
                            VisibilityMs = phases[0] / count, NativeMs = phases[1] / count, DisplayMs = phases[2] / count,
                            FallbackMs = phases[3] / count, OverlayMs = phases[4] / count,
                            BackdropMs = phases[5] / count, CullMs = phases[6] / count,
                            VisibleListMs = phases[7] / count, VisibilityAllocation = phases[8] / count,
                            OverlayMaskMs = overlays[0] / count, OverlayCompositeMs = overlays[1] / count,
                            OverlayCollisionMs = overlays[2] / count, OverlayGridMs = overlays[3] / count,
                            OverlayVolumeMs = overlays[4] / count, OverlayMarkerMs = overlays[5] / count,
                            OverlayPointMs = overlays[6] / count, OverlayCursorMs = overlays[7] / count,
                            LineMapMs = measuredLineMap / count,
                            LineUploadMs = measuredLineUpload / count,
                            LineConstantsMs = measuredLineConstants / count,
                            MeshConstantsMs = measuredMeshConstants / count,
                            LineMappedBytesPerFrame = measuredMappedVertices * 28 / count,
                            LineUploadedBytesPerFrame = measuredUploadedVertices * 28 / count,
                            OverlayLineUploads = measuredLineUploads,
                            RecordMs = measuredRecord / count,
                            SubmitMs = measuredSubmit / count,
                            TfxMs = measuredTfx / count,
                            ReadbackMs = measuredReadback / count,
                            Readbacks = measuredReadbacks,
                            ReadbackBusy = measuredBusy,
                            SkippedMeasurements = measuredSkipped,
                            ReflectionGenerations = measuredGenerations,
                            OpaqueRecordings = measuredOpaque,
                            SpatialQueries = measuredSpatial,
                            PackageReads = measuredReads.EntryReads - packageReads.EntryReads,
                            BlocksRead = measuredReads.BlockReads - packageReads.BlockReads,
                            measuredFrame.DrawnParts, measuredFrame.DrawCalls, measuredFrame.MaterialEvaluations,
                            TotalDrawCalls = measuredDraws / count,
                            VisibleVolumes = volumes.Length,
                            VolumeMeshes = volumes.Select(instance => instance.Mesh).Distinct().Count(),
                            VolumeVertices = volumes.Sum(instance => (long)instance.Mesh.Vertices.Length),
                            VolumeTriangles = volumes.Sum(instance => (long)instance.Mesh.Indices.Length / 3),
                            native.AtmosphereIndex, Backdrop = viewport.Backdrop.ActiveBubbleHash
                        };
                        results.Add(result);
                        Console.WriteLine($"frame_profile camera={view.Name} size={width}x{height} sky={sky} motion={motion} caches={cacheMode} lines={lineMode} median_ms={result.MedianMs:F2} p95_ms={result.P95Ms:F2} visibility_ms={result.VisibilityMs:F2} backdrop_ms={result.BackdropMs:F2} cull_ms={result.CullMs:F2} list_ms={result.VisibleListMs:F2} visibility_allocation={result.VisibilityAllocation:F0} native_ms={result.NativeMs:F2} readback_ms={result.ReadbackMs:F2} tfx_ms={result.TfxMs:F2} allocation={result.AllocationPerFrame} reads={result.PackageReads} generations={result.ReflectionGenerations} spatial_queries={result.SpatialQueries} pixel_sha={pixelsHash}");
                        string checkpointLogs = Path.Combine(output, "logs"); Directory.CreateDirectory(checkpointLogs);
                        File.WriteAllText(Path.Combine(checkpointLogs, "frame-times.json"), JsonSerializer.Serialize(new
                        { renderer.AdapterName, Frames = count, Instances = world.Scene.Instances.Count, Results = results },
                            new JsonSerializerOptions { WriteIndented = true }));
                        }
                        finally { viewport.OverlayGpuMark = null; }
                    }
        string logs = Path.Combine(output, "logs"); Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "frame-times.json"), JsonSerializer.Serialize(new
        {
            renderer.AdapterName, Frames = count, Instances = world.Scene.Instances.Count,
            Markers = world.Scene.Markers.Count, Results = results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

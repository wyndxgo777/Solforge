using System.Numerics;
using System.Reflection;
using SunriseStudio.Animation;
using SunriseStudio.Editor;
using SunriseStudio.Packages;
using SunriseStudio.Viewport;

/** Checks the live pose source: prepared weights and refusals follow the overlay generation and stale worker results are dropped. */
internal static class LivePoseSourceChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /** Completed weights and errors must still belong to the catalog bytes the page displays. */
    internal static int Run(AssetViewport viewport)
    {
        string root = Path.Combine(Path.GetTempPath(), "sunrise-live-pose-source-" + Guid.NewGuid().ToString("N"));
        string install = Path.Combine(root, "install");
        try
        {
            WorldFixture.Create(install);
            using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
            using var reopened = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
            using var workspace = new StudioWorkspace();
            using var page = new LivePage();
            var key = (WorldFixture.Base + 5, WorldFixture.Base + 6);
            var oldWeights = Binding(key.Item2, 0);
            var newWeights = Binding(key.Item2, 1);
            Property(workspace, "Catalog", catalog);
            try
            {
                Poll();
                var bindings = Field<Dictionary<(uint, uint), LivePoseModelBinding>>(page, "poseBindings");
                var errors = Field<Dictionary<(uint, uint), string>>(page, "poseErrors");
                bindings.Add(key, oldWeights); errors.Add((key.Item1, key.Item2 + 1), "Old source refusal");
                Poll();
                Check(ReferenceEquals(bindings[key], oldWeights) && errors.Count == 1,
                    "An unchanged source discarded its prepared weights or refusal.");

                var stale = Pending(catalog);
                long originalGeneration = catalog.ReadGeneration;
                catalog.ReadOverride = tag => tag == key.Item2 ? new byte[] { 1, 2, 3, 4 } : null;
                Check(catalog.ReadGeneration == originalGeneration, "The overlay fixture changed the original-byte generation.");
                Poll();
                Check(bindings.Count == 0 && errors.Count == 0 && Field<CancellationTokenSource>(page, "poseCancellation").IsCancellationRequested,
                    "Changed overlay bytes retained prepared weights, a refusal or active work.");
                stale.SetResult((oldWeights, "")); Poll();
                Check(bindings.Count == 0 && Field<object?>(page, "poseWork") is null,
                    "A worker republished weights prepared before the overlay change.");
                var current = Pending(catalog); current.SetResult((newWeights, "")); Poll();
                Check(ReferenceEquals(bindings[key], newWeights)
                    && Field<IReadOnlyList<ModelSkinBinding>>(bindings[key], "bindings")[0].Vertices[0].Bone0 == 1,
                    "The changed source could not publish its replacement weights under the same tags.");

                errors.Add((key.Item1, key.Item2 + 1), "Old decoder refusal");
                catalog.InvalidateReadCache(); Poll();
                Check(bindings.Count == 0 && errors.Count == 0,
                    "Original-byte invalidation retained prepared weights or errors.");
                var invalidated = Pending(catalog);
                catalog.InvalidateReadCache(); invalidated.SetResult((oldWeights, "Late refusal"));
                Invoke(page, "FinishPosePreparation");
                Check(bindings.Count == 0 && errors.Count == 0,
                    "Completion admitted a changed source before the next page poll.");
                Poll(); bindings.Add(key, newWeights);

                while (reopened.ReadGeneration < catalog.ReadGeneration) reopened.InvalidateReadCache();
                while (reopened.OverlayRevision < catalog.OverlayRevision) reopened.ReadOverride = null;
                Check(reopened.GameRoot == catalog.GameRoot && reopened.ReadGeneration == catalog.ReadGeneration
                    && reopened.OverlayRevision == catalog.OverlayRevision, "Reopened catalog did not reproduce the same path and counters.");
                var retiredCatalog = Pending(catalog);
                Property(workspace, "Catalog", reopened); Poll();
                Check(bindings.Count == 0, "A reopened installation inherited another catalog's weights.");
                retiredCatalog.SetResult((oldWeights, "Late catalog refusal")); Poll();
                Check(bindings.Count == 0 && errors.Count == 0,
                    "A worker from the replaced catalog published a result or refusal.");
                var replacement = Pending(reopened); replacement.SetResult((newWeights, "")); Poll();
                Check(ReferenceEquals(bindings[key], newWeights), "The replacement catalog could not prepare the same model tags.");
                Console.WriteLine("live_pose_source unchanged=retained overlay=revoked originals=revoked catalog=isolated stale_worker=rejected retry=accepted");
                return 1;

                void Poll() => Invoke(page, "PollSelectedPose", workspace, viewport);
                TaskCompletionSource<(LivePoseModelBinding? Binding, string Error)> Pending(PackageCatalog source)
                {
                    var pending = new TaskCompletionSource<(LivePoseModelBinding? Binding, string Error)>();
                    Set(page, "poseCancellation", new CancellationTokenSource());
                    Set(page, "poseWork", pending.Task); Set(page, "poseWorkKey", key);
                    typeof(LivePage).GetField("poseWorkSource", Private)?.SetValue(page,
                        (source, source.ReadGeneration, source.OverlayRevision));
                    return pending;
                }
            }
            finally { Property(workspace, "Catalog", null); }
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, true);
        }
    }

    private static LivePoseModelBinding Binding(uint model, int bone) =>
        (LivePoseModelBinding)Activator.CreateInstance(typeof(LivePoseModelBinding), Private, null,
            [model, new ModelSkinBinding[] { new(model, 0, 1, 8, [0],
                [new VertexSkinWeights(bone, 0, 0, 0, Vector4.UnitX)]) }], null)!;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void Set(object value, string name, object? data) => value.GetType().GetField(name, Private)!.SetValue(value, data);
    private static void Property(object value, string name, object? data) => value.GetType().GetProperty(name)!.SetValue(value, data);
    private static void Invoke(object value, string name, params object[] args) => value.GetType().GetMethod(name, Private)!.Invoke(value, args);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}

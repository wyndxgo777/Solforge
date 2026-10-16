using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Installed assets use a prepared tiny world so navigation does not load a full map or name index. */
internal static class AssetNavigationChecks
{
    private const uint EntityTag = 0x80C0FA98, ModelTag = 0x80FEE881, ClipTag = 0x80FE2C08;

    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(string gameRoot, string temporaryRoot)
    {
        string fixturePath = Path.Combine(temporaryRoot, "asset-navigation-world");
        WorldFixture.Create(fixturePath);
        using var fixtureCatalog = PackageCatalog.Open(fixturePath);
        var loader = new WorldLoader(fixtureCatalog);
        var world = loader.LoadMission(WorldFixture.Base, WorldFixture.Base + 4);
        var catalog = PackageCatalog.Open(gameRoot);
        using var workspace = new StudioWorkspace();
        SetPrepared(workspace, nameof(StudioWorkspace.Catalog), catalog);
        SetPrepared(workspace, nameof(StudioWorkspace.Names), StudioNameIndex.Create(catalog));
        SetPrepared(workspace, nameof(StudioWorkspace.World), world);
        SetPrepared(workspace, nameof(StudioWorkspace.WorldCatalog), loader.BuildCatalog());
        var entityScene = new AssetSceneLoader(catalog).Load(EntityTag);
        var selected = entityScene.Instances.First(mesh => mesh.Mesh.TagHash == ModelTag
            && mesh.ObjectClassTag == EntityTag && mesh.ModelSourceConfigTag != 0);
        var context = AssetOpenContext.ForObject(selected) ?? throw new InvalidDataException("Scene asset context is missing.");
        int sceneRevision = workspace.SceneRevision;
        workspace.Select(ModelTag, context: context);
        Wait(workspace);
        CheckModel(workspace.Document!, context);
        Require(ReferenceEquals(workspace.World, world) && ReferenceEquals(workspace.ActiveScene, world.Scene)
            && workspace.SceneRevision == sceneRevision, "Opening a model replaced the retained world.");
        var linkedClip = workspace.Document!.Connections!.Related.First(link => link.Kind == AssetRelationshipKind.AnimationClip
            && link.TagHash == ClipTag);
        Require(linkedClip.Context?.EntityTag == EntityTag, "Related clip omitted its verified animation owner.");
        workspace.SelectRelated(linkedClip);
        Wait(workspace);
        CheckClip(workspace.Document!);
        Play(workspace.Document!);
        Require(workspace.CanNavigateBack, "Clip navigation did not retain the selected model.");
        workspace.NavigateBack();
        Wait(workspace);
        CheckModel(workspace.Document!, context);
        Require(workspace.CanNavigateForward, "Back navigation discarded the clip visit.");
        workspace.NavigateForward();
        Wait(workspace);
        CheckClip(workspace.Document!);
        Play(workspace.Document!);
        Require(ReferenceEquals(workspace.World, world) && ReferenceEquals(workspace.ActiveScene, world.Scene)
            && workspace.SceneRevision == sceneRevision, "Asset history changed the loaded world.");
        CheckConnections(workspace, catalog);
        workspace.NavigateBack(); Wait(workspace);
        CheckConnections(workspace, catalog);
        workspace.Select(EntityTag); Wait(workspace);
        CheckConnections(workspace, catalog);
        Require(!workspace.CanNavigateForward, "A new asset visit retained the abandoned forward branch.");
        Console.WriteLine("editor_assets model-context=pass entity-preview=pass clip-playback=pass history=pass world=retained");
    }

    /** Test setup supplies already prepared state; every navigation action uses the public workspace API. */
    private static void SetPrepared(StudioWorkspace workspace, string property, object value) =>
        (typeof(StudioWorkspace).GetProperty(property, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(property)).SetValue(workspace, value);

    private static void CheckModel(StudioDocument document, AssetOpenContext context)
    {
        Require(document.Entry.TagHash == ModelTag && document.OpenContext == context,
            "Model inspection lost the selected asset or scene context.");
        Require(document.Connections?.PreviewTag == EntityTag && document.Connections.AnimationEntityTag == EntityTag
            && document.Scene?.TagHash == EntityTag && document.Animation?.Rig.EntityTag == EntityTag,
            "Model selection did not prepare its owning entity and animation source.");
        Require(document.Animation!.Clips.Any(clip => clip.ClipTag == ClipTag)
            && document.NativePlan is not null, "Prepared model omitted its clips or native asset plan.");
    }

    /** The document's one connection read equals a full read with its loaded scene, as the second read gave. */
    private static void CheckConnections(StudioWorkspace workspace, PackageCatalog catalog)
    {
        var document = workspace.Document!;
        uint tag = document.Entry.TagHash;
        var full = new AssetConnectionReader(catalog).Read(tag, document.OpenContext, document.Scene, workspace.ResolveName,
            workspace.Names?.GetAssetOwners(tag), default, document.Inspection);
        var actual = document.Connections!;
        Require(actual.AssetTag == full.AssetTag && actual.PreviewTag == full.PreviewTag
            && actual.AnimationEntityTag == full.AnimationEntityTag && actual.SelectedClipTag == full.SelectedClipTag
            && actual.Related.SequenceEqual(full.Related) && actual.EntityCandidates.SequenceEqual(full.EntityCandidates)
            && actual.Diagnostics.SequenceEqual(full.Diagnostics), $"Connections of {tag:X8} differ from a full read.");
    }

    private static void CheckClip(StudioDocument document)
    {
        Require(document.Entry.TagHash == ClipTag && document.OpenContext?.EntityTag == EntityTag
            && document.Connections?.SelectedClipTag == ClipTag && document.Connections.PreviewTag == EntityTag
            && document.Animation?.Rig.EntityTag == EntityTag && document.Scene?.TagHash == EntityTag,
            "Related clip navigation lost its owner, selected clip, or entity preview.");
    }

    /** The real playback controller must update skinned vertices after asynchronous clip loading. */
    private static void Play(StudioDocument document)
    {
        int updates = 0;
        var uploaded = new Dictionary<MeshData, RenderVertex[]>();
        using var animation = new AssetAnimationPreview(document.Animation!, document.Scene!,
            (mesh, vertices) => { uploaded[mesh] = vertices.ToArray(); updates++; }, () => { });
        animation.SelectClip(document.Connections!.SelectedClipTag);
        var watch = Stopwatch.StartNew();
        while (animation.IsLoading && watch.Elapsed < TimeSpan.FromSeconds(15))
        { animation.Tick(0); Thread.Sleep(1); }
        Require(!animation.IsLoading && animation.Error is null && animation.Clip?.TagHash == ClipTag
            && animation.Playing && animation.SkinnedPartCount > 0, animation.Error ?? "Selected clip did not start playback.");
        var initial = uploaded.ToDictionary(pair => pair.Key, pair => pair.Value);
        int initialUpdates = updates;
        animation.Tick(.1f);
        Require(animation.TimeSeconds > 0 && updates > initialUpdates,
            "Animation playback did not advance or upload a pose.");
        Require(uploaded.Any(pair => initial.TryGetValue(pair.Key, out var before)
            && pair.Value.Where((vertex, index) => Vector3.DistanceSquared(vertex.Position, before[index].Position) > 1e-8f).Any()),
            "Playing the selected clip did not move any skinned vertex.");
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var watch = Stopwatch.StartNew();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(45))
        { workspace.Poll(); Thread.Sleep(2); }
        Require(!workspace.IsBusy, "Installed asset navigation timed out.");
        Require(workspace.Error.Length == 0, workspace.Error);
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}

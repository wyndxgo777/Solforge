using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Nodes take their trigger from the sequence; only self-starting continuous nodes run by default. */
internal static class EffectActivationChecks
{
    private const uint Alive = 0x270E8F60;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string gameRoot)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var loader = new AssetSceneLoader(catalog);
        Centurion(loader);
        var beast = Nodes(loader, 0x80BEB497);
        Require(beast.Length == 2 && beast.All(a => !a.PlaysByDefault() && a.Gates.Count == 1 && a.Gates[0].Min == 0.0101f)
                && beast.Select(a => a.Gates[0].Channel).Order().SequenceEqual([0xC5586463u, 0xC558647Du]),
            "War Beast 80BEB497 nodes are not gated on C558647D and C5586463 from 0.0101.");
        var clip = new EffectReader(catalog).ReadClipChannels(0x80FEE13B);
        Require(clip.Contains(0xC558647Du) && clip.Contains(0xC5586463u) && clip.Contains(0xC241FBB8u),
            $"Clip 80FEE13B lists [{string.Join(", ", clip.Select(h => h.ToString("X8")))}].");
        Expect(loader, 0x80B3944A, 3, a => a is { Continuous: true, Gates.Count: 0 } && a.PlaysByDefault(), "on and continuous");
        Expect(loader, 0x80B3A469, 2, a => a is { AutoStart: true, Continuous: false } && !a.PlaysByDefault(), "plays once and off");
        Expect(loader, 0x80C156B1, 3, a => !a.AutoStart && !a.PlaysByDefault(), "gameplay-started and off");
        Expect(loader, 0x80B3BDA1, 2, a => a.RandomPicks.Count == 1 && !a.PlaysByDefault(), "a random branch and off");
        // The earlier sibling runs 3 to 6 s and the serial flow loops, so the node starts at 6 s.
        Expect(loader, 0x80B3BE21, 1, a => a is { SerialStart: 6, Continuous: true } && a.PlaysByDefault(), "serial after a timed sibling");
        Expect(loader, 0x80B587FF, 2, a => a.Gates is [{ Min: float.MinValue, Max: 0.0001f }] && a.PlaysByDefault(),
            "gated below 0.0001 and on at 0");
        Expect(loader, 0x80B5910E, 1, a => a.Gates.Any(g => g.Min > 0.99f && g.Min <= 1) && !a.PlaysByDefault(),
            "gated from 1 and off");
        Console.WriteLine("effect_activation entities=9 result=pass");
        Simulation(new EffectReader(catalog));
        Viewport(catalog);
        Animated(catalog);
    }

    /** The viewport runs default-on emitters; channels, Show all and force-on open the rest. */
    private static void Viewport(PackageCatalog catalog)
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, 160, 120, true);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false, Catalog = catalog };
        viewport.SetScene(new AssetSceneLoader(catalog).Load(0x80C19B1F));
        static bool Jetpack(EffectEmitterSetup e) => e.Marker.Effect!.SourceTag == 0x80FEE123;
        var emitters = viewport.EffectEmitters;
        bool State(bool jetpack, bool root)
        {
            viewport.RenderImage(160, 120);
            return emitters.Where(Jetpack).All(e => e.Simulation!.Active == jetpack)
                && emitters.Where(e => !Jetpack(e)).All(e => e.Simulation!.Active == root);
        }
        Require(State(false, true), "The Centurion's default run states differ from its triggers.");
        var controls = viewport.EffectControls;
        var key = EffectPlacementKey.Of(emitters.First(Jetpack).Marker);
        controls.SetChannel(key, Alive, 1);
        Require(State(false, true), "One open gate of two let the jetpack run.");
        controls.SetChannel(key, 0xC241FBB8, 1);
        Require(State(true, true), "Opening both jetpack gates did not run the jetpack.");
        controls.SetChannel(key, Alive, 0);
        Require(State(false, true), "Closing a jetpack gate did not stop the jetpack.");
        controls.ShowAll = true;
        Require(State(true, true) && emitters.All(e => e.Simulation!.Loop), "Show all did not run every emitter.");
        controls.ShowAll = false;
        var forced = emitters.First(Jetpack).Marker;
        controls.SetForced(forced, true);
        viewport.RenderImage(160, 120);
        Require(emitters.Where(Jetpack).All(e => e.Simulation!.Active == ReferenceEquals(e.Marker, forced)),
            "Force-on did not run only its own effect.");
        controls.SetForced(forced, false);
        var root = emitters.First(e => !Jetpack(e));
        controls.Play(key, root.Marker.Effect!.Activation!.SequenceTag, true);
        viewport.RenderImage(160, 120);
        Require(root.Generation == 1, "Play did not restart the root sequence.");
        Console.WriteLine($"effect_activation_viewport emitters={emitters.Count} gates=pass show_all=pass force=pass play=pass result=pass");
    }

    /** While the preview shows a clip that lists a gate's channel, the gate reads 1. */
    private static void Animated(PackageCatalog catalog)
    {
        const uint beast = 0x80BEB497;
        var source = AssetAnimationSource.Prepare(catalog, beast);
        var reader = new EffectReader(catalog);
        uint clip = source.Clips.Select(c => c.ClipTag).Distinct()
            .FirstOrDefault(tag => reader.ReadClipChannels(tag) is var list && list.Contains(0xC558647Du) && list.Contains(0xC5586463u));
        Require(clip != 0, "No War Beast clip lists both of its gate channels.");
        using var renderer = new Dx11Renderer(IntPtr.Zero, 160, 120, true);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false, Catalog = catalog };
        viewport.SetScene(new AssetSceneLoader(catalog).Load(beast));
        viewport.RenderImage(160, 120);
        var emitters = viewport.EffectEmitters.Where(e => e.Simulation is not null).ToArray();
        Require(emitters.Length > 0 && emitters.All(e => !e.Simulation!.Active), "War Beast emitters run before a clip.");
        viewport.SetAnimation(source);
        viewport.Animation!.SetClip(source.ReadClip(clip), false);
        viewport.RenderImage(160, 120);
        Require(emitters.All(e => e.Simulation!.Active), $"Clip {clip:X8} did not open the War Beast gates.");
        viewport.DetachAnimation();
        viewport.RenderImage(160, 120);
        Require(emitters.All(e => !e.Simulation!.Active), "War Beast gates stayed open after the clip was removed.");
        Console.WriteLine($"effect_activation_clip asset={beast:X8} clip={clip:X8} emitters={emitters.Length} result=pass");
    }

    /** The jetpack nodes wait on C241FBB8 and alive; the root nodes run from spawn. */
    private static void Centurion(AssetSceneLoader loader)
    {
        var scene = loader.Load(0x80C19B1F);
        var effects = scene.Markers.Select(m => m.Effect).OfType<EffectDescription>().Where(e => e.Kind == EffectKind.ParticleSystem).ToArray();
        var root = effects.Where(e => e.SourceTag == 0x80FEE12E).ToArray();
        var jetpack = effects.Where(e => e.SourceTag == 0x80FEE123).ToArray();
        Require(root.Length == 3 && root.All(e => e.Activation is { Continuous: true } && e.PlaysByDefault()),
            "Centurion root nodes of 80FEE12E are not on and continuous.");
        var both = new Dictionary<uint, float> { [0xC241FBB8] = 1, [Alive] = 1 };
        Require(jetpack.Length == 11 && jetpack.All(e => e.Activation is { } a && !a.PlaysByDefault() && a.PlaysByDefault(both)
                && a.Gates.Count == 2 && a.Gates.All(g => g.Min == 0.0001f && g.Max == float.MaxValue)
                && a.Gates.Select(g => g.Channel).Order().SequenceEqual([Alive, 0xC241FBB8u])),
            "Centurion jetpack nodes of 80FEE123 are not gated on C241FBB8 and alive: " + string.Join("; ", jetpack.Select(e =>
                e.Activation is { } a ? $"{a.Trigger()} [{string.Join(", ", a.Gates)}]" : "no activation")));
        Require(jetpack.All(e => !e.PlaysByDefault(new Dictionary<uint, float> { [Alive] = 1 })),
            "A Centurion jetpack node opens with one of its two gates closed.");
        Console.WriteLine("effect_activation asset=80C19B1F root=3 continuous jetpack=11 gated=C241FBB8,alive result=pass");
    }

    private static EffectActivation[] Nodes(AssetSceneLoader loader, uint entity) => loader.Load(entity).Markers
        .Select(m => m.Effect).OfType<EffectDescription>().Where(e => e.Kind == EffectKind.ParticleSystem)
        .Select(e => e.Activation ?? throw new InvalidOperationException($"A node of 0x{entity:X8} has no activation.")).ToArray();

    private static void Expect(AssetSceneLoader loader, uint entity, int count, Func<EffectActivation, bool> state, string text)
    {
        var nodes = Nodes(loader, entity);
        Require(nodes.Length == count && nodes.All(state), $"0x{entity:X8} has {nodes.Length} nodes; expected {count}, {text}: "
            + string.Join("; ", nodes.Select(a => a.Trigger())));
        Console.WriteLine($"effect_activation asset={entity:X8} nodes={count} state=\"{text}\" result=pass");
    }

    /** A play-once node waits for a start and runs once; a closing gate stops emission. */
    private static void Simulation(EffectReader reader)
    {
        var emitter = reader.ReadEmitter(0x80B38183) ?? throw new InvalidOperationException("Ring fixture emitter is missing.");
        var once = new EmitterSimulation(emitter, Matrix4x4.Identity, [], 0.5f, EffectSimulation.EmitterCap, 1)
        { Loop = false, Delay = 0.5f };
        once.Advance(0, 10);
        once.Advance(0.5, 10);
        Require(once.Count == 0 && !once.Running, "A play-once node ran before it was started.");
        once.Start();
        once.Advance(0.9, 10);
        Require(once.Count == 0 && once.Running, "A play-once node did not wait for its delay.");
        once.Advance(1.2, 10);
        Require(once.Count > 0, "A play-once node did not emit after its delay.");
        for (double t = 1.2; t < 20; t += 0.5) once.Advance(t, 10);
        Require(once.Count == 0 && !once.Running, "A play-once node restarted after its run.");

        var gated = new EmitterSimulation(emitter, Matrix4x4.Identity, [], 0, EffectSimulation.EmitterCap, 2) { Active = false };
        gated.Advance(0, 10);
        gated.Advance(0.5, 10);
        Require(gated.Count == 0 && !gated.Running, "A closed gate let its node emit.");
        gated.Active = true;
        gated.Advance(1, 10);
        int open = gated.Count;
        Require(open > 0, "A rising gate edge did not start its node.");
        gated.Active = false;
        int before = gated.Count;
        gated.Advance(1.1, 10);
        Require(gated.Count <= before, "A falling gate edge did not stop emission.");
        for (double t = 1.1; t < 30 && gated.Running; t += 0.5) gated.Advance(t, 10);
        Require(gated.Count == 0 && !gated.Running, "Particles of a closed gate did not die.");
        Console.WriteLine($"effect_activation_simulation play_once=pass delay=pass gate_open={open} gate_close=pass result=pass");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

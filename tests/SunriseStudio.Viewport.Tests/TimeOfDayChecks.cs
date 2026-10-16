using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** A map's start time: the time-of-day placement's +0x18 value, wrapped, from the list beside the sky's atmosphere. */
internal static class TimeOfDayChecks
{
    private const uint AltarOfFlame = 0x80FA4003, Eternity = 0x80F92002, DeepSix = 0x81508008, Edz = 0x80ED7B28;
    // EDZ's main list starts at 0.5; its forge bubble's list starts at 0.
    private const uint EdzMainList = 0x80C582C7, EdzForgeList = 0x80F2AE16;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string install)
    {
        Synthetic();
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        Map(catalog, AltarOfFlame, 0.4f, 3600);
        Map(catalog, Eternity, 0, 900);
        Map(catalog, DeepSix, 0.5313433f, 7200);
        var edz = Map(catalog, Edz, 0.5f, 3600);
        Check(edz.Time?.PlacementListTag == EdzMainList && edz.Atmosphere?.PlacementListTag == EdzMainList,
            $"EDZ starts from list {edz.Time?.PlacementListTag:X8}, not {EdzMainList:X8}.");
    }

    /** No placement keeps the viewer default; a start time wraps into [0, 1); a set viewport time holds across scenes. */
    private static void Synthetic()
    {
        Check(NativeScenePlan.SkySource(new SceneDocument()) is { Atmosphere: null, Time: null, StartTime: NativeFrameInputs.ViewerTimeOfDay },
            "A scene with no time of day did not keep the viewer default.");
        foreach (var (authored, expected) in new[] { (1.25f, 0.25f), (-0.25f, 0.75f), (float.NaN, 0f), (1f, 0f) })
        {
            float start = NativeScenePlan.SkySource(Scene(authored)).StartTime;
            Check(start == expected, $"Start time {authored} wraps to {start}, not {expected}.");
        }
        using var renderer = new Dx11Renderer(IntPtr.Zero, 64, 64, true);
        using var viewport = new AssetViewport(renderer);
        viewport.SetScene(Scene(0.4f));
        Check(viewport.TimeOfDay == 0.4f, $"A map opened at {viewport.TimeOfDay}, not its start time 0.4.");
        viewport.SetScene(new SceneDocument());
        Check(viewport.TimeOfDay == NativeFrameInputs.ViewerTimeOfDay, "An asset view kept the previous map's time.");
        viewport.TimeOfDay = 0.6f;
        viewport.SetScene(Scene(0.4f));
        Check(viewport.TimeOfDay == 0.6f, "A new map replaced the time the user chose.");
        Console.WriteLine("time_of_day_synthetic result=pass");
    }

    private static SceneDocument Scene(float start)
    {
        var scene = new SceneDocument();
        scene.TimesOfDay.Add(new PlacedTimeOfDay(0x12345678, 0x80A00001, 0x80A00002, 3600, start));
        return scene;
    }

    /** The installed map's authored start time and day length, as the scene loader reads them. */
    private static NativeSkySource Map(PackageCatalog catalog, uint map, float start, float daySeconds)
    {
        var scene = new AssetSceneLoader(catalog).Load(map);
        var source = NativeScenePlan.SkySource(scene);
        Check(source.Time is { } time && MathF.Abs(time.StartTime - start) < 1e-6f && time.DaySeconds == daySeconds
            && MathF.Abs(source.StartTime - start) < 1e-6f,
            $"{map:X8} starts at {source.StartTime} (day {source.Time?.DaySeconds}), not {start} (day {daySeconds}).");
        if (map == Edz)
            Check(scene.TimesOfDay.Any(item => item.PlacementListTag == EdzForgeList && item.StartTime == 0),
                "EDZ lost the forge bubble's second start time.");
        Console.WriteLine($"time_of_day_map map={map:X8} list={source.Time!.PlacementListTag:X8} start={source.StartTime:R} result=pass");
        return source;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}

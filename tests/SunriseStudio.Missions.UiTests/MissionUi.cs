using System.Reflection;
using SunriseStudio.Editor;

/** Reflection helpers and the Studio window scaffold shared by the mission UI checks. */
internal static class MissionUi
{
    public const BindingFlags Access = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    // The Lua draft moved into the Graph panel's GraphSourceSync ("source"); "sourceDraft" reads and writes its Draft.
    public static object? Field(object owner, string name) => name == "sourceDraft"
        ? Sync(owner).Draft : owner.GetType().GetField(name, Access)!.GetValue(owner);
    public static void Set(object owner, string name, object? value)
    {
        if (name == "sourceDraft") Sync(owner).Edit((string)value!, double.MaxValue);
        else owner.GetType().GetField(name, Access)!.SetValue(owner, value);
    }
    private static Solforge.Graphs.GraphSourceSync Sync(object owner) =>
        (Solforge.Graphs.GraphSourceSync)owner.GetType().GetField("source", Access)!.GetValue(owner)!;
    public static object? Invoke(object owner, string name, params object?[] arguments) =>
        owner.GetType().GetMethod(name, Access)!.Invoke(owner, arguments);
    public static MissionGraphPage Page(StudioForm form) => (MissionGraphPage)Field(form, "missionGraph")!;

    /**
     * Runs one Studio window in a temporary folder that is deleted afterwards.
     * @param label Names the temporary folder.
     * @param setup Gets the folder and returns the window options; the settings path is set here.
     * @param frame Runs once per drawn frame while the editor is ready and idle.
     * @param finish Runs after the window closes; throws to fail the check.
     * @param report Returns the summary line printed on success.
     * @return 0 on success, 1 on failure.
     */
    public static int Run(string label, Func<string, StudioOptions> setup, Action<StudioForm, MissionGraphPage> frame,
        Action<StudioForm, string> finish, Func<string> report)
    {
        string root = Path.Combine(Path.GetTempPath(), "sunrise-" + label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string settings = Path.Combine(root, "settings.json");
            new StudioSettings { BrowserWidth = 230, InspectorWidth = 250 }.Save(settings);
            using var form = new StudioForm(setup(root) with { SettingsPath = settings });
            form.FrameRendered += () =>
            {
                if (!form.EditorReady || form.Workspace.IsBusy) return;
                frame(form, Page(form));
            };
            Application.Run(form);
            if (form.Failure is { } failure) throw new InvalidDataException(failure);
            finish(form, root);
            Console.WriteLine(report());
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
}

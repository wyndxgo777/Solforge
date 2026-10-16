using System.Text;
using SunriseStudio.Projects;

namespace SunriseStudio.Missions;

/** A mod's mission scripts are plain Lua files in the project, laid out as the mod installs them. */
public static partial class MissionProject
{
    public const string ScriptsFolder = "scripts";

    /** Folder in the project that holds mission scripts. */
    public static string ScriptsRoot(StudioProject project) => Path.Combine(project.DirectoryPath, ScriptsFolder);

    /** Every `stem/stem.lua` in the project's scripts folder, in name order. */
    public static IReadOnlyList<MissionScriptEntry> List(StudioProject project)
    {
        string root = ScriptsRoot(project);
        if (!Directory.Exists(root)) return [];
        RejectLinks(root);
        var entries = new List<MissionScriptEntry>();
        foreach (string directory in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            RejectLinks(directory);
            string name = Path.GetFileName(directory), path = Path.Combine(directory, name + ".lua");
            if (!File.Exists(path)) continue;
            RejectLinks(path);
            string relative = name + "/" + name + ".lua";
            entries.Add(new MissionScriptEntry(name, relative, path, null));
        }
        return entries;
    }

    /** The project file a mod script with this runtime path is saved to. */
    public static string PathFor(StudioProject project, string relativePath) => Target(ScriptsRoot(project), relativePath);

    /** Writes a mod script into the project; it is a plain file, not a package edit. */
    public static string Save(StudioProject project, string relativePath, string source)
    {
        string target = PathFor(project, relativePath);
        Write(target, source);
        return target;
    }

    /** Scripts for a mod install pass the same graph checks as Export and keep their runtime path. */
    public static IReadOnlyList<ModScriptFile> ModScripts(StudioProject project) => List(project).Select(entry =>
    {
        string source = MissionScriptCatalog.ReadSource(entry.FullPath);
        try { MissionGraphDocument.Parse(source).RequireValid(); }
        catch (InvalidDataException error) { throw new InvalidDataException($"Mission script {entry.Name}: {error.Message}", error); }
        return new ModScriptFile(entry.RelativePath, Encoding.UTF8.GetBytes(source));
    }).ToArray();

    /** Export writes a review folder and cannot overwrite the installed scripts or SDK. */
    public static string Export(string relativePath, string source, string directory, string gameRoot)
    {
        MissionGraphDocument.Parse(source).RequireValid();
        string root = Path.GetFullPath(directory), game = Path.GetFullPath(gameRoot);
        if (Within(root, game)) throw new InvalidOperationException("Export to a review folder outside the installation.");
        string target = Target(root, relativePath);
        Write(target, source);
        return target;
    }

    /** Resolves `stem/stem.lua` under root, refusing other shapes, escapes and linked paths. */
    internal static string Target(string root, string relativePath)
    {
        string[] parts = relativePath.Replace('\\', '/').Split('/');
        if (parts.Length != 2 || parts[1] != parts[0] + ".lua" || parts[0].Length == 0
            || parts[0].Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new InvalidDataException("A mission script path has the form stem/stem.lua.");
        string target = Path.GetFullPath(Path.Combine(root, parts[0], parts[1]));
        if (!Within(target, Path.GetFullPath(root))) throw new InvalidDataException("Mission script path leaves its folder.");
        RejectLinks(target);
        return target;
    }

    /** Replaces the file in one step; the text is written exactly, byte order mark and line endings included. */
    internal static void Write(string target, string source, bool overwrite = true)
    {
        _ = MissionGraphDocument.Parse(source);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, source, new UTF8Encoding(false)); File.Move(temporary, target, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /** True when path is root or inside it. */
    internal static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /** Throws when any part of the path is a symbolic link or junction. */
    internal static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Mission paths cannot use linked folders or files.");
    }
}

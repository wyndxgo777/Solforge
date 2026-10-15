using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// Solforge Phase 04: audits the checkout itself. It reads tracked files through git and builds nothing.
// --repo <path> picks the checkout; by default the folder holding "Sunrise Studio.sln" above this program.
string root = args.Length >= 2 && args[0] == "--repo" ? Path.GetFullPath(args[1]) : FindRoot();
if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
{
    Console.WriteLine($"Architecture checks skipped: {root} is not a git checkout.");
    return 0;
}
var failures = new List<string>();
var passed = new List<string>();
string[] tracked = Git(root, "ls-files", "-z");

// (a) No extracted game data in the tree. git applies .gitignore itself; the fixed list and the content
// sniff still catch a game file someone force-added or a .gitignore line someone removed.
foreach (string path in Git(root, "ls-files", "-z", "-c", "-i", "--exclude-standard"))
    failures.Add($"Tracked file matches .gitignore: {path}");
var allowed = ReadNegations(Path.Combine(root, ".gitignore"));
string[] gameExtensions = [".pkg", ".bin", ".bik", ".wem", ".bnk", ".pck", ".usm"];
Regex[] gameNames =
[
    new(@"^oo2core.*\.dll$", RegexOptions.IgnoreCase), new(@"^package_keys", RegexOptions.IgnoreCase),
    new(@"^destiny.*\.exe$", RegexOptions.IgnoreCase)
];
string[] gameDirectories = ["dumps/", "extracted/", "unpacked/", "rip/", "exports/"];
int sniffed = 0;
foreach (string path in tracked)
{
    if (allowed.Contains(path)) continue;
    string name = Path.GetFileName(path);
    if (gameExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        || gameNames.Any(x => x.IsMatch(name))
        || gameDirectories.Any(x => path.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
        failures.Add($"Tracked file has a game-data name: {path}");
    string full = Path.Combine(root, path);
    if (!File.Exists(full)) continue;
    sniffed++;
    if (SniffGameData(full) is { } kind) failures.Add($"Tracked file holds {kind} data: {path}");
}
passed.Add($"{tracked.Length} tracked files, {sniffed} sniffed: no extracted game data");

// (b) The build reaches the network only through NuGet restore: no MSBuild Exec/download tasks or URLs,
// no package feeds besides nuget.org, and no downloads in the build scripts.
var msbuildDownload = new Regex(@"<(Exec|DownloadFile|UsingTask)\b|https?://|\b(RestoreSources|RestoreAdditionalProjectSources)\b",
    RegexOptions.IgnoreCase);
var scriptDownload = new Regex(@"Invoke-WebRequest|Invoke-RestMethod|\biwr\b|\bcurl\b|\bwget\b|Start-BitsTransfer|WebClient"
    + @"|DownloadFile|DownloadString|HttpClient|dotnet\s+(tool|workload)\s+(install|restore|update)", RegexOptions.IgnoreCase);
int audited = 0;
foreach (string path in tracked)
{
    string full = Path.Combine(root, path);
    if (!File.Exists(full)) continue;
    string extension = Path.GetExtension(path).ToLowerInvariant();
    string name = Path.GetFileName(path).ToLowerInvariant();
    if (extension is ".csproj" or ".props" or ".targets")
    {
        audited++;
        string text = Regex.Replace(File.ReadAllText(full), "<!--.*?-->", "", RegexOptions.Singleline);
        foreach (Match match in msbuildDownload.Matches(text))
            failures.Add($"{path} can reach the network outside NuGet restore: {match.Value}");
        foreach (Match match in Regex.Matches(text, @"<PackageReference\b[^>]*\bVersion\s*=\s*""[^""]*\*[^""]*""", RegexOptions.IgnoreCase))
            failures.Add($"{path} has a floating package version: {match.Value}");
    }
    else if (name == "nuget.config")
    {
        audited++;
        foreach (var source in XDocument.Load(full).Descendants("packageSources").Elements("add"))
        {
            string value = (string?)source.Attribute("value") ?? "";
            if (value.Contains("://", StringComparison.Ordinal)
                && !value.Equals("https://api.nuget.org/v3/index.json", StringComparison.OrdinalIgnoreCase))
                failures.Add($"{path} adds a package feed other than nuget.org: {value}");
        }
    }
    else if (name is "build.ps1" or "publish.ps1" or "global.json")
    {
        audited++;
        foreach (Match match in scriptDownload.Matches(File.ReadAllText(full)))
            failures.Add($"{path} downloads during the build: {match.Value}");
    }
}
passed.Add($"{audited} build files audited: no network access beyond NuGet restore");

// (c) ProjectReferences form a DAG.
var graph = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
foreach (string path in tracked.Where(x => x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
    && (x.StartsWith("src/", StringComparison.Ordinal) || x.StartsWith("tests/", StringComparison.Ordinal))))
{
    string full = Path.GetFullPath(Path.Combine(root, path));
    if (!File.Exists(full)) continue;
    var references = new List<string>();
    foreach (var reference in XDocument.Load(full).Descendants().Where(x => x.Name.LocalName == "ProjectReference"))
    {
        string include = ((string?)reference.Attribute("Include") ?? "").Trim();
        if (include.Length == 0) continue;
        if (include.Contains("$(", StringComparison.Ordinal))
        {
            Console.WriteLine($"Note: {path} references {include}; properties are not resolved here.");
            continue;
        }
        string target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, include.Replace('\\', '/')));
        if (!File.Exists(target)) failures.Add($"{path} references a missing project: {include}");
        else references.Add(target);
    }
    graph[full] = references;
}
var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var stack = new List<string>();
foreach (string project in graph.Keys.Order(StringComparer.OrdinalIgnoreCase)) Visit(project);
int edges = graph.Values.Sum(x => x.Count);
passed.Add($"{graph.Count} projects, {edges} ProjectReferences: no cycles");

foreach (string failure in failures) Console.Error.WriteLine("FAIL " + failure);
if (failures.Count != 0)
{
    Console.Error.WriteLine($"Architecture checks failed: {failures.Count} problem(s).");
    return 1;
}
foreach (string check in passed) Console.WriteLine("Passed: " + check);
return 0;

void Visit(string project)
{
    state.TryGetValue(project, out int mark);
    if (mark == 2) return;
    if (mark == 1)
    {
        int start = stack.FindIndex(x => x.Equals(project, StringComparison.OrdinalIgnoreCase));
        var cycle = stack.Skip(start).Append(project).Select(x => Path.GetRelativePath(root, x).Replace('\\', '/'));
        failures.Add("Circular ProjectReference: " + string.Join(" -> ", cycle));
        return;
    }
    state[project] = 1;
    stack.Add(project);
    if (graph.TryGetValue(project, out var references))
        foreach (string reference in references) Visit(reference);
    stack.RemoveAt(stack.Count - 1);
    state[project] = 2;
}

static string? SniffGameData(string path)
{
    Span<byte> head = stackalloc byte[4];
    long length;
    using (var file = File.OpenRead(path))
    {
        length = file.Length;
        if (length < 4 || file.ReadAtLeast(head, 4, false) < 4) return null;
    }
    // A package header starts with format 38, version 2 and is 0x180 bytes long.
    if (head[0] == 38 && head[1] == 0 && head[2] == 2 && head[3] == 0 && length >= 0x180) return "package";
    string tag = Encoding.ASCII.GetString(head);
    if (tag == "BKHD") return "Wwise sound bank";
    if (tag == "AKPK") return "Wwise file package";
    if (tag[..3] is "BIK" or "KB2") return "Bink video";
    return null;
}

static HashSet<string> ReadNegations(string gitignore)
{
    var result = new HashSet<string>(StringComparer.Ordinal);
    if (!File.Exists(gitignore)) return result;
    foreach (string line in File.ReadAllLines(gitignore))
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith('!')) result.Add(trimmed[1..].TrimStart('/'));
    }
    return result;
}

static string[] Git(string root, params string[] arguments)
{
    var start = new ProcessStartInfo("git")
    {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8
    };
    start.ArgumentList.Add("-C");
    start.ArgumentList.Add(root);
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
    var error = process.StandardError.ReadToEndAsync();
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error.Result.Trim()}");
    return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
}

static string FindRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "Sunrise Studio.sln"))) return directory.FullName;
    throw new DirectoryNotFoundException("Run inside the Solforge checkout or pass --repo <path>.");
}

using SunriseStudio.ProjectSystem.Tests;

// Solforge Phases 15-19: project file schema, watchers, overlay diff/revert, interchange packaging,
// dependency crawl, package-id allocation and the project integrity audit. No installation needed.
string root = Path.Combine(Path.GetTempPath(), "solforge-project-system-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = new List<string>();
try
{
    SchemaChecks.Run(checks);
    WatcherChecks.Run(Path.Combine(root, "watcher"), checks);
    foreach (string check in checks) Console.WriteLine("ok: " + check);
    Console.WriteLine($"Project system checks passed ({checks.Count}).");
}
catch (Exception error)
{
    Console.Error.WriteLine("Project system check failed: " + error.Message);
    Environment.ExitCode = 1;
}
finally
{
    try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

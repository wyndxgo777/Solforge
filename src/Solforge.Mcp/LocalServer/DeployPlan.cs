using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Solforge.Mcp.LocalServer;

/** One file to copy: a Windows source and a path relative to the run directory's Sunrise folder. */
public sealed record DeployFile(string Source, string Target);

/**
 * The files a deploy would copy to the local server, the build it picked, and notes about anything missing.
 */
public sealed record DeployPlan(string ProjectRoot, string? BuildDirectory, IReadOnlyList<DeployFile> Files, IReadOnlyList<string> Notes)
{
    /** Plan summary with files grouped by top folder and at most sample names per group. */
    public JsonObject ToJson(int sample = 20)
    {
        var groups = new JsonObject();
        foreach (var group in Files.GroupBy(file => file.Target.Split('/')[0], StringComparer.Ordinal))
            groups[group.Key] = group.Count();
        var files = new JsonArray();
        foreach (var file in Files.Take(sample)) files.Add(file.Target);
        var notes = new JsonArray();
        foreach (string note in Notes) notes.Add(note);
        return new JsonObject
        {
            ["project"] = ProjectRoot, ["build"] = BuildDirectory, ["count"] = Files.Count, ["groups"] = groups,
            ["files"] = files, ["notes"] = notes,
        };
    }
}

/**
 * What "deploy the current SolForge project build" copies into /root/sol-local/run/Sunrise:
 *   project scripts/**   -> scripts/**          (server Lua mission scripts)
 *   project sdk/**       -> sdk/**              (activity SDK Lua, catalog, scenarios)
 *   activity_sdk.pack    -> activity_sdk.pack   (from the newest build folder, the project root or project server/)
 *   build_data.bin       -> cache/build_data.bin (investment / item data, same search order)
 * Hidden files and *.bak are skipped. Nothing outside Sunrise is ever written.
 */
public static class DeployPlanner
{
    private static readonly Regex SafeRelative = new(@"^[A-Za-z0-9 _.+()\[\]@#,=-]+(/[A-Za-z0-9 _.+()\[\]@#,=-]+)*$", RegexOptions.CultureInvariant);

    /**
     * Collects the project's scripts/ and sdk/ files plus a server build into a plan, skipping hidden and
     * .bak files.
     */
    public static DeployPlan Plan(string projectRoot, string? buildDirectory = null)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(projectRoot)) throw new DirectoryNotFoundException($"Project folder {projectRoot} does not exist.");
        buildDirectory ??= NewestBuild(projectRoot);
        var notes = new List<string>();
        var files = new List<DeployFile>();
        foreach (string folder in new[] { "scripts", "sdk" })
        {
            string root = Path.Combine(projectRoot, folder);
            if (!Directory.Exists(root)) { notes.Add($"no {folder}/ folder in the project"); continue; }
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Split('/').Any(part => part.StartsWith('.')) || relative.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(new DeployFile(file, folder + "/" + relative));
            }
        }
        var searched = new[] { buildDirectory, projectRoot, Path.Combine(projectRoot, "server") }.OfType<string>().ToArray();
        foreach (var (name, target) in new[] { ("activity_sdk.pack", "activity_sdk.pack"), ("build_data.bin", "cache/build_data.bin") })
        {
            string? found = searched.Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
            if (found is null) notes.Add($"no {name} in the build, the project root or server/ (left as is on the server)");
            else files.Add(new DeployFile(found, target));
        }
        foreach (var file in files)
            if (!SafeRelative.IsMatch(file.Target) || file.Target.Split('/').Contains(".."))
                throw new InvalidDataException($"'{file.Target}' has characters the deploy script does not copy; rename it.");
        if (files.Count == 0) notes.Add("nothing to deploy");
        return new DeployPlan(projectRoot, buildDirectory, files, notes);
    }

    /** The newest folder under <project>/build, the default ProjectBuilder output. */
    public static string? NewestBuild(string projectRoot)
    {
        string builds = Path.Combine(projectRoot, "build");
        return Directory.Exists(builds)
            ? Directory.EnumerateDirectories(builds).OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
    }

    /** Timestamp id for a new backup. */
    public static string NewBackupId(DateTime utcNow) => utcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private static readonly Regex BackupIdPattern = new(@"^\d{8}-\d{6}(-[0-9a-f]{1,8})?$", RegexOptions.CultureInvariant);
    /** True when id has the shape NewBackupId produces; guards ids that reach shell scripts. */
    public static bool IsBackupId(string? id) => id is not null && BackupIdPattern.IsMatch(id);

    /**
     * The deploy script: write the plan into the backup folder, record which targets existed (tarred) and which are new,
     * then copy. Rollback restores the tar and removes the new files. The copy never starts unless the backup succeeded.
     */
    public static string DeployScript(DeployPlan plan, LocalServerOptions options, string backupId)
    {
        if (!IsBackupId(backupId)) throw new ArgumentException("Bad backup id.", nameof(backupId));
        var tsv = new StringBuilder();
        foreach (var file in plan.Files)
        {
            string source = Shell.WslPath(file.Source) ?? throw new InvalidDataException($"{file.Source} is not on a Windows drive WSL can read.");
            if (source.Contains('\t') || source.Contains('\n') || source.Contains("SOLFORGE_PLAN", StringComparison.Ordinal))
                throw new InvalidDataException($"{file.Source} has a tab or newline in its path.");
            tsv.Append(source).Append('\t').Append(file.Target).Append('\n');
        }
        string backup = options.BackupDirectory + "/" + backupId;
        return $$"""
            set -eu
            cd {{Shell.Quote(options.SunriseDirectory)}}
            B={{Shell.Quote(backup)}}
            mkdir -p "$B"
            cat > "$B/plan.tsv" <<'SOLFORGE_PLAN'
            {{tsv}}SOLFORGE_PLAN
            printf '%s\n' {{Shell.Quote(plan.ProjectRoot)}} > "$B/source.txt"
            : > "$B/existed.txt"; : > "$B/created.txt"
            while IFS=$'\t' read -r s d; do
              [ -f "$s" ] || { echo "MISSING $s"; exit 3; }
              if [ -e "$d" ]; then printf '%s\n' "$d" >> "$B/existed.txt"; else printf '%s\n' "$d" >> "$B/created.txt"; fi
            done < "$B/plan.tsv"
            if [ -s "$B/existed.txt" ]; then tar -czf "$B/backup.tar.gz" -T "$B/existed.txt"; fi
            n=0
            while IFS=$'\t' read -r s d; do mkdir -p "$(dirname -- "$d")"; cp -f -- "$s" "$d"; n=$((n+1)); done < "$B/plan.tsv"
            echo "DEPLOYED=$n EXISTED=$(wc -l < "$B/existed.txt") CREATED=$(wc -l < "$B/created.txt") BACKUP=$B"
            """.ReplaceLineEndings("\n") + "\n";
    }

    /**
     * Compile-checks each deployed Lua file with the package.path the mission tests use (scripts/?.lua, sdk/?.lua).
     * loadfile only compiles, so nothing runs and no server is touched. require("name") calls that resolve nowhere come
     * back as warnings. sol-server has no check mode, so this uses lua5.4 (or luac -p) in WSL.
     */
    public static string CheckScriptsScript(IReadOnlyList<string> files, LocalServerOptions options)
    {
        var list = new StringBuilder();
        foreach (string file in files)
        {
            if (!SafeRelative.IsMatch(file) || file.Split('/').Contains("..")) throw new InvalidDataException($"'{file}' is not a safe script path.");
            list.Append(file).Append('\n');
        }
        return $$"""
            cd {{Shell.Quote(options.SunriseDirectory)}}
            LUA=""; for c in lua5.4 lua54 lua; do if command -v "$c" >/dev/null 2>&1; then LUA="$c"; break; fi; done
            LUAC=""; for c in luac5.4 luac54 luac; do if command -v "$c" >/dev/null 2>&1; then LUAC="$c"; break; fi; done
            if [ -n "$LUA" ]; then
              echo "LUATOOL=$LUA"
              P=$(mktemp)
              cat > "$P" <<'SOLFORGE_LUA'
            package.path = "scripts/?.lua;sdk/?.lua;" .. package.path
            local ok, bad = 0, 0
            local function flat(s) return (tostring(s):gsub("[\r\n\t]+", " ")) end
            for f in io.lines() do
              local fn, err = loadfile(f)
              if fn then
                ok = ok + 1
                local h = io.open(f, "rb")
                local src = h and h:read("a") or ""
                if h then h:close() end
                for m in src:gmatch("require%s*%(?%s*[\"']([%w_%.%-]+)[\"']") do
                  if not package.loaded[m] and not package.searchpath(m, package.path) then print("LUAWARN\t" .. f .. "\trequire '" .. m .. "' not found in scripts/ or sdk/") end
                end
              else bad = bad + 1; print("LUAERR\t" .. f .. "\t" .. flat(err)) end
            end
            print("LUASUMMARY\t" .. ok .. "\t" .. bad)
            SOLFORGE_LUA
              "$LUA" "$P" <<'SOLFORGE_FILES'
            {{list}}SOLFORGE_FILES
              rm -f "$P"
            elif [ -n "$LUAC" ]; then
              echo "LUATOOL=$LUAC"
              ok=0; bad=0
              while IFS= read -r f; do
                if msg=$("$LUAC" -p -- "$f" 2>&1); then ok=$((ok+1)); else bad=$((bad+1)); printf 'LUAERR\t%s\t%s\n' "$f" "$(printf '%s' "$msg" | tr '\n\t' '  ')"; fi
              done <<'SOLFORGE_FILES'
            {{list}}SOLFORGE_FILES
              printf 'LUASUMMARY\t%s\t%s\n' "$ok" "$bad"
            else
              echo "LUAUNAVAILABLE"
            fi
            """.ReplaceLineEndings("\n") + "\n";
    }

    /** Reads CheckScriptsScript output; fallback is shown when the script printed nothing usable. */
    public static ScriptCheck ParseScriptCheck(string output, string fallback)
    {
        string tool = "";
        int loaded = 0;
        bool summary = false;
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            var cells = line.Split('\t');
            if (line.StartsWith("LUATOOL=", StringComparison.Ordinal)) tool = line[8..];
            else if (line == "LUAUNAVAILABLE") return ScriptCheck.Unavailable("no lua5.4 or luac in WSL");
            else if (cells[0] == "LUAERR" && cells.Length >= 3) errors.Add($"{cells[1]}: {cells[2]}");
            else if (cells[0] == "LUAWARN" && cells.Length >= 3) warnings.Add($"{cells[1]}: {cells[2]}");
            else if (cells[0] == "LUASUMMARY" && cells.Length >= 3 && int.TryParse(cells[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            { loaded = n; summary = true; }
        }
        return summary ? new ScriptCheck(true, tool, loaded, errors, warnings)
            : ScriptCheck.Unavailable("script check gave no result: " + LocalServerParse.Clip(fallback, 200));
    }

    /** Restores the tar over Sunrise and deletes files the deploy created (relative paths only, never with ..). */
    public static string RollbackScript(LocalServerOptions options, string backupId)
    {
        if (!IsBackupId(backupId)) throw new ArgumentException("Bad backup id.", nameof(backupId));
        string backup = options.BackupDirectory + "/" + backupId;
        return $$"""
            set -eu
            cd {{Shell.Quote(options.SunriseDirectory)}}
            B={{Shell.Quote(backup)}}
            [ -f "$B/plan.tsv" ] || { echo "NO_BACKUP $B"; exit 4; }
            if [ -f "$B/backup.tar.gz" ]; then tar -xzf "$B/backup.tar.gz"; fi
            removed=0
            if [ -f "$B/created.txt" ]; then
              while IFS= read -r f; do
                case "$f" in ''|/*|*..*) continue;; esac
                if [ -f "$f" ]; then rm -f -- "$f"; removed=$((removed+1)); fi
              done < "$B/created.txt"
            fi
            date -u +%FT%TZ > "$B/rolled-back"
            echo "ROLLED_BACK=$B REMOVED=$removed"
            """.ReplaceLineEndings("\n") + "\n";
    }

    /** id, file count, rolled-back flag and project per backup, newest first. */
    public static string ListBackupsScript(LocalServerOptions options) => $$"""
        B={{Shell.Quote(options.BackupDirectory)}}
        [ -d "$B" ] || exit 0
        for d in $(ls -1r "$B"); do
          [ -f "$B/$d/plan.tsv" ] || continue
          printf '%s\t%s\t%s\t%s\n' "$d" "$(wc -l < "$B/$d/plan.tsv")" "$( [ -f "$B/$d/rolled-back" ] && echo rolled-back || echo active)" "$(cat "$B/$d/source.txt" 2>/dev/null)"
        done
        """.ReplaceLineEndings("\n") + "\n";
}

/** One backup: id, file count, whether it has been rolled back and the project it came from. */
public sealed record BackupEntry(string Id, int Files, bool RolledBack, string Project);

/** Result of loading the deployed Lua scripts. Available is false when no interpreter ran, so nothing was checked. */
public sealed record ScriptCheck(bool Available, string Tool, int Loaded, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public string Why { get; init; } = "";
    public bool HasErrors => Errors.Count > 0;

    public static ScriptCheck Unavailable(string why) => new(false, "", 0, [], []) { Why = why };

    /** One line for the deploy message. */
    public string Summary => !Available ? $"scripts NOT CHECKED ({Why})"
        : Errors.Count == 0 ? $"scripts OK: {Loaded} loaded, 0 errors" + (Warnings.Count > 0 ? $", {Warnings.Count} warning(s)" : "")
        : $"scripts FAILED: {Loaded} loaded, {Errors.Count} error(s), first {Errors[0]}";

    /** JSON form. */
    public JsonObject ToJson() => new()
    {
        ["checked"] = Available, ["tool"] = Tool, ["loaded"] = Loaded, ["error_count"] = Errors.Count,
        ["errors"] = new JsonArray(Errors.Select(e => (JsonNode)e).ToArray()),
        ["warnings"] = new JsonArray(Warnings.Select(e => (JsonNode)e).ToArray()),
        ["why_unchecked"] = Available ? null : Why,
    };
}

using SunriseStudio.Missions;

/** The Lua syntax layer: exact spans, Lua 5.4 only, and every installed script read without loss. */
internal static class LuaSyntaxChecks
{
    /** Runs the checks that use generated fixtures instead of installed game content. */
    public static void Synthetic()
    {
        const string slot = "context:slot(mission.Slot.X):set_directive{directive = mission.Directive.Y, navpoint = context:slot(mission.Slot.Z)}";
        string source = "\uFEFF-- objective\r\nlocal function run(context, state)\r\n    " + slot + "\r\nend\r\n";
        var script = LuaScript.Parse(source);
        Check(script.Reconstruct() == source, "A byte order mark or CRLF did not survive the read.");
        var run = (LuaFunctionStatement)script.Chunk.Statements.Single();
        var call = (LuaCall)((LuaCallStatement)run.Function.Body.Statements.Single()).Expression;
        Check(call is
        {
            Method: "set_directive",
            Target: LuaCall { Method: "slot", Target: LuaName { Name: "context" }, Arguments.Values: [LuaMember { Name: "X" }] },
            Arguments: { Form: LuaArgumentsForm.Table, Table.Fields: [{ Name: "directive" }, { Name: "navpoint", Expression: LuaCall { Method: "slot" } }] }
        }, "A chained slot call did not read as a call on a call.");
        var directive = call.Arguments.Table!.Field("directive")!;
        Check(call.Span.Read(source) == slot && call.MethodSpan!.Value.Read(source) == "set_directive"
            && call.Callee.Read(source) == "context:slot(mission.Slot.X):set_directive"
            && directive.Value.Read(source) == "mission.Directive.Y" && directive.Span.Read(source) == "directive = mission.Directive.Y,"
            && ((LuaCall)call.Target).Arguments.Inner.Read(source) == "mission.Slot.X", "A call span is not exact.");
        string edited = source[..directive.Value.Start] + "mission.Directive.W" + source[directive.Value.End..];
        Check(edited == source.Replace("Directive.Y", "Directive.W", StringComparison.Ordinal), "A value edit touched other source.");

        var modern = LuaScript.Parse("local a <const> = 7 // 2 | 1 << 3 ~ 2 & ~0 >> 1\ngoto done\n::done::\nlocal b <close> = nil\n");
        Check(modern.Chunk.Statements.Count == 4 && modern.Chunk.Statements[0] is LuaLocalStatement { Names: [{ Attribute: "const" }] },
            "Lua 5.4 syntax was refused.");
        foreach (string invalid in new[] { "x = a != b", "x = !a", "x = a && b", "f(1,)", "return 1 x = 2", "x += 1", "continue",
            "x = 0b101", "local t = {1,,2}", "return " + new string('(', 300) + "1" + new string(')', 300) })
            Reject(() => LuaScript.Parse(invalid), invalid);
        Check(LuaScript.Parse("x = 1\nreturn -- done").Chunk.Statements[^1] is LuaReturnStatement { Values.Count: 0, Code.Length: 6 }
            && LuaScript.ParseExpressions(" -- none", true).Count == 0, "A bare return at the end of a chunk was refused.");
        var tolerant = LuaScript.Read("local x = {a = 1,\nf(");
        Check(tolerant.Errors.Count > 0 && tolerant.Reconstruct() == tolerant.Text, "An invalid chunk was not kept with its errors.");
        string colors = "local x = 0x1p+4 -- if 999\nlocal s = [=[end -- literal]=]\n--[=[while 3]=]\nreturn x";
        var syntax = LuaScript.Syntax(colors);
        Check(syntax.Count(token => token.Kind == LuaTokenKind.Keyword) == 3
            && syntax.Count(token => token.Kind == LuaTokenKind.Number) == 1
            && syntax.Count(token => token.Kind == LuaTokenKind.Comment) == 2
            && syntax.Count(token => token.Kind == LuaTokenKind.String) == 1,
            "Syntax colors parse keywords inside strings or comments.");
        Check(LuaScript.Syntax("--[=[unfinished").Single().Kind == LuaTokenKind.Comment
            && LuaScript.Syntax("'unfinished\\").Single().Kind == LuaTokenKind.String,
            "Incomplete editor drafts lost syntax spans.");
        Console.WriteLine("lua_syntax chained_calls=typed spans=exact bom=kept lua54=accepted c_operators=refused syntax_colors=pass");
    }

    /** Every installed script, lib included, reads with no error and every call span slices to a call. */
    public static void Installed(string gameRoot)
    {
        int files = 0, statements = 0, calls = 0, methods = 0, tables = 0, fields = 0, tokens = 0;
        foreach (var (name, source) in InstalledScripts(new MissionScriptCatalog(gameRoot).ScriptsRoot))
        {
            var script = LuaScript.Read(source);
            Check(script.Errors.Count == 0, $"{name}: {script.Errors.FirstOrDefault()}");
            Check(script.Reconstruct() == source, name + ": the syntax tree does not round-trip.");
            int previous = 0;
            foreach (var token in script.Tokens)
            {
                Check(token.Span.Start >= previous && token.Span.Read(source) == token.Text, $"{name}: token {token.Text} is misplaced.");
                previous = token.Span.End;
            }
            foreach (var node in script.Descendants())
            {
                string text = node.Span.Read(source);
                switch (node)
                {
                    case LuaStatement statement:
                        statements++;
                        Check(statement.Code.Start >= statement.Span.Start && statement.Code.End == statement.Span.End
                            && LuaScript.Parse(statement.Code.Read(source)).Chunk.Statements.Single().GetType() == statement.GetType(),
                            $"{name}: statement at {statement.Code.Start} does not slice to itself.");
                        break;
                    case LuaCall call:
                        calls++;
                        if (call.Method is not null) methods++;
                        var copy = LuaScript.ParseExpression(text) as LuaCall;
                        Check(copy is not null && copy.Method == call.Method && copy.Arguments.Form == call.Arguments.Form
                            && copy.Arguments.Values.Count == call.Arguments.Values.Count
                            && copy.Callee.Read(text) == call.Callee.Read(source)
                            && copy.Arguments.Span.Read(text) == call.Arguments.Span.Read(source)
                            && (call.MethodSpan is not { } method || method.Read(source) == call.Method),
                            $"{name}: call at {call.Span.Start} does not slice to the same call.");
                        break;
                    case LuaTable table:
                        tables++;
                        foreach (var field in table.Fields)
                        {
                            fields++;
                            Check(field.NameSpan is not { } key || key.Read(source) == field.Name, $"{name}: field name at {field.Span.Start} is misplaced.");
                            Check(field.Span.Start >= table.Span.Start && field.Span.End <= table.Span.End
                                && field.Value.End <= field.Span.End, $"{name}: field at {field.Span.Start} leaves its table.");
                        }
                        break;
                    case LuaMember member:
                        Check(member.NameSpan.Read(source) == member.Name, $"{name}: member name at {member.NameSpan.Start} is misplaced.");
                        break;
                }
            }
            files++; tokens += script.Tokens.Count;
        }
        Check(files > 0, "No installed scripts were read.");
        Console.WriteLine($"lua_syntax_installed files={files} errors=0 round_trip=exact tokens={tokens} statements={statements} "
            + $"calls={calls} method_calls={methods} tables={tables} fields={fields}");
    }

    /** Installed .lua files under root, ordinal order, .git skipped; names use '/'. */
    internal static IEnumerable<(string Name, string Source)> InstalledScripts(string root) =>
        Directory.EnumerateFiles(root, "*.lua", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(file => (Path.GetRelativePath(root, file).Replace('\\', '/'), MissionScriptCatalog.ReadSource(file)));

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Reject(Action action, string source)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidDataException("Invalid Lua was accepted: " + source);
    }
}

using System.Collections;
using System.Diagnostics;
using SunriseStudio.Editor;
using SunriseStudio.Missions;
using SunriseStudio.Packages;
using static MissionUi;

/** UI check of pending Lua drafts (flow graphs, campaigns): they stay unapplied until accepted. */
internal static class PendingLuaChecks
{
    private const string Predicate = "function() return true end";
    private const string Source = "local flow=require('lib.flow')\nlocal graph=flow.new{key='draft',facts={},steps={"
        + "{id='a',when={all={" + Predicate + "," + Predicate + "}},run=function(context,state) end}}}\n"
        + "local campaign=require('lib.campaign')\nlocal story=campaign.new{key='story',steps={"
        + "{id='a',lines={make_line(),make_line()}}},legs={},encounters={}}\nreturn {}\n";

    /** Runs the pending-Lua behavior checks; returns the number of cases run. */
    internal static int BehaviorOnly()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "sunrise-pending-lua-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temporary, "install", "packages"));
        try
        {
            PackageWriter.WriteNew(Path.Combine(temporary, "install", "packages", "w64_draft_0100_0.pkg"),
                0x100, [new AuthoredPackageEntry(0x80800001, 8 << 9, new byte[16])]);
            using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
            workspace.OpenInstallation(Path.Combine(temporary, "install")); Wait(workspace);
            workspace.CreateProject(Path.Combine(temporary, "project"), "Pending Lua"); Wait(workspace);
            string file = MissionProject.Save(workspace.Project!, "draft/draft.lua", Source);
            string other = MissionProject.Save(workspace.Project!, "other/other.lua", "-- other\n" + Source);
            var page = new MissionGraphPage(); page.OpenScript(file, workspace);
            var node = page.Document!.Groups.Single(group => group.Kind == MissionGraphKind.Flow && group.Collection == "steps").Nodes.Single();
            string id = node.Key + "\nwhen/0";
            Put(page, id, Predicate, "function() return false end", node.Key, "when/0");
            Check(page.IsDirty, "Unapplied node Lua did not mark the page dirty.");
            page.SaveCurrent(workspace);
            Check(((string)Field(page, "status")!).Contains("Apply or revert", StringComparison.Ordinal)
                && File.ReadAllText(file) == Source, "Save did not refuse pending node Lua.");
            page.OpenScript(other, workspace);
            Check(page.ScriptPath == file && Field(page, "pendingSwitch") is not null, "Switching scripts dropped pending node Lua.");
            Check(Draft(page, id) == "function() return false end", "Switching scripts dropped the draft text.");
            Console.WriteLine("mission_pending_lua dirty=True save_refused=True switch_retained=True text_retained=True");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 2; }
        finally { Directory.Delete(temporary, true); }
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var timer = Stopwatch.StartNew();
        while (workspace.IsBusy && timer.Elapsed < TimeSpan.FromSeconds(30)) { workspace.Poll(); Thread.Sleep(1); }
        Check(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error);
    }

    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(StudioWorkspace workspace, string temporary)
    {
        var project = workspace.Project!;
        string file = MissionProject.Save(project, "pending_lua/pending_lua.lua", Source);
        string other = MissionProject.Save(project, "pending_other/pending_other.lua", "-- other document\n" + Source);
        var page = new MissionGraphPage(); page.OpenScript(file, workspace);
        var condition = page.Document!.Groups.Single(group => group.Kind == MissionGraphKind.Flow && group.Collection == "steps").Nodes.Single();
        var dialogue = page.Document.Groups.Single(group => group.Kind == MissionGraphKind.Campaign && group.Collection == "steps").Nodes.Single();
        string conditionId = condition.Key + "\nwhen/0", dialogueId = dialogue.Key + "\nlines/0";
        Put(page, conditionId, Predicate, "function() return false end", condition.Key, "when/0");
        Put(page, dialogueId, "make_line()", "edited_line()", dialogue.Key, "lines/0");
        Check(page.IsDirty && !page.CanUndo, "Unapplied node Lua was not retained as a draft.");
        page.SaveCurrent(workspace);
        Check(File.ReadAllText(file) == Source && page.Document.Source == Source
            && Draft(page, conditionId) == "function() return false end" && Draft(page, dialogueId) == "edited_line()",
            "Save changed source or dropped pending field Lua.");
        Check(!Change(page, document => document.RemoveListValue(dialogue.Key, "lines", 0))
            && page.Document.Source == Source && Draft(page, dialogueId) == "edited_line()",
            "Removing an identical dialogue sibling retargeted its positional draft.");
        Check(!Change(page, document => new MissionVisualAuthoring(document, []).SetCondition(condition.Key, "when",
            new MissionConditionGroup(true, [new MissionCustomCondition(Predicate)])))
            && page.Document.Source == Source && Draft(page, conditionId) == "function() return false end",
            "Removing an identical condition sibling retargeted its positional draft.");
        page.OpenScript(other, workspace);
        Check(page.ScriptPath == file && page.Document.Source == Source && page.IsDirty
            && Field(page, "pendingSwitch") is not null, "Context switching discarded pending field text.");
        Set(page, "pendingSwitch", null); Set(page, "showSwitch", false);
        Check(ChangeLua(page, document => document.SetListValue(dialogue.Key, "lines", 0, "edited_line()"), dialogueId),
            "Applying one non-overlapping draft failed.");
        Check(Draft(page, conditionId) == "function() return false end",
            "Applying dialogue dropped the pending condition draft.");
        var replacement = new MissionConditionGroup(true,
            [new MissionCustomCondition("function() return false end"), new MissionCustomCondition(Predicate)]);
        Check(ChangeLua(page, document => new MissionVisualAuthoring(document, []).SetCondition(condition.Key, "when", replacement), conditionId),
            "Applying a pending condition failed.");
        string applied = page.Document.Source;
        page.Undo(); page.Undo();
        Check(page.Document.Source == Source && !page.CanUndo, "Field Apply created extra undo steps.");
        page.Redo(); page.Redo(); Check(page.Document.Source == applied, "Field Apply redo changed the source.");
        page.SaveCurrent(workspace); Check(File.ReadAllText(file) == applied && !page.IsDirty, "Applied Lua did not save normally.");

        Put(page, dialogueId, "edited_line()", "function(", dialogue.Key, "lines/0");
        Check(!ChangeLua(page, document => document.SetListValue(dialogue.Key, "lines", 0, "function("), dialogueId)
            && Draft(page, dialogueId) == "function(" && page.Document.Source == applied,
            "A failed Apply discarded its Lua draft.");
        page.DiscardDraft(); page.OpenScript(other, workspace);
        Check(!page.IsDirty && !Drafts(page).Contains(dialogueId), "Discard or reopen reused another document's field draft.");
        Put(page, dialogueId, "make_line()", "temporary_line()", dialogue.Key, "lines/0");
        Put(page, dialogueId, "make_line()", "make_line()", dialogue.Key, "lines/0");
        Check(Change(page, document => document.RemoveListValue(dialogue.Key, "lines", 0)), "Revert did not release structural editing.");
        var current = Read(page, dialogueId, "make_line()", dialogue.Key, "lines/0");
        Check((string)current.GetType().GetProperty("Draft")!.GetValue(current)! == "make_line()",
            "A removed identical row left its old draft on the survivor.");

        var creator = new MissionGraphPage();
        Set(creator, "catalog", new MissionScriptCatalog(Path.Combine(temporary, "other-installation")));
        var currentCatalog = new MissionScriptCatalog(workspace.Catalog!.GameRoot);
        var activity = currentCatalog.Activities.Single(value => value.Key == "MISSION_LIGHTS");
        var entry = currentCatalog.EntryForActivity(activity);
        var initial = currentCatalog.ReadReferences(entry).First(value => MissionVisualAuthoring.Accepts(value, MissionReferenceKind.State));
        creator.CreateFlow(workspace, activity, initial, "current_catalog", true);
        Check(((MissionScriptCatalog)Field(creator, "catalog")!).GameRoot == workspace.Catalog.GameRoot
            && creator.IsDirty && creator.Document is not null, "CreateFlow used the retained installation catalog.");
        creator.DiscardDraft();
        Console.WriteLine("mission_field_drafts save=refused switch=retained ordinal=guarded apply=single_undo failure=retained discard=clear catalog=current");
    }

    private static IDictionary Drafts(MissionGraphPage page) => (IDictionary)Field(page, "luaDrafts")!;
    private static string Draft(MissionGraphPage page, string id)
    { var value = Drafts(page)[id]!; return (string)value.GetType().GetProperty("Draft")!.GetValue(value)!; }
    private static object Read(MissionGraphPage page, string id, string source, string owner, string? path) =>
        Invoke(page, "ReadLuaDraft", id, source, owner, path)!;
    private static void Put(MissionGraphPage page, string id, string source, string text, string owner, string? path)
    {
        var value = Read(page, id, source, owner, path);
        var type = value.GetType();
        Drafts(page)[id] = Activator.CreateInstance(type, source, text, Field(page, "luaDraftDocument"), owner, path)!;
    }
    private static bool Change(MissionGraphPage page, Func<MissionGraphDocument, MissionGraphDocument> operation) =>
        (bool)Invoke(page, "Change", operation)!;
    private static bool ChangeLua(MissionGraphPage page, Func<MissionGraphDocument, MissionGraphDocument> operation, string id) =>
        (bool)Invoke(page, "ChangeLua", operation, new[] { id })!;
}

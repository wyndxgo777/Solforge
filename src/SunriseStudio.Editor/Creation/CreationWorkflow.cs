using System.Globalization;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor.Creation;

/**
 * What the editor opens after a workflow publishes its change.
 * @param Background True when the change finishes on a worker and can still fail there.
 */
public sealed record CreationFollowUp(string? AddedAssetId = null, uint? Tag = null, string? Page = null,
    string Status = "", bool Background = false);

/** A guided creation: a field list, a live problem list and one publishing step. */
public abstract class CreationWorkflow
{
    public abstract string Title { get; }
    public abstract IReadOnlyList<CreationField> Fields { get; }
    /** Known limits and blockers, shown above the fields. */
    public virtual IReadOnlyList<string> Limits => [];
    public virtual string CreateLabel => "Create";
    /** The Tag field Clone presets with its source; Validate must refuse a source that is not installed. */
    public virtual CreationField? CloneSource => null;

    /** Fields, then the new fields of every part, at any depth. */
    public IEnumerable<CreationField> Inputs => Expand(Fields);

    private static IEnumerable<CreationField> Expand(IEnumerable<CreationField> fields) =>
        fields.SelectMany(field => field.Part is { } part ? Expand(part.NewFields).Prepend(field) : [field]);

    /** ApplyAssetTransaction needs an open document; this one is opened first when none is. */
    public virtual uint? AnchorTag => null;
    protected virtual bool RequiresProject => true;

    /** Refreshes cached data between frames; runs before the fields are drawn. */
    public virtual void Update(StudioWorkspace workspace) { }

    /** Why a read the fields need failed; the dialog shows it with Retry and skips Update until then. */
    public string LoadError { get; protected internal set; } = "";

    private long investmentRequest = -1;
    private bool usesInvestment;

    /** True while the item catalog the fields list is still being read. */
    public bool IsLoading(StudioWorkspace workspace) => usesInvestment && LoadError.Length == 0 && workspace.Investment is null;

    /** Clears the failure and cached checks so the next Update reads again. */
    public void Retry()
    {
        LoadError = ""; investmentRequest = -1;
        IconChecks.Forget();
        Reload();
    }

    /** Forgets what Update loaded; a workflow that remembers its loaded source resets that here. */
    protected virtual void Reload() { }

    /**
     * Starts the item catalog read once per overlay; a read that ends without it sets LoadError.
     * @return True when the catalog is loaded.
     */
    protected bool InvestmentReady(StudioWorkspace workspace)
    {
        usesInvestment = true;
        if (workspace.Investment is not null) return true;
        if (workspace.IsBusy || workspace.Catalog is not { } catalog) return false;
        if (investmentRequest == catalog.OverlayRevision)
            LoadError = workspace.Error.Length != 0 ? workspace.Error : "The item catalog did not load.";
        else { investmentRequest = catalog.OverlayRevision; workspace.Run(workspace.LoadInvestment); }
        return false;
    }

    /**
     * Keeps a file or drive failure as LoadError, which Retry clears.
     * @param deviceOnly Keeps only a drive that dropped out, for checks that also read the user's own files.
     * @return The message, for the caller's own problem text.
     */
    protected string Unreadable(Exception error, bool deviceOnly = false)
    {
        if (deviceOnly ? PackageIo.IsDeviceError(error) : error is IOException or UnauthorizedAccessException)
            LoadError = error.Message;
        return error.Message;
    }

    /** Problems beyond single fields; Create stays disabled while any remain. */
    protected virtual IEnumerable<string> Validate(StudioWorkspace workspace) => [];

    /** Every reason the workflow cannot be created yet, including the subclass's own Validate. */
    public IReadOnlyList<string> Problems(StudioWorkspace workspace)
    {
        var problems = new List<string>();
        if (LoadError.Length != 0) problems.Add(LoadError);
        if (RequiresProject && workspace.Project is null) problems.Add("Create or open a project first.");
        if (workspace.Catalog is null) problems.Add("Open your Sunrise installation first.");
        foreach (var field in Fields) problems.AddRange(field.Problems());
        if (problems.Count == 0) problems.AddRange(Validate(workspace));
        return problems;
    }

    /** Publishes the change as one workspace operation; throws with a user-facing message. */
    public abstract CreationFollowUp? Create(StudioWorkspace workspace);

    /** A new-package id field; entries a workflow adds go to this package. */
    protected static CreationField PackageField(StudioWorkspace workspace) => new("Package ID", CreationFieldKind.Scalar, true,
        "Package id of the new entries")
    {
        Encoding = ScalarEncoding.UInt16, Hexadecimal = true,
        Text = DefaultPackage(workspace),
        Hint = $"Unused id in {PackageWriter.NewPackageIdRange}.",
        Evidence = "The client refuses package ids above 0CFF."
    };

    /** A problem when the text is not a new package id this project can use. */
    protected static string? PackageProblem(StudioWorkspace workspace, CreationField field)
    {
        if (!field.TryParse(out ulong value) || !PackageWriter.IsNewPackageId((int)value))
            return $"Package ID must be from {PackageWriter.NewPackageIdRange}.";
        if (workspace.Catalog?.ContainsInstalledPackage((ushort)value) == true)
            return $"Package {value:X4} is installed. Choose an unused id.";
        return null;
    }

    protected static ushort PackageId(CreationField field) =>
        ushort.Parse(field.Text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /** The newest package the project already adds to, else 0AA0. */
    private static string DefaultPackage(StudioWorkspace workspace) =>
        workspace.Project?.AddedAssets.LastOrDefault(asset => asset.Native is not null)?.Native!.PackageId.ToString("X4") ?? "0AA0";

    /** Next free entry index in a package, as project staging allocates it. */
    protected static int NextEntryIndex(StudioProject project, ushort packageId) =>
        project.AddedAssets.Where(asset => asset.Native?.PackageId == packageId)
            .Select(asset => asset.Native!.EntryIndex ?? -1).DefaultIfEmpty(-1).Max() + 1;

    protected static uint TagOf(ushort packageId, int entryIndex) => ProjectTags.Tag(packageId, entryIndex);

    protected static string FileName(string name) => AssetNames.FileName(name);

    /**
     * A Tag field that picks from a cached row array, such as CategoryRowsOf or ClassRows.
     * @param rows Returns the same array until the rows change; a new array refilters.
     * @param filter Keeps the rows that fit; runs on a worker, so it must not read packages or mutable workflow state.
     */
    protected static CreationField RowPick(string label, bool required, string target,
        Func<IReadOnlyList<BrowserAsset>> rows, Func<BrowserAsset, bool>? filter = null) =>
        new(label, CreationFieldKind.Tag, required, target) { Rows = rows, RowFilter = filter };
}

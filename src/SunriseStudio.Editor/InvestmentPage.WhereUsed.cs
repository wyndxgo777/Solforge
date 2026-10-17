namespace SunriseStudio.Editor;

/** The item page's "Where is this used..." button hands its item to StudioForm's panel (StudioForm.ItemSources.cs). */
public sealed partial class InvestmentPage
{
    private static uint whereUsedRequest;

    /** Asks the editor to open the "Where is this used" panel for an item tag. */
    public static void RequestWhereUsed(uint tag) => Interlocked.Exchange(ref whereUsedRequest, tag);

    /** The pending request, once; null when there is none. */
    public static uint? TakeWhereUsedRequest() => Interlocked.Exchange(ref whereUsedRequest, 0) is uint tag and not 0 ? tag : null;
}

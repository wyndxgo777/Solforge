// Checks for owner-only features (SolforgeRelay=true, RELEASING.md). Owner builds compile their checks in from the
// private folder (Directory.Build.props); public builds have none, so these do nothing.
using System.Text.Json.Nodes;
using Solforge.Mcp;

static partial class OwnerChecks
{
    /** get_viewport_stream's owner-only fields. */
    public static void StreamInfo(JsonObject info) => OwnerStreamInfo(info);

    /** A fresh viewport feed's owner-only state. */
    public static void Feed(ViewportFeed feed) => OwnerFeed(feed);

    static partial void OwnerStreamInfo(JsonObject info);
    static partial void OwnerFeed(ViewportFeed feed);
}

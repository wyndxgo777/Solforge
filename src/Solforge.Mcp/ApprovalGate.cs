namespace Solforge.Mcp;

/**
 * Decides whether an MCP action request waits for the user's click or runs at once. When auto-approve is on
 * the request is logged through onAuto (so the AI panel can list what ran) and approved with no click.
 */
public sealed class ApprovalGate(Func<bool> autoApprove, Action<string, string> onAuto)
{
    public async Task<bool> AskAsync(string tool, string summary, Func<Task<bool>> askUser)
    {
        if (autoApprove())
        {
            onAuto(tool, summary);
            return true;
        }
        return await askUser().ConfigureAwait(false);
    }
}

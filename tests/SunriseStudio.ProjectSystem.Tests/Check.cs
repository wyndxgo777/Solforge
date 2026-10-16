namespace SunriseStudio.ProjectSystem.Tests;

/** Minimal assertion helpers for the project system tests. */
internal static class Check
{
    /** Throws when the condition is false. */
    internal static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    /** Requires the action to throw T. */
    internal static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}

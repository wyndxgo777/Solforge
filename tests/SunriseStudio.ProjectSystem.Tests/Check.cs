namespace SunriseStudio.ProjectSystem.Tests;

internal static class Check
{
    internal static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}

using System;

/// <summary>Runs the offline checks for both Stardew Valley mods: nothing here needs the game.</summary>
internal static class Program
{
    public static int Main()
    {
        int failed = LayoutChecks.Run();
        failed += SettingsChecks.Run();
        return failed == 0 ? 0 : 1;
    }
}

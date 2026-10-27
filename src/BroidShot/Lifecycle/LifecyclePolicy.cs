using System;
using System.Collections.Generic;
using System.Linq;

namespace BroidShot.Lifecycle;

public static class LifecyclePolicy
{
    public static bool ShouldStartHidden(IEnumerable<string> arguments, UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(preferences);
        var args = arguments.ToArray();
        return preferences.RunInBackground &&
            (Has(args, "--background") || preferences.StartInTrayOnWindowsStartup && Has(args, "--autostart"));
    }

    public static bool ShouldRevealExistingWindow(IEnumerable<string> arguments) =>
        !Has(arguments, "--autostart") && !Has(arguments, "--background");

    private static bool Has(IEnumerable<string> args, string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);
}

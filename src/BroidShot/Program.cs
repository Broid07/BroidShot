using System;
using BroidShot.Updates;

namespace BroidShot;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--apply-update")
            return UpdateHandoff.RunHelperAsync(args[1]).GetAwaiter().GetResult();
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BroidShot;
using BroidShot.Lifecycle;

internal static class Program
{
    private static int assertions;
    private static int Main()
    {
        try
        {
            var preferences = JsonSerializer.Deserialize<UserPreferences>("{\"KeepHistory\":false,\"F12Enabled\":false}")!;
            Assert(preferences.RunInBackground && preferences.StartInTrayOnWindowsStartup && !preferences.KeepHistory && !preferences.F12Enabled,
                "Old settings preserve existing choices and receive compatible lifecycle defaults.");
            Assert(!LifecyclePolicy.ShouldStartHidden(Array.Empty<string>(), preferences), "Manual launch shows the main window.");
            Assert(LifecyclePolicy.ShouldStartHidden(new[] { "--autostart" }, preferences), "Autostart can open in the tray.");
            Assert(LifecyclePolicy.ShouldStartHidden(new[] { "--background" }, preferences), "Explicit background launch is supported.");
            preferences.StartInTrayOnWindowsStartup = false;
            Assert(!LifecyclePolicy.ShouldStartHidden(new[] { "--autostart" }, preferences), "Autostart respects the visible-start preference.");
            Assert(LifecyclePolicy.ShouldStartHidden(new[] { "--background" }, preferences), "Explicit background flag is independent of autostart display choice.");
            preferences.RunInBackground = false;
            Assert(!LifecyclePolicy.ShouldStartHidden(new[] { "--autostart", "--background" }, preferences), "Disabled background mode always keeps a usable main window.");
            Assert(LifecyclePolicy.ShouldRevealExistingWindow(Array.Empty<string>()), "Manual second launch reveals the existing window.");
            Assert(!LifecyclePolicy.ShouldRevealExistingWindow(new[] { "--autostart" }), "Autostart must not interrupt an existing session.");
            Assert(!LifecyclePolicy.ShouldRevealExistingWindow(new[] { "--background" }), "Background duplicate must stay silent.");

            string executable = @"C:\Apps With Spaces\BroidShot\BroidShot.exe";
            string command = StartupService.BuildLaunchCommand(executable);
            Assert(command == "\"C:\\Apps With Spaces\\BroidShot\\BroidShot.exe\" --autostart", "Executable path is quoted and marked as autostart.");
            string hosted = StartupService.BuildLaunchCommand(@"C:\Program Files\dotnet\dotnet.exe", @"C:\App Folder\BroidShot.dll");
            Assert(hosted == "\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\App Folder\\BroidShot.dll\" --autostart", "Dotnet-hosted launch retains the application DLL.");
            AssertThrows<ArgumentException>(() => StartupService.BuildLaunchCommand("relative.exe"), "Relative executable is rejected.");
            AssertThrows<ArgumentException>(() => StartupService.BuildLaunchCommand("C:\\bad\"path.exe"), "Invalid path cannot inject command arguments.");

            var store = new MemoryStore();
            store.Values["OtherApp"] = "unrelated command";
            var startup = new StartupService(store, () => command);
            Assert(!startup.IsEnabled && store.Writes == 0, "Reading startup state never registers the program.");
            startup.SetEnabled(true);
            Assert(startup.IsEnabled && store.Values["BroidShot"] == command, "Enabling persists the complete startup command.");
            startup.SetEnabled(false);
            Assert(!startup.IsEnabled && store.Values["OtherApp"] == "unrelated command", "Disabling preserves unrelated startup entries.");
            startup.SetEnabled(false);
            Assert(!startup.IsEnabled, "Disabling an absent registration is safe.");
            store.Values["BroidShot"] = @"C:\old-location\BroidShot.exe";
            startup.SetEnabled(true);
            Assert(store.Values["BroidShot"] == command, "Enabling refreshes an older executable location.");
            int writes = store.Writes;
            var invalid = new StartupService(store, () => new string('x', 261));
            AssertThrows<InvalidOperationException>(() => invalid.SetEnabled(true), "Overlong Windows command is rejected.");
            Assert(store.Writes == writes && store.Values["BroidShot"] == command, "Validation failure leaves the existing registration intact.");
            var unavailable = new StartupService(store, () => throw new InvalidOperationException("Application path unavailable"));
            unavailable.SetEnabled(false);
            Assert(!startup.IsEnabled, "Disabling still works when the current executable cannot be resolved.");
            store.RejectWrites = true;
            AssertThrows<UnauthorizedAccessException>(() => startup.SetEnabled(true), "Registry access failure reaches the settings handler.");
            Assert(!startup.IsEnabled, "Failed enabling is not shown as enabled.");
            store.RejectWrites = false;
            store.IgnoreWrites = true;
            AssertThrows<IOException>(() => startup.SetEnabled(true), "Registration is checked after writing.");

            string root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("BROIDSHOT_DATA_DIR", root);
            preferences.RunInBackground = false;
            preferences.StartInTrayOnWindowsStartup = false;
            preferences.Save();
            var reloaded = UserPreferences.Load();
            Assert(!reloaded.RunInBackground && !reloaded.StartInTrayOnWindowsStartup && !reloaded.F12Enabled,
                "Lifecycle settings survive a restart without overwriting other choices.");
            Console.WriteLine($"PASS: {assertions} lifecycle/startup assertions; live Windows startup registry was not modified.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }
    private static void AssertThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { assertions++; return; }
        throw new InvalidOperationException(message);
    }
    private sealed class MemoryStore : IStartupEntryStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public int Writes { get; private set; }
        public bool RejectWrites { get; set; }
        public bool IgnoreWrites { get; set; }
        public string? Read() => Values.TryGetValue("BroidShot", out string? value) ? value : null;
        public void Write(string command)
        {
            if (RejectWrites) throw new UnauthorizedAccessException("Test access denied");
            Writes++;
            if (!IgnoreWrites) Values["BroidShot"] = command;
        }
        public void Delete() => Values.Remove("BroidShot");
    }
}

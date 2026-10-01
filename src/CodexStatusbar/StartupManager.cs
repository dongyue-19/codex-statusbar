using Microsoft.Win32;

namespace CodexStatusbar;

/// <summary>
/// "Start with Windows" for a portable, per-user app.
///
/// <para><b>HKCU only.</b> <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> needs no
/// administrator rights, starts after the user logs on, and — unlike a service — runs inside the
/// interactive session, which is the only place UI Automation, <c>~/.codex</c> and
/// <c>\\.\pipe\codex-ipc</c> are reachable. Nothing here ever writes HKLM, a service, a scheduled
/// task, or a shortcut.</para>
///
/// <para><b>Explicitly not a service.</b> The overlay is a desktop companion: it must see the same
/// desktop the user sees. A service would run in session 0 and could read none of it.</para>
/// </summary>
internal static class StartupManager
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The value name. Fixed, so enabling twice or from two builds cannot leave duplicates.</summary>
    public const string ValueName = "CodexStatusbar";

    /// <summary>What the registry launches. The tray's "Start with Windows" writes exactly this.</summary>
    public const string BackgroundArgument = "--background";

    /// <summary>
    /// The command line for a given executable. The path is always quoted: an unquoted
    /// <c>C:\Program Files\...</c> would be parsed as the command <c>C:\Program</c> with an argument,
    /// which is the classic silent failure for a Run value.
    /// </summary>
    public static string BuildCommand(string executablePath) =>
        "\"" + executablePath + "\" " + BackgroundArgument;

    /// <summary>
    /// The executable path back out of a Run value. Handles the quoted form we write, an unquoted
    /// path, and a quoted path followed by arguments; returns null when nothing usable is there.
    /// </summary>
    public static string? ParseExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        // Unquoted: everything up to the first argument. A path with a space cannot be recovered
        // unambiguously, so the extension is the best signal available (".exe" then end-of-string
        // or a space).
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? text[..(exe + 4)] : text;
    }

    /// <summary>True when the registered command already runs this exact executable.</summary>
    public static bool PathMatches(string? command, string executablePath) =>
        ParseExecutablePath(command) is { } registered
        && string.Equals(
            Path.GetFullPath(registered),
            Path.GetFullPath(executablePath),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The raw Run value, or null when "Start with Windows" is off.</summary>
    public static string? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException
            or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static bool Enable(string executablePath, out string? error) =>
        Write(BuildCommand(executablePath), out error);

    public static bool Write(string command, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "could not open HKCU\\...\\Run for writing";
                return false;
            }

            key.SetValue(ValueName, command, RegistryValueKind.String);
            return true;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException
            or UnauthorizedAccessException or IOException)
        {
            error = exception.Message;
            return false;
        }
    }

    public static bool Disable(out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                // The key does not exist, which is the desired end state anyway.
                return true;
            }

            if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException
            or UnauthorizedAccessException or IOException)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>What the registry says right now, for <c>--startup-status</c> and the debug block.</summary>
    public static StartupStatus Describe(string executablePath)
    {
        var command = Read();
        return new StartupStatus(
            Enabled: command is not null,
            Command: command,
            ExecutablePath: executablePath,
            PathMatches: command is not null && PathMatches(command, executablePath));
    }
}

internal readonly record struct StartupStatus(
    bool Enabled,
    string? Command,
    string ExecutablePath,
    bool PathMatches)
{
    /// <summary>
    /// What to do to the registry so it agrees with the settings — the whole decision table in one
    /// place, so it can be asserted without touching the registry.
    /// </summary>
    public static StartupRegistryAction ResolveAction(
        bool configured,
        bool startWithWindows,
        string? registeredCommand,
        string executablePath)
    {
        // First run of a build that has this feature: opt in once. After that the user's choice is
        // the only thing that matters — an upgrade must never silently re-enable what they turned off.
        var desired = configured ? startWithWindows : true;

        if (!desired)
        {
            return registeredCommand is null
                ? StartupRegistryAction.None
                : StartupRegistryAction.Disable;
        }

        if (registeredCommand is null)
        {
            return StartupRegistryAction.Enable;
        }

        return StartupManager.PathMatches(registeredCommand, executablePath)
            ? StartupRegistryAction.None
            : StartupRegistryAction.RepairPath;
    }
}

internal enum StartupRegistryAction
{
    None,
    Enable,
    Disable,
    RepairPath
}

/// <summary>What the launch-time reconciliation did, for the tray label and the debug log.</summary>
internal readonly record struct StartupApplyResult(
    bool RegistryEnabled,
    bool FirstRunOptIn,
    string Outcome,
    string? Error);

/// <summary>
/// Keeps three things in agreement: the user's intent in <c>settings.json</c>, the state they can see
/// in the tray, and the <c>HKCU\...\Run</c> value Windows will actually use.
///
/// <para>The first-run rule lives here and only here: a build that has this feature opts in once, on
/// its first launch, and never again. That is why the decision needs both flags — "never configured"
/// and "the user turned it off" are the same <c>false</c> in
/// <see cref="OverlaySettings.StartWithWindows"/>.</para>
/// </summary>
internal static class StartupCoordinator
{
    /// <summary>Reconciles the registry with the settings. Called once per launch, in both modes.</summary>
    public static StartupApplyResult Apply(OverlaySettings settings, string executablePath, out bool settingsChanged)
    {
        var configured = settings.StartupConfigured;
        var desired = configured ? settings.StartWithWindows : true;
        var firstRun = !configured;
        var registered = StartupManager.Read();
        var action = StartupStatus.ResolveAction(configured, settings.StartWithWindows, registered, executablePath);

        string? error = null;
        var outcome = action switch
        {
            StartupRegistryAction.Enable => StartupManager.Enable(executablePath, out error)
                ? "enabled (first run)"
                : "enable failed",
            StartupRegistryAction.RepairPath => StartupManager.Enable(executablePath, out error)
                ? "enabled (path repaired)"
                : "repair failed",
            StartupRegistryAction.Disable => StartupManager.Disable(out error)
                ? "disabled"
                : "disable failed",
            _ => registered is null ? "disabled" : "enabled"
        };

        // Intent is what the settings keep; the registry is re-read so the reported state is the
        // truth, not the intention. A transient write failure must not silently flip the preference.
        var effective = StartupManager.Read() is not null;
        settingsChanged = firstRun || settings.StartWithWindows != desired;
        settings.StartWithWindows = desired;
        settings.StartupConfigured = true;

        return new StartupApplyResult(effective, firstRun, outcome, error);
    }

    /// <summary>The tray toggle. Marks the setting as configured so no later launch re-enables it.</summary>
    public static StartupApplyResult SetEnabled(
        OverlaySettings settings,
        string executablePath,
        bool enabled)
    {
        string? error = null;
        var ok = enabled
            ? StartupManager.Enable(executablePath, out error)
            : StartupManager.Disable(out error);

        settings.StartWithWindows = enabled;
        settings.StartupConfigured = true;
        var effective = StartupManager.Read() is not null;

        return new StartupApplyResult(
            effective,
            FirstRunOptIn: false,
            Outcome: ok ? (enabled ? "enabled" : "disabled") : (enabled ? "enable failed" : "disable failed"),
            Error: error);
    }
}

/// <summary>
/// The portable-app startup commands: <c>--install-startup</c>, <c>--uninstall-startup</c>,
/// <c>--startup-status</c>. They run before the overlay, so they never need the tray, the mutex or a
/// visible window, and they are what a script (or a troubleshooter) uses instead of the tray menu.
/// </summary>
internal static class StartupCommandLine
{
    public static int LastExitCode { get; private set; }

    /// <summary>
    /// The executable Windows should launch. Empty when this process is the .NET host rather than the
    /// app itself (a <c>dotnet run</c> during development), because registering that would leave a
    /// Run value that starts the SDK instead of the overlay.
    /// </summary>
    public static string ResolveExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        return name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? string.Empty : path;
    }

    public static bool TryRun(IReadOnlyList<string> args, string? settingsPath)
    {
        var install = Has(args, "--install-startup");
        var uninstall = Has(args, "--uninstall-startup");
        var status = Has(args, "--startup-status");
        if (!install && !uninstall && !status)
        {
            return false;
        }

        var executable = ResolveExecutablePath();
        if (executable.Length == 0)
        {
            Console.Error.WriteLine(
                "--install-startup / --uninstall-startup / --startup-status need the published exe; "
                + "this process is running under the .NET host.");
            LastExitCode = 3;
            return true;
        }

        if (status)
        {
            var settings = OverlaySettings.Load(settingsPath);
            PrintStatus(executable, settings);
            LastExitCode = 0;
            return true;
        }

        var loaded = OverlaySettings.Load(settingsPath);
        var result = StartupCoordinator.SetEnabled(loaded, executable, enabled: install);
        loaded.Save(settingsPath);

        Console.WriteLine(
            $"{(install ? "install-startup" : "uninstall-startup")}: {result.Outcome}");
        Console.WriteLine($"  value:    HKCU\\{StartupManager.RunKeyPath}\\{StartupManager.ValueName}");
        Console.WriteLine($"  command:  {StartupManager.BuildCommand(executable)}");
        if (result.Error is { Length: > 0 } error)
        {
            Console.Error.WriteLine($"  error:    {error}");
        }

        LastExitCode = result.Error is null ? 0 : 3;
        return true;
    }

    private static void PrintStatus(string executable, OverlaySettings settings)
    {
        var status = StartupManager.Describe(executable);
        Console.WriteLine($"Start with Windows: {(status.Enabled ? "enabled" : "disabled")}");
        Console.WriteLine($"Registry path:      HKEY_CURRENT_USER\\{StartupManager.RunKeyPath}");
        Console.WriteLine($"Value name:         {StartupManager.ValueName}");
        Console.WriteLine($"Command:            {status.Command ?? "(absent)"}");
        Console.WriteLine($"Current exe:        {executable}");
        Console.WriteLine($"Path matches:       {(status.Enabled ? (status.PathMatches ? "yes" : "no") : "n/a")}");
        Console.WriteLine(
            $"Settings:           startWithWindows={settings.StartWithWindows.ToString().ToLowerInvariant()} "
            + $"startupConfigured={settings.StartupConfigured.ToString().ToLowerInvariant()}");
        Console.WriteLine($"Admin required:     no (HKCU only)");
    }

    private static bool Has(IReadOnlyList<string> args, string flag) =>
        args.Any(argument => argument.Equals(flag, StringComparison.OrdinalIgnoreCase));
}
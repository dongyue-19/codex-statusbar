using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodexStatusbar;

/// <summary>
/// Identity of the running Codex Desktop client, for the <c>--debug</c> diagnostics.
///
/// The MSIX build ships as <c>ChatGPT.exe</c> inside
/// <c>...\WindowsApps\OpenAI.Codex_&lt;version&gt;_x64__&lt;publisher-hash&gt;\app\</c>, so the package
/// version is recoverable straight from the executable path — the same signal the window locator
/// uses to tell Codex apart from the real ChatGPT app.
/// </summary>
internal sealed record CodexDesktopInfo(int ProcessId, string Version, string ExecutablePath)
{
    public static CodexDesktopInfo Unknown { get; } = new(0, "unknown", string.Empty);

    private static readonly Regex MsixVersion = new(
        @"OpenAI\.Codex_(?<version>[0-9][0-9A-Za-z.\-]*)_",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Finds the Codex Desktop process. Prefers a process that currently owns a main window, then
    /// one whose path is the MSIX install, then any process literally named Codex.exe.
    /// </summary>
    public static CodexDesktopInfo Detect()
    {
        var candidates = new List<(int Score, int Pid, string Path)>();

        foreach (var process in SafeProcesses())
        {
            string? path;
            bool hasWindow;
            try
            {
                path = process.MainModule?.FileName;
                hasWindow = process.MainWindowHandle != IntPtr.Zero;
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                continue;
            }

            var name = process.ProcessName;
            var isChatGpt = name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase);
            var isCodex = name.Equals("Codex", StringComparison.OrdinalIgnoreCase);
            if (!isChatGpt && !isCodex)
            {
                continue;
            }

            var msix = path is not null
                && path.Contains(@"\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase);

            // Range is a UI process, not the helper processes that share the name.
            if (isChatGpt && !msix)
            {
                continue;
            }

            var score = (hasWindow ? 4 : 0) + (msix ? 2 : 0) + (isCodex ? 1 : 0);
            candidates.Add((score, SafeId(process), path ?? string.Empty));
        }

        if (candidates.Count == 0)
        {
            return Unknown;
        }

        var best = candidates.OrderByDescending(candidate => candidate.Score).First();
        return new CodexDesktopInfo(best.Pid, ReadVersion(best.Path), best.Path);
    }

    private static string ReadVersion(string path)
    {
        var match = MsixVersion.Match(path);
        if (match.Success)
        {
            return match.Groups["version"].Value;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return "unknown";
        }

        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(version.FileVersion) ? "unknown" : version.FileVersion;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }

    private static IEnumerable<Process> SafeProcesses()
    {
        try
        {
            return Process.GetProcesses();
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    private static int SafeId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }
}
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexStatusbar;

/// <summary>
/// What makes a process the official Codex Desktop client.
///
/// <para><b>Why the process name alone is not enough.</b> Codex Desktop is Electron, and its
/// executable is literally named <c>ChatGPT.exe</c>. A machine can also have the real ChatGPT
/// desktop app installed, whose executable has the same name. Matching on the name would attach this
/// overlay to the wrong application and then read a session file that belongs to a different
/// conversation — so the name is only a cheap pre-filter and the decision is made on package
/// identity.</para>
///
/// <para><b>The identity signal.</b> Codex Desktop ships as the MSIX package <c>OpenAI.Codex</c>.
/// Every process that belongs to it — the browser process and all of its renderers — reports
/// <c>GetPackageFamilyName</c> = <c>OpenAI.Codex_2p2nqsd0c76g0</c>, and an unpackaged process (the
/// <c>codex.exe</c> CLI, or a non-store ChatGPT build) fails the same call with
/// <c>APPMODEL_ERROR_NO_PACKAGE (15700)</c>. So:
/// <list type="bullet">
///   <item>package family name present → it must start with <c>OpenAI.Codex_</c>; anything else
///   (including <c>OpenAI.ChatGPT-Desktop_…</c>) is rejected outright;</item>
///   <item>package family name unreadable → fall back to the install path containing
///   <c>\WindowsApps\OpenAI.Codex_</c>, which is a weaker but still package-specific signal.</item>
/// </list>
/// The full rule is printed by <c>--debug</c> and by <c>--startup-status</c>.</para>
/// </summary>
internal static class CodexIdentityRules
{
    /// <summary>Process names worth opening a handle for. Never sufficient on its own.</summary>
    public static readonly string[] CandidateProcessNames = { "ChatGPT", "Codex" };

    public const string PackageFamilyPrefix = "OpenAI.Codex_";

    public const string MsixPathFragment = @"\WindowsApps\OpenAI.Codex_";

    /// <summary>
    /// Whether a name is worth a handle open. Accepts both the bare process name and a file name:
    /// <c>Process.GetProcessesByName</c> reports <c>ChatGPT</c> while the Toolhelp snapshot reports
    /// <c>ChatGPT.exe</c>, and comparing only one of the two silently finds nothing.
    /// </summary>
    public static bool IsCandidateProcessName(string? nameOrFileName)
    {
        if (string.IsNullOrWhiteSpace(nameOrFileName))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(nameOrFileName);
        return CandidateProcessNames.Any(
            candidate => name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The whole detection rule, as a pure function so the self-test can pin it without a running
    /// Codex.
    ///
    /// <para><paramref name="processName"/> is optional: pass it when it is already known (the
    /// snapshot path) to get the cheap name guard as well, or <c>null</c> when only the identity of a
    /// specific PID was read (the incremental path, where the candidate set is already tiny).</para>
    /// </summary>
    public static bool IsCodexDesktop(string? processName, string? packageFamilyName, string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(processName) && !IsCandidateProcessName(processName))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(packageFamilyName))
        {
            // Authoritative: a packaged process must belong to the Codex package. This is what
            // rejects a real ChatGPT Desktop, whose family name is also package-specific.
            return packageFamilyName.StartsWith(PackageFamilyPrefix, StringComparison.OrdinalIgnoreCase);
        }

        // No package identity. Accept only an executable that lives inside the Codex MSIX package;
        // an unpackaged ChatGPT.exe (a development build, or the real ChatGPT app) is rejected.
        return !string.IsNullOrWhiteSpace(executablePath)
            && executablePath.Contains(MsixPathFragment, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The rule that was applied, for the debug log — so a wrong attach is diagnosable.</summary>
    public static string Explain(string? processName, string? packageFamilyName, string? executablePath)
    {
        var name = string.IsNullOrWhiteSpace(processName) ? "(by pid)" : processName;
        var family = string.IsNullOrWhiteSpace(packageFamilyName) ? "(no package)" : packageFamilyName;
        var path = string.IsNullOrWhiteSpace(executablePath) ? "(unknown path)" : executablePath;
        return IsCodexDesktop(processName, packageFamilyName, executablePath)
            ? $"accepted: {name}, family {family}"
            : $"rejected: {name}, family {family}, path {path}";
    }
}

/// <summary>One identified Codex Desktop process.</summary>
internal sealed record CodexProcessInfo(
    int ProcessId,
    string PackageFamilyName,
    string PackageFullName,
    string ExecutablePath,
    string ApplicationUserModelId,
    string Version,
    bool HasMainWindow)
{
    public static CodexProcessInfo None { get; } = new(
        0, string.Empty, string.Empty, string.Empty, string.Empty, "unknown", false);

    public bool IsRunning => ProcessId != 0;

    /// <summary>The MSIX package name shown in the debug block: <c>OpenAI.Codex</c>.</summary>
    public string Package =>
        PackageFamilyName.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
            ? "OpenAI.Codex"
            : PackageFamilyName;
}

/// <summary>
/// Watches the Codex Desktop process lifecycle.
///
/// <para><b>Why polling, and how it is kept cheap.</b> The event-driven options were each rejected on
/// this machine: <c>Win32_ProcessStartTrace</c> / <c>ManagementEventWatcher</c> require administrator
/// rights (the user's hard requirement is HKCU-only, no elevation), and there is no unprivileged
/// per-package notification for "a process with this identity started". So while Codex is
/// <i>absent</i> the watcher polls — but it does so in two tiers, because measuring this machine
/// showed the naive version was not cheap at all:</para>
///
/// <list type="bullet">
///   <item><c>EnumProcesses</c> returns every PID in one bulk call: <b>0.10 ms</b>.</item>
///   <item><c>CreateToolhelp32Snapshot</c> returns names as well: <b>9.62 ms</b> (375 processes).</item>
/// </list>
///
/// <para>Polling the expensive one every two seconds is 4.8 ms/s — 0.5 % of a core — for a machine
/// where Codex is closed. So the poll takes the cheap one, and a full identity check only happens for
/// PIDs that <i>appeared</i> since the previous poll: a process set that gained nothing cannot contain
/// a Codex that just started. Looking up a new PID costs one handle open and one
/// <c>GetPackageFamilyName</c> (~0.1 ms), and it needs no name at all, because the family name is the
/// authoritative signal.</para>
///
/// <para>Once Codex <i>is</i> present the polling stops entirely: the watcher blocks on the process
/// handle, so the attached state costs nothing.</para>
/// </summary>
internal sealed class CodexProcessWatcher : IDisposable
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>Idle poll while Codex is not running. Nothing here is timing-critical.</summary>
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Handle-wait slice while Codex is running, so shutdown stays responsive.</summary>
    private static readonly TimeSpan ExitWaitSlice = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly Thread? _thread;
    private readonly CancellationTokenSource? _cancellation;

    private CodexProcessInfo _current = CodexProcessInfo.None;
    private CodexProcessInfo _cachedCodex = CodexProcessInfo.None;
    private List<int> _knownCodexPids = new();
    private HashSet<int>? _lastPidSet;
    private string? _lastError;
    private long _detectCount;
    private long _errorCount;
    private long _version;
    private string _lastDecision = "not evaluated";
    private long _lastDetectTicks;

    public CodexProcessWatcher()
    {
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        _thread = new Thread(() => Run(token))
        {
            IsBackground = true,
            Name = "CodexStatusbar.CodexProcessWatcher"
        };
        _thread.Start();
    }

    /// <summary>Raised on the watcher thread whenever the identified Codex process changes.</summary>
    public event Action<CodexProcessInfo>? Changed;

    /// <summary>The Codex process as of the last check; <see cref="CodexProcessInfo.None"/> when absent.</summary>
    public CodexProcessInfo Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Increments on every transition, so a poller can tell "changed" from "same" cheaply.</summary>
    public long Version => Interlocked.Read(ref _version);

    public long DetectCount => Interlocked.Read(ref _detectCount);

    /// <summary>Detections that failed outright. Surfaced in the debug block.</summary>
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    public double MillisecondsSinceLastDetect
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastDetectTicks);
            return ticks == 0
                ? double.NaN
                : (Stopwatch.GetTimestamp() - ticks) * 1000d / Stopwatch.Frequency;
        }
    }

    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    /// <summary>The last accept/reject decision, so the debug block shows the rule in action.</summary>
    public string LastDecision
    {
        get
        {
            lock (_gate)
            {
                return _lastDecision;
            }
        }
    }

    /// <summary>
    /// Runs one detection right now, on the caller's thread. Used by startup (Codex may already be
    /// running) and by the tray's "Start / Attach now".
    /// </summary>
    public CodexProcessInfo DetectNow() => Detect();

    public void Dispose()
    {
        try
        {
            _cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        _ = _thread;
        _cancellation?.Dispose();
    }

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            CodexProcessInfo detected;
            try
            {
                detected = Detect();
            }
            catch (Exception exception)
            {
                // Broad on purpose. A watcher that dies on one unexpected failure stops tracking the
                // lifecycle for the rest of the session, silently: the strip would simply never come
                // back. Whatever goes wrong is recorded and the loop carries on.
                RecordError(exception);
                detected = CodexProcessInfo.None;
            }

            Publish(detected);

            if (!detected.IsRunning)
            {
                if (token.WaitHandle.WaitOne(IdlePollInterval))
                {
                    break;
                }

                continue;
            }

            // Codex is up: stop enumerating and block on its handle. This is the whole point of the
            // "no high-frequency polling" requirement — the active state does no polling at all.
            try
            {
                WaitForAnchorExit(detected.ProcessId, token);
            }
            catch (Exception exception)
            {
                RecordError(exception);
                if (token.WaitHandle.WaitOne(IdlePollInterval))
                {
                    break;
                }
            }
        }
    }

    private void RecordError(Exception exception)
    {
        lock (_gate)
        {
            _lastError = exception.GetType().Name + ": " + exception.Message;
        }

        Interlocked.Increment(ref _errorCount);
    }

    private static void WaitForAnchorExit(int processId, CancellationToken token)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Already gone between detection and here; the caller re-detects on the next loop.
            return;
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (process.WaitForExit((int)ExitWaitSlice.TotalMilliseconds))
                {
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The handle became unusable: fall through and let the loop re-detect.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void Publish(CodexProcessInfo info)
    {
        Action<CodexProcessInfo>? handler = null;
        lock (_gate)
        {
            if (_current == info)
            {
                return;
            }

            _current = info;
            handler = Changed;
        }

        Interlocked.Increment(ref _version);
        try
        {
            handler?.Invoke(info);
        }
        catch (Exception exception)
        {
            // A consumer's callback must never be able to kill the watcher.
            RecordError(exception);
        }
    }

    // ------------------------------------------------------------------ detection

    /// <summary>
    /// One detection pass: a bulk PID read first, then identity work only for what is new.
    /// </summary>
    private CodexProcessInfo Detect()
    {
        Interlocked.Increment(ref _detectCount);
        Interlocked.Exchange(ref _lastDetectTicks, Stopwatch.GetTimestamp());

        var pids = EnumerateProcessIds();
        if (pids is not null)
        {
            // 1. A Codex we already know about is still alive: stay attached to it.
            var live = _knownCodexPids.Where(pids.Contains).ToList();
            if (live.Count > 0)
            {
                _lastPidSet = pids;
                if (_cachedCodex.IsRunning && live.Contains(_cachedCodex.ProcessId))
                {
                    SetDecision($"known Codex process still running (PID {_cachedCodex.ProcessId})");
                    return _cachedCodex;
                }

                // The anchor exited but other Codex processes remain (a replaced browser process, or a
                // renderer that outlived it). Keep tracking with a live one instead of reporting an
                // exit that has not happened — and never block on a dead handle.
                _knownCodexPids = live;
                _cachedCodex = _cachedCodex with { ProcessId = live[0] };
                SetDecision($"Codex still running, anchor moved to PID {live[0]}");
                return _cachedCodex;
            }

            // 2. Nothing known is alive, so only a process that *appeared* can be Codex starting.
            //    No new PID means nothing to look at, and no name resolution at all.
            if (_lastPidSet is not null)
            {
                var added = pids.Where(pid => !_lastPidSet.Contains(pid)).ToList();
                _lastPidSet = pids;
                if (added.Count == 0)
                {
                    _knownCodexPids = new List<int>();
                    _cachedCodex = CodexProcessInfo.None;
                    SetDecision("no new process since the last check");
                    return CodexProcessInfo.None;
                }

                var identified = IdentifyByPid(added);
                _knownCodexPids = identified.Pids;
                _cachedCodex = identified.Anchor;
                return identified.Anchor;
            }
        }

        // First pass (or no bulk enumeration available): resolve by name once, which also gives the
        // window-owner ordering for free.
        _lastPidSet = pids;
        var byName = IdentifyByNames();
        _knownCodexPids = byName.Pids;
        _cachedCodex = byName.Anchor;
        return byName.Anchor;
    }

    private void SetDecision(string decision)
    {
        lock (_gate)
        {
            _lastDecision = decision;
        }
    }

    /// <summary>Every live PID, in one bulk call, with no names and no per-process work.</summary>
    private static HashSet<int>? EnumerateProcessIds()
    {
        var buffer = _processIdBuffer ?? new int[4096];
        try
        {
            while (true)
            {
                if (!EnumProcesses(buffer, buffer.Length * sizeof(int), out var needed))
                {
                    return null;
                }

                var count = needed / sizeof(int);
                if (count < buffer.Length)
                {
                    var set = new HashSet<int>(count);
                    for (var index = 0; index < count; index++)
                    {
                        if (buffer[index] != 0)
                        {
                            set.Add(buffer[index]);
                        }
                    }

                    return set;
                }

                // The machine grew past the buffer: one retry with room to spare.
                buffer = new int[count + 512];
                _processIdBuffer = buffer;
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Identity check for specific PIDs. <paramref name="knownNames"/> is optional: when the name is
    /// already known the cheap name guard applies too, otherwise the package identity alone decides.
    /// </summary>
    private (CodexProcessInfo Anchor, List<int> Pids) Identify(
        IReadOnlyList<(int ProcessId, string? Name)> candidates)
    {
        if (candidates.Count == 0)
        {
            SetDecision("no process named ChatGPT or Codex");
            return (CodexProcessInfo.None, new List<int>());
        }

        // Phase 1 — identity only. On a busy machine processes come and go constantly, and the answer
        // is almost always "none of them is Codex", so this phase must not do any work beyond one
        // handle open and one package read per new PID.
        var accepted = new List<(int ProcessId, string Decision, CodexProcessInfo Info)>(4);
        var lastRejected = "not evaluated";
        foreach (var (processId, name) in candidates.OrderBy(candidate => candidate.ProcessId))
        {
            var info = Inspect(processId, name, hasWindow: false, out var decision);
            if (info is null)
            {
                lastRejected = decision;
                continue;
            }

            accepted.Add((processId, decision, info));
        }

        if (accepted.Count == 0)
        {
            SetDecision(lastRejected);
            return (CodexProcessInfo.None, new List<int>());
        }

        // Phase 2 — only now is a window enumeration worth paying for. The process that owns a window
        // is the Electron browser process, whose handle is the one that tells us when Codex exits.
        var windowOwners = WindowOwners();
        var ordered = accepted
            .OrderByDescending(entry => windowOwners.Contains(entry.ProcessId) ? 1 : 0)
            .ThenBy(entry => entry.ProcessId)
            .ToList();

        var anchor = ordered[0];
        var pids = new List<int>(ordered.Count) { anchor.ProcessId };
        pids.AddRange(ordered.Skip(1).Select(entry => entry.ProcessId));
        SetDecision(anchor.Decision);

        return (anchor.Info with { HasMainWindow = windowOwners.Contains(anchor.ProcessId) }, pids);
    }

    /// <summary>The incremental path: a handful of PIDs that appeared since the last poll.</summary>
    private (CodexProcessInfo Anchor, List<int> Pids) IdentifyByPid(IReadOnlyList<int> processIds)
    {
        var candidates = processIds.Select(processId => (ProcessId: processId, Name: (string?)null)).ToList();
        return Identify(candidates);
    }

    /// <summary>
    /// The full path, used once at startup: names come from a Toolhelp snapshot, which is also what
    /// makes the cheap name guard and the window-owner ordering possible.
    /// </summary>
    private (CodexProcessInfo Anchor, List<int> Pids) IdentifyByNames()
    {
        var candidates = new List<(int ProcessId, string? Name)>(16);
        var snapshot = Snapshot();
        if (snapshot is not null)
        {
            foreach (var (processId, executableName) in snapshot)
            {
                if (CodexIdentityRules.IsCandidateProcessName(executableName))
                {
                    candidates.Add((processId, Path.GetFileNameWithoutExtension(executableName)));
                }
            }

            return Identify(candidates);
        }

        // Toolhelp can fail on a severely restricted token. The managed API is slower but always
        // available, so detection degrades instead of stopping.
        foreach (var name in CodexIdentityRules.CandidateProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    candidates.Add((process.Id, name));
                }
                catch (InvalidOperationException)
                {
                    // Exited during enumeration.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return Identify(candidates);
    }

    /// <summary>
    /// PIDs that own a visible top-level window, in one <c>EnumWindows</c> pass. For Codex this is the
    /// browser process; the renderers, GPU and crashpad helpers own nothing visible.
    /// </summary>
    private static HashSet<int> WindowOwners()
    {
        var owners = new HashSet<int>();
        try
        {
            EnumWindows(
                (handle, _) =>
                {
                    if (IsWindowVisible(handle)
                        && GetWindowThreadProcessId(handle, out var processId) != 0
                        && processId != 0)
                    {
                        owners.Add((int)processId);
                    }

                    return true;
                },
                IntPtr.Zero);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            // No window information: the PID ordering still lets detection work.
        }

        return owners;
    }

    private CodexProcessInfo? Inspect(int processId, string? name, bool hasWindow, out string decision)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            decision = $"rejected: could not open PID {processId}";
            return null;
        }

        try
        {
            // One read decides it for a packaged process, which is the overwhelmingly common case for
            // either answer: a reject (an ordinary process has no package) or an accept (Codex does).
            var family = ReadPackageFamilyName(handle);
            var path = string.Empty;
            if (family.Length == 0)
            {
                path = ReadImagePath(handle);
                if (path.Length == 0)
                {
                    path = ManagedImagePath(processId);
                }
            }

            decision = CodexIdentityRules.Explain(name, family, path);
            if (!CodexIdentityRules.IsCodexDesktop(name, family, path))
            {
                return null;
            }

            // Accepted: only now pay for the remaining fields, which nothing but the debug block reads.
            if (path.Length == 0)
            {
                path = ReadImagePath(handle);
            }

            return new CodexProcessInfo(
                processId,
                family,
                ReadPackageFullName(handle),
                path,
                ReadApplicationUserModelId(handle),
                // The version lives in the *full* name (OpenAI.Codex_26.928.3736.0_x64__2p2nqsd0c76g0);
                // the family name has the version stripped out of it.
                ReadVersion(ReadPackageFullName(handle), path),
                hasWindow);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string ManagedImagePath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// <c>OpenAI.Codex_26.928.3736.0_x64__2p2nqsd0c76g0</c> already carries the version, so the
    /// debug block can name the exact build without touching the file.
    /// </summary>
    private static string ReadVersion(string packageFullName, string path)
    {
        var text = packageFullName.Length > 0 ? packageFullName : path;
        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"OpenAI\.Codex_(?<version>[0-9][0-9A-Za-z.\-]*)_",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["version"].Value : "unknown";
    }

    /// <summary>
    /// A Toolhelp32 process snapshot, read through a raw buffer.
    ///
    /// <para><c>PROCESSENTRY32W</c> ends in a fixed <c>WCHAR[260]</c>; marshalling that as a
    /// <c>ByValTStr</c> makes the interop layer decode a 260-character string for every entry on the
    /// machine. Reading the buffer by hand costs one 2-byte read per entry and decodes a name only for
    /// the handful that can possibly match. (The snapshot call itself is the expensive part — 9.6 ms
    /// here — which is why it is only used on the startup pass.)</para>
    /// </summary>
    private static List<(int ProcessId, string Name)>? Snapshot()
    {
        var handle = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (handle == InvalidHandleValue)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(EntrySize);
        try
        {
            Marshal.WriteInt32(buffer, EntrySize);
            if (!Process32FirstRaw(handle, buffer))
            {
                return null;
            }

            var results = new List<(int, string)>(16);
            do
            {
                var processId = Marshal.ReadInt32(buffer, ProcessIdOffset);
                if (processId <= 0)
                {
                    continue;
                }

                // Both candidate names start with C. One 16-bit read rejects the vast majority of
                // processes without touching a string.
                var first = (char)Marshal.ReadInt16(buffer, NameOffset);
                if (first is not ('C' or 'c'))
                {
                    continue;
                }

                results.Add((processId, ReadExecutableName(buffer)));
            }
            while (Process32NextRaw(handle, buffer));

            return results;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException or ArgumentException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseHandle(handle);
        }
    }

    /// <summary>The NUL-terminated <c>szExeFile</c>, decoded at its real length.</summary>
    private static string ReadExecutableName(IntPtr buffer)
    {
        var length = 0;
        while (length < 260 && Marshal.ReadInt16(buffer, NameOffset + (length * 2)) != 0)
        {
            length++;
        }

        return length == 0
            ? string.Empty
            : Marshal.PtrToStringUni(buffer + NameOffset, length) ?? string.Empty;
    }

    private static string ReadPackageFamilyName(IntPtr handle) => ReadPackageString(handle, GetPackageFamilyName);

    private static string ReadPackageFullName(IntPtr handle) => ReadPackageString(handle, GetPackageFullName);

    private static string ReadApplicationUserModelId(IntPtr handle) =>
        ReadPackageString(handle, GetApplicationUserModelId);

    private static string ReadPackageString(IntPtr handle, PackageStringReader read)
    {
        try
        {
            var length = 0;
            var required = read(handle, ref length, null);
            if (required != ErrorInsufficientBuffer || length <= 0)
            {
                return string.Empty;
            }

            var buffer = new StringBuilder(length);
            return read(handle, ref length, buffer) == 0 ? buffer.ToString() : string.Empty;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException or ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string ReadImagePath(IntPtr handle)
    {
        try
        {
            var capacity = 1024;
            var buffer = new StringBuilder(capacity);
            return QueryFullProcessImageName(handle, 0, buffer, ref capacity)
                ? buffer.ToString()
                : string.Empty;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static int[]? _processIdBuffer;

    // GetPackageFamilyName / GetPackageFullName / GetApplicationUserModelId share this shape:
    // (HANDLE, ref int length, StringBuilder? buffer) -> int. Declared through one delegate so the
    // three reads share a single implementation.
    private delegate int PackageStringReader(IntPtr handle, ref int length, StringBuilder? buffer);

    private delegate bool EnumWindowProc(IntPtr handle, IntPtr parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetPackageFamilyName(IntPtr handle, ref int length, StringBuilder? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetPackageFullName(IntPtr handle, ref int length, StringBuilder? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetApplicationUserModelId(IntPtr handle, ref int length, StringBuilder? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        IntPtr handle,
        int flags,
        StringBuilder name,
        ref int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcesses(int[] processIds, int size, out int bytesNeeded);

    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    // Layout is taken from the struct rather than hard-coded, so a 32-bit build would still read the
    // right offsets.
    private static readonly int EntrySize = Marshal.SizeOf<ProcessEntry>();
    private static readonly int ProcessIdOffset = (int)Marshal.OffsetOf<ProcessEntry>(nameof(ProcessEntry.ProcessId));
    private static readonly int NameOffset = (int)Marshal.OffsetOf<ProcessEntry>(nameof(ProcessEntry.ExecutableName));

    /// <summary>
    /// The Win32 layout. Only used for <see cref="Marshal.SizeOf{T}"/> and
    /// <see cref="Marshal.OffsetOf{T}"/> — the snapshot is read from a raw buffer instead of being
    /// marshalled through this as a <c>ByValTStr</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    private static extern bool Process32FirstRaw(IntPtr snapshot, IntPtr entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    private static extern bool Process32NextRaw(IntPtr snapshot, IntPtr entry);
}
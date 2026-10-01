using System.Runtime.InteropServices;

namespace CodexStatusbar;

/// <summary>
/// A single system-wide hotkey (<c>Ctrl+Alt+Shift+P</c>) used to enter and leave position editing.
///
/// <para>Needed because the strip is click-through while locked: there is otherwise no way to start a
/// drag without going to the tray. This uses the documented <c>RegisterHotKey</c> input API on a
/// private message window — it does not hook, inject into, or modify Codex in any way, and if another
/// application already owns the same combination registration simply fails and the tray menu still
/// works.</para>
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    public const string Description = "Ctrl+Alt+Shift+P";

    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x5B17;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    private MessageWindow? _window;
    private bool _registered;
    private bool _disposed;

    public event EventHandler? Pressed;

    /// <summary>True when the combination is actually owned by this process.</summary>
    public bool IsRegistered => _registered;

    public bool TryRegister()
    {
        if (_disposed)
        {
            return false;
        }

        if (_registered)
        {
            return true;
        }

        try
        {
            _window ??= new MessageWindow(this);
            _registered = RegisterHotKey(
                _window.Handle,
                HotkeyId,
                ModControl | ModAlt | ModShift | ModNoRepeat,
                (uint)Keys.P);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            _registered = false;
        }

        return _registered;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_registered && _window is not null)
        {
            _ = UnregisterHotKey(_window.Handle, HotkeyId);
        }

        _registered = false;
        _window?.DestroyHandle();
        _window = null;
    }

    private void HandleHotkey() => Pressed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// An invisible top-level window whose only job is to receive <c>WM_HOTKEY</c>. It is created with
    /// zero size and no visible style, so it never appears anywhere.
    /// </summary>
    private sealed class MessageWindow : NativeWindow
    {
        private readonly GlobalHotkey _owner;

        public MessageWindow(GlobalHotkey owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams
            {
                Caption = "CodexStatusbarHotkey",
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
                Style = unchecked((int)0x80000000),
                ExStyle = 0x00000080,
                Parent = IntPtr.Zero
            });
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotkey && message.WParam.ToInt32() == HotkeyId)
            {
                _owner.HandleHotkey();
                return;
            }

            base.WndProc(ref message);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
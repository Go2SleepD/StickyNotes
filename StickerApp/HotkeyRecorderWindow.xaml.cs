using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace StickerApp;

public partial class HotkeyRecorderWindow : Window
{
    public string? Result { get; private set; }

    private readonly HashSet<string> _held     = [];
    private readonly HashSet<string> _recorded = [];

    private IntPtr _mouseHook;
    private Win32.LowLevelMouseProc _mouseProc = null!;

    public HotkeyRecorderWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        using var proc   = Process.GetCurrentProcess();
        using var module = proc.MainModule!;
        _mouseProc = MouseHookProc;
        _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc,
            Win32.GetModuleHandle(module.ModuleName), 0);
        Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_mouseHook);
        base.OnClosed(e);
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            var s   = Win32.GetHookStruct(lParam);

            (string btn, bool down) ev = msg switch
            {
                Win32.WM_RBUTTONDOWN => ("MB2", true),
                Win32.WM_RBUTTONUP   => ("MB2", false),
                Win32.WM_MBUTTONDOWN => ("MB3", true),
                Win32.WM_MBUTTONUP   => ("MB3", false),
                Win32.WM_XBUTTONDOWN => (Win32.HiWord(s.mouseData) == Win32.XBUTTON1 ? "MB4" : "MB5", true),
                Win32.WM_XBUTTONUP   => (Win32.HiWord(s.mouseData) == Win32.XBUTTON1 ? "MB4" : "MB5", false),
                _                    => ("", false),
            };

            if (ev.btn.Length > 0)
            {
                var captured = ev;
                Dispatcher.BeginInvoke(() => Handle(captured.btn, captured.down));
            }
        }
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        e.Handled = true;
        Handle(KeyToName(e.Key == Key.System ? e.SystemKey : e.Key), true);
    }

    protected override void OnPreviewKeyUp(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        e.Handled = true;
        Handle(KeyToName(e.Key == Key.System ? e.SystemKey : e.Key), false);
    }

    private void Handle(string name, bool down)
    {
        if (down && name == "Esc") { Cancel(); return; }

        if (down)
        {
            _held.Add(name);
            _recorded.Add(name);
            RecordedText.Text = string.Join(" + ", _recorded);
        }
        else
        {
            _held.Remove(name);
            if (_recorded.Count > 0) Confirm();
        }
    }

    private void Confirm()
    {
        Result = string.Join("+", _recorded);
        Close();
    }

    private void Cancel()
    {
        Result = null;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Cancel();

    private static string KeyToName(Key key) => key switch
    {
        Key.LeftCtrl  or Key.RightCtrl  => "Ctrl",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.LeftAlt   or Key.RightAlt   => "Alt",
        Key.Escape   => "Esc",
        Key.Space    => "Space",
        Key.Enter    => "Enter",
        Key.Tab      => "Tab",
        Key.Back     => "Back",
        Key.Delete   => "Delete",
        Key.Insert   => "Insert",
        Key.Home     => "Home",
        Key.End      => "End",
        Key.PageUp   => "PageUp",
        Key.PageDown => "PageDown",
        >= Key.F1 and <= Key.F12 => $"F{key - Key.F1 + 1}",
        >= Key.A  and <= Key.Z   => key.ToString(),
        >= Key.D0 and <= Key.D9  => ((char)('0' + key - Key.D0)).ToString(),
        _ => key.ToString(),
    };
}

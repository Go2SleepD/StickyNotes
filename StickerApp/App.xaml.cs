using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using WpfComposition = System.Windows.Media.CompositionTarget;
using Microsoft.Win32;
using DFontStyle = System.Drawing.FontStyle;

namespace StickerApp;

public partial class App : System.Windows.Application
{
    public AppSettings Settings { get; private set; } = null!;

    private static Mutex? _instanceMutex;
    private NotifyIcon _tray = null!;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private Win32.LowLevelMouseProc    _mouseProc    = null!;
    private Win32.LowLevelKeyboardProc _keyboardProc = null!;
    private readonly List<StickerWindow> _stickers = [];
    private readonly List<BrickWindow> _bricks = [];
    private readonly Dictionary<string, BrickWindow> _brickByStickerId = [];
    private DateTime _lastStickerCreated = DateTime.MinValue;
    private StickerWindow? _primaryDragger;
    private readonly List<(StickerWindow Win, double Dx, double Dy)> _groupFollowers = [];

    private bool _mb2Held;
    private bool _mb3Held;
    private bool _mb4Held;
    private bool _mb5Held;

    // Long-press detection for "convert to rule"
    private DateTime? _createPressStart;
    private Win32.POINT _createPressPos;
    private StickerWindow? _createPressSticker;

    internal bool RecordingHotkey;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, "StickerApp_SingleInstance", out bool isFirst);
        if (!isFirst) { Shutdown(); return; }

        Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(Timeline), new FrameworkPropertyMetadata(240));

        base.OnStartup(e);
        Settings = AppSettings.Load();
        StickerStore.Init();
        SetupTray();
        InstallHooks();
        RestoreStickers();

        // Require task creation on startup if no active tasks exist
        bool hasActiveTasks = _stickers.Any(s => s.Data.Tasks.Any(t => !t.Done));
        if (!hasActiveTasks)
        {
            var prompt = new StartupPromptWindow(_stickers);
            if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.ResultText))
            {
                if (prompt.CreateNew)
                {
                    CreateSticker(GetScreenCenter(), prompt.ResultText);
                }
                else if (prompt.ExistingIndex is { } idx && idx >= 0 && idx < _stickers.Count)
                {
                    _stickers[idx].AddTask(prompt.ResultText);
                }
            }
        }

        // Rubber ball + dog are hidden by default; toggled via Mouse5 + '+'
    }

    // ── Tray ──────────────────────────────────────────────────────────────
    private void SetupTray()
    {
        _tray = new NotifyIcon
        {
            Icon = CreateEmojiIcon("📌"),
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Новый стикер", null, (_, _) => CreateSticker(GetScreenCenter()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Настройки цвета...", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Автозапуск", null, ToggleAutostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => CreateSticker(GetScreenCenter());
        UpdateTrayTooltip();
    }

    private static Icon CreateEmojiIcon(string emoji)
    {
        using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);
        using var font = new Font("Segoe UI Emoji", 22f, DFontStyle.Regular, GraphicsUnit.Pixel);
        var sf = new StringFormat(StringFormat.GenericDefault)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(emoji, font, Brushes.White, new RectangleF(0, 0, 32, 32), sf);
        return Icon.FromHandle(bmp.GetHicon());
    }

    // ── Hooks ─────────────────────────────────────────────────────────────
    private void InstallHooks()
    {
        using var proc   = Process.GetCurrentProcess();
        using var module = proc.MainModule!;
        var hMod = Win32.GetModuleHandle(module.ModuleName);

        _mouseProc    = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
        _mouseHook    = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL,    _mouseProc,    hMod, 0);
        _keyboardHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            var s   = Win32.GetHookStruct(lParam);

            bool justMb2 = false, justMb3 = false, justMb4 = false, justMb5 = false;

            if (msg == Win32.WM_RBUTTONDOWN) { _mb2Held = true;  justMb2 = true; }
            if (msg == Win32.WM_RBUTTONUP)   { EndCreatePress(); _mb2Held = false; }
            if (msg == Win32.WM_MBUTTONDOWN) { _mb3Held = true;  justMb3 = true; }
            if (msg == Win32.WM_MBUTTONUP)   { EndCreatePress(); _mb3Held = false; }
            if (msg == Win32.WM_XBUTTONDOWN)
            {
                if (Win32.HiWord(s.mouseData) == Win32.XBUTTON1) { _mb4Held = true;  justMb4 = true; }
                if (Win32.HiWord(s.mouseData) == Win32.XBUTTON2) { _mb5Held = true;  justMb5 = true; }
            }
            if (msg == Win32.WM_XBUTTONUP)
            {
                EndCreatePress();
                if (Win32.HiWord(s.mouseData) == Win32.XBUTTON1) _mb4Held = false;
                if (Win32.HiWord(s.mouseData) == Win32.XBUTTON2) _mb5Held = false;
            }

            if (!RecordingHotkey && (justMb2 || justMb3 || justMb4 || justMb5))
            {
                bool ctrl  = Win32.IsKeyDown(Win32.VK_CTRL);
                bool shift = Win32.IsKeyDown(Win32.VK_SHIFT);
                bool alt   = Win32.IsKeyDown(Win32.VK_ALT);
                var  pt    = s.pt;

                if (CheckCombo(Settings.HotkeyCreate, justMb2, justMb3, justMb4, justMb5, ctrl, shift, alt))
                {
                    // Defer creation to button-up so we can detect long-press over a sticker
                    _createPressStart = DateTime.UtcNow;
                    _createPressPos   = pt;
                    _createPressSticker = GetStickerAtCursor();
                }
                else if (CheckCombo(Settings.HotkeyComplete, justMb2, justMb3, justMb4, justMb5, ctrl, shift, alt))
                    Dispatcher.BeginInvoke(() => GetStickerAtCursor()?.HandleMouse5());
                else if (CheckCombo(Settings.HotkeyClearDesk, justMb2, justMb3, justMb4, justMb5, ctrl, shift, alt))
                    Dispatcher.BeginInvoke(ClearDesk);
            }

            if (msg == Win32.WM_MOUSEWHEEL && IsCursorOverSticker())
            {
                int delta = Win32.HiWord(s.mouseData);
                Dispatcher.BeginInvoke(() => GetStickerAtCursor()?.CycleOutlineColor(delta > 0));
                return new IntPtr(1); // suppress scroll
            }

            if (msg == Win32.WM_LBUTTONUP &&
                (_stickers.Any(s => s.IsDragging) || _groupFollowers.Count > 0))
                Dispatcher.BeginInvoke(ForceEndAllDrags);
        }
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void EndCreatePress()
    {
        if (_createPressStart == null) return;
        bool wasLong = (DateTime.UtcNow - _createPressStart.Value).TotalMilliseconds >= 600;
        var stickerNow = GetStickerAtCursor();
        var targetSticker = _createPressSticker;
        var targetPos = _createPressPos;
        _createPressStart = null;
        _createPressSticker = null;

        if (wasLong && targetSticker != null && targetSticker == stickerNow)
        {
            // Long press over sticker -> convert to rule
            Dispatcher.BeginInvoke(() => targetSticker.ConvertToRule());
        }
        else if (!wasLong && targetSticker == null)
        {
            // Short press over empty space -> create sticker
            Dispatcher.BeginInvoke(() => TryCreateSticker(new System.Windows.Point(targetPos.X, targetPos.Y)));
        }
    }

    private bool CheckCombo(string combo, bool justMb2, bool justMb3, bool justMb4, bool justMb5,
                             bool ctrl, bool shift, bool alt)
    {
        var (needMb2, needMb3, needMb4, needMb5, needCtrl, needShift, needAlt) = AppSettings.ParseHotkey(combo);
        // At least one required button must be the one just pressed
        if (!((justMb2 && needMb2) || (justMb3 && needMb3) || (justMb4 && needMb4) || (justMb5 && needMb5))) return false;
        // All required buttons must be currently held
        return (!needMb2 || _mb2Held) && (!needMb3 || _mb3Held) && (!needMb4 || _mb4Held) && (!needMb5 || _mb5Held)
            && ctrl == needCtrl && shift == needShift && alt == needAlt;
    }

    private void ForceEndAllDrags() => EndCustomDrag();

    private void ToggleDogAndBall()
    {
        if (RubberBallWindow.IsAlive)
            RubberBallWindow.Despawn();
        else
            RubberBallWindow.Spawn();
    }

    private void ClearDesk()
    {
        foreach (var s in _stickers.ToList())
            s.Destroy();
        foreach (var b in _bricks.ToList())
            b.Close();
        _bricks.Clear();
        _brickByStickerId.Clear();
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == Win32.WM_KEYDOWN)
        {
            var kb = Win32.GetKbHookStruct(lParam);

            if (_mb5Held && (kb.vkCode == Win32.VK_OEM_PLUS || kb.vkCode == Win32.VK_ADD))
            {
                Dispatcher.BeginInvoke(ToggleDogAndBall);
                return new IntPtr(1);
            }

            if (IsCursorOverSticker() && !IsSystemCombo(kb.vkCode))
            {
                Dispatcher.BeginInvoke(() => GetStickerAtCursor()?.HandleKeyDown(kb));
                return new IntPtr(1);
            }
        }
        return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static bool IsSystemCombo(uint vk)
    {
        bool alt   = Win32.IsKeyDown(Win32.VK_ALT);
        bool ctrl  = Win32.IsKeyDown(Win32.VK_CTRL);
        bool shift = Win32.IsKeyDown(Win32.VK_SHIFT);
        bool win   = Win32.IsKeyDown(Win32.VK_WIN_L) || Win32.IsKeyDown(Win32.VK_WIN_R);

        if (vk == Win32.VK_WIN_L || vk == Win32.VK_WIN_R) return true;
        if (win) return true;
        if (alt   && vk == Win32.VK_TAB)    return true;
        if (alt   && vk == Win32.VK_F4)     return true;
        if (ctrl  && shift && vk == Win32.VK_ESCAPE) return true;
        return false;
    }

    private bool IsCursorOverSticker()
    {
        Win32.GetCursorPos(out var pt);
        return _stickers.Where(s => s.IsVisible).Any(s =>
        {
            var r = s.ContentRect;
            return pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom;
        });
    }

    private StickerWindow? GetStickerAtCursor()
    {
        Win32.GetCursorPos(out var pt);
        return _stickers.Where(s => s.IsVisible).FirstOrDefault(s =>
        {
            var r = s.ContentRect;
            return pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom;
        });
    }

    // ── Group drag ────────────────────────────────────────────────────────
    internal void BeginCustomDrag(StickerWindow primary)
    {
        if (_groupFollowers.Count > 0) return;
        Win32.GetCursorPos(out var pt);
        _primaryDragger = primary;
        _groupFollowers.Add((primary, primary.Left - pt.X, primary.Top - pt.Y));
        foreach (var s in _stickers)
        {
            if (ReferenceEquals(s, primary)) continue;
            var r = s.ContentRect;
            if (pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom)
            {
                s.BeginGroupDrag();
                _groupFollowers.Add((s, s.Left - pt.X, s.Top - pt.Y));
            }
        }
        primary.BeginDrag();
        WpfComposition.Rendering += OnGroupDragTick;
    }

    private void EndCustomDrag()
    {
        if (_groupFollowers.Count == 0) return;
        WpfComposition.Rendering -= OnGroupDragTick;
        foreach (var (win, _, _) in _groupFollowers)
        {
            if (!ReferenceEquals(win, _primaryDragger))
                win.EndGroupDrag();
        }
        _primaryDragger?.EndDrag();
        _primaryDragger = null;
        _groupFollowers.Clear();
    }

    private void OnGroupDragTick(object? sender, EventArgs e)
    {
        Win32.GetCursorPos(out var pt);
        foreach (var (win, dx, dy) in _groupFollowers)
        {
            win.Left = pt.X + dx;
            win.Top  = pt.Y + dy;
        }
    }

    // ── Typing overlap transparency ───────────────────────────────────────
    internal void NotifyTypingChanged(StickerWindow source, bool isTyping)
    {
        var sr = source.ContentRect;
        foreach (var s in _stickers)
        {
            if (ReferenceEquals(s, source)) continue;
            if (!sr.IntersectsWith(s.ContentRect)) continue;
            s.SetTypingOverlapOpacity(isTyping);
        }
    }

    // ── Sticker lifecycle ─────────────────────────────────────────────────
    private void RestoreStickers()
    {
        foreach (var data in StickerStore.LoadAll())
            OpenSticker(data);

        // Force height recalc after all items have rendered
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            foreach (var s in _stickers)
                s.RecalcHeight();
        });
    }

    private void TryCreateSticker(System.Windows.Point spawnPoint)
    {
        if ((DateTime.UtcNow - _lastStickerCreated).TotalSeconds < 0.2) return;
        _lastStickerCreated = DateTime.UtcNow;
        CreateSticker(spawnPoint);
    }

    private void CreateSticker(System.Windows.Point spawnPoint, string? initialTask = null)
    {
        var data = new StickerData
        {
            X           = spawnPoint.X - 130,
            Y           = spawnPoint.Y - 39,
            AccentColor = Settings.DefaultAccentColor
        };
        if (!string.IsNullOrWhiteSpace(initialTask))
        {
            var text = initialTask.Trim();
            text = text.Length > 0 ? char.ToUpper(text[0]) + text[1..] : text;
            data.Tasks.Add(new TaskItem { Text = text });
        }
        StickerStore.Save(data);
        OpenSticker(data);
        UpdateTrayTooltip();
    }

    private void OpenSticker(StickerData data)
    {
        var win = new StickerWindow(data);
        win.Destroyed += OnStickerDestroyed;
        _stickers.Add(win);

        if (data.IsMinimized)
        {
            // Keep the sticker window hidden; show a brick instead
            var pos = GetNextBrickPosition();
            var brick = new BrickWindow(data, pos);
            brick.ExpandRequested += () => RestoreStickerFromBrick(win, brick);
            _bricks.Add(brick);
            _brickByStickerId[data.Id] = brick;
            brick.Show();
        }
        else
        {
            win.Show();
        }
    }

    public void RestoreSticker(StickerData data)
    {
        StickerStore.Save(data);
        OpenSticker(data);
        UpdateTrayTooltip();
    }

    private void OnStickerDestroyed(StickerWindow win)
    {
        _stickers.Remove(win);
        if (_brickByStickerId.Remove(win.Data.Id, out var brick))
        {
            _bricks.Remove(brick);
            brick.Close();
            RepositionBricks();
        }
        UpdateTrayTooltip();
    }

    internal void MinimizeSticker(StickerWindow sticker)
    {
        if (sticker.Data.IsMinimized) return;
        sticker.Data.IsMinimized = true;
        StickerStore.Save(sticker.Data);

        var pos = GetNextBrickPosition();
        var brick = new BrickWindow(sticker.Data, pos);
        brick.ExpandRequested += () => RestoreStickerFromBrick(sticker, brick);
        _bricks.Add(brick);
        _brickByStickerId[sticker.Data.Id] = brick;
        brick.Show();

        sticker.AnimateToBrick(pos, () => sticker.Hide());
        UpdateTrayTooltip();
    }

    private void RestoreStickerFromBrick(StickerWindow sticker, BrickWindow brick)
    {
        if (!sticker.Data.IsMinimized) return;
        _bricks.Remove(brick);
        _brickByStickerId.Remove(sticker.Data.Id);

        sticker.Data.IsMinimized = false;
        StickerStore.Save(sticker.Data);

        var brickPos = new System.Windows.Point(brick.Left, brick.Top);

        // Fade out brick
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
            { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => brick.Close();
        brick.BeginAnimation(System.Windows.UIElement.OpacityProperty, fade);

        sticker.AnimateFromBrick(brickPos, () => { });
        RepositionBricks();
        UpdateTrayTooltip();
    }

    private System.Windows.Point GetNextBrickPosition()
    {
        var area = SystemParameters.WorkArea;
        double x = area.Left;
        double y = area.Bottom - StickerWindow.BRICK_HEIGHT * (_bricks.Count + 1);
        return new System.Windows.Point(x, y);
    }

    private void RepositionBricks()
    {
        var area = SystemParameters.WorkArea;
        for (int i = 0; i < _bricks.Count; i++)
        {
            double targetY = area.Bottom - StickerWindow.BRICK_HEIGHT * (i + 1);
            _bricks[i].AnimateToY(targetY);
        }
    }

    private void UpdateTrayTooltip() =>
        _tray.Text = $"StickerApp — {_stickers.Count(s => s.IsVisible)} стикер(ов), {_bricks.Count} свёрнуто  •  Ctrl+Shift+Mouse5 | Mouse5 над стикером — удалить";

    // ── Z-order management ────────────────────────────────────────────────
    internal void BringToFront(StickerWindow win)
    {
        var hwnd = new WindowInteropHelper(win).Handle;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    internal void RestoreZOrder()
    {
        foreach (var s in _stickers)
        {
            var hwnd = new WindowInteropHelper(s).Handle;
            Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
    }

    internal void RaiseStickerInOrder(StickerWindow win)
    {
        _stickers.Remove(win);
        _stickers.Add(win);
        RestoreZOrder();
    }

    // ── Settings ──────────────────────────────────────────────────────────
    private void OpenSettings()
    {
        var win = new SettingsWindow(Settings);
        win.ShowDialog();
    }

    // ── Autostart ─────────────────────────────────────────────────────────
    private void ToggleAutostart(object? sender, EventArgs e) =>
        RegisterAutostart(!IsAutostartEnabled());

    public static void RegisterAutostart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)!;
        if (enable)
            key.SetValue("StickerApp", $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue("StickerApp", false);
    }

    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run")!;
        return Array.Exists(key.GetValueNames(), n => n == "StickerApp");
    }

    // ── Utility ───────────────────────────────────────────────────────────
    private static System.Windows.Point GetScreenCenter()
    {
        var screen = Screen.PrimaryScreen!.WorkingArea;
        return new System.Windows.Point(screen.Width / 2.0, screen.Height / 2.0);
    }

    private void ExitApp()
    {
        Win32.UnhookWindowsHookEx(_mouseHook);
        Win32.UnhookWindowsHookEx(_keyboardHook);
        _tray.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Win32.UnhookWindowsHookEx(_mouseHook);
        Win32.UnhookWindowsHookEx(_keyboardHook);
        _tray.Dispose();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

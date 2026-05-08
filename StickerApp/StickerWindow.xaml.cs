using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using WpfApp = System.Windows.Application;
using Color  = System.Windows.Media.Color;

namespace StickerApp;

public partial class StickerWindow : Window
{
    public event Action<StickerWindow>? Destroyed;
    public StickerData Data => _data;

    private readonly StickerData   _data;

    // ── Text editor state ────────────────────────────────────────────────
    private readonly StringBuilder _inputBuffer = new();
    private int       _cursorPos;
    private int       _selAnchor = -1; // -1 = no selection; selection spans [min, max) of cursor/anchor
    private TaskItem? _editingTask;
    private string    _editingOriginalText = "";

    private bool HasSelection => _selAnchor >= 0 && _selAnchor != _cursorPos;

    private (int start, int end) GetSelectionRange() =>
        _selAnchor < _cursorPos ? (_selAnchor, _cursorPos) : (_cursorPos, _selAnchor);

    private static readonly SolidColorBrush _selBrush    = new(Color.FromArgb(0x55, 0x5E, 0x6A, 0xD2));
    private static readonly SolidColorBrush _cursorBrush = new(Color.FromRgb(0xCC, 0xCC, 0xFF));

    // ── Transform layers ─────────────────────────────────────────────────
    private readonly RotateTransform _rotateT  = new();
    private readonly ScaleTransform  _scaleT   = new(1, 1);
    private readonly ScaleTransform  _breatheT = new(1, 1); // idle micro-pulse

    private static readonly Color _borderDefault = Color.FromRgb(0x2A, 0x2A, 0x35);
    private static readonly Color _borderHover   = Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF);
    private readonly SolidColorBrush _borderBrush = new(_borderDefault);

    // ── Outline (wheel-cycled) ────────────────────────────────────────────
    private static readonly Color[] _outlineColors =
    [
        Color.FromRgb(0xE5, 0x39, 0x35), // red
        Color.FromRgb(0xFF, 0xD8, 0x35), // yellow
        Color.FromRgb(0x43, 0xA0, 0x47), // green
        Color.FromRgb(0x1E, 0x88, 0xE5), // blue
    ];
    private static readonly string[] _outlineColorNames = ["#E53935", "#FFD835", "#43A047", "#1E88E5"];
    private readonly SolidColorBrush _outlineBrush = new(Colors.Transparent);
    private int _outlineIndex = -1; // -1 = off

    // ── Timer ─────────────────────────────────────────────────────────────
    private readonly SolidColorBrush _timerBrush       = new(Color.FromRgb(0x88, 0x88, 0x99));
    private readonly DispatcherTimer  _timerDispatcher  = new() { Interval = TimeSpan.FromSeconds(1) };
    private const    double           TimerBaseFontSize = 10.5;
    private int  _timerLevel = -1;
    private bool _allDone;
    private static readonly Color _timerDoneColor = Color.FromRgb(0x4A, 0xDE, 0x80);

    // ── Done button ───────────────────────────────────────────────────────
    private DoneButtonWindow? _doneWin;
    private bool              _isDestroying;
    private bool              _spawning = true;

    // ── Collapse state ────────────────────────────────────────────────────
    private bool   _isHovered;
    private bool   _isCollapsed;   // true while collapsed or animating height
    private double _fullHeight;

    // ── Physics / drag state ──────────────────────────────────────────────
    private bool _isDragging;
    private bool _borderHighlighted;
    private int  _lastHitTest = Win32.HTCLIENT;
    private bool _isTyping;

    // ── Layout constants ──────────────────────────────────────────────────
    private const int BorderSize = 20;
    private const int CornerSize = 14;

    // ── Input keyboard layout (override; shared across all stickers) ─────
    private static IntPtr _inputLayout;        // IntPtr.Zero = follow foreground window
    private static IntPtr[] _availableLayouts = [];
    private static readonly SolidColorBrush _langChipBrush =
        new(Color.FromArgb(0xCC, 0x9B, 0xA3, 0xFF));

    public StickerWindow(StickerData data)
    {
        InitializeComponent();
        _data = data;

        Left   = data.X;
        Top    = data.Y;
        Width  = data.Width;
        Height = data.Height;

        ApplyAccentColor();
        if (_data.IsRule) ApplyRuleMode();
        _outlineIndex = Array.IndexOf(_outlineColorNames, _data.OutlineColor ?? "");
        if (_outlineIndex >= 0) _outlineBrush.Color = _outlineColors[_outlineIndex];
        UpdateDoneMarkBrush();
        TaskList.ItemsSource = _data.Tasks;
        _data.Tasks.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { FitHeight(); UpdatePendingText(); });

        StickerBorder.RenderTransformOrigin = new System.Windows.Point(0.5, 0.06);
        StickerBorder.RenderTransform = new TransformGroup
            { Children = { _rotateT, _scaleT, _breatheT } };

        StickerBorder.BorderBrush  = _borderBrush;
        OutlineBorder.BorderBrush  = _outlineBrush;

        StickerBorder.PreviewMouseLeftButtonDown += OnBodyMouseDown;
        MouseEnter += (_, _) =>
        {
            _isHovered = true;
            if (!_isDragging) SetBorderHighlight(true);
            if (_isCollapsed) ExpandSticker();
            UpdateDoneButton();
            ((App)WpfApp.Current).BringToFront(this);
        };
        MouseLeave += (_, _) =>
        {
            _isHovered = false;
            SetBorderHighlight(false);
            if (_inputBuffer.Length > 0) CommitInput();
            UpdateDoneButton();
            if (!_isDestroying && !_spawning && _data.Tasks.Count > 0 && _data.Tasks.All(t => t.Done))
                CollapseSticker();
            ((App)WpfApp.Current).RestoreZOrder();
        };

        SourceInitialized += OnSourceInitialized;
        LocationChanged   += (_, _) => { PersistBounds(); UpdateDoneButtonTarget(); };
        SizeChanged       += (_, _) => { PersistBounds(); UpdateDoneButtonTarget(); };
        Loaded            += (_, _) =>
        {
            PlaySpawn();
            StartIdle();
            TimerText.Foreground = _timerBrush;
            _timerDispatcher.Tick += (_, _) => UpdateTimer();
            if (_data.IsActive)
            {
                UpdateTimer();
                _timerDispatcher.Start();
            }
            else
            {
                TimerText.Text = "--:--";
            }
            UpdateAllDoneState();
            UpdatePendingText();
            UpdateResetDots();
            UpdateToggleVisual();
            ContentPanel.SizeChanged += (_, _) => FitHeight();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, FitHeight);
        };
    }

    // ── Win32 / WndProc ───────────────────────────────────────────────────
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
            ex | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(Win32.MA_NOACTIVATE);

            case Win32.WM_NCHITTEST:
                int ht = CalcHitTest(hwnd);
                _lastHitTest = ht;
                if (ht != Win32.HTCLIENT) { handled = true; return new IntPtr(ht); }
                break;

        }
        return IntPtr.Zero;
    }

    private int CalcHitTest(IntPtr hwnd)
    {
        Win32.GetCursorPos(out var cur);
        Win32.GetWindowRect(hwnd, out var r);
        int x = cur.X - r.left, y = cur.Y - r.top;
        int w = r.right - r.left, h = r.bottom - r.top;

        bool onL = x < BorderSize,  onR = x > w - BorderSize;
        bool onT = y < BorderSize,  onB = y > h - BorderSize;
        bool cL  = x < CornerSize,  cR  = x > w - CornerSize;
        bool cT  = y < CornerSize,  cB  = y > h - CornerSize;

        if (onT && cL) return Win32.HTTOPLEFT;
        if (onT && cR) return Win32.HTTOPRIGHT;
        if (onB && cL) return Win32.HTBOTTOMLEFT;
        if (onB && cR) return Win32.HTBOTTOMRIGHT;
        if (onT) return Win32.HTTOP;
        if (onB) return Win32.HTBOTTOM;
        if (onL) return Win32.HTLEFT;
        if (onR) return Win32.HTRIGHT;

        return Win32.HTCLIENT;
    }

    // ── Content bounds (screen logical, margin-adjusted) ──────────────────
    public System.Windows.Rect ContentRect =>
        new(Left + BorderSize, Top + BorderSize,
            ActualWidth - BorderSize * 2, ActualHeight - BorderSize * 2);

    // ── Animations ────────────────────────────────────────────────────────
    private void PlaySpawn()
    {
        _scaleT.ScaleX = 0.3;
        _scaleT.ScaleY = 0.3;
        var ease = new ElasticEase
            { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 4 };
        Animate(_scaleT, ScaleTransform.ScaleXProperty, 0.3, 1.0, 480, ease);

        var animY = new DoubleAnimation(0.3, 1.0, TimeSpan.FromMilliseconds(480))
            { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        animY.Completed += (_, _) =>
        {
            _scaleT.ScaleY = 1.0;
            _spawning = false;
            OuterGrid.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        };
        _scaleT.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void StartIdle()
    {
        double phase = Random.Shared.NextDouble() * 4.2;
        var sineEase = new SineEase { EasingMode = EasingMode.EaseInOut };

        var rotAnim = new DoubleAnimation(-1.2, 1.2, new Duration(TimeSpan.FromSeconds(4.2)))
        {
            AutoReverse    = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = sineEase,
            BeginTime      = TimeSpan.FromSeconds(-phase)
        };
        _rotateT.BeginAnimation(RotateTransform.AngleProperty, rotAnim);

        // Micro-pulse on a different period so it never phase-locks with the sway
        var breatheAnim = new DoubleAnimation(0.997, 1.003, new Duration(TimeSpan.FromSeconds(3.1)))
        {
            AutoReverse    = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            BeginTime      = TimeSpan.FromSeconds(-phase * 0.74)
        };
        _breatheT.BeginAnimation(ScaleTransform.ScaleXProperty, breatheAnim);
        _breatheT.BeginAnimation(ScaleTransform.ScaleYProperty, breatheAnim);
    }

    private void StopIdle()
    {
        _rotateT.BeginAnimation(RotateTransform.AngleProperty, null);
        _breatheT.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _breatheT.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _breatheT.ScaleX = 1;
        _breatheT.ScaleY = 1;
    }

    internal bool IsDragging => _isDragging;

    internal void BeginDrag()
    {
        if (_isDragging) return;
        _isDragging = true;
        SetBorderHighlight(false);
        StopIdle();
        Win32.SetWindowPos(new WindowInteropHelper(this).Handle, Win32.HWND_TOPMOST,
            0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

        var fadeEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fadeOut  = new DoubleAnimation(Opacity, 0.55, TimeSpan.FromMilliseconds(130))
            { EasingFunction = fadeEase, FillBehavior = FillBehavior.Stop };
        fadeOut.Completed += (_, _) => Opacity = 0.55;
        BeginAnimation(OpacityProperty, fadeOut);
    }

    internal void EndDrag()
    {
        if (!_isDragging) return;
        _isDragging = false;
        _rotateT.Angle = 0;
        StartIdle();

        var fadeEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fadeIn   = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(220))
            { EasingFunction = fadeEase, FillBehavior = FillBehavior.Stop };
        fadeIn.Completed += (_, _) => Opacity = 1.0;
        BeginAnimation(OpacityProperty, fadeIn);

        ((App)WpfApp.Current).RaiseStickerInOrder(this);
        TryMagneticSnap();
    }

    // ── Group drag ────────────────────────────────────────────────────────
    internal void BeginGroupDrag()
    {
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty,  null);
        StopIdle();
        _isDragging = true;
        SetBorderHighlight(false);
        Win32.SetWindowPos(new WindowInteropHelper(this).Handle, Win32.HWND_TOPMOST,
            0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

        var fadeEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fadeOut  = new DoubleAnimation(Opacity, 0.55, TimeSpan.FromMilliseconds(130))
            { EasingFunction = fadeEase, FillBehavior = FillBehavior.Stop };
        fadeOut.Completed += (_, _) => Opacity = 0.55;
        BeginAnimation(OpacityProperty, fadeOut);
    }

    internal void EndGroupDrag()
    {
        _isDragging = false;
        StartIdle();
        PersistBounds();

        var fadeEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fadeIn   = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(220))
            { EasingFunction = fadeEase, FillBehavior = FillBehavior.Stop };
        fadeIn.Completed += (_, _) => Opacity = 1.0;
        BeginAnimation(OpacityProperty, fadeIn);
    }

    private void OnBodyMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_spawning || _isDestroying || _isDragging) return;
        if (IsInteractiveControl((DependencyObject)e.OriginalSource)) return;

        if (e.ClickCount == 2 && FindTaskItemAt((DependencyObject)e.OriginalSource) is { } editItem)
        {
            BeginTaskEdit(editItem);
            e.Handled = true;
            return;
        }

        ((App)WpfApp.Current).BeginCustomDrag(this);
        e.Handled = true;
    }

    private TaskItem? FindTaskItemAt(DependencyObject source)
    {
        for (var el = source; el is DependencyObject cur && !ReferenceEquals(cur, StickerBorder);
             el = VisualTreeHelper.GetParent(el))
            if (el is FrameworkElement { DataContext: TaskItem item }) return item;
        return null;
    }

    private void BeginTaskEdit(TaskItem item)
    {
        CommitInput(); // commit or cancel any in-progress input first
        _editingTask = item;
        _editingOriginalText = item.Text;
        _inputBuffer.Clear();
        _inputBuffer.Append(item.Text);
        _selAnchor = 0;
        _cursorPos = item.Text.Length; // select all on entry
        item.IsEditing = true;
        UpdatePendingText();
    }

    private bool IsInteractiveControl(DependencyObject el)
    {
        while (!ReferenceEquals(el, StickerBorder))
        {
            if (el is System.Windows.Controls.Button
                   or System.Windows.Controls.CheckBox
                   or System.Windows.Controls.TextBox) return true;
            el = VisualTreeHelper.GetParent(el);
        }
        return false;
    }


    // Snap to screen edge if dropped within threshold
    private void TryMagneticSnap()
    {
        const double Threshold = 36;
        var screen = Screen.FromPoint(
            new System.Drawing.Point((int)(Left + ActualWidth / 2), (int)(Top + ActualHeight / 2)));
        var area = screen.WorkingArea;

        double snapL = Left, snapT = Top;

        if      (Math.Abs(Left - area.Left)                 < Threshold) snapL = area.Left;
        else if (Math.Abs(Left + ActualWidth - area.Right)  < Threshold) snapL = area.Right  - ActualWidth;

        if      (Math.Abs(Top - area.Top)                   < Threshold) snapT = area.Top;
        else if (Math.Abs(Top + ActualHeight - area.Bottom) < Threshold) snapT = area.Bottom - ActualHeight;

        if (snapL == Left && snapT == Top) return;

        var ease  = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 8 };
        var animL = new DoubleAnimation(Left, snapL, TimeSpan.FromMilliseconds(420))
            { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        var animT = new DoubleAnimation(Top,  snapT, TimeSpan.FromMilliseconds(420))
            { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        animL.Completed += (_, _) => Left = snapL;
        animT.Completed += (_, _) => Top  = snapT;
        BeginAnimation(LeftProperty, animL);
        BeginAnimation(TopProperty,  animT);
    }

    private static void Animate(Animatable target, DependencyProperty prop,
        double from, double to, double ms, IEasingFunction? ease = null)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        anim.Completed += (_, _) => target.SetValue(prop, to);
        target.BeginAnimation(prop, anim);
    }

    // ── Border hover ─────────────────────────────────────────────────────
    private void SetBorderHighlight(bool on)
    {
        if (_borderHighlighted == on) return;
        _borderHighlighted = on;
        AnimateBorder(on ? _borderHover : _borderDefault, on ? 120 : 220);
    }

    private void AnimateBorder(Color to, double ms)
    {
        var anim = new ColorAnimation(to, TimeSpan.FromMilliseconds(ms))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        _borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    // ── Outline color cycling ─────────────────────────────────────────────
    public void CycleOutlineColor(bool forward)
    {
        if (_spawning || _isDestroying) return;
        int count = _outlineColors.Length;
        if (forward)
            _outlineIndex = _outlineIndex >= count - 1 ? -1 : _outlineIndex + 1;
        else
            _outlineIndex = _outlineIndex <= -1 ? count - 1 : _outlineIndex - 1;

        var target = _outlineIndex >= 0 ? _outlineColors[_outlineIndex] : Colors.Transparent;
        var anim = new ColorAnimation(target, TimeSpan.FromMilliseconds(150))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        _outlineBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);

        // Glow follows outline color
        var glowColorAnim = new ColorAnimation(target, TimeSpan.FromMilliseconds(150))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var glowOpacityAnim = new DoubleAnimation(_outlineIndex >= 0 ? 0.45 : 0.0, TimeSpan.FromMilliseconds(150))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        OutlineGlow.BeginAnimation(DropShadowEffect.ColorProperty, glowColorAnim);
        OutlineGlow.BeginAnimation(DropShadowEffect.OpacityProperty, glowOpacityAnim);

        _data.OutlineColor = _outlineIndex >= 0 ? _outlineColorNames[_outlineIndex] : null;
        StickerStore.Save(_data);
        UpdateDoneMarkBrush();
    }

    // ── Timer ─────────────────────────────────────────────────────────────
    private void UpdateTimer()
    {
        if (!_data.IsActive) return;
        if (_allDone) return;

        var elapsed = DateTime.UtcNow - _data.CreatedAt;
        TimerText.Text = FormatElapsed(elapsed);

        int newLevel = GetTimerLevel(elapsed);
        if (newLevel != _timerLevel)
        {
            _timerLevel = newLevel;
            ApplyTimerLevel(newLevel);
        }
    }

    private void UpdateAllDoneState()
    {
        bool allDone = _data.Tasks.Count > 0 && _data.Tasks.All(t => t.Done);
        if (allDone == _allDone) return;
        _allDone = allDone;

        if (allDone)
        {
            _timerDispatcher.Stop();
            TimerText.Text = FormatElapsed(DateTime.UtcNow - _data.CreatedAt);
            TimerText.Effect = null;
            _timerBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _timerBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(_timerDoneColor, TimeSpan.FromMilliseconds(600)));
            LockIcon.Visibility = Visibility.Visible;
            RefreshBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            LockIcon.Visibility = Visibility.Collapsed;
            _timerLevel = -1;
            if (_data.IsActive)
            {
                _timerDispatcher.Start();
                UpdateTimer();
            }
            else
            {
                TimerText.Text = "--:--";
            }
        }
    }

    private void OnActiveToggleClick(object sender, RoutedEventArgs e)
    {
        _data.IsActive = !_data.IsActive;
        StickerStore.Save(_data);

        if (_data.IsActive)
        {
            _data.CreatedAt = DateTime.UtcNow;
            _timerLevel = -1;
            _timerDispatcher.Start();
            UpdateTimer();
        }
        else
        {
            _timerDispatcher.Stop();
            TimerText.Text = "--:--";
            TimerText.Effect = null;
            _timerBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _timerBrush.Color = Color.FromRgb(0x88, 0x88, 0x99);
            RefreshBtn.Visibility = Visibility.Collapsed;
        }
        UpdateToggleVisual();
    }

    private void UpdateToggleVisual()
    {
        ActiveToggle.Content = _data.IsActive ? "\u25CF" : "\u25CB";
        ActiveToggle.Foreground = _data.IsActive
            ? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80))
            : new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    }

    private void UpdateResetDots()
    {
        ResetDotsPanel.Children.Clear();
        if (_data.ResetCount == 0) return;

        var color = _outlineIndex >= 0
            ? _outlineColors[_outlineIndex]
            : ((SolidColorBrush)Resources["AccentBrush"]).Color;
        var brush = new SolidColorBrush(color);

        for (int i = 0; i < _data.ResetCount; i++)
            ResetDotsPanel.Children.Add(new Ellipse
                { Width = 5, Height = 5, Fill = brush, Margin = new Thickness(0, 0, 0, 3) });
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _data.CreatedAt = DateTime.UtcNow;
        _data.ResetCount++;
        StickerStore.Save(_data);
        _timerLevel = -1;
        UpdateTimer();
        UpdateResetDots();
    }

    private static string FormatElapsed(TimeSpan t)
    {
        if (t.TotalHours < 1)
            return $"{(int)t.TotalMinutes} min. {t.Seconds:D2} sec.";
        if (t.TotalDays < 1)
            return $"{(int)t.TotalHours} h. {t.Minutes:D2} min.";
        if (t.TotalDays < 7)
            return $"{(int)t.TotalDays} d. {t.Hours:D2} h.";
        return $"{(int)(t.TotalDays / 7)} w. {(int)(t.TotalDays % 7)} d.";
    }

    private static int GetTimerLevel(TimeSpan elapsed)
    {
        double m = elapsed.TotalMinutes;
        if (m >= 120) return 4;
        if (m >=  60) return 3;
        if (m >=  30) return 2;
        if (m >=  15) return 1;
        return 0;
    }

    private void ApplyTimerLevel(int level)
    {
        _timerBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        TimerText.Effect = null;
        RefreshBtn.Visibility = level == 4 ? Visibility.Visible : Visibility.Collapsed;

        TimerText.Visibility = Visibility.Visible;

        if (level == 4)
        {
            var blink = new ColorAnimationUsingKeyFrames
            {
                Duration      = new Duration(TimeSpan.FromMilliseconds(900)),
                AutoReverse   = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            blink.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(0xFF, 0xFF, 0xFF), KeyTime.FromPercent(0)));
            blink.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(0xFF, 0x22, 0x22), KeyTime.FromPercent(1)));
            _timerBrush.BeginAnimation(SolidColorBrush.ColorProperty, blink);
            return;
        }

        Color target = level switch
        {
            0 => Color.FromRgb(0x88, 0x88, 0x99),
            1 => Color.FromRgb(0x88, 0x88, 0x99),
            2 => Color.FromRgb(0xCC, 0x66, 0x00),
            _ => Color.FromRgb(0xFF, 0x33, 0x33),
        };
        _timerBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(800)));
    }

    // ── Accent color ─────────────────────────────────────────────────────
    private void ApplyAccentColor()
    {
        var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(_data.AccentColor);
        Resources["AccentBrush"] = new SolidColorBrush(color);
    }

    private void UpdateDoneMarkBrush()
    {
        var color = _outlineIndex >= 0
            ? _outlineColors[_outlineIndex]
            : Color.FromRgb(0x94, 0x94, 0xA4);
        Resources["DoneMarkBrush"] = new SolidColorBrush(color);
    }

    // ── Hover keyboard input ─────────────────────────────────────────────
    public void HandleKeyDown(Win32.KBDLLHOOKSTRUCT kb)
    {
        uint vk = kb.vkCode;

        // Ctrl+Shift cycles the input keyboard layout
        if (vk == Win32.VK_SHIFT && Win32.IsKeyDown(Win32.VK_CTRL)) { CycleInputLayout(); return; }
        if (vk == Win32.VK_CTRL  && Win32.IsKeyDown(Win32.VK_SHIFT)) { CycleInputLayout(); return; }

        if (vk is Win32.VK_SHIFT or Win32.VK_CTRL or Win32.VK_ALT
                or Win32.VK_WIN_L or Win32.VK_WIN_R) return;

        bool ctrl  = Win32.IsKeyDown(Win32.VK_CTRL);
        bool shift = Win32.IsKeyDown(Win32.VK_SHIFT);

        if (vk == Win32.VK_RETURN) { CommitInput();  return; }
        if (vk == Win32.VK_ESCAPE) { ClearInput();   return; }
        if (vk == Win32.VK_UP || vk == Win32.VK_DOWN) return;

        if (vk == Win32.VK_LEFT)   { MoveByChar(-1, shift, ctrl); return; }
        if (vk == Win32.VK_RIGHT)  { MoveByChar(+1, shift, ctrl); return; }
        if (vk == Win32.VK_HOME)   { MoveCursorTo(0,                   shift); return; }
        if (vk == Win32.VK_END)    { MoveCursorTo(_inputBuffer.Length,  shift); return; }
        if (vk == Win32.VK_BACK)
        {
            if (_inputBuffer.Length == 0 && !ctrl && _data.Tasks.Count > 0)
                { EditLastTask(); return; }
            DeleteBack(ctrl);
            return;
        }
        if (vk == Win32.VK_DELETE) { DeleteForward(ctrl); return; }

        if (ctrl)
        {
            if (vk == 0x41) { SelectAll();          return; } // Ctrl+A
            if (vk == 0x43) { CopyToClipboard();    return; } // Ctrl+C
            if (vk == 0x58) { CutToClipboard();     return; } // Ctrl+X
            if (vk == 0x56) { PasteFromClipboard(); return; } // Ctrl+V
            return;
        }

        var keyState = new byte[256];
        Win32.GetKeyboardState(keyState);
        var layout = _inputLayout != IntPtr.Zero
            ? _inputLayout
            : Win32.GetKeyboardLayout(
                Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), IntPtr.Zero));
        var buf    = new StringBuilder(4);
        int count  = Win32.ToUnicodeEx(vk, kb.scanCode, keyState, buf, buf.Capacity, 0, layout);
        if (count > 0) InsertText(buf.ToString(0, count));
    }

    private void CycleInputLayout()
    {
        if (_availableLayouts.Length == 0)
        {
            int n = Win32.GetKeyboardLayoutList(0, []);
            if (n <= 0) return;
            var list = new IntPtr[n];
            Win32.GetKeyboardLayoutList(n, list);
            _availableLayouts = list;
        }
        if (_availableLayouts.Length < 2) return;

        IntPtr current = _inputLayout != IntPtr.Zero
            ? _inputLayout
            : Win32.GetKeyboardLayout(
                Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), IntPtr.Zero));

        int idx = Array.IndexOf(_availableLayouts, current);
        idx = (idx + 1) % _availableLayouts.Length;
        _inputLayout = _availableLayouts[idx];
        UpdatePendingText();
    }

    private static string CurrentLayoutCode()
    {
        var layout = _inputLayout != IntPtr.Zero
            ? _inputLayout
            : Win32.GetKeyboardLayout(
                Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), IntPtr.Zero));
        int langId = (int)((uint)layout.ToInt64() & 0xFFFF);
        try
        {
            var ci = System.Globalization.CultureInfo.GetCultureInfo(langId);
            var name = ci.TwoLetterISOLanguageName.ToUpperInvariant();
            return name.Length == 2 ? name : "??";
        }
        catch { return "??"; }
    }

    private void InsertText(string text)
    {
        if (HasSelection) EraseSelection();
        if (_inputBuffer.Length == 0 && text.Length > 0)
            text = char.ToUpper(text[0]) + text[1..];
        _inputBuffer.Insert(_cursorPos, text);
        _cursorPos += text.Length;
        UpdatePendingText();
    }

    private void EraseSelection()
    {
        var (start, end) = GetSelectionRange();
        _inputBuffer.Remove(start, end - start);
        _cursorPos = start;
        _selAnchor = -1;
    }

    private void MoveByChar(int dir, bool shift, bool ctrl)
    {
        if (!shift && HasSelection)
        {
            var (start, end) = GetSelectionRange();
            _cursorPos = dir < 0 ? start : end;
            _selAnchor = -1;
            UpdatePendingText();
            return;
        }

        if (shift && _selAnchor < 0) _selAnchor = _cursorPos;
        else if (!shift) _selAnchor = -1;

        _cursorPos = ctrl
            ? (dir < 0 ? WordStart() : WordEnd())
            : Math.Clamp(_cursorPos + dir, 0, _inputBuffer.Length);

        if (_selAnchor == _cursorPos) _selAnchor = -1;
        UpdatePendingText();
    }

    private void MoveCursorTo(int pos, bool shift)
    {
        if (shift && _selAnchor < 0) _selAnchor = _cursorPos;
        else if (!shift) _selAnchor = -1;
        _cursorPos = pos;
        if (_selAnchor == _cursorPos) _selAnchor = -1;
        UpdatePendingText();
    }

    private void DeleteBack(bool ctrl)
    {
        if (HasSelection) { EraseSelection(); UpdatePendingText(); return; }
        if (_cursorPos == 0) return;
        int n = ctrl ? _cursorPos - WordStart() : 1;
        _inputBuffer.Remove(_cursorPos - n, n);
        _cursorPos -= n;
        UpdatePendingText();
    }

    private void DeleteForward(bool ctrl)
    {
        if (HasSelection) { EraseSelection(); UpdatePendingText(); return; }
        if (_cursorPos >= _inputBuffer.Length) return;
        int n = ctrl ? WordEnd() - _cursorPos : 1;
        _inputBuffer.Remove(_cursorPos, n);
        UpdatePendingText();
    }

    private void SelectAll()
    {
        _selAnchor = 0;
        _cursorPos = _inputBuffer.Length;
        UpdatePendingText();
    }

    private void CopyToClipboard()
    {
        if (!HasSelection) return;
        var (start, end) = GetSelectionRange();
        System.Windows.Clipboard.SetText(_inputBuffer.ToString(start, end - start));
    }

    private void CutToClipboard()
    {
        CopyToClipboard();
        if (HasSelection) { EraseSelection(); UpdatePendingText(); }
    }

    private void PasteFromClipboard()
    {
        if (!System.Windows.Clipboard.ContainsText()) return;
        var text = System.Windows.Clipboard.GetText()
            .Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        InsertText(text);
    }

    private int WordStart()
    {
        int i = _cursorPos;
        while (i > 0 && !char.IsLetterOrDigit(_inputBuffer[i - 1])) i--;
        while (i > 0 && char.IsLetterOrDigit(_inputBuffer[i - 1])) i--;
        return i;
    }

    private int WordEnd()
    {
        int i = _cursorPos;
        while (i < _inputBuffer.Length && !char.IsLetterOrDigit(_inputBuffer[i])) i++;
        while (i < _inputBuffer.Length && char.IsLetterOrDigit(_inputBuffer[i])) i++;
        return i;
    }

    private void EditLastTask()
    {
        var item = _data.Tasks[^1];
        _data.Tasks.RemoveAt(_data.Tasks.Count - 1);
        StickerStore.Save(_data);
        UpdateDoneButton();

        _inputBuffer.Clear();
        _inputBuffer.Append(item.Text);
        _cursorPos = _inputBuffer.Length;
        _selAnchor = -1;
        UpdatePendingText();
    }

    private void CommitInput()
    {
        var raw  = _inputBuffer.ToString().Trim();
        var text = raw.Length > 0 ? char.ToUpper(raw[0]) + raw[1..] : raw;

        if (_editingTask is { } editing)
        {
            editing.Text = text.Length > 0 ? text : _editingOriginalText;
            editing.IsEditing = false;
            _editingTask = null;
            StickerStore.Save(_data);
            ResetBuffer();
            return;
        }

        if (text.Length > 0)
        {
            var item = new TaskItem { Text = text };
            _data.Tasks.Add(item);
            StickerStore.Save(_data);
            UpdateDoneButton();
            UpdateAllDoneState();

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var container = (FrameworkElement)TaskList.ItemContainerGenerator.ContainerFromItem(item);
                AnimateTaskIn(container);
            });
        }
        ResetBuffer();
    }

    private void ClearInput()
    {
        if (_editingTask is { } editing)
        {
            editing.IsEditing = false;
            _editingTask = null;
        }
        ResetBuffer();
    }

    private void ResetBuffer()
    {
        _inputBuffer.Clear();
        _cursorPos = 0;
        _selAnchor = -1;
        UpdatePendingText();
    }

    private void UpdatePendingText()
    {
        bool hasText = _inputBuffer.Length > 0;
        bool showPlaceholder = _data.Tasks.Count == 0 && !hasText && _editingTask == null;
        EmptyPlaceholder.Visibility = showPlaceholder ? Visibility.Visible : Visibility.Collapsed;

        if (_editingTask is { } editing)
        {
            PendingText.Visibility = Visibility.Collapsed;
            var text = _inputBuffer.ToString();
            editing.DisplayText = HasSelection
                ? BuildSelectionDisplay(text)
                : text[.._cursorPos] + "│" + text[_cursorPos..];
            FitHeight();
            return;
        }

        PendingText.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        if (hasText)
        {
            var text = _inputBuffer.ToString();
            PendingText.Inlines.Clear();

            PendingText.Inlines.Add(new Run(CurrentLayoutCode() + " ")
                { Foreground = _langChipBrush, FontSize = 9.5, FontWeight = FontWeights.SemiBold });

            if (HasSelection)
            {
                var (s, e) = GetSelectionRange();
                if (s > 0)           PendingText.Inlines.Add(new Run(text[..s]));
                PendingText.Inlines.Add(new Run(text[s..e]) { Background = _selBrush });
                if (e < text.Length) PendingText.Inlines.Add(new Run(text[e..]));
            }
            else
            {
                if (_cursorPos > 0)           PendingText.Inlines.Add(new Run(text[.._cursorPos]));
                PendingText.Inlines.Add(new Run("|") { Foreground = _cursorBrush });
                if (_cursorPos < text.Length) PendingText.Inlines.Add(new Run(text[_cursorPos..]));
            }
        }

        FitHeight();

        if (hasText != _isTyping)
        {
            _isTyping = hasText;
            ((App)WpfApp.Current).NotifyTypingChanged(this, _isTyping);
        }
    }

    private string BuildSelectionDisplay(string text)
    {
        var (s, e) = GetSelectionRange();
        return text[..s] + "❮" + text[s..e] + "❯" + text[e..];
    }

    internal void SetTypingOverlapOpacity(bool dim)
    {
        double target = dim ? 0.12 : 1.0;
        double ms     = dim ? 150 : 300;
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var anim = new DoubleAnimation(Opacity, target, TimeSpan.FromMilliseconds(ms))
            { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        anim.Completed += (_, _) => Opacity = target;
        BeginAnimation(OpacityProperty, anim);
    }

    // ── Task animations ───────────────────────────────────────────────────
    private static void AnimateTaskIn(FrameworkElement container)
    {
        container.Opacity = 0;
        var trans = new TranslateTransform(0, 14);
        container.RenderTransform = trans;

        var ease  = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fade  = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        var slide = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        container.BeginAnimation(OpacityProperty, fade);
        trans.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    // ── Task management ───────────────────────────────────────────────────
    private void OnDeleteTaskClick(object sender, RoutedEventArgs e)
    {
        var id        = (string)((System.Windows.Controls.Button)sender).Tag;
        var item      = _data.Tasks.First(t => t.Id == id);
        var container = (FrameworkElement)TaskList.ItemContainerGenerator.ContainerFromItem(item);

        var trans   = new TranslateTransform();
        container.RenderTransform = trans;
        var ease    = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        var fade    = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        var slideUp = new DoubleAnimation(0, -10, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };

        fade.Completed += (_, _) =>
        {
            _data.Tasks.Remove(item);
            StickerStore.Save(_data);
            UpdateDoneButton();
            UpdateAllDoneState();
            FitHeight();
        };
        container.BeginAnimation(OpacityProperty, fade);
        trans.BeginAnimation(TranslateTransform.YProperty, slideUp);
    }

    private void OnTaskCheckedChanged(object sender, RoutedEventArgs e)
    {
        StickerStore.Save(_data);
        UpdateDoneButton();
        UpdateAllDoneState();
    }

    private void FitHeight()
    {
        if (_isCollapsed) return;
        if (_data.IsRule)
        {
            Height = Math.Max(MinHeight, 150);
            _fullHeight = Height;
            return;
        }
        // StickerBorder.Margin=20 (×2=40) + ContentPanel.Margin top=4 bottom=24 = 68 ≈ 70
        const double Overhead = 70;
        double preferred = ContentPanel.ActualHeight + Overhead;
        Height = Math.Max(MinHeight, preferred);
        _fullHeight = Height;
    }

    private void RefreshList() => UpdateDoneButton();

    private void UpdateDoneButton()
    {
        bool allDone = _data.Tasks.Count > 0 && _data.Tasks.All(t => t.Done);

        if (!allDone && _isCollapsed) ExpandSticker();

        if (allDone && _isHovered)
        {
            if (_doneWin is not { IsVisible: true })
            {
                _doneWin = new DoneButtonWindow(OnDoneClick,
                    (System.Windows.Media.Brush)Resources["AccentBrush"]);
                _doneWin.SnapTo(DoneButtonX(), DoneButtonY());
                _doneWin.Show();
            }
        }
        else
        {
            _doneWin?.CloseSmooth();
            _doneWin = null;
        }
    }

    private void CollapseSticker()
    {
        if (_isCollapsed || _isDestroying || _spawning || _data.IsRule) return;
        _isCollapsed = true;
        var anim = new DoubleAnimation(ActualHeight, MinHeight, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseIn, Oscillations = 1, Springiness = 5 }, FillBehavior = FillBehavior.Stop };
        anim.Completed += (_, _) => Height = MinHeight;
        BeginAnimation(HeightProperty, anim);

        // Slight squeeze on collapse
        var squeeze = new DoubleAnimation(1.0, 0.96, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop };
        squeeze.Completed += (_, _) => _scaleT.ScaleY = 1.0;
        _scaleT.BeginAnimation(ScaleTransform.ScaleYProperty, squeeze);
    }

    private void ExpandSticker()
    {
        if (!_isCollapsed) return;
        _isCollapsed = false; // set now so CollapseSticker can re-trigger if cursor leaves mid-expand
        var anim = new DoubleAnimation(ActualHeight, _fullHeight, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 4 }, FillBehavior = FillBehavior.Stop };
        anim.Completed += (_, _) =>
        {
            Height = _fullHeight;
            if (!_isHovered && !_isDestroying && _data.Tasks.Count > 0 && _data.Tasks.All(t => t.Done))
                CollapseSticker();
        };
        BeginAnimation(HeightProperty, anim);

        // Overshoot "unfold" bounce
        var overshoot = new DoubleAnimation(1.04, 1.0, TimeSpan.FromMilliseconds(280))
            { EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 6 }, FillBehavior = FillBehavior.Stop };
        overshoot.Completed += (_, _) => { _scaleT.ScaleY = 1.0; StartIdle(); };
        _scaleT.BeginAnimation(ScaleTransform.ScaleYProperty, overshoot);
    }

    private double DoneButtonX() => Left + Width  / 2 - 65;
    private double DoneButtonY() => Top  + Height + 10;

    private void UpdateDoneButtonTarget() =>
        _doneWin?.SetTarget(DoneButtonX(), DoneButtonY());

    // ── Persistence ───────────────────────────────────────────────────────
    private void PersistBounds()
    {
        if (_isDestroying || _isDragging) return;
        _data.X = Left; _data.Y = Top;
        _data.Width = Width; _data.Height = Height;
        StickerStore.Save(_data);
    }

    // ── Destroy ───────────────────────────────────────────────────────────
    private void OnDoneClick()
    {
        _isDestroying = true;
        _isCollapsed  = false;
        BeginAnimation(HeightProperty, null);
        _timerDispatcher.Stop();
        _doneWin?.CloseSmooth();
        _doneWin = null;

        StickerStore.Delete(_data.Id);
        Destroyed?.Invoke(this);

        var src   = PresentationSource.FromVisual(this);
        double dpiX = src?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
        double dpiY = src?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
        var screenPt = PointToScreen(new System.Windows.Point(ActualWidth / 2, ActualHeight / 2));
        var dipPt    = new System.Windows.Point(screenPt.X / dpiX, screenPt.Y / dpiY);
        var accent   = ((SolidColorBrush)Resources["AccentBrush"]).Color;

        BallOverlayWindow.Burst(dipPt, accent);
        PlayCrumple(() => { BallOverlayWindow.DropBall(dipPt, accent, _data); Close(); });
    }

    private void PlayCrumple(Action onComplete)
    {
        _rotateT.BeginAnimation(RotateTransform.AngleProperty, null);
        _breatheT.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _breatheT.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _breatheT.ScaleX = _breatheT.ScaleY = 1;

        StickerBorder.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

        var spinEase   = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        var shrinkEase = new QuadraticEase { EasingMode = EasingMode.EaseIn };

        var spin    = new DoubleAnimation(0, 720, TimeSpan.FromMilliseconds(460)) { EasingFunction = spinEase };
        var shrinkX = new DoubleAnimation(_scaleT.ScaleX, 0, TimeSpan.FromMilliseconds(460)) { EasingFunction = shrinkEase };
        var shrinkY = new DoubleAnimation(_scaleT.ScaleY, 0, TimeSpan.FromMilliseconds(460)) { EasingFunction = shrinkEase };
        shrinkY.Completed += (_, _) => onComplete();

        _rotateT.BeginAnimation(RotateTransform.AngleProperty, spin);
        _scaleT.BeginAnimation(ScaleTransform.ScaleXProperty, shrinkX);
        _scaleT.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkY);
    }

    private void OnDestroyClick(object sender, RoutedEventArgs e) => Destroy();

    public void AddTask(string text)
    {
        var raw = text.Trim();
        var taskText = raw.Length > 0 ? char.ToUpper(raw[0]) + raw[1..] : raw;
        if (string.IsNullOrWhiteSpace(taskText)) return;

        var item = new TaskItem { Text = taskText };
        _data.Tasks.Add(item);
        StickerStore.Save(_data);
        UpdateDoneButton();
        UpdateAllDoneState();

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var container = (FrameworkElement)TaskList.ItemContainerGenerator.ContainerFromItem(item);
            AnimateTaskIn(container);
        });
    }

    private void ApplyRuleMode()
    {
        ContentPanel.Visibility      = Visibility.Collapsed;
        PendingText.Visibility       = Visibility.Collapsed;
        TimerText.Visibility         = Visibility.Collapsed;
        LockIcon.Visibility          = Visibility.Collapsed;
        RefreshBtn.Visibility        = Visibility.Collapsed;
        ResetDotsPanel.Visibility    = Visibility.Collapsed;
        DestroyBtn.Visibility        = Visibility.Collapsed;
        ActiveToggle.Visibility      = Visibility.Collapsed;
        RuleTextBlock.Visibility     = Visibility.Visible;
        RuleTextBlock.Text           = _data.Title;
        _timerDispatcher.Stop();
    }

    public void ConvertToRule()
    {
        if (_data.IsRule) return;

        var firstTask = _data.Tasks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text));
        if (firstTask != null)
            _data.Title = firstTask.Text;

        _data.Tasks.Clear();
        _data.IsRule = true;
        StickerStore.Save(_data);

        ApplyRuleMode();
        FitHeight();
    }

    public void HandleMouse5()
    {
        if (_spawning || _isDestroying) return;
        if (_data.Tasks.Count == 0) { Destroy(); return; }
        if (_data.Tasks.All(t => t.Done)) { OnDoneClick(); return; }
        _data.Tasks.First(t => !t.Done).Done = true;
    }

    public void Destroy()
    {
        if (_spawning) return;
        _isDestroying = true;
        _isCollapsed  = false;
        BeginAnimation(HeightProperty, null);
        _timerDispatcher.Stop();
        _doneWin?.CloseSmooth();
        _doneWin = null;

        StickerStore.Delete(_data.Id);
        Destroyed?.Invoke(this);
        PlayDissolve();
    }

    private void PlayDissolve()
    {
        var fadeEase = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(280))
            { EasingFunction = fadeEase };
        fade.Completed += (_, _) => Close();

        var shrinkEase = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        Animate(_scaleT, ScaleTransform.ScaleXProperty, _scaleT.ScaleX, 0.7, 280, shrinkEase);
        Animate(_scaleT, ScaleTransform.ScaleYProperty, _scaleT.ScaleY, 0.7, 280, shrinkEase);

        BeginAnimation(OpacityProperty, fade);
    }
}

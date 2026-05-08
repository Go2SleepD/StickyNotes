using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace StickerApp;

public partial class DoneButtonWindow : Window
{
    private readonly Action _onDone;
    private double _targetX, _targetY;
    private bool   _snapped;

    public DoneButtonWindow(Action onDone, System.Windows.Media.Brush accentBrush)
    {
        InitializeComponent();
        _onDone = onDone;
        Resources["AccentBrush"] = accentBrush;
        SourceInitialized += OnSourceInitialized;
        CompositionTarget.Rendering += OnTick;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex   = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
            ex | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    public void SnapTo(double x, double y)
    {
        Left = _targetX = x;
        Top  = _targetY = y;
        _snapped = true;
    }

    public void SetTarget(double x, double y)
    {
        _targetX = x;
        _targetY = y;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_snapped) return;
        Left += (_targetX - Left) * 0.13;
        Top  += (_targetY - Top)  * 0.13;
    }

    public void CloseSmooth()
    {
        CompositionTarget.Rendering -= OnTick;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void OnDoneClick(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnTick;
        _onDone();
    }
}

using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace StickerApp;

public partial class BrickWindow : Window
{
    public event Action? ExpandRequested;

    private readonly StickerData _data;

    public BrickWindow(StickerData data, System.Windows.Point pos)
    {
        InitializeComponent();
        _data = data;
        Left = pos.X;
        Top = pos.Y;

        // Border color follows the sticker outline (or accent as fallback)
        if (!string.IsNullOrEmpty(data.OutlineColor))
        {
            BrickBorder.BorderBrush = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(data.OutlineColor));
        }
        else
        {
            var accent = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(data.AccentColor);
            BrickBorder.BorderBrush = new SolidColorBrush(accent);
        }

        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
            ex | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private void OnExpandClick(object sender, RoutedEventArgs e)
    {
        ExpandRequested?.Invoke();
    }

    public void AnimateToY(double targetY)
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var anim = new DoubleAnimation(Top, targetY, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        anim.Completed += (_, _) => Top = targetY;
        BeginAnimation(TopProperty, anim);
    }
}

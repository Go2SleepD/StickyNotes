using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using WpfColor = System.Windows.Media.Color;

namespace StickerApp;

public partial class TargetWindow : Window
{
    public const double TargetRadius   = 36;          // 3x ball diameter (BallR=12 → ø24, target ø72)
    private const double WindowSize    = 420;         // room for shards to fly outside the target
    private const double LocalCenter   = WindowSize / 2;
    private const int    ShardCount    = 28;
    private const int    WsExTransparent = 0x00000020;

    private static readonly WpfColor RingRed     = WpfColor.FromRgb(0xC8, 0x1E, 0x1E);
    private static readonly WpfColor RingWhite   = WpfColor.FromRgb(0xF6, 0xF1, 0xE6);
    private static readonly WpfColor RingDarkRed = WpfColor.FromRgb(0x7B, 0x10, 0x10);
    private static readonly WpfColor RingGold    = WpfColor.FromRgb(0xE5, 0xB4, 0x3B);

    // Ring radii as fractions of TargetRadius, outer→inner with their fill color.
    private static readonly (double rOuter, WpfColor color)[] Rings =
    {
        (1.00, RingRed),
        (0.80, RingWhite),
        (0.60, RingRed),
        (0.40, RingWhite),
        (0.20, RingGold),
    };

    private double _centerX, _centerY;
    private bool   _active;     // visible + can be hit
    private bool   _exploding;  // shards in flight
    private IntPtr _hwnd;
    private double _pixelsPerDip = 1.0;

    private Grid           _targetVisual = null!;
    private ScaleTransform _targetScale  = null!;

    private readonly Ellipse[]            _shards     = new Ellipse[ShardCount];
    private readonly TranslateTransform[] _shardTrans = new TranslateTransform[ShardCount];
    private readonly RotateTransform[]    _shardRot   = new RotateTransform[ShardCount];
    private readonly double[]             _shardX     = new double[ShardCount];
    private readonly double[]             _shardY     = new double[ShardCount];
    private readonly double[]             _shardVx    = new double[ShardCount];
    private readonly double[]             _shardVy    = new double[ShardCount];
    private readonly double[]             _shardOmega = new double[ShardCount];
    private readonly double[]             _shardLife  = new double[ShardCount];
    private readonly double[]             _shardMax   = new double[ShardCount];

    private bool     _renderHooked;
    private DateTime _lastTick;

    public bool IsHittable => _active && !_exploding;
    public (double X, double Y) Center => (_centerX, _centerY);

    public TargetWindow()
    {
        InitializeComponent();

        Width  = WindowSize;
        Height = WindowSize;
        Left   = -10000;
        Top    = -10000;

        BuildTarget();
        BuildShards();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = Win32.GetWindowLong(_hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLong(_hwnd, Win32.GWL_EXSTYLE,
                ex | WsExTransparent | Win32.WS_EX_NOACTIVATE);
            var src = PresentationSource.FromVisual(this);
            _pixelsPerDip = src?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
        };

        // Force HWND creation without becoming visible; then we control visibility via Show/Hide.
        new WindowInteropHelper(this).EnsureHandle();
    }

    private void BuildTarget()
    {
        _targetVisual = new Grid
        {
            Width  = TargetRadius * 2,
            Height = TargetRadius * 2,
            IsHitTestVisible = false,
            Opacity = 0,
        };

        foreach (var (rFrac, c) in Rings)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            var ring = new Ellipse
            {
                Width  = TargetRadius * 2 * rFrac,
                Height = TargetRadius * 2 * rFrac,
                Fill   = brush,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment   = System.Windows.VerticalAlignment.Center,
                IsHitTestVisible    = false,
            };
            _targetVisual.Children.Add(ring);
        }

        var rim = new Ellipse
        {
            Width  = TargetRadius * 2,
            Height = TargetRadius * 2,
            Stroke = new SolidColorBrush(WpfColor.FromArgb(180, 30, 0, 0)),
            StrokeThickness = 1.6,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment   = System.Windows.VerticalAlignment.Center,
            IsHitTestVisible    = false,
        };
        _targetVisual.Children.Add(rim);

        _targetScale = new ScaleTransform(0.1, 0.1)
        {
            CenterX = TargetRadius,
            CenterY = TargetRadius,
        };
        _targetVisual.RenderTransform = _targetScale;
        _targetVisual.CacheMode       = new BitmapCache { RenderAtScale = 1.5, SnapsToDevicePixels = false, EnableClearType = false };
        _targetVisual.Effect          = new DropShadowEffect
        {
            BlurRadius  = 18,
            ShadowDepth = 4,
            Opacity     = 0.55,
            Color       = Colors.Black,
        };

        Canvas.SetLeft(_targetVisual, LocalCenter - TargetRadius);
        Canvas.SetTop (_targetVisual, LocalCenter - TargetRadius);

        Root.Children.Add(_targetVisual);
    }

    private void BuildShards()
    {
        for (int i = 0; i < ShardCount; i++)
        {
            var t = new TranslateTransform(-1000, -1000);
            var r = new RotateTransform(0) { CenterX = 5, CenterY = 5 };
            var g = new TransformGroup();
            g.Children.Add(r);
            g.Children.Add(t);

            var ellipse = new Ellipse
            {
                Width  = 10,
                Height = 10,
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransform = g,
                CacheMode = new BitmapCache { RenderAtScale = 1.0, SnapsToDevicePixels = false, EnableClearType = false },
            };

            _shards[i]     = ellipse;
            _shardTrans[i] = t;
            _shardRot[i]   = r;
            Root.Children.Add(ellipse);
        }
    }

    public static (double x, double y) PickRandomPosition(double cursorX, double cursorY, double minDistFromCursor)
    {
        double margin = TargetRadius + 24;
        double left   = SystemParameters.VirtualScreenLeft + margin;
        double right  = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth  - margin;
        double top    = SystemParameters.VirtualScreenTop  + margin;
        double bottom = SystemParameters.VirtualScreenTop  + SystemParameters.VirtualScreenHeight - margin;

        if (right <= left || bottom <= top)
            return (cursorX, cursorY);

        double bestX = (left + right)  / 2;
        double bestY = (top  + bottom) / 2;
        double bestDist = -1;

        for (int i = 0; i < 40; i++)
        {
            double x = left + Random.Shared.NextDouble() * (right - left);
            double y = top  + Random.Shared.NextDouble() * (bottom - top);
            double dx = x - cursorX, dy = y - cursorY;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d >= minDistFromCursor) return (x, y);
            if (d > bestDist) { bestDist = d; bestX = x; bestY = y; }
        }
        return (bestX, bestY);
    }

    public void ShowAt(double centerX, double centerY)
    {
        // Cancel any in-flight explosion / hide animation.
        _exploding = false;
        StopRenderLoop();
        for (int i = 0; i < ShardCount; i++)
        {
            _shardLife[i] = 0;
            _shards[i].Opacity = 0;
        }

        _centerX = centerX;
        _centerY = centerY;
        _active  = true;

        RepositionWindow();

        if (!IsVisible) Show();

        _targetVisual.BeginAnimation(OpacityProperty, null);
        _targetScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _targetScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        var ease = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 };
        var dur  = TimeSpan.FromMilliseconds(380);
        _targetScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.15, 1.0, dur) { EasingFunction = ease });
        _targetScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.15, 1.0, dur) { EasingFunction = ease });
        _targetVisual.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1.0, TimeSpan.FromMilliseconds(180)));
    }

    public void HideAnimated()
    {
        if (!_active) return;
        _active = false;

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        var dur  = TimeSpan.FromMilliseconds(180);

        _targetScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(_targetScale.ScaleX, 0.1, dur) { EasingFunction = ease });
        _targetScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(_targetScale.ScaleY, 0.1, dur) { EasingFunction = ease });

        var fade = new DoubleAnimation(_targetVisual.Opacity, 0, dur);
        fade.Completed += (_, _) =>
        {
            if (!_active && !_exploding) Hide();
        };
        _targetVisual.BeginAnimation(OpacityProperty, fade);
    }

    public void Explode()
    {
        if (!_active) return;
        _active   = false;
        _exploding = true;

        _targetVisual.BeginAnimation(OpacityProperty, null);
        _targetVisual.Opacity = 0;

        for (int i = 0; i < ShardCount; i++)
        {
            int ringIdx = Random.Shared.Next(Rings.Length);
            double ringMid = (ringIdx == Rings.Length - 1)
                ? Rings[ringIdx].rOuter * TargetRadius * 0.5
                : ((Rings[ringIdx].rOuter + Rings[ringIdx + 1].rOuter) * 0.5) * TargetRadius;

            double ang  = Random.Shared.NextDouble() * Math.PI * 2;
            double cosA = Math.Cos(ang), sinA = Math.Sin(ang);
            double startX = LocalCenter + cosA * ringMid;
            double startY = LocalCenter + sinA * ringMid;

            double speed = 240 + Random.Shared.NextDouble() * 360;
            _shardVx[i] = cosA * speed;
            _shardVy[i] = sinA * speed - 80;          // slight upward bias
            _shardOmega[i] = (Random.Shared.NextDouble() - 0.5) * 1400;

            double size = 6 + Random.Shared.NextDouble() * 7;
            _shards[i].Width  = size;
            _shards[i].Height = size;
            _shardRot[i].CenterX = size / 2;
            _shardRot[i].CenterY = size / 2;

            var brush = new SolidColorBrush(Rings[ringIdx].color);
            brush.Freeze();
            _shards[i].Fill = brush;

            _shardX[i] = startX;
            _shardY[i] = startY;
            _shardTrans[i].X = startX - size / 2;
            _shardTrans[i].Y = startY - size / 2;
            _shardRot[i].Angle = Random.Shared.NextDouble() * 360;
            _shards[i].Opacity = 1.0;

            double life = 0.55 + Random.Shared.NextDouble() * 0.40;
            _shardLife[i] = life;
            _shardMax[i]  = life;
        }

        EnsureRenderLoop();
    }

    private void RepositionWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            int x = (int)((_centerX - LocalCenter) * _pixelsPerDip);
            int y = (int)((_centerY - LocalCenter) * _pixelsPerDip);
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
        }
        else
        {
            Left = _centerX - LocalCenter;
            Top  = _centerY - LocalCenter;
        }
    }

    private void EnsureRenderLoop()
    {
        if (_renderHooked) return;
        _renderHooked = true;
        _lastTick = DateTime.UtcNow;
        CompositionTarget.Rendering += OnTick;
    }

    private void StopRenderLoop()
    {
        if (!_renderHooked) return;
        _renderHooked = false;
        CompositionTarget.Rendering -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min((now - _lastTick).TotalSeconds, 0.033);
        _lastTick = now;

        const double ShardGravity = 1500;
        const double ShardDrag    = 0.6;

        bool any = false;
        for (int i = 0; i < ShardCount; i++)
        {
            if (_shardLife[i] <= 0) continue;
            _shardLife[i] -= dt;
            if (_shardLife[i] <= 0)
            {
                _shards[i].Opacity = 0;
                continue;
            }
            any = true;
            _shardVy[i] += ShardGravity * dt;
            _shardVx[i] *= 1.0 - ShardDrag * dt;
            _shardVy[i] *= 1.0 - ShardDrag * 0.4 * dt;
            _shardX[i]  += _shardVx[i] * dt;
            _shardY[i]  += _shardVy[i] * dt;
            _shardRot[i].Angle += _shardOmega[i] * dt;

            double half = _shards[i].Width / 2;
            _shardTrans[i].X = _shardX[i] - half;
            _shardTrans[i].Y = _shardY[i] - half;

            double t = _shardLife[i] / _shardMax[i];
            _shards[i].Opacity = Math.Min(1.0, t * 1.6);
        }

        if (!any)
        {
            _exploding = false;
            StopRenderLoop();
            Hide();
        }
    }
}

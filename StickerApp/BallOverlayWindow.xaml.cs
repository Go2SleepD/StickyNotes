using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using WpfColor      = System.Windows.Media.Color;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfPoint      = System.Windows.Point;

namespace StickerApp;

public partial class BallOverlayWindow : Window
{
    private static BallOverlayWindow? _instance;

    private const double BallR           = 18;
    private const double Gravity         = 4200;
    private const double BoxSz           = 72;
    private const double DrawerW         = 780;  // 3 × default sticker width
    private const double DrawerH         = 520;  // 2 × default sticker height
    private const double ProximityRadius = 260;  // open drawer when cursor is within a sticker-width of the box
    private const double BtnW            = 72;
    private const double BtnH            = 28;
    private const int    WsExTransparent = 0x00000020;

    private readonly List<BallState> _balls = [];
    private DateTime _lastTick = DateTime.UtcNow;

    private double _leftWall, _rightWall;
    private double _boxCx, _boxCy;
    private double _pixelsPerDip = 1.0;

    private FrameworkElement? _boxVisual;
    private ScaleTransform?   _boxScale;
    private TextBlock?        _totalLabel;
    private ScaleTransform?   _labelScale;
    private bool              _boxScheduled;
    private DispatcherTimer?  _boxTimer;
    private WpfColor          _boxAccentColor;

    // Hover + buttons + drawer
    private DispatcherTimer?  _hoverTimer;
    private Border?           _clearBtn;
    private Border?           _openBtn;
    private bool              _isHovering;
    private bool              _buttonsShown;
    private bool              _drawerOpen;
    private Border?           _drawerVisual;

    // Card drag-out
    private bool              _isDraggingCard;
    private FrameworkElement? _draggingCard;
    private StickerData?      _draggingData;
    private Canvas?           _draggingOrigParent;
    private double            _draggingOrigLeft;
    private double            _draggingOrigTop;
    private int               _draggingOrigZ;
    private WpfPoint          _draggingGrabOffset;

    private static int  _allTimeTotal;
    private static bool _totalLoaded;
    private static readonly List<StickerData> _archivedData = [];

    // ── Public API ────────────────────────────────────────────────────────
    public static void DropBall(WpfPoint screenDip, WpfColor accent, StickerData data)
    {
        _instance = _instance is { IsVisible: true } ? _instance : Create();
        _instance._boxAccentColor = accent;
        _archivedData.Add(data);
        var s = AppSettings.Load();
        s.ArchivedStickers.Add(data);
        s.Save();
        _instance.AddBall(screenDip);
    }

    public static void Burst(WpfPoint screenDip, WpfColor accent)
    {
        _instance = _instance is { IsVisible: true } ? _instance : Create();
        _instance.AddConfetti(screenDip, accent);
    }

    private static BallOverlayWindow Create()
    {
        if (!_totalLoaded)
        {
            var s = AppSettings.Load();
            _allTimeTotal = s.TotalAbsorbed;
            _archivedData.Clear();
            _archivedData.AddRange(s.ArchivedStickers);
            _totalLoaded = true;
        }
        var w = new BallOverlayWindow();
        w.Show();
        return w;
    }

    // ── Init ──────────────────────────────────────────────────────────────
    public BallOverlayWindow()
    {
        InitializeComponent();

        Left   = SystemParameters.VirtualScreenLeft;
        Top    = SystemParameters.VirtualScreenTop;
        Width  = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _leftWall  = 0;
        _rightWall = SystemParameters.VirtualScreenWidth;

        _boxCx = SystemParameters.WorkArea.Left + SystemParameters.WorkArea.Width  / 2.0
                 - SystemParameters.VirtualScreenLeft;
        _boxCy = SystemParameters.WorkArea.Bottom - SystemParameters.VirtualScreenTop;

        SourceInitialized += (_, _) =>
        {
            SetClickThrough(true);
            var src = PresentationSource.FromVisual(this);
            _pixelsPerDip = src?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
        };
        CompositionTarget.Rendering += OnTick;
    }

    private void SetClickThrough(bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex   = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        if (clickThrough)
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
                ex | WsExTransparent | Win32.WS_EX_NOACTIVATE);
        else
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
                (ex & ~WsExTransparent) | Win32.WS_EX_NOACTIVATE);
    }

    // ── Ball creation ─────────────────────────────────────────────────────
    private static readonly string[] BlobPaths =
    [
        "M 21,2 C 29,0 37,7 35,16 C 34,24 31,33 22,36 C 14,38 5,34 2,26 C -1,18 3,7 10,3 C 13,1 17,1 21,2 Z",
        "M 19,3 C 27,1 36,8 36,18 C 36,27 29,37 19,37 C 10,37 2,31 0,22 C -2,13 4,4 13,2 C 15,1 17,3 19,3 Z",
        "M 22,2 C 31,1 38,9 37,19 C 36,28 27,37 17,36 C 8,35 1,28 1,19 C 1,9 7,2 16,1 C 18,0 20,2 22,2 Z",
        "M 18,2 C 26,0 35,7 36,17 C 37,26 30,37 19,38 C 9,38 0,31 0,20 C 0,11 5,3 13,1 C 15,0 16,2 18,2 Z",
        "M 20,3 C 28,2 36,10 35,19 C 34,28 26,36 16,36 C 7,36 0,29 0,19 C 0,10 5,3 14,2 C 16,2 18,3 20,3 Z",
    ];

    private static DrawingBrush MakeCrumpledBrush()
    {
        double cx = 0.38 + Random.Shared.NextDouble() * 0.18;
        double cy = 0.32 + Random.Shared.NextDouble() * 0.18;

        var gradient = new RadialGradientBrush(new GradientStopCollection
        {
            new(WpfColor.FromRgb(250, 245, 230), 0.0),
            new(WpfColor.FromRgb(222, 214, 195), 0.45),
            new(WpfColor.FromRgb(188, 179, 158), 0.78),
            new(WpfColor.FromRgb(152, 144, 122), 1.0),
        })
        {
            Center         = new WpfPoint(cx, cy),
            GradientOrigin = new WpfPoint(cx - 0.06, cy - 0.09),
            RadiusX        = 0.68,
            RadiusY        = 0.68,
            MappingMode    = BrushMappingMode.RelativeToBoundingBox,
        };

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(gradient, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));

        int creases = 2 + Random.Shared.Next(2);
        for (int i = 0; i < creases; i++)
        {
            byte   alpha = (byte)(28 + Random.Shared.Next(38));
            var    pen   = new System.Windows.Media.Pen(new SolidColorBrush(WpfColor.FromArgb(alpha, 65, 55, 35)),
                                   0.016 + Random.Shared.NextDouble() * 0.014);
            double angle = Random.Shared.NextDouble() * Math.PI;
            double x1    = 0.15 + Random.Shared.NextDouble() * 0.55;
            double y1    = 0.15 + Random.Shared.NextDouble() * 0.55;
            double len   = 0.45 + Random.Shared.NextDouble() * 0.35;
            group.Children.Add(new GeometryDrawing(null, pen,
                new LineGeometry(new WpfPoint(x1, y1),
                                 new WpfPoint(x1 + Math.Cos(angle) * len,
                                              y1 + Math.Sin(angle) * len))));
        }

        return new DrawingBrush(group)
        {
            ViewboxUnits  = BrushMappingMode.RelativeToBoundingBox,
            Viewbox       = new Rect(0, 0, 1, 1),
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport      = new Rect(0, 0, 1, 1),
            Stretch       = Stretch.Fill,
        };
    }

    private void AddBall(WpfPoint pos)
    {
        double canvasX = pos.X - Left;
        double canvasY = pos.Y - Top;

        var scale     = new ScaleTransform(0.1, 0.1) { CenterX = BallR, CenterY = BallR };
        var rotate    = new RotateTransform(0)        { CenterX = BallR, CenterY = BallR };
        var translate = new TranslateTransform(canvasX - BallR, canvasY - BallR);
        var group     = new TransformGroup();
        group.Children.Add(rotate);
        group.Children.Add(scale);
        group.Children.Add(translate);

        var ball = new Path
        {
            Data            = Geometry.Parse(BlobPaths[Random.Shared.Next(BlobPaths.Length)]),
            Fill            = MakeCrumpledBrush(),
            RenderTransform = group,
            CacheMode       = new BitmapCache { EnableClearType = false, SnapsToDevicePixels = false },
        };

        var trailBrush = new LinearGradientBrush(new GradientStopCollection
        {
            new(WpfColor.FromArgb(  0, 228, 215, 188), 0.0),
            new(WpfColor.FromArgb(160, 228, 215, 188), 1.0),
        }) { MappingMode = BrushMappingMode.Absolute };

        var trail = new Polyline
        {
            Stroke             = trailBrush,
            StrokeThickness    = 2.5,
            StrokeLineJoin     = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap   = PenLineCap.Round,
            IsHitTestVisible   = false,
        };
        trail.Points = new PointCollection();
        Root.Children.Add(trail);
        Root.Children.Add(ball);

        var popEase = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1 };
        var popAnim = new DoubleAnimation(0.1, 1.0, TimeSpan.FromMilliseconds(220))
            { EasingFunction = popEase };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, popAnim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, popAnim);

        double sign     = Random.Shared.Next(2) == 0 ? 1 : -1;
        double angleDeg = sign * (45 + Random.Shared.NextDouble() * 35);
        double angleRad = angleDeg * Math.PI / 180.0;
        double speed    = 120 + Random.Shared.NextDouble() * 100;
        double vx       = speed * Math.Sin(angleRad);
        double vy       = speed * Math.Cos(angleRad);
        double omega    = vx / BallR * (180.0 / Math.PI) * 1.5
                        + (Random.Shared.NextDouble() - 0.5) * 720;

        var state = new BallState
        {
            Visual     = ball,
            Scale      = scale,
            Rotate     = rotate,
            Translate  = translate,
            Trail      = trail,
            TrailBrush = trailBrush,
            X          = canvasX,
            Y          = canvasY,
            Vx         = 0,
            Vy         = 0,
            Omega      = 0,
            FloorY     = _boxCy,
            Frozen     = true,
        };
        _balls.Add(state);

        // Hold the crumpled ball still until the pop-in finishes, then release physics.
        var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            state.Vx     = vx;
            state.Vy     = vy;
            state.Omega  = omega;
            state.Frozen = false;
        };
        settle.Start();

        var absorb = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        absorb.Tick += (_, _) => { absorb.Stop(); TriggerAbsorb(state); };
        absorb.Start();
    }

    // ── Confetti creation ─────────────────────────────────────────────────
    private void AddConfetti(WpfPoint pos, WpfColor accent)
    {
        double cx = pos.X - Left;
        double cy = pos.Y - Top;

        var palette = new WpfColor[]
        {
            accent,
            Lerp(accent, WpfColor.FromRgb(255, 255, 255), 0.5f),
            WpfColor.FromRgb(255, 255, 255),
            WpfColor.FromRgb(255, 213, 80),
            WpfColor.FromRgb(120, 220, 175),
            WpfColor.FromRgb(200, 160, 255),
        };

        for (int i = 0; i < 9; i++)
        {
            double angleDeg = Random.Shared.NextDouble() * 360;
            double angleRad = angleDeg * Math.PI / 180.0;
            double dist     = 35 + Random.Shared.NextDouble() * 55;
            double w        = 4 + Random.Shared.NextDouble() * 5;
            double h        = 7 + Random.Shared.NextDouble() * 8;
            double lifetime = 0.35 + Random.Shared.NextDouble() * 0.3;
            var    color    = palette[Random.Shared.Next(palette.Length)];

            double startX = cx - w / 2;
            double startY = cy - h / 2;
            double endX   = startX + Math.Cos(angleRad) * dist;
            double endY   = startY + Math.Sin(angleRad) * dist;

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width  = w, Height = h,
                Fill   = new SolidColorBrush(color),
                RenderTransformOrigin = new WpfPoint(0.5, 0.5),
                RenderTransform = new RotateTransform(Random.Shared.NextDouble() * 360),
            };
            Canvas.SetLeft(rect, startX);
            Canvas.SetTop(rect,  startY);
            Root.Children.Add(rect);

            var dur  = TimeSpan.FromSeconds(lifetime);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            var animX = new DoubleAnimation(startX, endX, dur) { EasingFunction = ease };
            var animY = new DoubleAnimation(startY, endY, dur) { EasingFunction = ease };
            var animO = new DoubleAnimation(1.0, 0.0, dur);
            animO.Completed += (_, _) => Root.Children.Remove(rect);

            rect.BeginAnimation(Canvas.LeftProperty, animX);
            rect.BeginAnimation(Canvas.TopProperty,  animY);
            rect.BeginAnimation(OpacityProperty,     animO);
        }
    }

    private static WpfColor Lerp(WpfColor a, WpfColor b, float t) => WpfColor.FromArgb(
        (byte)(a.A + (b.A - a.A) * t),
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    // ── Physics loop ──────────────────────────────────────────────────────
    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min((now - _lastTick).TotalSeconds, 0.1);
        _lastTick = now;

        TickBalls(dt);
    }

    private void TickBalls(double dt)
    {
        foreach (var b in _balls)
        {
            if (b.Absorbed || b.Frozen) continue;
            b.Vy    += Gravity * dt;
            b.X     += b.Vx * dt;
            b.Y     += b.Vy * dt;
            b.Omega *= 1.0 - 1.2 * dt;
            b.Angle += b.Omega * dt;
            b.Rotate.Angle = b.Angle;
        }

        const double Restitution = 0.62;
        for (int i = 0; i < _balls.Count; i++)
        {
            var a = _balls[i];
            if (a.Absorbed || a.Frozen) continue;
            for (int j = i + 1; j < _balls.Count; j++)
            {
                var b = _balls[j];
                if (b.Absorbed || b.Frozen) continue;

                double dx   = b.X - a.X;
                double dy   = b.Y - a.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double minD = BallR * 2;
                if (dist >= minD || dist < 0.001) continue;

                double nx = dx / dist;
                double ny = dy / dist;

                double push = (minD - dist) * 0.5;
                a.X -= nx * push; a.Y -= ny * push;
                b.X += nx * push; b.Y += ny * push;

                double dvn = (b.Vx - a.Vx) * nx + (b.Vy - a.Vy) * ny;
                if (dvn >= 0) continue;

                double imp = -(1 + Restitution) * dvn * 0.5;
                a.Vx -= imp * nx; a.Vy -= imp * ny;
                b.Vx += imp * nx; b.Vy += imp * ny;
            }
        }

        foreach (var b in _balls)
        {
            if (b.Absorbed || b.Frozen) continue;

            if (b.X - BallR < _leftWall)  { b.X = _leftWall  + BallR; b.Vx =  Math.Abs(b.Vx) * 0.7; }
            if (b.X + BallR > _rightWall) { b.X = _rightWall - BallR; b.Vx = -Math.Abs(b.Vx) * 0.7; }

            if (b.Y + BallR >= b.FloorY)
            {
                b.Y  = b.FloorY - BallR;
                b.Vy = -b.Vy * 0.72;
                b.Vx *= 0.75;
                double rolling = b.Vx / BallR * (180.0 / Math.PI);
                b.Omega = b.Omega * 0.35 + rolling * 0.65;
            }

            b.Translate.X = b.X - BallR;
            b.Translate.Y = b.Y - BallR;
            UpdateTrail(b, b.X, b.Y);
        }
    }

    private static void UpdateTrail(BallState b, double cx, double cy)
    {
        var pts = b.Trail.Points;
        pts.Add(new WpfPoint(cx, cy));
        if (pts.Count > 26) pts.RemoveAt(0);
        if (pts.Count < 2) return;

        b.TrailBrush.StartPoint = pts[0];
        b.TrailBrush.EndPoint   = pts[pts.Count - 1];
    }

    // ── Box logic ─────────────────────────────────────────────────────────
    private void TriggerAbsorb(BallState b)
    {
        if (b.Absorbed) return;
        b.Absorbed = true;
        ResetBoxTimer();
        if (!_boxScheduled)
        {
            _boxScheduled = true;
            ShowBox();
        }
        AnimateBallIntoBox(b);
    }

    private void ResetBoxTimer()
    {
        _boxTimer?.Stop();
        _boxTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _boxTimer.Tick += (_, _) => OnBoxTimeout();
        _boxTimer.Start();
    }

    private void ShowBox()
    {
        _boxScale = new ScaleTransform(0, 0);

        var lightColor = Lerp(_boxAccentColor, WpfColor.FromRgb(255, 255, 255), 0.28f);
        var darkColor  = Lerp(_boxAccentColor, WpfColor.FromRgb(0, 0, 0), 0.22f);
        var boxBrush = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(lightColor,      0.0),
                new(_boxAccentColor, 0.55),
                new(darkColor,       1.0),
            }, 90);

        var inner = new Border
        {
            Margin       = new Thickness(6, 6, 6, 0),
            Background   = new SolidColorBrush(WpfColor.FromArgb(55, 255, 255, 255)),
            CornerRadius = new CornerRadius(8),
        };

        var slot = new Border
        {
            Width               = 26,
            Height              = 5,
            Background          = new SolidColorBrush(WpfColor.FromArgb(160, 0, 0, 0)),
            CornerRadius        = new CornerRadius(2.5),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment   = System.Windows.VerticalAlignment.Top,
            Margin              = new Thickness(0, 10, 0, 0),
        };

        var grid = new System.Windows.Controls.Grid();
        grid.Children.Add(inner);
        grid.Children.Add(slot);

        var box = new Border
        {
            Width       = BoxSz,
            Height      = BoxSz,
            Background  = boxBrush,
            CornerRadius = new CornerRadius(14),
            Child       = grid,
            RenderTransformOrigin = new WpfPoint(0.5, 1.0),
            RenderTransform       = _boxScale,
            Opacity     = 0,
        };
        _boxVisual = box;

        Canvas.SetLeft(_boxVisual, _boxCx - BoxSz / 2);
        Canvas.SetTop(_boxVisual,  _boxCy - BoxSz);
        Root.Children.Add(_boxVisual);

        _labelScale = new ScaleTransform(0, 0);
        _totalLabel = new TextBlock
        {
            Text                = _allTimeTotal.ToString(),
            FontSize            = 30,
            FontWeight          = FontWeights.Bold,
            Foreground          = System.Windows.Media.Brushes.White,
            Width               = BoxSz,
            TextAlignment       = System.Windows.TextAlignment.Center,
            Opacity             = 0,
            RenderTransformOrigin = new WpfPoint(0.5, 0.5),
            RenderTransform     = _labelScale,
        };
        Canvas.SetLeft(_totalLabel, _boxCx - BoxSz / 2);
        Canvas.SetTop(_totalLabel,  _boxCy - BoxSz - 48);
        Root.Children.Add(_totalLabel);

        var ease = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 4 };
        _boxScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        _boxScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        _boxVisual.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        _labelScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        _labelScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        _totalLabel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));

        StartHoverTracking();
    }

    private void AnimateBallIntoBox(BallState b)
    {
        var dur  = TimeSpan.FromMilliseconds(420);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

        var animX  = new DoubleAnimation(b.Translate.X, _boxCx - BallR, dur) { EasingFunction = ease };
        var animY  = new DoubleAnimation(b.Translate.Y, _boxCy - BallR - 14, dur) { EasingFunction = ease };
        var fade   = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
            { BeginTime = TimeSpan.FromMilliseconds(250) };
        var shrink = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220))
            { BeginTime = TimeSpan.FromMilliseconds(230), EasingFunction = ease };

        animX.Completed += (_, _) => BumpCounter();
        fade.Completed  += (_, _) =>
        {
            Root.Children.Remove(b.Visual);
            Root.Children.Remove(b.Trail);
            _balls.Remove(b);
        };

        var flyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        int flyMs = 0;
        flyTimer.Tick += (_, _) =>
        {
            flyMs += 16;
            if (flyMs >= 250) { flyTimer.Stop(); return; }
            UpdateTrail(b, b.Translate.X + BallR, b.Translate.Y + BallR);
        };
        flyTimer.Start();

        b.Trail.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
                { BeginTime = TimeSpan.FromMilliseconds(200) });

        b.Translate.BeginAnimation(TranslateTransform.XProperty, animX);
        b.Translate.BeginAnimation(TranslateTransform.YProperty, animY);
        b.Visual.BeginAnimation(OpacityProperty, fade);
        b.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        b.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    private void BumpCounter()
    {
        _allTimeTotal++;

        _totalLabel!.Text = _allTimeTotal.ToString();
        var labelEase = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 };
        _labelScale!.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1.45, 1.0, TimeSpan.FromMilliseconds(340)) { EasingFunction = labelEase });
        _labelScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1.45, 1.0, TimeSpan.FromMilliseconds(340)) { EasingFunction = labelEase });

        var boxEase = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 5 };
        _boxScale!.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1.13, 1.0, TimeSpan.FromMilliseconds(430)) { EasingFunction = boxEase });
        _boxScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.86, 1.0, TimeSpan.FromMilliseconds(430)) { EasingFunction = boxEase });

        var s = AppSettings.Load();
        s.TotalAbsorbed = _allTimeTotal;
        s.Save();
    }

    private void OnBoxTimeout()
    {
        if (_isHovering) return;
        _boxTimer?.Stop();
        DismissBox();
    }

    private void DismissBox()
    {
        StopHoverTracking();
        _isHovering = false;
        SetClickThrough(true);

        if (_buttonsShown)
        {
            Root.Children.Remove(_clearBtn!);
            Root.Children.Remove(_openBtn!);
            _clearBtn     = null;
            _openBtn      = null;
            _buttonsShown = false;
        }
        CloseDrawerImmediate();

        var visual = _boxVisual!;
        var label  = _totalLabel!;
        _boxVisual = null; _totalLabel = null; _labelScale = null; _boxScale = null;
        _boxScheduled = false;

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350));
        fade.Completed += (_, _) =>
        {
            Root.Children.Remove(visual);
            Root.Children.Remove(label);
            CompositionTarget.Rendering -= OnTick;
            _instance = null;
            Close();
        };
        visual.BeginAnimation(OpacityProperty, fade);
        label.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350)));
    }

    // ── Hover tracking ────────────────────────────────────────────────────
    private void StartHoverTracking()
    {
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _hoverTimer.Tick += OnHoverTick;
        _hoverTimer.Start();
    }

    private void StopHoverTracking()
    {
        _hoverTimer?.Stop();
        _hoverTimer = null;
    }

    private void OnHoverTick(object? sender, EventArgs e)
    {
        Win32.GetCursorPos(out var pt);
        double cx = pt.X / _pixelsPerDip;
        double cy = pt.Y / _pixelsPerDip;

        double boxScreenLeft = Left + _boxCx - BoxSz / 2;
        double boxScreenTop  = Top  + _boxCy - BoxSz;

        // Hover zone: box + side buttons + padding
        double zoneLeft   = boxScreenLeft - BtnW - 8;
        double zoneRight  = boxScreenLeft + BoxSz + BtnW + 8;
        double zoneTop    = boxScreenTop  - 8;
        double zoneBottom = boxScreenTop  + BoxSz + 8;

        // Extend zone to cover the open drawer (it's wider than the box)
        if (_drawerOpen)
        {
            double drawerScreenLeft = Left + Canvas.GetLeft(_drawerVisual!);
            double drawerScreenTop  = Top  + Canvas.GetTop(_drawerVisual!);
            zoneTop   = drawerScreenTop - 8;
            zoneLeft  = Math.Min(zoneLeft,  drawerScreenLeft - 8);
            zoneRight = Math.Max(zoneRight, drawerScreenLeft + DrawerW + 8);
        }

        bool over = cx >= zoneLeft && cx <= zoneRight && cy >= zoneTop && cy <= zoneBottom;

        // While dragging a card out, keep box "active" regardless of cursor.
        if (_isDraggingCard) over = true;

        // Proximity to the box rectangle: open drawer when cursor is within a sticker-width.
        double dx = Math.Max(0, Math.Max(boxScreenLeft - cx, cx - (boxScreenLeft + BoxSz)));
        double dy = Math.Max(0, Math.Max(boxScreenTop  - cy, cy - (boxScreenTop  + BoxSz)));
        double distToBox = Math.Sqrt(dx * dx + dy * dy);
        bool nearBox = distToBox <= ProximityRadius;

        if (nearBox) over = true;

        if (over && !_isHovering)
        {
            _isHovering = true;
            _boxTimer?.Stop();
            SetClickThrough(false);
            ShowButtons();
        }
        else if (!over && _isHovering)
        {
            _isHovering = false;
            SetClickThrough(true);
            HideButtons();
            if (_drawerOpen) CloseDrawer();
            ResetBoxTimer();
        }

        if (nearBox && !_drawerOpen)
        {
            ShowDrawer();
            if (_openBtn is { } ob) ((TextBlock)ob.Child).Text = "✕ Закрыть";
        }
    }

    // ── Buttons ───────────────────────────────────────────────────────────
    private void ShowButtons()
    {
        double boxLeft = _boxCx - BoxSz / 2;
        double boxMidY = _boxCy - BoxSz / 2;

        _clearBtn = MakeActionButton("✕ Очистить", OnClearClick);
        Canvas.SetLeft(_clearBtn, boxLeft - BtnW - 6);
        Canvas.SetTop(_clearBtn,  boxMidY - BtnH / 2);
        Root.Children.Add(_clearBtn);

        _openBtn = MakeActionButton("▤ Открыть", OnOpenClick);
        Canvas.SetLeft(_openBtn, boxLeft + BoxSz + 6);
        Canvas.SetTop(_openBtn,  boxMidY - BtnH / 2);
        Root.Children.Add(_openBtn);

        _buttonsShown = true;
        FadeIn(_clearBtn);
        FadeIn(_openBtn);
    }

    private void HideButtons()
    {
        _buttonsShown = false;
        var cb = _clearBtn!;
        var ob = _openBtn!;
        _clearBtn = null;
        _openBtn  = null;
        FadeOutAndRemove(cb);
        FadeOutAndRemove(ob);
    }

    private Border MakeActionButton(string text, Action onClick)
    {
        var bg      = WpfColor.FromArgb(210, 24, 24, 34);
        var bgHover = WpfColor.FromArgb(235, 52, 52, 72);

        var label = new TextBlock
        {
            Text                = text,
            FontSize            = 11,
            Foreground          = System.Windows.Media.Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment   = System.Windows.VerticalAlignment.Center,
        };

        var btn = new Border
        {
            Width               = BtnW,
            Height              = BtnH,
            Background          = new SolidColorBrush(bg),
            CornerRadius        = new CornerRadius(14),
            Child               = label,
            Cursor              = System.Windows.Input.Cursors.Hand,
            Opacity             = 0,
        };

        btn.MouseEnter        += (_, _) => btn.Background = new SolidColorBrush(bgHover);
        btn.MouseLeave        += (_, _) => btn.Background = new SolidColorBrush(bg);
        btn.MouseLeftButtonUp += (_, _) => onClick();
        return btn;
    }

    private static void FadeIn(UIElement el) =>
        el.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));

    private void FadeOutAndRemove(UIElement el)
    {
        var fade = new DoubleAnimation(el.Opacity, 0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) => Root.Children.Remove(el);
        el.BeginAnimation(OpacityProperty, fade);
    }

    // ── Button actions ────────────────────────────────────────────────────
    private void OnClearClick()
    {
        _archivedData.Clear();
        _allTimeTotal = 0;
        var s = AppSettings.Load();
        s.ArchivedStickers.Clear();
        s.TotalAbsorbed = 0;
        s.Save();
        StopHoverTracking();
        _isHovering = false;
        DismissBox();
    }

    private void OnOpenClick()
    {
        if (_drawerOpen)
        {
            CloseDrawer();
            ((TextBlock)_openBtn!.Child).Text = "▤ Открыть";
        }
        else
        {
            ShowDrawer();
            ((TextBlock)_openBtn!.Child).Text = "✕ Закрыть";
        }
    }

    // ── Drawer ────────────────────────────────────────────────────────────
    private void ShowDrawer()
    {
        _drawerOpen = true;

        double drawerLeft   = _boxCx - DrawerW / 2;
        double drawerFinalY = _boxCy - BoxSz - DrawerH - 4;

        var contentCanvas = new Canvas { Width = DrawerW, Height = DrawerH };
        BuildCardContent(contentCanvas);

        var drawer = new Border
        {
            Width        = DrawerW,
            Height       = DrawerH,
            Background   = new SolidColorBrush(WpfColor.FromArgb(225, 18, 18, 28)),
            CornerRadius = new CornerRadius(16, 16, 0, 0),
            ClipToBounds = true,
            Child        = contentCanvas,
        };
        _drawerVisual = drawer;

        Canvas.SetLeft(_drawerVisual, drawerLeft);
        Canvas.SetTop(_drawerVisual,  _boxCy - BoxSz);

        // Insert below box so box renders on top of drawer
        int boxIdx = Root.Children.IndexOf(_boxVisual!);
        if (boxIdx >= 0)
            Root.Children.Insert(boxIdx, _drawerVisual);
        else
            Root.Children.Add(_drawerVisual);

        var slideEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        _drawerVisual.BeginAnimation(Canvas.TopProperty,
            new DoubleAnimation(_boxCy - BoxSz, drawerFinalY, TimeSpan.FromMilliseconds(400))
                { EasingFunction = slideEase });
        _drawerVisual.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    private void CloseDrawer()
    {
        if (!_drawerOpen) return;
        _drawerOpen = false;

        var visual = _drawerVisual!;
        _drawerVisual = null;

        double startY = Canvas.GetTop(visual);
        var slideEase = new CubicEase { EasingMode = EasingMode.EaseIn };
        var slide = new DoubleAnimation(startY, _boxCy - BoxSz + 16,
            TimeSpan.FromMilliseconds(260)) { EasingFunction = slideEase };
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => Root.Children.Remove(visual);
        visual.BeginAnimation(Canvas.TopProperty, slide);
        visual.BeginAnimation(OpacityProperty, fade);
    }

    private void CloseDrawerImmediate()
    {
        if (!_drawerOpen) return;
        Root.Children.Remove(_drawerVisual!);
        _drawerVisual = null;
        _drawerOpen   = false;
    }

    private void BuildCardContent(Canvas canvas)
    {
        if (_archivedData.Count == 0)
        {
            var empty = new TextBlock
            {
                Text       = "Нет сохранённых стикеров",
                FontSize   = 15,
                Foreground = new SolidColorBrush(WpfColor.FromArgb(100, 255, 255, 255)),
            };
            Canvas.SetLeft(empty, DrawerW / 2 - 130);
            Canvas.SetTop(empty,  DrawerH / 2 - 10);
            canvas.Children.Add(empty);
            return;
        }

        const double ColW     = DrawerW / 3;        // 260
        const double CardW    = 228;
        const double CardH    = 158;
        const double ColPad   = (ColW - CardW) / 2; // ~16
        const double StackOff = 22;
        const double TopPad   = 20;

        for (int col = 0; col < 3; col++)
        {
            var colCards = _archivedData
                .Where((_, i) => i % 3 == col)
                .ToList();

            // Oldest first → behind; newest last → in front
            for (int j = 0; j < colCards.Count; j++)
            {
                double cardY = TopPad + (colCards.Count - 1 - j) * StackOff;
                double cardX = col * ColW + ColPad;
                var data = colCards[j];
                var card = MakeStickerCard(data, CardW, CardH);
                Canvas.SetLeft(card, cardX);
                Canvas.SetTop(card,  cardY);
                Canvas.SetZIndex(card, j);
                int cardZ = j;
                card.Cursor = System.Windows.Input.Cursors.Hand;
                card.MouseLeftButtonDown += (_, e) => OnCardMouseDown(card, data, cardZ, e);
                canvas.Children.Add(card);
            }
        }
    }

    // ── Card drag-out ─────────────────────────────────────────────────────
    private void OnCardMouseDown(FrameworkElement card, StickerData data, int origZ, MouseButtonEventArgs e)
    {
        if (_isDraggingCard) return;

        var origParent = (Canvas)card.Parent;

        _isDraggingCard     = true;
        _draggingCard       = card;
        _draggingData       = data;
        _draggingOrigParent = origParent;
        _draggingOrigLeft   = Canvas.GetLeft(card);
        _draggingOrigTop    = Canvas.GetTop(card);
        _draggingOrigZ      = origZ;
        _draggingGrabOffset = e.GetPosition(card);

        // Re-parent to Root so the card can move outside the drawer's clip bounds.
        var rootPos = card.TranslatePoint(new WpfPoint(0, 0), Root);
        origParent.Children.Remove(card);
        Canvas.SetLeft(card, rootPos.X);
        Canvas.SetTop(card,  rootPos.Y);
        Canvas.SetZIndex(card, 9999);
        Root.Children.Add(card);

        card.MouseMove         += OnCardMouseMove;
        card.MouseLeftButtonUp += OnCardMouseUp;
        card.LostMouseCapture  += OnCardLostCapture;
        Mouse.Capture(card);

        e.Handled = true;
    }

    private void OnCardMouseMove(object? sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingCard) return;
        var pos = e.GetPosition(Root);
        Canvas.SetLeft(_draggingCard!, pos.X - _draggingGrabOffset.X);
        Canvas.SetTop(_draggingCard!,  pos.Y - _draggingGrabOffset.Y);
    }

    private void OnCardMouseUp(object? sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingCard) return;

        var card = _draggingCard!;
        var data = _draggingData!;

        card.MouseMove         -= OnCardMouseMove;
        card.MouseLeftButtonUp -= OnCardMouseUp;
        card.LostMouseCapture  -= OnCardLostCapture;
        Mouse.Capture(null);

        bool dropInsideDrawer = false;
        if (_drawerOpen)
        {
            var dv = _drawerVisual!;
            double dl = Canvas.GetLeft(dv);
            double dt = Canvas.GetTop(dv);
            double cx = Canvas.GetLeft(card) + card.Width  / 2;
            double cy = Canvas.GetTop(card)  + card.Height / 2;
            dropInsideDrawer = cx >= dl && cx <= dl + DrawerW
                            && cy >= dt && cy <= dt + DrawerH;
        }

        if (dropInsideDrawer)
            SnapCardBack(card);
        else
            RestoreStickerFromCard(card, data);

        _isDraggingCard     = false;
        _draggingCard       = null;
        _draggingData       = null;
        _draggingOrigParent = null;
    }

    private void OnCardLostCapture(object? sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingCard) return;
        // External capture loss: snap back as a safe default.
        var card = _draggingCard!;
        card.MouseMove         -= OnCardMouseMove;
        card.MouseLeftButtonUp -= OnCardMouseUp;
        card.LostMouseCapture  -= OnCardLostCapture;
        SnapCardBack(card);

        _isDraggingCard     = false;
        _draggingCard       = null;
        _draggingData       = null;
        _draggingOrigParent = null;
    }

    private void SnapCardBack(FrameworkElement card)
    {
        var parent = _draggingOrigParent!;
        double origLeft = _draggingOrigLeft;
        double origTop  = _draggingOrigTop;
        int    origZ    = _draggingOrigZ;

        double curLeft = Canvas.GetLeft(card);
        double curTop  = Canvas.GetTop(card);

        // Animate back in Root coords, then re-parent at the end.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        double parentLeft = Canvas.GetLeft(_drawerVisual!);
        double parentTop  = Canvas.GetTop(_drawerVisual!);
        double targetLeft = parentLeft + origLeft;
        double targetTop  = parentTop  + origTop;

        var animX = new DoubleAnimation(curLeft, targetLeft, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        var animY = new DoubleAnimation(curTop,  targetTop,  TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        animX.Completed += (_, _) =>
        {
            card.BeginAnimation(Canvas.LeftProperty, null);
            card.BeginAnimation(Canvas.TopProperty,  null);
            Root.Children.Remove(card);
            Canvas.SetLeft(card, origLeft);
            Canvas.SetTop(card,  origTop);
            Canvas.SetZIndex(card, origZ);
            parent.Children.Add(card);
        };
        card.BeginAnimation(Canvas.LeftProperty, animX);
        card.BeginAnimation(Canvas.TopProperty,  animY);
    }

    private void RestoreStickerFromCard(FrameworkElement card, StickerData data)
    {
        // Map card center in Root → screen DIP coords, then place sticker centered there.
        double cardRootX = Canvas.GetLeft(card);
        double cardRootY = Canvas.GetTop(card);
        double cardCx    = cardRootX + card.Width  / 2;
        double cardCy    = cardRootY + card.Height / 2;

        double w = data.Width  > 0 ? data.Width  : 260;
        double h = data.Height > 0 ? data.Height : 260;
        data.X = Left + cardCx - w / 2;
        data.Y = Top  + cardCy - h / 2;

        ((App)System.Windows.Application.Current).RestoreSticker(data);

        _archivedData.Remove(data);
        var settings = AppSettings.Load();
        settings.ArchivedStickers.RemoveAll(d => d.Id == data.Id);
        _allTimeTotal = Math.Max(0, _allTimeTotal - 1);
        settings.TotalAbsorbed = _allTimeTotal;
        settings.Save();

        _totalLabel!.Text = _allTimeTotal.ToString();
        var labelEase = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 };
        _labelScale!.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.7, 1.0, TimeSpan.FromMilliseconds(340)) { EasingFunction = labelEase });
        _labelScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.7, 1.0, TimeSpan.FromMilliseconds(340)) { EasingFunction = labelEase });

        var fade = new DoubleAnimation(card.Opacity, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Root.Children.Remove(card);
        card.BeginAnimation(OpacityProperty, fade);
    }

    // Outline-color wheel mirrors StickerWindow._outlineColors / _outlineColorNames.
    private static readonly Dictionary<string, WpfColor> _outlineWheel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#E53935"] = WpfColor.FromRgb(0xE5, 0x39, 0x35),
        ["#FFD835"] = WpfColor.FromRgb(0xFF, 0xD8, 0x35),
        ["#43A047"] = WpfColor.FromRgb(0x43, 0xA0, 0x47),
        ["#1E88E5"] = WpfColor.FromRgb(0x1E, 0x88, 0xE5),
    };

    private static Border MakeStickerCard(StickerData data, double cardW, double cardH)
    {
        // Render the sticker at its natural size, then scale-fit into the (cardW, cardH) slot.
        var visual = BuildStickerVisual(data);
        var box = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Width   = cardW,
            Height  = cardH,
            Child   = visual,
        };
        return new Border { Width = cardW, Height = cardH, Child = box };
    }

    private static FrameworkElement BuildStickerVisual(StickerData data)
    {
        double w = data.Width  > 0 ? data.Width  : 260;
        double h = data.Height > 0 ? data.Height : 260;

        var surfaceBrush  = new SolidColorBrush(WpfColor.FromRgb(0x1E, 0x1E, 0x24));
        var borderBrush   = new SolidColorBrush(WpfColor.FromRgb(0x2A, 0x2A, 0x35));
        var subtextBrush  = new SolidColorBrush(WpfColor.FromRgb(0x6B, 0x72, 0x80));
        var textBrush     = new SolidColorBrush(WpfColor.FromRgb(0xE2, 0xE2, 0xE8));
        var doneTextColor = WpfColor.FromRgb(0x6B, 0x72, 0x80);

        var accent = WpfColor.FromRgb(0x5E, 0x6A, 0xD2);
        try { accent = (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(data.AccentColor)!; }
        catch { }

        WpfColor outlineColor = WpfColor.FromArgb(0, 0, 0, 0);
        if (data.OutlineColor is { } oc && _outlineWheel.TryGetValue(oc, out var ocVal))
            outlineColor = ocVal;
        var hasOutline = outlineColor.A > 0;
        var doneMarkColor = hasOutline ? outlineColor : accent;
        var doneMarkBrush = new SolidColorBrush(doneMarkColor);

        // ── Inner drawer (tasks container) ──
        var drawerGrid = new Grid();

        var topShadow = new Border
        {
            Height = 14,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            IsHitTestVisible  = false,
            CornerRadius      = new CornerRadius(6, 6, 0, 0),
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(WpfColor.FromArgb(0x35, 0, 0, 0), 0),
                    new GradientStop(WpfColor.FromArgb(0x00, 0, 0, 0), 1),
                },
                new WpfPoint(0, 0), new WpfPoint(0, 1)),
        };
        drawerGrid.Children.Add(topShadow);

        var pullHandle = new Border
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment   = System.Windows.VerticalAlignment.Top,
            Width  = 26,
            Height = 3,
            CornerRadius     = new CornerRadius(1.5),
            Margin           = new Thickness(0, 5, 0, 0),
            IsHitTestVisible = false,
            Background = new SolidColorBrush(WpfColor.FromRgb(0x2A, 0x2A, 0x3C)),
        };
        drawerGrid.Children.Add(pullHandle);

        var taskStack = new StackPanel { Margin = new Thickness(5, 14, 5, 5) };
        foreach (var t in data.Tasks)
            taskStack.Children.Add(BuildTaskRow(
                t, surfaceBrush, borderBrush, subtextBrush, textBrush,
                doneMarkBrush, doneTextColor));
        drawerGrid.Children.Add(taskStack);

        var drawer = new Border
        {
            Margin       = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(7),
            Background   = new SolidColorBrush(WpfColor.FromRgb(0x0E, 0x0E, 0x16)),
            BorderBrush  = new SolidColorBrush(WpfColor.FromRgb(0x21, 0x21, 0x2F)),
            BorderThickness = new Thickness(1),
            Child = drawerGrid,
        };

        var contentPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 24) };
        contentPanel.Children.Add(drawer);

        // ── Folded corner ──
        var foldedCorner = new Path
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment   = System.Windows.VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 4, 4),
            Data   = Geometry.Parse("M0,13 L13,0 L13,13 Z"),
            IsHitTestVisible = false,
            Opacity = 0.75,
            Fill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(WpfColor.FromRgb(0x13, 0x13, 0x1A), 0.0),
                    new GradientStop(WpfColor.FromRgb(0x25, 0x25, 0x33), 1.0),
                },
                new WpfPoint(0, 1), new WpfPoint(1, 0)),
            Effect = new DropShadowEffect
            {
                Color       = WpfColor.FromRgb(0, 0, 0),
                Opacity     = 0.45,
                BlurRadius  = 4,
                ShadowDepth = 1.5,
                Direction   = 135,
            },
        };

        // ── Outline overlay ──
        var outlineOverlay = new Border
        {
            CornerRadius     = new CornerRadius(9),
            Background       = System.Windows.Media.Brushes.Transparent,
            BorderThickness  = new Thickness(2),
            BorderBrush      = new SolidColorBrush(outlineColor),
            IsHitTestVisible = false,
        };

        var inner = new Grid { Background = System.Windows.Media.Brushes.Transparent };
        inner.Children.Add(contentPanel);
        inner.Children.Add(foldedCorner);
        inner.Children.Add(outlineOverlay);

        return new Border
        {
            Width  = w,
            Height = h,
            Background      = surfaceBrush,
            BorderBrush     = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(10),
            ClipToBounds    = true,
            Child = inner,
            Effect = new DropShadowEffect
            {
                Color       = WpfColor.FromRgb(0, 0, 0),
                Opacity     = 0.50,
                BlurRadius  = 12,
                ShadowDepth = 5,
                Direction   = 300,
            },
        };
    }

    private static FrameworkElement BuildTaskRow(
        TaskItem t,
        System.Windows.Media.Brush surface,
        System.Windows.Media.Brush border,
        System.Windows.Media.Brush subtext,
        System.Windows.Media.Brush text,
        SolidColorBrush doneMark,
        WpfColor doneTextColor)
    {
        var row = new Grid { MinHeight = 26, Margin = new Thickness(6, 2, 2, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });

        var checkBox = new Border
        {
            Width  = 14,
            Height = 14,
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1.5),
            BorderBrush     = t.Done ? doneMark : subtext,
            Background      = System.Windows.Media.Brushes.Transparent,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        if (t.Done)
        {
            checkBox.Child = new Path
            {
                Data = Geometry.Parse("M2,7 L5,10 L12,3"),
                Stroke             = doneMark,
                StrokeThickness    = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap   = PenLineCap.Round,
            };
        }
        Grid.SetColumn(checkBox, 0);
        row.Children.Add(checkBox);

        var textGrid = new Grid();
        Grid.SetColumn(textGrid, 1);

        var textForeground = t.Done
            ? new SolidColorBrush(doneTextColor)
            : text;

        var taskText = new TextBlock
        {
            Text         = t.Text,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            FontSize     = 12.5,
            FontFamily   = new WpfFontFamily("Segoe UI Variable, Segoe UI, sans-serif"),
            Margin       = new Thickness(5, 0, 3, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground   = textForeground,
        };
        textGrid.Children.Add(taskText);

        if (t.Done)
        {
            var strike = new System.Windows.Shapes.Rectangle
            {
                Height              = 1.5,
                VerticalAlignment   = System.Windows.VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                Margin              = new Thickness(5, 0, 3, 0),
                Fill                = doneMark,
                IsHitTestVisible    = false,
            };
            textGrid.Children.Add(strike);
        }

        row.Children.Add(textGrid);

        return new Border
        {
            Margin          = new Thickness(0, 2, 0, 2),
            CornerRadius    = new CornerRadius(5),
            Background      = surface,
            BorderBrush     = border,
            BorderThickness = new Thickness(1),
            Child           = row,
        };
    }

    // ── Data ──────────────────────────────────────────────────────────────
    private class BallState
    {
        public required Path             Visual;
        public required ScaleTransform   Scale;
        public required RotateTransform  Rotate;
        public required TranslateTransform Translate;
        public required Polyline             Trail;
        public required LinearGradientBrush  TrailBrush;
        public double X, Y, Vx, Vy, FloorY;
        public double Omega, Angle;
        public bool   Absorbed;
        public bool   Frozen;
    }
}

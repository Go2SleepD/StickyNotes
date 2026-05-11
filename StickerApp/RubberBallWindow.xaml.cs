using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace StickerApp;

public partial class RubberBallWindow : Window
{
    private static RubberBallWindow? _instance;

    private const double BallR           = 12;          // 24px diameter ≈ 1/3 of BoxSz=72
    private const double Gravity         = 4200;
    private const double WallRestitution = 0.82;
    private const double FloorRestitution= 0.78;
    private const double FloorFriction   = 0.78;
    private const double AirDrag         = 0.025;
    private const double GrabRadius      = BallR + 10;
    private const double SleepSpeed      = 14;
    private const double SleepFloorEps   = 1.5;
    private const double SleepDelay      = 0.35;
    private const double FloorSnapSpeed  = 110;   // |vy| below this on floor → snap to rest, no effects
    private const double WallSnapSpeed   = 60;    // |v⊥| below this on wall  → snap, no effects
    private const double MinFloorVx      = 10;    // |vx| below this on floor → zero out
    private const double ParticleSpeed   = 700;   // impact below this → no particles
    private const double WindowSize      = 320;   // small window that follows the ball — UpdateLayeredWindow cost is per-pixel
    private const double LocalCenter     = WindowSize / 2;

    private const int WsExTransparent = 0x00000020;

    // Walls in screen DIP coords (independent of window position)
    private double _leftWall, _rightWall, _topWall, _floorY;
    private double _pixelsPerDip = 1.0;
    private IntPtr _hwnd;

    // Ball visuals
    private Ellipse            _ball       = null!;
    private TranslateTransform _trans      = null!;
    private ScaleTransform     _scale      = null!;
    private RotateTransform    _rotate     = null!;
    private Ellipse            _shadow     = null!;
    private TranslateTransform _shadowTrans= null!;

    // Trail
    private Polyline           _trail       = null!;
    private PointCollection    _trailPoints = null!;
    private LinearGradientBrush _trailBrush = null!;
    private readonly List<WpfPoint> _trailWorld = [];
    private const int MaxTrailPoints = 16;

    // Trajectory preview rendered in BallOverlayWindow

    // Per-tick bounce consolidation
    private double _tickFloorImpact;
    private double _tickWallImpact;
    private double _tickCeilImpact;

    // Particle pool (reused). Particle world position tracked separately so they
    // appear stationary in screen space while the window follows the ball.
    private const int ParticleCount = 16;
    private readonly Ellipse[]            _particles      = new Ellipse[ParticleCount];
    private readonly TranslateTransform[] _particleTrans  = new TranslateTransform[ParticleCount];
    private readonly ScaleTransform[]     _particleScale  = new ScaleTransform[ParticleCount];
    private readonly double[]             _particleX      = new double[ParticleCount];
    private readonly double[]             _particleY      = new double[ParticleCount];
    private readonly double[]             _particleVx     = new double[ParticleCount];
    private readonly double[]             _particleVy     = new double[ParticleCount];
    private readonly double[]             _particleLife   = new double[ParticleCount];
    private readonly double[]             _particleMaxLife= new double[ParticleCount];

    // Physics state — ball CENTER in screen DIP coords (world space)
    private double _x, _y, _vx, _vy;
    private double _omega, _angle;
    private double _idleAccum;
    private bool   _sleeping;
    private bool   _renderHooked;

    // Grab state
    private bool      _grabbing;
    private WpfPoint  _grabOffset;
    private double    _grabVx, _grabVy;

    // Proximity / clickthrough
    private DispatcherTimer _proximityTimer = null!;
    private bool            _clickThrough = false;

    // Time
    private DateTime _lastTick = DateTime.UtcNow;

    // Sound (round-robin pool, pre-loaded)
    private const int       BoingPoolSize = 4;
    private static SoundPlayer[] _boingPool = null!;
    private static int      _boingIdx;
    private DateTime        _lastBoing = DateTime.MinValue;

    // Target challenge: appears on grab at random screen point, hides if cursor approaches
    // or ball stops, explodes when struck.
    private TargetWindow _target = null!;
    private const double TargetCursorAvoidDist  = TargetWindow.TargetRadius + 80;
    private const double TargetMinSpawnDistance = 320;

    // ASCII dog companion — wakes on grab, chases the ball, carries it back home.
    private AsciiDogWindow _dog = null!;
    private bool _captured;

    // ── Public read-only state (consumed by AsciiDogWindow) ──────────────
    public double BallX        => _x;
    public double BallY        => _y;
    public double BallVx       => _vx;
    public double BallVy       => _vy;
    public double BallRadius   => BallR;
    public bool   BallGrabbed  => _grabbing;
    public bool   BallSleeping => _sleeping;
    public bool   BallCaptured => _captured;
    public double FloorY       => _floorY;
    public double LeftWall     => _leftWall;
    public double RightWall    => _rightWall;
    public double RestX        { get; private set; }
    public double RestY        { get; private set; }

    // Fired when the user grabs while the ball is captured by the dog —
    // signals the dog to drop and revert to watching.
    public event Action? CarryInterrupted;

    // ── Public API ───────────────────────────────────────────────────────
    public static void Spawn()
    {
        if (_instance is { IsVisible: true }) return;
        _instance = new RubberBallWindow();
        _instance.Show();
    }

    public static void Despawn()
    {
        BallOverlayWindow.HideTrajectory();
        _instance?.Close();
        _instance = null;
    }

    public static double DogRestX =>
        _instance?._dog?.RestX ?? (SystemParameters.WorkArea.Left + SystemParameters.WorkArea.Width / 2);

    public static double DogFloorY =>
        _instance?._dog?.FloorY ?? SystemParameters.WorkArea.Bottom;

    public static void NotifyDogOfMeat(double meatX) => _instance?._dog?.NotifyMeat(meatX);

    public static bool IsAlive => _instance is { IsVisible: true };

    // ── Init ─────────────────────────────────────────────────────────────
    public RubberBallWindow()
    {
        InitializeComponent();

        Width  = WindowSize;
        Height = WindowSize;

        // Walls in screen DIP coords (multi-monitor virtual screen + primary work-area floor)
        _leftWall  = SystemParameters.VirtualScreenLeft;
        _rightWall = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        _topWall   = SystemParameters.VirtualScreenTop;
        _floorY    = SystemParameters.WorkArea.Bottom;

        // Spawn at right edge of primary work area, on the floor
        RestX  = SystemParameters.WorkArea.Right - BallR;
        RestY  = _floorY - BallR;
        _x     = RestX;
        _y     = RestY;
        _vx    = 0;
        _vy    = 0;
        _omega = 0;

        Left = _x - LocalCenter;
        Top  = _y - LocalCenter;

        BuildBall();
        BuildParticles();
        EnsureBoingPool();

        _target = new TargetWindow();
        _dog    = new AsciiDogWindow(this);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough(true);
            var src = PresentationSource.FromVisual(this);
            _pixelsPerDip = src?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
        };

        Loaded += (_, _) =>
        {
            PopIn();
            StartProximityTimer();
        };

        Closed += (_, _) =>
        {
            StopRenderLoop();
            _proximityTimer.Stop();
            _target.Close();
            _dog.Close();
        };
    }

    // ── Visuals ──────────────────────────────────────────────────────────
    private void BuildBall()
    {
        var bodyBrush = new RadialGradientBrush(new GradientStopCollection
        {
            new(WpfColor.FromRgb(0xFF, 0xC2, 0x66), 0.0),
            new(WpfColor.FromRgb(0xFF, 0x7A, 0x35), 0.55),
            new(WpfColor.FromRgb(0xC2, 0x44, 0x12), 1.0),
        })
        {
            Center         = new WpfPoint(0.38, 0.34),
            GradientOrigin = new WpfPoint(0.30, 0.26),
            RadiusX        = 0.78,
            RadiusY        = 0.78,
            MappingMode    = BrushMappingMode.RelativeToBoundingBox,
        };
        bodyBrush.Freeze();

        _scale  = new ScaleTransform(0.1, 0.1)  { CenterX = BallR, CenterY = BallR };
        _rotate = new RotateTransform(0)        { CenterX = BallR, CenterY = BallR };
        // Ball is drawn at fixed window center; physics moves the WINDOW, not the ball.
        _trans  = new TranslateTransform(LocalCenter - BallR, LocalCenter - BallR);

        var group = new TransformGroup();
        group.Children.Add(_rotate);
        group.Children.Add(_scale);
        group.Children.Add(_trans);

        _ball = new Ellipse
        {
            Width  = BallR * 2,
            Height = BallR * 2,
            Fill   = bodyBrush,
            RenderTransform = group,
            // Rasterize once into a bitmap, GPU-transform thereafter. RenderAtScale 2.0
            // keeps the ball crisp even during 1.28x stretch on bounce.
            CacheMode = new BitmapCache { RenderAtScale = 2.0, SnapsToDevicePixels = false, EnableClearType = false },
            IsHitTestVisible = false,
        };

        var shadowBrush = new RadialGradientBrush(new GradientStopCollection
        {
            new(WpfColor.FromArgb(140, 0, 0, 0), 0.0),
            new(WpfColor.FromArgb( 70, 0, 0, 0), 0.55),
            new(WpfColor.FromArgb(  0, 0, 0, 0), 1.0),
        })
        {
            Center         = new WpfPoint(0.5, 0.5),
            GradientOrigin = new WpfPoint(0.5, 0.5),
            RadiusX        = 0.6, RadiusY = 0.6,
            MappingMode    = BrushMappingMode.RelativeToBoundingBox,
        };
        shadowBrush.Freeze();

        // Shadow is also static within the window; just sits below the ball.
        _shadowTrans = new TranslateTransform(LocalCenter - BallR, LocalCenter - BallR * 0.7 + 5);
        _shadow = new Ellipse
        {
            Width  = BallR * 2,
            Height = BallR * 1.4,
            Fill   = shadowBrush,
            RenderTransform  = _shadowTrans,
            CacheMode        = new BitmapCache { RenderAtScale = 1.0, SnapsToDevicePixels = false, EnableClearType = false },
            IsHitTestVisible = false,
        };

        Root.Children.Add(_shadow);
        BuildTrail();
        BuildTrajectory();
        Root.Children.Add(_ball);
    }

    private void BuildTrajectory()
    {
        // Trajectory is rendered in BallOverlayWindow (full screen, no clipping)
    }

    private void BuildTrail()
    {
        _trailBrush = new LinearGradientBrush(new GradientStopCollection
        {
            new(WpfColor.FromArgb(0, 0xFF, 0xC2, 0x66), 0.0),
            new(WpfColor.FromArgb(130, 0xFF, 0xC2, 0x66), 1.0),
        }) { MappingMode = BrushMappingMode.Absolute };

        _trail = new Polyline
        {
            Stroke             = _trailBrush,
            StrokeThickness    = 2.8,
            StrokeLineJoin     = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap   = PenLineCap.Round,
            IsHitTestVisible   = false,
            Opacity            = 0.0,
        };
        _trailPoints = new PointCollection();
        _trail.Points = _trailPoints;
        Root.Children.Add(_trail);
    }

    private void BuildParticles()
    {
        var particleBrush = new SolidColorBrush(WpfColor.FromRgb(0xFF, 0xC2, 0x66));
        particleBrush.Freeze();
        for (int i = 0; i < ParticleCount; i++)
        {
            var t = new TranslateTransform(-100, -100);
            var s = new ScaleTransform(1, 1) { CenterX = 2, CenterY = 2 };
            var g = new TransformGroup();
            g.Children.Add(s);
            g.Children.Add(t);

            var p = new Ellipse
            {
                Width  = 4,
                Height = 4,
                Fill   = particleBrush,
                Opacity = 0,
                RenderTransform = g,
                CacheMode = new BitmapCache { RenderAtScale = 1.5, SnapsToDevicePixels = false, EnableClearType = false },
                IsHitTestVisible = false,
            };
            _particles[i]     = p;
            _particleTrans[i] = t;
            _particleScale[i] = s;
            Root.Children.Add(p);
        }
    }

    private void PopIn()
    {
        var ease = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 4 };
        var pop  = new DoubleAnimation(0.1, 1.0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    // ── Click-through toggle ─────────────────────────────────────────────
    private void ApplyClickThrough(bool clickThrough)
    {
        if (_clickThrough == clickThrough) return;
        _clickThrough = clickThrough;
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        if (clickThrough)
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
                ex | WsExTransparent | Win32.WS_EX_NOACTIVATE);
        else
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
                (ex & ~WsExTransparent) | Win32.WS_EX_NOACTIVATE);
    }

    // ── Proximity check ──────────────────────────────────────────────────
    // Sleep-mode timer: only runs when render loop is OFF, polls cursor at 80ms to
    // wake the ball when user approaches. While the render loop is active, the
    // proximity check is folded into OnTick (no extra UI-thread interruption).
    private void StartProximityTimer()
    {
        _proximityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _proximityTimer.Tick += (_, _) => CheckProximity();
        _proximityTimer.Start();
    }

    private (double x, double y) CursorScreenDip()
    {
        Win32.GetCursorPos(out var pt);
        return (pt.X / _pixelsPerDip, pt.Y / _pixelsPerDip);
    }

    private void CheckProximity()
    {
        // Safety net: WM_LBUTTONUP can be eaten by WS_EX_TRANSPARENT toggling on
        // a layered window. Polling the physical button via GetAsyncKeyState
        // guarantees the grab releases the moment the user lifts the button.
        if (_grabbing && !Win32.IsKeyDown(Win32.VK_LBUTTON))
        {
            ReleaseGrab();
        }

        var (cx, cy) = CursorScreenDip();
        double dx = cx - _x;
        double dy = cy - _y;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        bool near = dist <= GrabRadius + 8;

        if (_grabbing) ApplyClickThrough(false);
        else           ApplyClickThrough(!near);

        if (near && _sleeping) WakeUp();

        // Cursor (carrying the ball) wandered too close to the target → cheap delivery, hide it.
        if (_grabbing && _target.IsHittable)
        {
            var (tx, ty) = _target.Center;
            double tdx = cx - tx, tdy = cy - ty;
            if (Math.Sqrt(tdx * tdx + tdy * tdy) < TargetCursorAvoidDist)
                _target.HideAnimated();
        }
    }

    // ── Render loop control ──────────────────────────────────────────────
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

    private void WakeUp()
    {
        if (_captured) return;        // dog drives ball position; don't let physics race it
        _sleeping  = false;
        _idleAccum = 0;
        // Render loop will check proximity inline → don't double-tick from the timer.
        _proximityTimer.Stop();
        EnsureRenderLoop();
    }

    private void GoToSleep()
    {
        _sleeping = true;
        _vx = _vy = _omega = 0;
        StopRenderLoop();
        _proximityTimer.Start();
        _target.HideAnimated();
    }

    private void SpawnTarget(double cursorX, double cursorY)
    {
        var (tx, ty) = TargetWindow.PickRandomPosition(cursorX, cursorY, TargetMinSpawnDistance);
        _target.ShowAt(tx, ty);
    }

    // ── Dog carry handoff ────────────────────────────────────────────────
    public bool TryCaptureBall()
    {
        if (_grabbing || _captured) return false;
        _captured = true;
        _vx = _vy = _omega = 0;
        StopRenderLoop();
        _target.HideAnimated();
        return true;
    }

    public void SetCarriedPosition(double x, double y)
    {
        if (!_captured) return;
        _x = x;
        _y = y;
        UpdateTransforms();
    }

    public void DropAtRest()
    {
        if (!_captured) return;
        _captured = false;
        _x = RestX;
        _y = RestY;
        _vx = _vy = _omega = 0;
        UpdateTransforms();
        _sleeping  = true;
        _idleAccum = 0;
        if (!_proximityTimer.IsEnabled) _proximityTimer.Start();
    }

    // ── Tick ─────────────────────────────────────────────────────────────
    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min((now - _lastTick).TotalSeconds, 0.033);
        _lastTick = now;

        if (_grabbing)
        {
            UpdateGrab(dt);
        }
        else
        {
            // Sub-step to prevent tunneling at high speed (compositor often = 60Hz → 16ms,
            // a 4000 px/s ball moves > its own diameter per frame).
            double speed = Math.Sqrt(_vx * _vx + _vy * _vy);
            int steps = (int)Math.Ceiling(speed * dt / (BallR * 0.5));
            if (steps < 1)  steps = 1;
            if (steps > 16) steps = 16;
            double subDt = dt / steps;

            _tickFloorImpact = _tickWallImpact = _tickCeilImpact = 0;
            for (int i = 0; i < steps; i++)
            {
                UpdatePhysics(subDt);
                if (CheckTargetHit()) break;
            }

            // Fire FX at most once per tick per axis (strongest impact only) — avoids
            // animation-system thrashing when ball makes many small contacts in one tick.
            if (_tickWallImpact  >= WallSnapSpeed)  TriggerBounceFx(_tickWallImpact,  vertical: true);
            if (_tickCeilImpact  >= WallSnapSpeed)  TriggerBounceFx(_tickCeilImpact,  vertical: false);
            if (_tickFloorImpact >= FloorSnapSpeed) TriggerBounceFx(_tickFloorImpact, vertical: false);
        }

        UpdateTransforms();
        UpdateParticles(dt);
        UpdateTrajectory();
        CheckProximity();

        if (!_grabbing
            && Math.Abs(_vx) + Math.Abs(_vy) < SleepSpeed
            && _floorY - _y - BallR < SleepFloorEps
            && !AnyParticleAlive())
        {
            _idleAccum += dt;
            if (_idleAccum >= SleepDelay) GoToSleep();
        }
        else
        {
            _idleAccum = 0;
        }
    }

    private void UpdatePhysics(double dt)
    {
        _vy += Gravity * dt;
        _vx *= 1.0 - AirDrag * dt;
        _vy *= 1.0 - AirDrag * dt;

        _x += _vx * dt;
        _y += _vy * dt;

        _omega *= 1.0 - 1.4 * dt;
        _angle += _omega * dt;

        if (_x + BallR > _rightWall)
        {
            _x = _rightWall - BallR;
            double impact = Math.Abs(_vx);
            if (impact < WallSnapSpeed) { _vx = 0; }
            else { _vx = -_vx * WallRestitution; if (impact > _tickWallImpact) _tickWallImpact = impact; }
        }
        if (_x - BallR < _leftWall)
        {
            _x = _leftWall + BallR;
            double impact = Math.Abs(_vx);
            if (impact < WallSnapSpeed) { _vx = 0; }
            else { _vx = -_vx * WallRestitution; if (impact > _tickWallImpact) _tickWallImpact = impact; }
        }
        if (_y - BallR < _topWall)
        {
            _y = _topWall + BallR;
            double impact = Math.Abs(_vy);
            if (impact < WallSnapSpeed) { _vy = 0; }
            else { _vy = -_vy * WallRestitution; if (impact > _tickCeilImpact) _tickCeilImpact = impact; }
        }
        if (_y + BallR >= _floorY)
        {
            _y = _floorY - BallR;
            double impact = Math.Abs(_vy);
            if (impact < FloorSnapSpeed)
            {
                _vy = 0;
                _vx *= FloorFriction;
                if (Math.Abs(_vx) < MinFloorVx) { _vx = 0; _omega = 0; }
                else
                {
                    double rolling = _vx / BallR * (180.0 / Math.PI);
                    _omega = _omega * 0.4 + rolling * 0.6;
                }
            }
            else
            {
                _vy = -_vy * FloorRestitution;
                _vx *= FloorFriction;
                double rolling = _vx / BallR * (180.0 / Math.PI);
                _omega = _omega * 0.4 + rolling * 0.6;
                if (impact > _tickFloorImpact) _tickFloorImpact = impact;
            }
        }
    }

    private bool CheckTargetHit()
    {
        if (!_target.IsHittable) return false;
        var (tx, ty) = _target.Center;
        double dx = _x - tx, dy = _y - ty;
        double d  = Math.Sqrt(dx * dx + dy * dy);
        double hitR = TargetWindow.TargetRadius + BallR;
        if (d > hitR) return false;

        if (d < 0.001) { dx = 0; dy = -1; d = 1; }
        double nx = dx / d, ny = dy / d;

        _x = tx + nx * hitR;
        _y = ty + ny * hitR;

        double vDotN = _vx * nx + _vy * ny;
        if (vDotN < 0)
        {
            _vx -= 2 * vDotN * nx;
            _vy -= 2 * vDotN * ny;
            _vx *= WallRestitution;
            _vy *= WallRestitution;
            _omega += (Random.Shared.NextDouble() - 0.5) * 720;
        }

        double impact = Math.Abs(vDotN);
        _target.Explode();
        BallOverlayWindow.SpawnMeat();
        if (impact >= WallSnapSpeed)
            TriggerBounceFx(impact, vertical: Math.Abs(nx) > Math.Abs(ny));
        return true;
    }

    private void UpdateTransforms()
    {
        _rotate.Angle = _angle;

        // Update motion trail (world-space points rendered local to current window)
        double speed = Math.Sqrt(_vx * _vx + _vy * _vy);
        _trail.Opacity = Math.Min(0.5, speed / 700.0);
        if (speed > 25)
        {
            _trailWorld.Add(new WpfPoint(_x, _y));
            if (_trailWorld.Count > MaxTrailPoints) _trailWorld.RemoveAt(0);
        }
        else if (_trailWorld.Count > 0 && speed < 5)
        {
            _trailWorld.Clear();
        }
        _trailPoints.Clear();
        double wx = _x - LocalCenter;
        double wy = _y - LocalCenter;
        foreach (var pt in _trailWorld)
            _trailPoints.Add(new WpfPoint(pt.X - wx, pt.Y - wy));
        if (_trailPoints.Count >= 2)
        {
            _trailBrush.StartPoint = _trailPoints[0];
            _trailBrush.EndPoint   = _trailPoints[_trailPoints.Count - 1];
        }

        // Move the WINDOW to follow the ball. SetWindowPos for a layered window with
        // NOSIZE | NOZORDER | NOACTIVATE is just an OS reposition — no repaint, no
        // UpdateLayeredWindow. WPF's Window.Left/Top setters would be heavier.
        if (_hwnd != IntPtr.Zero)
        {
            int x = (int)((_x - LocalCenter) * _pixelsPerDip);
            int y = (int)((_y - LocalCenter) * _pixelsPerDip);
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
        }
    }

    // ── Trajectory preview ───────────────────────────────────────────────
    private void UpdateTrajectory()
    {
        double vx = _grabbing ? _grabVx : _vx;
        double vy = _grabbing ? _grabVy : _vy;
        double speed = Math.Sqrt(vx * vx + vy * vy);

        // Show trajectory whenever ball is moving
        if (speed < 15 || _sleeping)
        {
            BallOverlayWindow.HideTrajectory();
            return;
        }

        var points = new List<WpfPoint>();
        double simX = _x;
        double simY = _y;
        double simVx = vx;
        double simVy = vy;
        const double simDt = 0.012;

        for (int i = 0; i < 24; i++)
        {
            simVy += Gravity * simDt;
            simVx *= 1.0 - AirDrag * simDt;
            simVy *= 1.0 - AirDrag * simDt;
            simX += simVx * simDt;
            simY += simVy * simDt;

            if (simY + BallR >= _floorY)
            {
                simY = _floorY - BallR;
                if (Math.Abs(simVy) < FloorSnapSpeed) simVy = 0;
                else simVy = -simVy * FloorRestitution;
                simVx *= FloorFriction;
            }
            if (simX + BallR > _rightWall) { simX = _rightWall - BallR; simVx = -Math.Abs(simVx) * WallRestitution; }
            if (simX - BallR < _leftWall)  { simX = _leftWall + BallR;  simVx = Math.Abs(simVx) * WallRestitution; }

            points.Add(new WpfPoint(simX, simY));

            if (Math.Abs(simVx) + Math.Abs(simVy) < SleepSpeed) break;
        }

        BallOverlayWindow.ShowTrajectory(points.ToArray(), Math.Min(0.55, speed / 300.0));
    }

    // ── Bounce reactions ─────────────────────────────────────────────────
    private void TriggerBounceFx(double impactSpeed, bool vertical)
    {
        double t = Math.Min(1.0, impactSpeed / 1400.0);
        double squash  = 1.0 - 0.32 * t;
        double stretch = 1.0 + 0.28 * t;

        var ease = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 5 };
        var dur  = TimeSpan.FromMilliseconds(420);
        if (vertical)
        {
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(squash, 1.0, dur) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(stretch, 1.0, dur) { EasingFunction = ease });
        }
        else
        {
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(squash, 1.0, dur) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(stretch, 1.0, dur) { EasingFunction = ease });
        }

        if (impactSpeed >= ParticleSpeed) EmitParticles(impactSpeed, vertical);
        PlayBoing(impactSpeed);
    }

    // ── Particles ────────────────────────────────────────────────────────
    private void EmitParticles(double impactSpeed, bool vertical)
    {
        int n = (int)Math.Min(ParticleCount, 4 + impactSpeed / 120.0);
        double speedScale = Math.Min(1.0, impactSpeed / 800.0);

        // Direction: hemisphere away from the impacted wall
        bool hitRight  = vertical && _x > _rightWall - BallR - 1;
        bool hitTop    = !vertical && _y < _topWall + BallR + 1;

        for (int i = 0, emitted = 0; i < ParticleCount && emitted < n; i++)
        {
            if (_particleLife[i] > 0) continue;

            double baseDeg;
            if (vertical) baseDeg = hitRight ? 180 : 0;     // away from side wall
            else          baseDeg = hitTop   ? 90  : 270;   // away from floor/ceiling
            double spreadDeg = (Random.Shared.NextDouble() - 0.5) * 160;
            double angleRad  = (baseDeg + spreadDeg) * Math.PI / 180.0;

            double speed    = (90 + Random.Shared.NextDouble() * 220) * (0.4 + speedScale);
            _particleVx[i] = Math.Cos(angleRad) * speed;
            _particleVy[i] = Math.Sin(angleRad) * speed;
            double life = 0.30 + Random.Shared.NextDouble() * 0.30;
            _particleLife[i]    = life;
            _particleMaxLife[i] = life;

            _particleX[i] = _x;
            _particleY[i] = _y;
            _particleTrans[i].X = LocalCenter - 2;
            _particleTrans[i].Y = LocalCenter - 2;
            double sc = 0.7 + Random.Shared.NextDouble() * 0.9;
            _particleScale[i].ScaleX = sc;
            _particleScale[i].ScaleY = sc;
            _particles[i].Opacity = 0.95;
            emitted++;
        }
    }

    private void UpdateParticles(double dt)
    {
        // World-space positions stay fixed in screen as the window moves with the ball.
        // Window-local draw position = world - (window upper-left in world) - half-particle
        // (window upper-left = (_x - LocalCenter, _y - LocalCenter)).
        for (int i = 0; i < ParticleCount; i++)
        {
            if (_particleLife[i] <= 0) continue;
            _particleLife[i] -= dt;
            if (_particleLife[i] <= 0)
            {
                _particles[i].Opacity = 0;
                continue;
            }
            _particleVy[i] += Gravity * 0.45 * dt;
            _particleVx[i] *= 1.0 - 0.6 * dt;
            _particleX[i]  += _particleVx[i] * dt;
            _particleY[i]  += _particleVy[i] * dt;
            _particleTrans[i].X = _particleX[i] - _x + LocalCenter - 2;
            _particleTrans[i].Y = _particleY[i] - _y + LocalCenter - 2;
            _particles[i].Opacity = (_particleLife[i] / _particleMaxLife[i]) * 0.95;
        }
    }

    private bool AnyParticleAlive()
    {
        for (int i = 0; i < ParticleCount; i++)
            if (_particleLife[i] > 0) return true;
        return false;
    }

    // ── Grab ─────────────────────────────────────────────────────────────
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var (cx, cy) = CursorScreenDip();
        double dx = cx - _x;
        double dy = cy - _y;
        if (Math.Sqrt(dx * dx + dy * dy) > GrabRadius) return;

        if (_captured)
        {
            _captured = false;
            CarryInterrupted?.Invoke();
        }

        _grabbing      = true;
        _grabOffset    = new WpfPoint(dx, dy);
        _grabVx        = 0;
        _grabVy        = 0;
        _vx = _vy = _omega = 0;

        Mouse.Capture(this);
        WakeUp();
        SpawnTarget(cx, cy);

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var dur  = TimeSpan.FromMilliseconds(140);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(_scale.ScaleX, 0.85, dur) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(_scale.ScaleY, 0.85, dur) { EasingFunction = ease });

        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_grabbing) return;
        ReleaseGrab();
        e.Handled = true;
    }

    private void ReleaseGrab()
    {
        if (!_grabbing) return;
        Mouse.Capture(null);
        _grabbing = false;

        _vx = _grabVx;
        _vy = _grabVy;
        _omega = _vx / BallR * (180.0 / Math.PI) * 1.4
               + (Random.Shared.NextDouble() - 0.5) * 360;

        var ease = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 };
        var dur  = TimeSpan.FromMilliseconds(380);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.85, 1.0, dur) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.85, 1.0, dur) { EasingFunction = ease });
    }

    private void UpdateGrab(double dt)
    {
        // Spring-damper "rubber band" between cursor and ball. Ball lags the cursor;
        // fast flicks build real velocity that's transferred on release for a true throw.
        const double Stiffness = 140;   // pull-toward-cursor strength
        const double Damping   = 14;    // velocity damping (slightly under critical → tiny springy feel)

        var (cx, cy) = CursorScreenDip();
        double targetX = cx - _grabOffset.X;
        double targetY = cy - _grabOffset.Y;

        double ax = (targetX - _x) * Stiffness - _grabVx * Damping;
        double ay = (targetY - _y) * Stiffness - _grabVy * Damping;

        _grabVx += ax * dt;
        _grabVy += ay * dt;
        _x      += _grabVx * dt;
        _y      += _grabVy * dt;

        _angle += (_grabVx / BallR * (180.0 / Math.PI)) * dt * 0.4;
    }

    // ── Sound ────────────────────────────────────────────────────────────
    private static void EnsureBoingPool()
    {
        if (_boingPool is { Length: > 0 }) return;
        var wav = GenerateBoingWav();
        _boingPool = new SoundPlayer[BoingPoolSize];
        for (int i = 0; i < BoingPoolSize; i++)
        {
            var sp = new SoundPlayer(new MemoryStream(wav, writable: false));
            try { sp.Load(); } catch { }
            _boingPool[i] = sp;
        }
    }

    private void PlayBoing(double impactSpeed)
    {
        if (impactSpeed < 60) return;
        var now = DateTime.UtcNow;
        if ((now - _lastBoing).TotalMilliseconds < 35) return;
        _lastBoing = now;

        var sp = _boingPool[_boingIdx];
        _boingIdx = (_boingIdx + 1) % BoingPoolSize;
        try { sp.Play(); } catch { }
    }

    private static byte[] GenerateBoingWav()
    {
        const int sampleRate = 22050;
        const double duration = 0.22;
        int numSamples = (int)(sampleRate * duration);
        var samples = new short[numSamples];

        double phase = 0;
        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double freq = 320 - 220 * (t / duration);
            phase += 2 * Math.PI * freq / sampleRate;

            double attack = Math.Min(1.0, t / 0.005);
            double decay  = Math.Exp(-t / 0.085);
            double env    = attack * decay;

            double wobble = 1.0 + 0.20 * Math.Sin(2 * Math.PI * 14 * t);
            double s = env * wobble * Math.Sin(phase);
            samples[i] = (short)(s * short.MaxValue * 0.55);
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        int dataSize = numSamples * 2;
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);          // PCM
        bw.Write((short)1);          // mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);    // byte rate
        bw.Write((short)2);          // block align
        bw.Write((short)16);         // bits per sample
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        foreach (var s in samples) bw.Write(s);
        return ms.ToArray();
    }
}

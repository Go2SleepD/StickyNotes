using System.Windows;
using Brushes = System.Windows.Media.Brushes;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using WpfColor = System.Windows.Media.Color;

namespace StickerApp;

public partial class AsciiDogWindow : Window
{
    // ── Layout ───────────────────────────────────────────────────────────
    private const int    WsExTransparent  = 0x00000020;
    private const double WindowWidth      = 240;
    private const double WindowHeight     = 300;
    private const double HomeOffsetFromBall = 90;
    private const double RunSpeed           = 360;
    private const double CarrySpeed         = 280;
    private const double JumpVy0            = 760;
    private const double DogGravity         = 2200;
    private const double MaxJumpYOffset     = 150;
    private const double JumpReachHeight    = 170;
    private const double JumpHorizWindow    = 50;
    private const double GroundCatchHorizDx = 30;
    private const double CatchRadius        = 30;
    private const double WakeDuration       = 0.45;
    private const double LieDuration        = 0.55;
    private const double SleepFrameInterval = 0.55;
    private const double StandIdleInterval  = 1.4;
    private const double RunFrameInterval   = 0.12;
    private const double CarryFrameInterval = 0.16;
    private const double JumpCooldown       = 0.35;
    private const double DropProximity      = 12;

    // ── State machine ────────────────────────────────────────────────────
    private enum DogState { Sleeping, Waking, Standing, Following, Jumping, Carrying, LyingDown, Eating }

    private readonly RubberBallWindow _ball;
    private DogState _state;
    private double   _stateTimer;
    private double   _animTimer;
    private int      _animFrame;
    private double   _jumpCooldown;

    private double _x;
    private double _floorY;
    private double _yOffset;
    private double _vy;
    private double _facing = 1;

    private double _restX;
    private double _meatX;
    private bool   _hasMeatQueued;
    private double _eatTimer;

    private IntPtr _hwnd;
    private double _pixelsPerDip = 1.0;

    private DateTime _lastTick;
    private bool     _renderHooked;

    // ── Visual parts ─────────────────────────────────────────────────────
    private Canvas _dogCanvas = null!;
    private ScaleTransform _dogFlip = null!;
    private TranslateTransform _dogTrans = null!;

    private Ellipse _shadow = null!;
    private Ellipse _body = null!;
    private Ellipse _head = null!;
    private Path _earL = null!;
    private Path _earR = null!;
    private Ellipse _eyeL = null!;
    private Ellipse _eyeR = null!;
    private Ellipse _pupilL = null!;
    private Ellipse _pupilR = null!;
    private Ellipse _nose = null!;
    private Path _tail = null!;
    private Ellipse _legFL = null!;
    private Ellipse _legFR = null!;
    private Ellipse _legBL = null!;
    private Ellipse _legBR = null!;

    // Anim transforms
    private ScaleTransform _bodyScale = null!;
    private RotateTransform _tailRot = null!;
    private RotateTransform _legFLRot = null!;
    private RotateTransform _legFRRot = null!;
    private RotateTransform _legBLRot = null!;
    private RotateTransform _legBRRot = null!;
    private ScaleTransform _eyeLScale = null!;
    private ScaleTransform _eyeRScale = null!;
    private TranslateTransform _headTrans = null!;
    private TextBlock? _eatLabel;

    // ── Constructor ──────────────────────────────────────────────────────
    public double RestX => _restX;
    public double FloorY => _floorY;

    public AsciiDogWindow(RubberBallWindow ball)
    {
        InitializeComponent();
        _ball = ball;

        Width  = WindowWidth;
        Height = WindowHeight;
        Left   = -10000;
        Top    = -10000;

        _floorY = ball.FloorY;
        double minX = SystemParameters.VirtualScreenLeft + WindowWidth / 2;
        double maxX = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - WindowWidth / 2;
        _restX = Math.Max(minX, Math.Min(maxX, ball.RestX - HomeOffsetFromBall));
        _x     = _restX;

        BuildDog();
        StartBreathAnimation();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = Win32.GetWindowLong(_hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLong(_hwnd, Win32.GWL_EXSTYLE,
                ex | WsExTransparent | Win32.WS_EX_NOACTIVATE);
            var src = PresentationSource.FromVisual(this);
            _pixelsPerDip = src?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
        };

        Loaded += (_, _) =>
        {
            RepositionWindow();
            TransitionTo(DogState.Sleeping);
            EnsureRenderLoop();
        };

        Closed += (_, _) => StopRenderLoop();
        ball.CarryInterrupted += OnBallStolenByUser;
        Show();
    }

    // ── Build vector dog ─────────────────────────────────────────────────
    private void BuildDog()
    {
        var bodyGradient = new RadialGradientBrush(new GradientStopCollection
        {
            new(WpfColor.FromRgb(0xE8, 0xA0, 0x4A), 0.0),
            new(WpfColor.FromRgb(0xC0, 0x70, 0x28), 0.5),
            new(WpfColor.FromRgb(0x8B, 0x4A, 0x15), 1.0),
        })
        { Center = new System.Windows.Point(0.4, 0.35), RadiusX = 0.7, RadiusY = 0.7 };

        var headGradient = new RadialGradientBrush(new GradientStopCollection
        {
            new(WpfColor.FromRgb(0xEC, 0xA8, 0x52), 0.0),
            new(WpfColor.FromRgb(0xC8, 0x78, 0x30), 0.55),
            new(WpfColor.FromRgb(0x90, 0x4E, 0x18), 1.0),
        })
        { Center = new System.Windows.Point(0.42, 0.32), RadiusX = 0.7, RadiusY = 0.7 };

        var earBrush = new SolidColorBrush(WpfColor.FromRgb(0xA0, 0x58, 0x1C));
        var legBrush = new SolidColorBrush(WpfColor.FromRgb(0x7A, 0x42, 0x14));
        var noseBrush = new SolidColorBrush(WpfColor.FromRgb(0x2A, 0x1A, 0x0E));

        _shadow = new Ellipse { Width = 90, Height = 14, Fill = new SolidColorBrush(WpfColor.FromArgb(60, 0, 0, 0)) };
        Place(_shadow, 75, 278);

        _tail = new Path
        {
            Data = Geometry.Parse("M0,8 Q10,-18 26,-4"),
            Stroke = new SolidColorBrush(WpfColor.FromRgb(0xA0, 0x58, 0x1C)),
            StrokeThickness = 7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = null,
        };
        _tailRot = new RotateTransform(0, 0, 8);
        _tail.RenderTransform = _tailRot;
        Place(_tail, 152, 228);

        _legBL = MkEllipse(12, 20, legBrush);
        _legBLRot = new RotateTransform(0, 6, 10);
        _legBL.RenderTransform = _legBLRot;
        Place(_legBL, 82, 258);

        _legBR = MkEllipse(12, 20, legBrush);
        _legBRRot = new RotateTransform(0, 6, 10);
        _legBR.RenderTransform = _legBRRot;
        Place(_legBR, 146, 258);

        _body = MkEllipse(84, 54, bodyGradient);
        _bodyScale = new ScaleTransform(1, 1) { CenterX = 42, CenterY = 27 };
        _body.RenderTransform = _bodyScale;
        Place(_body, 78, 212);

        _head = MkEllipse(52, 46, headGradient);
        _headTrans = new TranslateTransform(0, 0);
        _head.RenderTransform = _headTrans;
        Place(_head, 94, 172);

        _earL = new Path { Data = Geometry.Parse("M0,18 L9,0 L18,16 Z"), Fill = earBrush };
        Place(_earL, 88, 158);

        _earR = new Path { Data = Geometry.Parse("M0,16 L9,0 L18,18 Z"), Fill = earBrush };
        Place(_earR, 128, 158);

        _eyeL = MkEllipse(10, 10, Brushes.White);
        _eyeLScale = new ScaleTransform(1, 1) { CenterX = 5, CenterY = 5 };
        _eyeL.RenderTransform = _eyeLScale;
        Place(_eyeL, 106, 188);

        _eyeR = MkEllipse(10, 10, Brushes.White);
        _eyeRScale = new ScaleTransform(1, 1) { CenterX = 5, CenterY = 5 };
        _eyeR.RenderTransform = _eyeRScale;
        Place(_eyeR, 128, 188);

        _pupilL = MkEllipse(5, 5, Brushes.Black);
        Place(_pupilL, 109, 191);

        _pupilR = MkEllipse(5, 5, Brushes.Black);
        Place(_pupilR, 131, 191);

        _nose = MkEllipse(10, 7, noseBrush);
        Place(_nose, 134, 198);

        _legFL = MkEllipse(12, 20, legBrush);
        _legFLRot = new RotateTransform(0, 6, 10);
        _legFL.RenderTransform = _legFLRot;
        Place(_legFL, 98, 260);

        _legFR = MkEllipse(12, 20, legBrush);
        _legFRRot = new RotateTransform(0, 6, 10);
        _legFR.RenderTransform = _legFRRot;
        Place(_legFR, 130, 260);

        _dogCanvas = new Canvas { Width = WindowWidth, Height = WindowHeight, IsHitTestVisible = false };
        _dogCanvas.Children.Add(_shadow);
        _dogCanvas.Children.Add(_tail);
        _dogCanvas.Children.Add(_legBL);
        _dogCanvas.Children.Add(_legBR);
        _dogCanvas.Children.Add(_body);
        _dogCanvas.Children.Add(_head);
        _dogCanvas.Children.Add(_earL);
        _dogCanvas.Children.Add(_earR);
        _dogCanvas.Children.Add(_eyeL);
        _dogCanvas.Children.Add(_eyeR);
        _dogCanvas.Children.Add(_pupilL);
        _dogCanvas.Children.Add(_pupilR);
        _dogCanvas.Children.Add(_nose);
        _dogCanvas.Children.Add(_legFL);
        _dogCanvas.Children.Add(_legFR);

        _dogFlip = new ScaleTransform(1, 1) { CenterX = 120, CenterY = 150 };
        _dogTrans = new TranslateTransform(0, 0);
        var grp = new TransformGroup();
        grp.Children.Add(_dogFlip);
        grp.Children.Add(_dogTrans);
        _dogCanvas.RenderTransform = grp;

        // Soft drop shadow under the whole dog
        _dogCanvas.Effect = new DropShadowEffect
        {
            BlurRadius = 10,
            ShadowDepth = 4,
            Opacity = 0.35,
            Color = Colors.Black,
        };

        Root.Children.Add(_dogCanvas);
    }

    private static Ellipse MkEllipse(double w, double h, System.Windows.Media.Brush fill) =>
        new Ellipse { Width = w, Height = h, Fill = fill, IsHitTestVisible = false };

    private static void Place(FrameworkElement el, double x, double y)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
    }

    // ── Animations ───────────────────────────────────────────────────────
    private void StartBreathAnimation()
    {
        var breath = new DoubleAnimation(1, 1.018, new Duration(TimeSpan.FromSeconds(1.4)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        _bodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, breath);
        _bodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, breath);
    }

    private void SetTailWag(double range, double sec)
    {
        var wag = new DoubleAnimation(-range, range, new Duration(TimeSpan.FromSeconds(sec)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        _tailRot.BeginAnimation(RotateTransform.AngleProperty, wag);
    }

    private void StopTailWag() => _tailRot.BeginAnimation(RotateTransform.AngleProperty, null);

    private void SetRunLegs()
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var a = new DoubleAnimation(-22, 22, new Duration(TimeSpan.FromSeconds(0.16))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
        var b = new DoubleAnimation(22, -22, new Duration(TimeSpan.FromSeconds(0.16))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
        _legFLRot.BeginAnimation(RotateTransform.AngleProperty, a);
        _legBLRot.BeginAnimation(RotateTransform.AngleProperty, b);
        _legFRRot.BeginAnimation(RotateTransform.AngleProperty, b);
        _legBRRot.BeginAnimation(RotateTransform.AngleProperty, a);
    }

    private void StopLegs()
    {
        _legFLRot.BeginAnimation(RotateTransform.AngleProperty, null);
        _legBLRot.BeginAnimation(RotateTransform.AngleProperty, null);
        _legFRRot.BeginAnimation(RotateTransform.AngleProperty, null);
        _legBRRot.BeginAnimation(RotateTransform.AngleProperty, null);
        _legFLRot.Angle = _legBLRot.Angle = _legFRRot.Angle = _legBRRot.Angle = 0;
    }

    private void SetEyesOpen(bool open)
    {
        var target = open ? 1 : 0.1;
        var dur = TimeSpan.FromMilliseconds(open ? 180 : 120);
        var anim = new DoubleAnimation(target, dur) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        _eyeLScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        _eyeRScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    // ── Render loop ──────────────────────────────────────────────────────
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

        _stateTimer += dt;
        _animTimer  += dt;
        if (_jumpCooldown > 0) _jumpCooldown -= dt;

        switch (_state)
        {
            case DogState.Sleeping:    TickSleeping(dt);  break;
            case DogState.Waking:      TickWaking(dt);    break;
            case DogState.Standing:    TickStanding(dt);  break;
            case DogState.Following:   TickFollowing(dt); break;
            case DogState.Jumping:     TickJumping(dt);   break;
            case DogState.Carrying:    TickCarrying(dt);  break;
            case DogState.LyingDown:   TickLyingDown(dt); break;
            case DogState.Eating:      TickEating(dt);    break;
        }

        // Meat-seeking logic (outside state switch so it can interrupt)
        if (_hasMeatQueued && _state != DogState.Eating)
        {
            if (_state == DogState.Carrying)
            {
                _ball.DropAtRest();
                TransitionTo(DogState.Standing);
            }
            else if (_state == DogState.Sleeping)
            {
                TransitionTo(DogState.Waking);
            }
            else if (_state == DogState.LyingDown)
            {
                TransitionTo(DogState.Standing);
            }
            else if (_state == DogState.Standing || _state == DogState.Waking)
            {
                FaceTowards(_meatX);
                StepHorizontal(_meatX, RunSpeed, dt);
                if (Math.Abs(_x - _meatX) < 20 && _yOffset <= 0)
                {
                    _hasMeatQueued = false;
                    _eatTimer = 10.0;
                    TransitionTo(DogState.Eating);
                }
            }
            else if (_state == DogState.Following || _state == DogState.Jumping)
            {
                // Interrupt ball chase to go for meat
                FaceTowards(_meatX);
                StepHorizontal(_meatX, RunSpeed, dt);
                if (Math.Abs(_x - _meatX) < 20 && _yOffset <= 0)
                {
                    _hasMeatQueued = false;
                    _eatTimer = 10.0;
                    TransitionTo(DogState.Eating);
                }
            }
        }

        RepositionWindow();

        if (_state == DogState.Carrying && _ball.BallCaptured)
        {
            var (mx, my) = MouthScreen();
            _ball.SetCarriedPosition(mx, my - _ball.BallRadius - 2);
        }
    }

    // ── Per-state behavior ───────────────────────────────────────────────
    private void TickSleeping(double dt)
    {
        if (_animTimer >= SleepFrameInterval)
        {
            _animTimer = 0;
            _animFrame = (_animFrame + 1) % 4;
            // Subtle Z-bubble could be added here; for now just breathing
        }
        if (_ball.BallGrabbed) TransitionTo(DogState.Waking);
    }

    private void TickWaking(double dt)
    {
        if (_stateTimer >= WakeDuration) TransitionTo(DogState.Standing);
    }

    private void TickStanding(double dt)
    {
        if (_animTimer >= StandIdleInterval)
        {
            _animTimer = 0;
            _animFrame = (_animFrame + 1) % 2;
            // Blink occasionally
            if (Random.Shared.NextDouble() < 0.35) SetEyesOpen(false);
            else SetEyesOpen(true);
        }

        FaceTowards(_ball.BallX);

        if (!_ball.BallGrabbed && !_ball.BallCaptured)
        {
            bool ballAtHome =
                Math.Abs(_ball.BallX - _ball.RestX) < DropProximity &&
                Math.Abs(_ball.BallY - _ball.RestY) < DropProximity;
            if (!ballAtHome) TransitionTo(DogState.Following);
        }
    }

    private void TickFollowing(double dt)
    {
        if (_ball.BallGrabbed)  { TransitionTo(DogState.Standing); return; }
        if (_ball.BallCaptured) return;

        FaceTowards(_ball.BallX);
        StepHorizontal(_ball.BallX, RunSpeed, dt);

        double ballHeight = _floorY - _ball.BallY - _ball.BallRadius;
        double horiz = Math.Abs(_ball.BallX - _x);

        if (ballHeight < 18 && horiz < GroundCatchHorizDx)
        {
            if (_ball.TryCaptureBall())
            {
                TransitionTo(DogState.Carrying);
                return;
            }
        }

        if (_jumpCooldown <= 0 && horiz < JumpHorizWindow
            && ballHeight > 30 && ballHeight < JumpReachHeight)
        {
            _vy = JumpVy0;
            _yOffset = 0.001;
            _jumpCooldown = JumpCooldown + 0.6;
            TransitionTo(DogState.Jumping);
        }
    }

    private void TickJumping(double dt)
    {
        _vy -= DogGravity * dt;
        _yOffset += _vy * dt;
        if (_yOffset > MaxJumpYOffset) { _yOffset = MaxJumpYOffset; if (_vy > 0) _vy = 0; }

        StepHorizontal(_ball.BallX, RunSpeed * 0.6, dt);
        FaceTowards(_ball.BallX);

        if (!_ball.BallCaptured && !_ball.BallGrabbed)
        {
            var (mx, my) = MouthScreen();
            double dx = _ball.BallX - mx;
            double dy = _ball.BallY - my;
            if (Math.Sqrt(dx * dx + dy * dy) < CatchRadius + _ball.BallRadius)
            {
                if (_ball.TryCaptureBall())
                {
                    TransitionTo(DogState.Carrying);
                    return;
                }
            }
        }

        if (_yOffset <= 0 && _vy < 0)
        {
            _yOffset = 0;
            _vy = 0;
            _jumpCooldown = JumpCooldown;
            if (_ball.BallGrabbed) TransitionTo(DogState.Standing);
            else                   TransitionTo(DogState.Following);
        }
    }

    private void TickCarrying(double dt)
    {
        if (!_ball.BallCaptured)
        {
            TransitionTo(DogState.Standing);
            return;
        }

        if (_yOffset > 0)
        {
            _vy -= DogGravity * dt;
            _yOffset += _vy * dt;
            if (_yOffset < 0) { _yOffset = 0; _vy = 0; }
        }

        FaceTowards(_restX);
        StepHorizontal(_restX, CarrySpeed, dt);

        if (Math.Abs(_x - _restX) < DropProximity && _yOffset <= 0)
        {
            _ball.DropAtRest();
            TransitionTo(DogState.LyingDown);
        }
    }

    private void TickLyingDown(double dt)
    {
        FaceTowards(_ball.RestX);
        if (_ball.BallGrabbed) { TransitionTo(DogState.Waking); return; }
        if (_stateTimer >= LieDuration)
        {
            TransitionTo(DogState.Sleeping);
        }
    }

    private void OnBallStolenByUser()
    {
        if (_state == DogState.Carrying)
            TransitionTo(DogState.Standing);
    }

    public void NotifyMeat(double meatX)
    {
        _meatX = meatX;
        _hasMeatQueued = true;
        if (_state == DogState.Eating)
        {
            _eatTimer += 10.0;
            return;
        }
        if (_state == DogState.Sleeping)
            TransitionTo(DogState.Waking);
    }

    private void EnsureEatLabel()
    {
        if (_eatLabel != null) return;
        _eatLabel = new TextBlock
        {
            FontSize = 13,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.6 },
            TextAlignment = TextAlignment.Center,
            Width = 60,
        };
        Canvas.SetLeft(_eatLabel, 90);
        Canvas.SetTop(_eatLabel, 145);
        _dogCanvas.Children.Add(_eatLabel);
    }

    private void TickEating(double dt)
    {
        _eatTimer -= dt;
        _stateTimer += dt;

        // Chew animation: bob head up/down
        if (_headTrans != null)
            _headTrans.Y = Math.Abs(Math.Sin(_stateTimer * 18)) * 4;

        if (_eatLabel != null)
        {
            _eatLabel.Text = _eatTimer > 0 ? $"{_eatTimer:F1}s" : "";
            _eatLabel.Visibility = _eatTimer > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_eatTimer <= 0)
        {
            if (_headTrans != null) _headTrans.Y = 0;
            BallOverlayWindow.TryConsumeMeatAt(_meatX);
            TransitionTo(DogState.LyingDown);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────
    private void TransitionTo(DogState s)
    {
        _state = s;
        _stateTimer = 0;
        _animTimer = 0;
        _animFrame = 0;

        switch (s)
        {
            case DogState.Sleeping:
                StopTailWag(); StopLegs();
                SetEyesOpen(false);
                _tailRot.Angle = -10;
                break;
            case DogState.Waking:
                StopTailWag(); StopLegs();
                SetEyesOpen(true);
                break;
            case DogState.Standing:
                StopLegs();
                SetTailWag(12, 0.6);
                SetEyesOpen(true);
                break;
            case DogState.Following:
                SetRunLegs();
                SetTailWag(28, 0.22);
                SetEyesOpen(true);
                break;
            case DogState.Jumping:
                StopLegs();
                SetTailWag(40, 0.18);
                SetEyesOpen(true);
                break;
            case DogState.Carrying:
                SetRunLegs();
                SetTailWag(20, 0.35);
                SetEyesOpen(true);
                break;
            case DogState.LyingDown:
                StopTailWag(); StopLegs();
                SetEyesOpen(false);
                _tailRot.Angle = -5;
                break;
            case DogState.Eating:
                StopTailWag(); StopLegs();
                SetEyesOpen(true);
                _tailRot.Angle = 0;
                EnsureEatLabel();
                if (_eatLabel != null) _eatLabel.Visibility = Visibility.Visible;
                break;
        }
    }

    private void StepHorizontal(double targetX, double speed, double dt)
    {
        double dx = targetX - _x;
        double step = speed * dt;
        if (Math.Abs(dx) <= step) _x = targetX;
        else                      _x += Math.Sign(dx) * step;

        double minX = SystemParameters.VirtualScreenLeft + WindowWidth / 2;
        double maxX = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - WindowWidth / 2;
        if (_x < minX) _x = minX;
        if (_x > maxX) _x = maxX;
    }

    private void FaceTowards(double targetX)
    {
        double want = targetX < _x - 8 ? -1 : (targetX > _x + 8 ? 1 : _facing);
        if (want != _facing)
        {
            _facing = want;
            _dogFlip.ScaleX = _facing;
        }
    }

    private (double x, double y) MouthScreen()
    {
        double mouthLocalX = _facing > 0 ? 145 : 95;
        double winLeft = _x - WindowWidth / 2;
        double mouthX  = winLeft + mouthLocalX;
        double winTop  = _floorY - WindowHeight;
        double mouthY  = winTop + 202 - _yOffset;
        return (mouthX, mouthY);
    }

    private void RepositionWindow()
    {
        double left = _x - WindowWidth / 2;
        double top  = _floorY - WindowHeight;

        if (_hwnd != IntPtr.Zero)
        {
            int px = (int)(left * _pixelsPerDip);
            int py = (int)(top  * _pixelsPerDip);
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, px, py, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
        }
        else
        {
            Left = left;
            Top  = top;
        }

        _dogTrans.Y = -_yOffset;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WpfColor = System.Windows.Media.Color;

namespace StickerApp;

public partial class AsciiDogWindow : Window
{
    // ── Layout ───────────────────────────────────────────────────────────
    private const int    WsExTransparent  = 0x00000020;
    private const double WindowWidth      = 240;
    private const double WindowHeight     = 300;
    private const double DogFontSize      = 14;
    private const double CharCellWidth    = 8.4;     // approximate Consolas advance
    private const double CharCellHeight   = 17.0;
    private const int    FrameCols        = 14;
    private const int    FrameRows        = 8;
    private const double TextWidth        = FrameCols * CharCellWidth;   // ≈ 117
    private const double TextHeight       = FrameRows * CharCellHeight;  // = 136
    private const double TextRestTop      = WindowHeight - TextHeight;   // dog row 8 sits at floor
    private const double TextLeftCenter   = (WindowWidth - TextWidth) / 2;

    // Mouth anchor inside the rendered 14×8 frame (when facing right).
    // Dog head spans roughly rows 4–6, snout at right around col 11.
    private const double MouthColFromLeft = 11.0;
    private const double MouthRowFromTop  = 5.0;
    private const double DogMouthXFromFrameLeft = MouthColFromLeft * CharCellWidth;
    private const double DogMouthYFromFrameTop  = MouthRowFromTop  * CharCellHeight;

    // ── Tuning ───────────────────────────────────────────────────────────
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
    private enum DogState { Sleeping, Waking, Standing, Following, Jumping, Carrying, LyingDown }

    private readonly RubberBallWindow _ball;
    private DogState _state;
    private double   _stateTimer;
    private double   _animTimer;
    private int      _animFrame;
    private double   _jumpCooldown;

    private double _x;
    private double _floorY;
    private double _yOffset;     // positive = dog has leaped upward
    private double _vy;          // positive = upward velocity
    private double _facing = 1;  // +1 right, -1 left

    private double _restX;

    private IntPtr _hwnd;
    private double _pixelsPerDip = 1.0;

    private TextBlock          _text       = null!;
    private TranslateTransform _textTrans  = null!;
    private ScaleTransform     _textFlip   = null!;
    private DateTime           _lastTick;
    private bool               _renderHooked;

    // ── ASCII frames ─────────────────────────────────────────────────────
    // Each frame is exactly 8 lines. Right-padding is irrelevant; line count is what matters.
    // Dog faces RIGHT in source; left motion uses ScaleTransform(X=-1) to mirror.

    private static readonly string[] FrSleep =
    {
        // Curled, no zzz — silent breath
        "\n\n\n\n\n      .---.   \n   __/ -.- \\__\n   '----------'",
        // One little z
        "\n\n\n\n         z    \n      .---.   \n   __/ -.- \\__\n   '----------'",
        // Z and z rising
        "\n\n        Z     \n              \n         z    \n      .---.   \n   __/ -.- \\__\n   '----------'",
        // Z drifting up, z fading
        "\n       Z      \n              \n        z     \n              \n      .---.   \n   __/ -.- \\__\n   '----------'",
    };

    private static readonly string[] FrWake =
    {
        // Eyes pop open, surprised — still curled
        "\n\n\n        !     \n              \n      .---.   \n   __/ o.o \\__\n   '----------'",
        // Sitting up, head raised
        "\n\n\n     /\\___    \n    /    \\___ \n   ( o.o    \\ \n    \\_______/ \n     /|   |\\  ",
    };

    private static readonly string[] FrStand =
    {
        // Standing alert, eye open
        "\n\n\n     /\\___    \n    /    \\___ \n   ( o      \\ \n    \\_______/ \n     /|   |\\  ",
        // Subtle blink
        "\n\n\n     /\\___    \n    /    \\___ \n   ( -      \\ \n    \\_______/ \n     /|   |\\  ",
    };

    private static readonly string[] FrRun =
    {
        // Legs splayed outward — extended stride
        "\n\n\n     /\\___    \n    /    \\___ \n   ( o      \\ \n    \\_______/ \n     /<   >\\  ",
        // Legs converged — gathered stride
        "\n\n\n     /\\___    \n    /    \\___ \n   ( o      \\ \n    \\_______/ \n     <\\   />  ",
    };

    private static readonly string[] FrJump =
    {
        // Ascending — paws tucked, mouth open ready to catch
        "\n\n     /\\___    \n    /    \\___ \n   ( o      v \n    \\_______/ \n     /||\\     \n              ",
        // Apex — fully extended, body lifted one row higher in frame
        "\n     /\\___    \n    /    \\___ \n   ( o      v \n    \\_______/ \n      ||||    \n              \n              ",
    };

    private static readonly string[] FrCarry =
    {
        // Same posture as Run; the actual orange ball is overlaid above the snout each tick.
        "\n\n\n     /\\___    \n    /    \\___ \n   ( o      \\ \n    \\_______/ \n     /<   >\\  ",
        "\n\n\n     /\\___    \n    /    \\___ \n   ( o      \\ \n    \\_______/ \n     <\\   />  ",
    };

    private static readonly string[] FrLie =
    {
        // Settling — same low silhouette as deep sleep
        "\n\n\n\n\n      .---.   \n   __/ -.- \\__\n   '----------'",
    };

    public AsciiDogWindow(RubberBallWindow ball)
    {
        InitializeComponent();
        _ball = ball;

        Width  = WindowWidth;
        Height = WindowHeight;
        Left   = -10000;
        Top    = -10000;

        _floorY = ball.FloorY;
        // Clamp the home spot into the dog's reachable range so it can actually arrive
        // and trigger the drop. StepHorizontal clamps _x to [minX, maxX]; if _restX falls
        // outside that, the distance test in TickCarrying never closes.
        double minX = SystemParameters.VirtualScreenLeft + WindowWidth / 2;
        double maxX = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - WindowWidth / 2;
        _restX = Math.Max(minX, Math.Min(maxX, ball.RestX - HomeOffsetFromBall));
        _x     = _restX;

        BuildText();

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
            RenderFrame(FrSleep[0]);
            EnsureRenderLoop();
        };

        Closed += (_, _) => StopRenderLoop();

        ball.CarryInterrupted += OnBallStolenByUser;

        Show();
    }

    // ── Visual setup ─────────────────────────────────────────────────────
    private void BuildText()
    {
        var brush = new SolidColorBrush(WpfColor.FromRgb(0x36, 0x22, 0x10));
        brush.Freeze();

        _textTrans = new TranslateTransform(0, 0);
        _textFlip  = new ScaleTransform(1, 1)
        {
            CenterX = TextLeftCenter + TextWidth / 2,
            CenterY = TextRestTop    + TextHeight / 2,
        };
        var grp = new TransformGroup();
        grp.Children.Add(_textFlip);
        grp.Children.Add(_textTrans);

        _text = new TextBlock
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize   = DogFontSize,
            FontWeight = FontWeights.Bold,
            Foreground = brush,
            Text       = "",
            IsHitTestVisible = false,
            RenderTransform  = grp,
            Effect = new DropShadowEffect
            {
                BlurRadius  = 6,
                ShadowDepth = 0,
                Opacity     = 0.8,
                Color       = WpfColor.FromRgb(0xF8, 0xF4, 0xE8),
            },
        };
        Canvas.SetLeft(_text, TextLeftCenter);
        Canvas.SetTop (_text, TextRestTop);
        Root.Children.Add(_text);
    }

    private void RenderFrame(string frame) => _text.Text = frame;

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
            _animFrame = (_animFrame + 1) % FrSleep.Length;
            RenderFrame(FrSleep[_animFrame]);
        }
        if (_ball.BallGrabbed) TransitionTo(DogState.Waking);
    }

    private void TickWaking(double dt)
    {
        int n = FrWake.Length;
        int idx = Math.Min(n - 1, (int)(_stateTimer / (WakeDuration / n)));
        if (idx != _animFrame)
        {
            _animFrame = idx;
            RenderFrame(FrWake[idx]);
        }
        if (_stateTimer >= WakeDuration) TransitionTo(DogState.Standing);
    }

    private void TickStanding(double dt)
    {
        if (_animTimer >= StandIdleInterval)
        {
            _animTimer = 0;
            _animFrame = (_animFrame + 1) % FrStand.Length;
            RenderFrame(FrStand[_animFrame]);
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

        if (_animTimer >= RunFrameInterval)
        {
            _animTimer = 0;
            _animFrame = (_animFrame + 1) % FrRun.Length;
            RenderFrame(FrRun[_animFrame]);
        }

        double ballHeight = _floorY - _ball.BallY - _ball.BallRadius;
        double horiz = Math.Abs(_ball.BallX - _x);

        // Ground catch.
        if (ballHeight < 18 && horiz < GroundCatchHorizDx)
        {
            if (_ball.TryCaptureBall())
            {
                TransitionTo(DogState.Carrying);
                return;
            }
        }

        // Air catch — leap.
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

        int idx = _vy > 200 ? 0 : 1;
        if (idx != _animFrame) { _animFrame = idx; RenderFrame(FrJump[idx]); }

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

        if (_animTimer >= CarryFrameInterval)
        {
            _animTimer = 0;
            _animFrame = (_animFrame + 1) % FrCarry.Length;
            RenderFrame(FrCarry[_animFrame]);
        }

        if (Math.Abs(_x - _restX) < DropProximity && _yOffset <= 0)
        {
            _ball.DropAtRest();
            TransitionTo(DogState.LyingDown);
        }
    }

    private void TickLyingDown(double dt)
    {
        RenderFrame(FrLie[0]);
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

    // ── Helpers ──────────────────────────────────────────────────────────
    private void TransitionTo(DogState s)
    {
        _state = s;
        _stateTimer = 0;
        _animTimer = 0;
        _animFrame = 0;

        switch (s)
        {
            case DogState.Sleeping:  RenderFrame(FrSleep[0]); break;
            case DogState.Waking:    RenderFrame(FrWake[0]);  break;
            case DogState.Standing:  RenderFrame(FrStand[0]); break;
            case DogState.Following: RenderFrame(FrRun[0]);   break;
            case DogState.Jumping:   RenderFrame(FrJump[0]);  break;
            case DogState.Carrying:  RenderFrame(FrCarry[0]); break;
            case DogState.LyingDown: RenderFrame(FrLie[0]);   break;
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
            _textFlip.ScaleX = _facing;
        }
    }

    private (double x, double y) MouthScreen()
    {
        // Mouth coords inside the unflipped text frame.
        double mouthLocalX = TextLeftCenter + DogMouthXFromFrameLeft;
        double centerLocalX = TextLeftCenter + TextWidth / 2;
        if (_facing < 0)
            mouthLocalX = centerLocalX - (mouthLocalX - centerLocalX);

        double winLeft = _x - WindowWidth / 2;
        double mouthX  = winLeft + mouthLocalX;

        double mouthLocalY = TextRestTop + DogMouthYFromFrameTop;
        double winTop = _floorY - WindowHeight;
        double mouthY = winTop + mouthLocalY - _yOffset;

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

        _textTrans.Y = -_yOffset;
    }
}

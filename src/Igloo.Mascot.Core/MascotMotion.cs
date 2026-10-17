using System.Diagnostics.CodeAnalysis;

namespace Igloo.Mascot.Core;

public enum MascotAction { None, Wave, Point, Think, Celebrate }
public enum TaskCardState { Todo, Busy, Done, Failed }
public enum GazePriority { Drift, Cursor, Scanning, HeldCard, WaitingForUser }

public interface ILipSyncSource
{
    /// <summary>Current audio amplitude, in the range zero to one.</summary>
    double Amplitude { get; }
}

public static class MotionMath
{
    public static double Smooth(double current, double target, double dt, double rate) =>
        current + (target - current) * (1 - Math.Exp(-Math.Clamp(dt, 0, .05) * rate));

    public static double Envelope(double age, double duration) =>
        WingProfile.SmoothStep(age / .45) * (1 - WingProfile.SmoothStep((age - (duration - .5)) / .5));

    public static double CelebrateEnvelope(double age) =>
        WingProfile.SmoothStep(age / .25) * (1 - WingProfile.SmoothStep((age - 2.2) / .4));

    public static double Blink(double age) => age < 0 || age >= .2 ? 0 : 1 - Math.Abs(2 * age / .2 - 1);
}

public readonly record struct GazeInput(Vec2? Cursor, Vec2? HeldCard, Vec2? WaitingTarget,
    Vec2? ScanTarget = null);
public readonly record struct GazeResult(Vec2 Target, GazePriority Priority);

public static class GazeSolver
{
    public static GazeResult Resolve(GazeInput input, Vec2 drift)
    {
        if (input.WaitingTarget is Vec2 wait) return new(Clamp(wait), GazePriority.WaitingForUser);
        if (input.HeldCard is Vec2 card) return new(Clamp(card), GazePriority.HeldCard);
        if (input.ScanTarget is Vec2 scan) return new(Clamp(scan), GazePriority.Scanning);
        if (input.Cursor is Vec2 cursor) return new(Clamp(cursor), GazePriority.Cursor);
        return new(drift, GazePriority.Drift);
    }

    public static Vec2 FromScreen(Vec2 target, Vec2 avatarTopLeft, Vec2 avatarSize) => Clamp(new(
        (target.X - (avatarTopLeft.X + avatarSize.X * .5)) / avatarSize.X * 5,
        (target.Y - (avatarTopLeft.Y + avatarSize.Y * .28)) / avatarSize.Y * 5));

    private static Vec2 Clamp(Vec2 value) => new(Math.Clamp(value.X, -1, 1), Math.Clamp(value.Y, -1, 1));
}

public readonly record struct OrbitTarget(double X, double Y, double Scale, double Opacity, int Z);

public static class OrbitLayout
{
    public static OrbitTarget Place(int index, int count, double phase, TaskCardState state,
        double avatarCentreX, double avatarTop, double hostCentreX, double hostWidth)
    {
        double phi = phase + index * 2 * Math.PI / count;
        double depth = (Math.Cos(phi) + 1) / 2;
        double centreX = (avatarCentreX + hostCentreX) / 2;
        double radiusX = Math.Min(170, hostWidth * .27);
        return new(centreX + radiusX * Math.Sin(phi), avatarTop - 88 + 26 * Math.Cos(phi),
            .7 + .3 * depth, (state == TaskCardState.Todo ? .6 : .85) + .15 * depth,
            depth > .5 ? 3 : 0);
    }
}

public readonly record struct ManualPose(WingAngles Left, WingAngles Right, double HeadTilt,
    double HeadTurn, double MouthOpen, double EyesClosed);

public readonly record struct MascotFrame(WingAngles Left, WingAngles Right, double HeadTilt,
    double HeadTurn, double EyeX, double EyeY, double Bob, double Mouth, double Blink,
    double Hold, double Wait, GazePriority Gaze);

/// <summary>Pure animation state; host supplies normalized gaze coordinates and time.</summary>
[SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Blink and eye drift are visual only and deterministic in tests.")]
public sealed class MascotMotion
{
    private readonly Random _random;
    private double _nextBlink = 1.5, _blinkStart = -1, _nextEye = 1;
    private Vec2 _eyeDrift;
    private double _actionStart, _talkUntil, _pulse;
    private MascotAction _action;
    private MascotFrame _frame;
    private double _hold, _wait;

    public MascotMotion(int randomSeed = 1234) => _random = new(randomSeed);
    public MascotFrame Current => _frame;
    public MascotAction Action => _action;
    public double NextBlinkTime => _nextBlink;
    public ILipSyncSource? LipSyncSource { get; set; }

    public void Trigger(MascotAction action, double now)
    {
        _action = action;
        _actionStart = now;
    }

    public void Talk(double now, double duration = 3.5) => _talkUntil = now + duration;
    public void WordBoundary() => _pulse = 1;

    public MascotFrame Step(double time, double delta, ManualPose manual, GazeInput gaze,
        bool holding, bool waiting, bool reducedMotion, bool scanning = false)
    {
        double dt = Math.Clamp(delta, 0, .05);
        double c = reducedMotion ? .3 : 1;
        double Sn(double f, double p = 0) => Math.Sin(time * f + p) * c;
        double ls = Sn(1.1) * 1.5, la = Sn(1.3, .5) * 2, lh = Sn(1.7) * 3;
        double rs = Sn(1.1, 1) * 1.5, ra = Sn(1.3, 2) * 2, rh = Sn(1.7, 1) * 3;
        double hr = Sn(.5) * 1.6, hx = Sn(.37) * .8, bob = Sn(1.6) * .8, mouth = 0;

        if (time > _nextEye)
        {
            _eyeDrift = new((_random.NextDouble() - .5) * 2.4, (_random.NextDouble() - .5) * 1.6);
            _nextEye = time + 1.5 + _random.NextDouble() * 3;
        }
        Vec2? scanTarget = scanning
            ? new Vec2(Math.Cos(time * 2.5 + .4) * c, Math.Sin(time * 1.7 + .8) * .45 * c)
            : null;
        GazeResult look = GazeSolver.Resolve(gaze with { ScanTarget = scanTarget }, _eyeDrift);
        GazeResult unoccupied = GazeSolver.Resolve(new(gaze.Cursor, null, null, scanTarget), _eyeDrift);
        double ex = unoccupied.Priority == GazePriority.Drift ? unoccupied.Target.X : unoccupied.Target.X * 2.6;
        double ey = unoccupied.Priority == GazePriority.Drift ? unoccupied.Target.Y : unoccupied.Target.Y * 1.8;
        if (look.Priority == GazePriority.Cursor) { hx += look.Target.X * 1.6; hr += look.Target.X * 2.2; }
        if (look.Priority == GazePriority.Scanning)
        {
            hx = Math.Clamp(hx + look.Target.X * 3.7, -4, 4);
            hr = Math.Clamp(hr + look.Target.X * 6, -6, 6);
            ls += 52 * Math.Max(0, -look.Target.X) * c;
            rs += 52 * Math.Max(0, look.Target.X) * c;
        }

        _hold = MotionMath.Smooth(_hold, holding ? 1 : 0, dt, 5);
        _wait = MotionMath.Smooth(_wait, waiting ? 1 : 0, dt, 6);
        if (_hold > .01 && gaze.HeldCard is Vec2 card)
        {
            ex += (card.X * 2.6 - ex) * _hold;
            ey += (card.Y * 1.8 - ey) * _hold;
            hx -= 1.5 * _hold;
            hr -= 2 * _hold;
        }
        if (_wait > .01 && gaze.WaitingTarget is Vec2 target)
        {
            ex += (target.X * 2.6 - ex) * _wait;
            ey += (target.Y * 1.8 - ey) * _wait;
            hx += (-3.5 - hx) * _wait;
            hr += (-7 - hr) * _wait;
        }
        ls += _hold * 32;
        la += _hold * (80 + Math.Sin(time * 2.2) * 1.5);
        lh += _hold * 18;

        if (_action != MascotAction.None)
        {
            double age = time - _actionStart;
            double duration = _action switch { MascotAction.Wave => 3.2, MascotAction.Point => 3.4,
                MascotAction.Think => 4.2, _ => 2.6 };
            double env = MotionMath.Envelope(age, duration);
            switch (_action)
            {
                case MascotAction.Wave:
                    double w = WingProfile.SmoothStep((age - .3) / .3);
                    ls += env * 85; la += env * (38 + 22 * Math.Sin(11 * age) * w);
                    lh += env * 14 * Math.Sin(11 * age - 1) * w; hr -= 4 * env;
                    break;
                case MascotAction.Point:
                    ls += 48 * env; la += 6 * env; hx -= 3 * env; hr -= 5 * env;
                    ex += (-2.8 - ex) * env; ey += (-1.2 - ey) * env;
                    break;
                case MascotAction.Think:
                    hr += 8 * env; hx += 2 * env; rs += 70 * env; ra -= 80 * env; rh -= 15 * env;
                    ex += (2.2 * Math.Cos(1.6 * age) - ex) * env;
                    ey += (-1.6 + .9 * Math.Sin(1.6 * age) - ey) * env;
                    break;
                case MascotAction.Celebrate:
                    double e2 = MotionMath.CelebrateEnvelope(age);
                    ls += e2 * (110 + 8 * Math.Sin(14 * age));
                    rs += e2 * (110 + 8 * Math.Sin(14 * age + 1));
                    la += e2 * 12 * Math.Sin(14 * age); ra += e2 * 12 * Math.Sin(14 * age + 1);
                    bob -= Math.Abs(Math.Sin(7 * age)) * 7 * e2;
                    hr += 3 * Math.Sin(7 * age) * e2; mouth = Math.Max(mouth, .5 * e2);
                    break;
            }
            if (age > duration) _action = MascotAction.None;
        }
        if (_talkUntil > time)
        {
            _pulse *= Math.Exp(-6 * dt);
            double synthetic = Math.Abs(Math.Sin(13 * time) * Math.Sin(5.3 * time + 1));
            double level = LipSyncSource is null ? synthetic : Math.Clamp(LipSyncSource.Amplitude, 0, 1);
            mouth = Math.Clamp(.12 + .6 * level + .28 * _pulse, 0, 1);
            hr += 2.2 * Math.Sin(4.1 * time); hx += Math.Sin(2.7 * time);
            rs += 6 + 5 * Math.Sin(3.3 * time); ra += 8 * Math.Sin(3.3 * time + 1);
        }

        WingAngles left = _frame.Left, right = _frame.Right;
        left = new(MotionMath.Smooth(left.Shoulder, ls + Math.Clamp(manual.Left.Shoulder, -20, 160), dt, 9),
            MotionMath.Smooth(left.Arm, la + Math.Clamp(manual.Left.Arm, -90, 90), dt, 9),
            MotionMath.Smooth(left.Hand, lh + Math.Clamp(manual.Left.Hand, -70, 70), dt, 9));
        right = new(MotionMath.Smooth(right.Shoulder, rs + Math.Clamp(manual.Right.Shoulder, -20, 160), dt, 9),
            MotionMath.Smooth(right.Arm, ra + Math.Clamp(manual.Right.Arm, -90, 90), dt, 9),
            MotionMath.Smooth(right.Hand, rh + Math.Clamp(manual.Right.Hand, -70, 70), dt, 9));

        if (time > _nextBlink && _blinkStart < 0)
        {
            _blinkStart = time;
            _nextBlink = time + 2 + _random.NextDouble() * 3.5;
        }
        double blink = MotionMath.Blink(time - _blinkStart);
        if (_blinkStart >= 0 && time - _blinkStart >= .2) _blinkStart = -1;
        _frame = new(left, right,
            MotionMath.Smooth(_frame.HeadTilt, hr + Math.Clamp(manual.HeadTilt, -15, 15), dt, 8),
            MotionMath.Smooth(_frame.HeadTurn, hx + Math.Clamp(manual.HeadTurn, -8, 8), dt, 8),
            MotionMath.Smooth(_frame.EyeX, ex, dt, 18), MotionMath.Smooth(_frame.EyeY, ey, dt, 18),
            MotionMath.Smooth(_frame.Bob, bob, dt, 14),
            MotionMath.Smooth(_frame.Mouth, Math.Clamp(mouth + manual.MouthOpen, 0, 1), dt, 28),
            Math.Clamp(Math.Max(blink, manual.EyesClosed), 0, 1), _hold, _wait, look.Priority);
        return _frame;
    }
}

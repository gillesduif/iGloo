using Igloo.Mascot.Core;
using Xunit;

namespace Igloo.Mascot.Core.Tests;

public sealed class WingSkinTests
{
    private sealed class FixedLipSync(double amplitude) : ILipSyncSource
    {
        public double Amplitude => amplitude;
    }

    private static readonly (WingProfile Left, WingProfile Right) Profiles = WingProfile.LoadArtwork();
    private static readonly WingTuning Default = new();

    private static void Near(Vec2 actual, double x, double y, double tolerance = 1)
    {
        Assert.InRange(actual.X, x - tolerance, x + tolerance);
        Assert.InRange(actual.Y, y - tolerance, y + tolerance);
    }

    [Fact]
    public void ExportedSvgSamplesMatchBrowserPivotsAndStations()
    {
        WingProfile left = Profiles.Left, right = Profiles.Right;
        Near(left.Pivot, 65.39, 103.43);
        Near(left.Tip, 74.72, 215.63);
        Assert.InRange(left.Length, 111.58, 113.58);
        Assert.InRange(left.AMin, -11.97, -9.97);
        Assert.InRange(left.AMax, 111.59, 113.59);
        Near(left.RestElbow(Default), 42.34, 148.28);
        Near(left.RestWrist(Default), 54.56, 192.45);
        Assert.Equal(15, left.GlareStart);
        Assert.Equal(44, left.GlareEnd);
        CheckStation(left, 16, 20.41, 13.54, 13.10);
        CheckStation(left, 32, 51.79, 31.16, 12.09);
        CheckStation(left, 56, 98.86, 9.25, 26.56);

        Near(right.Pivot, 161.96, 127.92);
        Near(right.Tip, 172.66, 197.15);
        Assert.InRange(right.Length, 69.05, 71.05);
        Assert.InRange(right.AMin, -42.48, -40.48);
        Assert.InRange(right.AMax, 69.05, 71.05);
        Near(right.RestElbow(Default), 182.07, 151.75);
        Near(right.RestWrist(Default), 175.89, 181.06);
        Assert.Equal(17, right.GlareStart);
        Assert.Equal(58, right.GlareEnd);
        CheckStation(right, 24, 1.01, -11.34, 19.15);
        CheckStation(right, 40, 29.33, -15.83, 21.30);
    }

    private static void CheckStation(WingProfile profile, int index, double a, double c, double width)
    {
        Assert.InRange(profile.A[index], a - 1, a + 1);
        Assert.InRange(profile.C[index], c - 1, c + 1);
        Assert.InRange(profile.Width[index], width - 1.5, width + 1.5);
    }

    [Theory]
    [InlineData(0, 0, 0, 74.72, 215.63)]
    [InlineData(45, 25, 15, -41.28, 167.54)]
    [InlineData(60, 50, 30, -61.00, 97.47)]
    [InlineData(32, 80, 18, -54.17, 124.58)]
    [InlineData(100, 0, 0, -46.72, 93.14)]
    public void LeftNestedMatrixMatchesBrowserTip(double shoulder, double arm, double hand, double x, double y) =>
        Near(Profiles.Left.Joints(new(shoulder, arm, hand), Default).HandTip, x, y);

    [Theory]
    [InlineData(45, 25, 15, 234.20, 151.01)]
    [InlineData(60, 50, 30, 235.13, 107.96)]
    [InlineData(32, 80, 18, 235.53, 124.36)]
    [InlineData(100, 0, 0, 228.28, 105.37)]
    public void RightAnglesAreNegatedBeforeNestedMatrix(double shoulder, double arm, double hand, double x, double y) =>
        Near(Profiles.Right.Joints(new(shoulder, arm, hand), Default, true).HandTip, x, y);

    [Fact]
    public void JointsMatchBrowser()
    {
        WingJoints left = Profiles.Left.Joints(new(45, 25, 15), Default);
        Near(left.Elbow, 17.38, 118.84);
        Near(left.Wrist, -19.95, 145.43);
        WingJoints right = Profiles.Right.Joints(new(45, 25, 15), Default, true);
        Near(right.Elbow, 193.03, 130.56);
        Near(right.Wrist, 218.45, 146.39);
    }

    [Fact]
    public void TuningClampsToSupportedRange()
    {
        Assert.Equal(new WingTuning(.15, .95, .04), new WingTuning(-1, 5, 0).Clamped);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(140)]
    public void SkinKeepsRootFixedAndThicknessConstant(double shoulder)
    {
        WingProfile profile = Profiles.Left;
        var deformed = new Vec2[WingProfile.Stations];
        var normals = new Vec2[WingProfile.Stations];
        profile.Deform(new(shoulder, -70, -40), Default, false, deformed, normals);
        double bs = .1 * profile.Length;
        for (int i = 0; i < WingProfile.Stations; i++)
        {
            if (profile.A[i] < -bs) Near(deformed[i], profile.Rest[i].X, profile.Rest[i].Y, 1e-7);
            Vec2 upper = deformed[i] + normals[i] * (profile.Width[i] / 2);
            Vec2 lower = deformed[i] - normals[i] * (profile.Width[i] / 2);
            Assert.InRange((upper - lower).Length, profile.Width[i] - 1e-7, profile.Width[i] + 1e-7);
        }
    }

    [Fact]
    public void SmoothingIsFrameRateIndependent()
    {
        double a = 0, b = 0;
        for (int i = 0; i < 20; i++) a = MotionMath.Smooth(a, 100, .01, 9);
        for (int i = 0; i < 4; i++) b = MotionMath.Smooth(b, 100, .05, 9);
        Assert.InRange(Math.Abs(a - b), 0, 1e-10);
        Assert.InRange(a, 80, 90);
    }

    [Fact]
    public void OrbitAndGazePrioritiesAreDeterministic()
    {
        OrbitTarget front = OrbitLayout.Place(0, 4, 0, TaskCardState.Busy, 100, 200, 200, 500);
        OrbitTarget back = OrbitLayout.Place(2, 4, 0, TaskCardState.Todo, 100, 200, 200, 500);
        Assert.True(front.Z > back.Z);
        Assert.True(front.Scale > back.Scale);
        Assert.True(front.Opacity > back.Opacity);
        Assert.Equal(GazePriority.WaitingForUser,
            GazeSolver.Resolve(new(new(1, 1), new(1, 1), new(-1, 1)), default).Priority);
        Assert.Equal(GazePriority.HeldCard,
            GazeSolver.Resolve(new(new(1, 1), new(0, 0), null), default).Priority);
        Assert.Equal(GazePriority.Cursor,
            GazeSolver.Resolve(new(new(1, 1), null, null), default).Priority);
        Assert.Equal(GazePriority.Scanning,
            GazeSolver.Resolve(new(new(1, 1), null, null, new(-1, 0)), default).Priority);
        Assert.Equal(GazePriority.HeldCard,
            GazeSolver.Resolve(new(null, new(0, 0), null, new(-1, 0)), default).Priority);
        Assert.Equal(GazePriority.WaitingForUser,
            GazeSolver.Resolve(new(null, null, new(0, 1), new(-1, 0)), default).Priority);
    }

    [Fact]
    public void SystemScanLooksAroundInsteadOfFollowingTheCursor()
    {
        var motion = new MascotMotion();
        MascotFrame scanning = motion.Step(1, .05, default,
            new(new(-1, 0), null, null), false, false, false, scanning: true);
        Assert.Equal(GazePriority.Scanning, scanning.Gaze);
        Assert.True(scanning.EyeX < -1);
        Assert.True(scanning.HeadTilt < -1);
        Assert.True(scanning.Left.Shoulder > scanning.Right.Shoulder);

        MascotFrame opposite = default;
        for (int i = 0; i < 12; i++)
            opposite = motion.Step(2 + i * .02, .02, default,
                new(new(-1, 0), null, null), false, false, false, scanning: true);
        Assert.True(opposite.EyeX > 0);
        Assert.True(opposite.HeadTilt > 0);
        Assert.True(opposite.Right.Shoulder > opposite.Left.Shoulder);

        MascotFrame finished = motion.Step(4, .05, default,
            new(new(-1, 0), null, null), false, false, false);
        Assert.Equal(GazePriority.Cursor, finished.Gaze);
    }

    [Fact]
    public void HeldAndWaitingGazeRampWithTheirWeights()
    {
        var held = new MascotMotion();
        MascotFrame card = held.Step(0, .05, default, new(null, new(1, 0), null), true, false, false);
        Assert.InRange(card.Hold, .2, .25);
        Assert.InRange(card.EyeX, .1, .5);
        Assert.Equal(GazePriority.HeldCard, card.Gaze);

        var waiting = new MascotMotion();
        MascotFrame input = waiting.Step(0, .05, default, new(null, null, new(-1, 1)), false, true, false);
        Assert.InRange(input.Wait, .24, .27);
        Assert.InRange(input.EyeX, -.6, -.1);
        Assert.InRange(input.EyeY, .1, .5);
        Assert.Equal(GazePriority.WaitingForUser, input.Gaze);
    }

    [Fact]
    public void ActionAndBlinkEnvelopesHaveExpectedBounds()
    {
        Assert.Equal(0, MotionMath.Envelope(0, 3.2));
        Assert.InRange(MotionMath.Envelope(1.6, 3.2), .99, 1);
        Assert.Equal(0, MotionMath.Envelope(3.2, 3.2));
        Assert.Equal(0, MotionMath.CelebrateEnvelope(0));
        Assert.InRange(MotionMath.CelebrateEnvelope(1.3), .99, 1);
        Assert.Equal(0, MotionMath.CelebrateEnvelope(2.6));
        Assert.Equal(0, MotionMath.Blink(0));
        Assert.InRange(MotionMath.Blink(.1), .999, 1);
        Assert.Equal(0, MotionMath.Blink(.2));
    }

    [Theory]
    [InlineData(MascotAction.Wave, 1.6, true, false)]
    [InlineData(MascotAction.Point, 1.6, true, false)]
    [InlineData(MascotAction.Think, 1.6, false, true)]
    [InlineData(MascotAction.Celebrate, 1.3, true, true)]
    public void ActionsRaiseTheIntendedWings(MascotAction action, double age, bool left, bool right)
    {
        var baseline = new MascotMotion();
        MascotFrame idle = baseline.Step(age, .05, default, default, false, false, false);
        var active = new MascotMotion();
        active.Trigger(action, 0);
        MascotFrame posed = active.Step(age, .05, default, default, false, false, false);
        if (left) Assert.True(posed.Left.Shoulder > idle.Left.Shoulder + 10);
        if (right) Assert.True(posed.Right.Shoulder > idle.Right.Shoulder + 10);
    }

    [Fact]
    public void LipSyncAmplitudeAndWordPulseDriveTheMouth()
    {
        var motion = new MascotMotion { LipSyncSource = new FixedLipSync(1) };
        motion.Talk(0, 1);
        MascotFrame level = motion.Step(0, .05, default, default, false, false, false);
        Assert.InRange(level.Mouth, .5, .6);
        motion.WordBoundary();
        MascotFrame pulse = motion.Step(.05, .05, default, default, false, false, false);
        Assert.True(pulse.Mouth > level.Mouth);
    }

    [Fact]
    public void BlinkSchedulesTwoToFiveAndHalfSecondsLater()
    {
        var motion = new MascotMotion(42);
        motion.Step(1.51, .05, default, default, false, false, false);
        Assert.InRange(motion.NextBlinkTime - 1.51, 2, 5.5);
        Assert.Equal(0, motion.Current.Blink);
        motion.Step(1.61, .05, default, default, false, false, false);
        Assert.InRange(motion.Current.Blink, .99, 1);
    }

    [Fact]
    public void ManualAnglesClampBeforeTheyAreAddedToIdle()
    {
        var motion = new MascotMotion();
        var manual = new ManualPose(new(1000, -1000, 1000), new(-1000, 1000, -1000),
            1000, -1000, 10, 10);
        MascotFrame frame = default;
        for (int i = 0; i < 200; i++)
            frame = motion.Step(i * .05, .05, manual, default, false, false, false);
        Assert.InRange(frame.Left.Shoulder, 158.4, 161.6);
        Assert.InRange(frame.Left.Arm, -92.1, -87.9);
        Assert.InRange(frame.Left.Hand, 66.9, 73.1);
        Assert.InRange(frame.Right.Shoulder, -21.6, -18.4);
        Assert.InRange(frame.Mouth, .99, 1);
        Assert.InRange(frame.Blink, .99, 1);
    }
}

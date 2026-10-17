using System.Reflection;
using System.Text.Json;

namespace Igloo.Mascot.Core;

public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);
    public static Vec2 operator /(Vec2 a, double k) => new(a.X / k, a.Y / k);
    public static Vec2 Add(Vec2 a, Vec2 b) => a + b;
    public static Vec2 Subtract(Vec2 a, Vec2 b) => a - b;
    public static Vec2 Multiply(Vec2 a, double k) => a * k;
    public static Vec2 Divide(Vec2 a, double k) => a / k;
    public double Length => Math.Sqrt(X * X + Y * Y);
    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
    public static Vec2 Lerp(Vec2 a, Vec2 b, double k) => a + (b - a) * k;
}

/// <summary>SVG column-vector composition, independent of WPF's row-vector Matrix convention.</summary>
public readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public Vec2 Apply(Vec2 p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);

    public static Affine Rotation(double degrees, Vec2 pivot)
    {
        double radians = degrees * Math.PI / 180;
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new(c, s, -s, c, pivot.X - c * pivot.X + s * pivot.Y,
            pivot.Y - s * pivot.X - c * pivot.Y);
    }

    // parent * child: child acts first, exactly as DOMMatrix.multiply does here.
    public Affine Multiply(Affine child) => new(
        A * child.A + C * child.B, B * child.A + D * child.B,
        A * child.C + C * child.D, B * child.C + D * child.D,
        A * child.E + C * child.F + E, B * child.E + D * child.F + F);
}

public readonly record struct WingAngles(double Shoulder, double Arm, double Hand);
public readonly record struct WingTuning(double Elbow = .38, double Wrist = .78, double Blend = .14)
{
    public WingTuning() : this(.38, .78, .14) { }
    public WingTuning Clamped => new(Math.Clamp(Elbow, .15, .6),
        Math.Clamp(Wrist, .55, .95), Math.Clamp(Blend, .04, .3));
}

public readonly record struct WingJoints(Vec2 Shoulder, Vec2 Elbow, Vec2 Wrist, Vec2 HandTip, Affine HandMatrix);

/// <summary>Measured centre line and thickness of one original SVG wing.</summary>
public sealed class WingProfile
{
    public const int Stations = 64;
    public Vec2 Pivot { get; }
    public Vec2 Tip { get; }
    public double Length { get; }
    public double AMin { get; }
    public double AMax { get; }
    public int GlareStart { get; }
    public int GlareEnd { get; }
    private readonly double[] _a = new double[Stations];
    private readonly double[] _c = new double[Stations];
    private readonly double[] _width = new double[Stations];
    private readonly double[] _glareLow = new double[Stations];
    private readonly double[] _glareHigh = new double[Stations];
    private readonly Vec2[] _rest = new Vec2[Stations];
    public ReadOnlySpan<double> A => _a;
    public ReadOnlySpan<double> C => _c;
    public ReadOnlySpan<double> Width => _width;
    public ReadOnlySpan<double> GlareLow => _glareLow;
    public ReadOnlySpan<double> GlareHigh => _glareHigh;
    public ReadOnlySpan<Vec2> Rest => _rest;

    private readonly Vec2 _u;
    private readonly Vec2 _v;

    private WingProfile(Vec2[] root, Vec2[][] far, Vec2[][] bases, Vec2[][] glares)
    {
        Vec2 start = root[0], sum = default;
        int count = 0;
        foreach (Vec2 q in root)
        {
            if ((q - start).Length < 16) { sum += q; count++; }
        }
        Pivot = sum / count;
        Vec2 farthest = Pivot;
        double farDistance = 0;
        foreach (Vec2[] path in far)
        {
            foreach (Vec2 q in path)
            {
                double distance = (q - Pivot).Length;
                if (distance > farDistance) { farthest = q; farDistance = distance; }
            }
        }
        Tip = farthest;
        Length = farDistance;
        _u = (Tip - Pivot) / Length;
        _v = new(-_u.Y, _u.X);

        var baseSamples = Project(bases);
        var glareSamples = Project(glares);
        AMin = double.PositiveInfinity;
        AMax = double.NegativeInfinity;
        foreach ((double a, _) in baseSamples)
        {
            AMin = Math.Min(AMin, a);
            AMax = Math.Max(AMax, a);
        }
        double da = (AMax - AMin) / (Stations - 1), window = da * 1.4;
        var rawWidth = new double[Stations];
        Array.Fill(_c, double.NaN);
        Array.Fill(rawWidth, double.NaN);
        Array.Fill(_glareLow, double.NaN);
        Array.Fill(_glareHigh, double.NaN);
        for (int i = 0; i < Stations; i++)
        {
            double station = AMin + i * da;
            _a[i] = station;
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            double gl = double.PositiveInfinity, gh = double.NegativeInfinity;
            foreach ((double a, double b) in baseSamples)
            {
                if (Math.Abs(a - station) < window) { lo = Math.Min(lo, b); hi = Math.Max(hi, b); }
            }
            foreach ((double a, double b) in glareSamples)
            {
                if (Math.Abs(a - station) < window) { gl = Math.Min(gl, b); gh = Math.Max(gh, b); }
            }
            if (lo <= hi) { _c[i] = (lo + hi) / 2; rawWidth[i] = hi - lo; }
            if (gl <= gh) { _glareLow[i] = gl; _glareHigh[i] = gh; }
        }
        FillGapsByNearest(_c);
        FillGapsByNearest(rawWidth);
        Smooth(_c);
        Smooth(_c);
        Smooth(rawWidth);
        Smooth(rawWidth);
        int g0 = -1, g1 = -1;
        for (int i = 0; i < Stations; i++)
        {
            if (!double.IsNaN(_glareLow[i]))
            {
                double half = rawWidth[i] / 2;
                _glareLow[i] = Math.Clamp((_glareLow[i] - _c[i]) / half, -1, 1);
                _glareHigh[i] = Math.Clamp((_glareHigh[i] - _c[i]) / half, -1, 1);
                if (g0 < 0) g0 = i;
                g1 = i;
            }
            int i0 = Math.Max(0, i - 1), i1 = Math.Min(Stations - 1, i + 1);
            double dc = (_c[i1] - _c[i0]) / ((i1 - i0) * da);
            _width[i] = Math.Max(3, rawWidth[i] / Math.Sqrt(1 + dc * dc));
            _rest[i] = At(_a[i], _c[i]);
        }
        GlareStart = g0;
        GlareEnd = g1;
        for (int i = g0 + 1; i < g1; i++)
        {
            if (!double.IsNaN(_glareLow[i])) continue;
            int next = i + 1;
            while (double.IsNaN(_glareLow[next])) next++;
            double t = 1.0 / (next - i + 1);
            _glareLow[i] = _glareLow[i - 1] + (_glareLow[next] - _glareLow[i - 1]) * t;
            _glareHigh[i] = _glareHigh[i - 1] + (_glareHigh[next] - _glareHigh[i - 1]) * t;
        }
    }

    private List<(double A, double B)> Project(Vec2[][] paths)
    {
        var result = new List<(double, double)>();
        foreach (Vec2[] path in paths)
            foreach (Vec2 q in path)
            {
                Vec2 d = q - Pivot;
                result.Add((Vec2.Dot(d, _u), Vec2.Dot(d, _v)));
            }
        return result;
    }

    private static void FillGapsByNearest(double[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (!double.IsNaN(values[i])) continue;
            int j = i, k = i;
            while (j < values.Length && double.IsNaN(values[j])) j++;
            while (k >= 0 && double.IsNaN(values[k])) k--;
            values[i] = k >= 0 ? values[k] : values[j];
        }
    }

    private static void Smooth(double[] values)
    {
        var copy = (double[])values.Clone();
        for (int i = 1; i < values.Length - 1; i++)
            copy[i] = (values[i - 1] + 2 * values[i] + values[i + 1]) / 4;
        Array.Copy(copy, values, values.Length);
    }

    private Vec2 At(double a, double c) => Pivot + _u * a + _v * c;

    private double CentreAt(double a)
    {
        if (a <= A[0]) return C[0];
        for (int i = 1; i < Stations; i++)
            if (a <= A[i]) return C[i - 1] + (C[i] - C[i - 1]) * ((a - A[i - 1]) / (A[i] - A[i - 1]));
        return C[^1];
    }

    public WingJoints Joints(WingAngles angles, WingTuning tuning, bool viewerRight = false)
    {
        WingTuning cfg = tuning.Clamped;
        double sign = viewerRight ? -1 : 1;
        Vec2 elbow = At(Length * cfg.Elbow, CentreAt(Length * cfg.Elbow));
        Vec2 wrist = At(Length * cfg.Wrist, CentreAt(Length * cfg.Wrist));
        Affine m0 = Affine.Rotation(sign * angles.Shoulder, Pivot);
        Affine m1 = m0.Multiply(Affine.Rotation(sign * angles.Arm, elbow));
        Affine m2 = m1.Multiply(Affine.Rotation(sign * angles.Hand, wrist));
        return new(Pivot, m0.Apply(elbow), m1.Apply(wrist), m2.Apply(Tip), m2);
    }

    public Vec2 RestElbow(WingTuning tuning) { double a = Length * tuning.Clamped.Elbow; return At(a, CentreAt(a)); }
    public Vec2 RestWrist(WingTuning tuning) { double a = Length * tuning.Clamped.Wrist; return At(a, CentreAt(a)); }

    public WingJoints Deform(WingAngles angles, WingTuning tuning, bool viewerRight,
        Span<Vec2> deformed, Span<Vec2> normals)
    {
        WingTuning cfg = tuning.Clamped;
        double sign = viewerRight ? -1 : 1;
        Vec2 elbow = RestElbow(cfg), wrist = RestWrist(cfg);
        Affine m0 = Affine.Rotation(sign * angles.Shoulder, Pivot);
        Affine m1 = m0.Multiply(Affine.Rotation(sign * angles.Arm, elbow));
        Affine m2 = m1.Multiply(Affine.Rotation(sign * angles.Hand, wrist));
        double bs = .1 * Length, bw = cfg.Blend * Length;
        for (int i = 0; i < Stations; i++)
        {
            Vec2 p = Rest[i]; double a = A[i];
            Vec2 q = Vec2.Lerp(p, m0.Apply(p), SmoothStep((a + bs) / (2 * bs)));
            q = Vec2.Lerp(q, m1.Apply(p), SmoothStep((a - (Length * cfg.Elbow - bw)) / (2 * bw)));
            deformed[i] = Vec2.Lerp(q, m2.Apply(p), SmoothStep((a - (Length * cfg.Wrist - bw)) / (2 * bw)));
        }
        for (int i = 0; i < Stations; i++)
        {
            Vec2 tangent = deformed[Math.Min(Stations - 1, i + 1)] - deformed[Math.Max(0, i - 1)];
            double length = Math.Max(1e-9, tangent.Length);
            normals[i] = new(-tangent.Y / length, tangent.X / length);
        }
        return new(Pivot, m0.Apply(elbow), m1.Apply(wrist), m2.Apply(Tip), m2);
    }

    public static double SmoothStep(double value)
    {
        double t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }

    public static (WingProfile Left, WingProfile Right) LoadArtwork()
    {
        using Stream stream = typeof(WingProfile).Assembly.GetManifestResourceStream(
            "Igloo.Mascot.Core.Artwork.WingSamples.json")!;
        using JsonDocument document = JsonDocument.Parse(stream);
        return (FromJson(document.RootElement.GetProperty("left")), FromJson(document.RootElement.GetProperty("right")));
    }

    private static WingProfile FromJson(JsonElement side) => new(
        ReadPath(side.GetProperty("root")), ReadPaths(side.GetProperty("far")),
        ReadPaths(side.GetProperty("base")), ReadPaths(side.GetProperty("glare")));

    private static Vec2[][] ReadPaths(JsonElement paths)
    {
        var result = new Vec2[paths.GetArrayLength()][];
        int i = 0;
        foreach (JsonElement path in paths.EnumerateArray()) result[i++] = ReadPath(path);
        return result;
    }

    private static Vec2[] ReadPath(JsonElement path)
    {
        var result = new Vec2[path.GetArrayLength()];
        int i = 0;
        foreach (JsonElement point in path.EnumerateArray())
            result[i++] = new(point[0].GetDouble(), point[1].GetDouble());
        return result;
    }
}

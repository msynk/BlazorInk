using System.Numerics;
using BlazorInk.Geometry;

namespace BlazorInk;

/// <summary>
/// A stack-only, allocation-free view over stroke points that works for both the immutable
/// <see cref="StrokePoints"/> and the live <see cref="StrokePointBuffer"/>. Geometry generators take this
/// so the same code renders committed and in-progress strokes.
/// </summary>
public readonly ref struct StrokePointsView
{
    /// <summary>Creates a view over parallel spans.</summary>
    public StrokePointsView(ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> pressure, ReadOnlySpan<float> time, Rect bounds)
    {
        X = x;
        Y = y;
        Pressure = pressure;
        Time = time;
        Bounds = bounds;
    }

    /// <summary>X coordinates.</summary>
    public ReadOnlySpan<float> X { get; }

    /// <summary>Y coordinates.</summary>
    public ReadOnlySpan<float> Y { get; }

    /// <summary>Pressure values.</summary>
    public ReadOnlySpan<float> Pressure { get; }

    /// <summary>Time values in milliseconds.</summary>
    public ReadOnlySpan<float> Time { get; }

    /// <summary>Bounds of positions.</summary>
    public Rect Bounds { get; }

    /// <summary>Number of points.</summary>
    public int Count => X.Length;

    /// <summary>Position of a point.</summary>
    public Vector2 PositionAt(int index) => new(X[index], Y[index]);
}

/// <summary>Extension methods to obtain views.</summary>
public static class StrokePointsViewExtensions
{
    /// <summary>Creates a view over immutable points.</summary>
    public static StrokePointsView AsView(this StrokePoints points) => new(points.X, points.Y, points.Pressure, points.Time, points.Bounds);

    /// <summary>Creates a view over a live buffer.</summary>
    public static StrokePointsView AsView(this StrokePointBuffer buffer) => new(buffer.X, buffer.Y, buffer.Pressure, buffer.Time, buffer.Bounds);
}

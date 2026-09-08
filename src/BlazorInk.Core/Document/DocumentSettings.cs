using BlazorInk.Geometry;

namespace BlazorInk;

/// <summary>Page-level settings of a document.</summary>
public sealed record DocumentSettings
{
    /// <summary>Default: 800 by 600 units, white background.</summary>
    public static readonly DocumentSettings Default = new();

    /// <summary>Page width in document units, or 0 for an unbounded canvas.</summary>
    public float Width { get; init; } = 800f;

    /// <summary>Page height in document units, or 0 for an unbounded canvas.</summary>
    public float Height { get; init; } = 600f;

    /// <summary>Background color. Use <see cref="InkColor.Transparent"/> for no background.</summary>
    public InkColor Background { get; init; } = InkColor.White;

    /// <summary>True when the page has a finite size.</summary>
    public bool HasFixedSize => Width > 0 && Height > 0;

    /// <summary>The page rectangle (empty for unbounded documents).</summary>
    public Rect PageRect => HasFixedSize ? new Rect(0, 0, Width, Height) : Rect.Empty;
}

/// <summary>Who caused a document change. History records local changes only; collaboration forwards local and undo/redo changes.</summary>
public enum ChangeOrigin : byte
{
    /// <summary>A tool or API call in this session.</summary>
    Local = 0,
    /// <summary>An undo of a previous local change.</summary>
    Undo = 1,
    /// <summary>A redo of a previously undone change.</summary>
    Redo = 2,
    /// <summary>A change received from another collaborator.</summary>
    Remote = 3,
    /// <summary>The document was loaded or reset.</summary>
    Load = 4,
}

/// <summary>Configurable resource limits that protect against pathological documents. All limits are inclusive maxima.</summary>
public sealed record DocumentLimits
{
    /// <summary>Generous defaults suitable for interactive editors.</summary>
    public static readonly DocumentLimits Default = new();

    /// <summary>Maximum number of layers.</summary>
    public int MaxLayers { get; init; } = 256;

    /// <summary>Maximum number of elements across all layers.</summary>
    public int MaxElements { get; init; } = 200_000;

    /// <summary>Maximum number of points in a single stroke.</summary>
    public int MaxPointsPerStroke { get; init; } = 100_000;

    /// <summary>Maximum total number of stroke points in the document.</summary>
    public long MaxTotalPoints { get; init; } = 20_000_000;

    /// <summary>Maximum absolute coordinate value accepted from untrusted input.</summary>
    public float MaxCoordinate { get; init; } = 1_000_000f;

    /// <summary>Maximum brush/stroke width accepted from untrusted input.</summary>
    public float MaxStrokeWidth { get; init; } = 2_000f;
}

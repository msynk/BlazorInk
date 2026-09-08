namespace BlazorInk.Brushes;

/// <summary>
/// Maps serialized brush kinds to factories so documents containing custom brushes can be loaded.
/// The built-in brushes are registered by default. Registration is thread-safe.
/// </summary>
public sealed class BrushRegistry
{
    private readonly Dictionary<string, Func<Brush>> _factories = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>The shared default registry containing the built-in brushes.</summary>
    public static BrushRegistry Default { get; } = CreateDefault();

    /// <summary>Creates an empty registry.</summary>
    public BrushRegistry() { }

    private static BrushRegistry CreateDefault()
    {
        var registry = new BrushRegistry();
        registry.Register(PenBrush.KindName, static () => new PenBrush());
        registry.Register(PencilBrush.KindName, static () => new PencilBrush());
        registry.Register(MarkerBrush.KindName, static () => new MarkerBrush());
        registry.Register(HighlighterBrush.KindName, static () => new HighlighterBrush());
        registry.Register(PixelEraserBrush.KindName, static () => new PixelEraserBrush());
        return registry;
    }

    /// <summary>Registers (or replaces) a factory for a kind. The factory returns a brush with default values;
    /// serialized properties are applied on top of it.</summary>
    public void Register(string kind, Func<Brush> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate) _factories[kind] = factory;
    }

    /// <summary>Registers a brush type by its parameterless constructor.</summary>
    public void Register<TBrush>() where TBrush : Brush, new()
    {
        var sample = new TBrush();
        Register(sample.Kind, static () => new TBrush());
    }

    /// <summary>Tries to create a default instance of the brush for a kind.</summary>
    public bool TryCreate(string kind, out Brush? brush)
    {
        Func<Brush>? factory;
        lock (_gate) _factories.TryGetValue(kind, out factory);
        brush = factory?.Invoke();
        return brush is not null;
    }

    /// <summary>True when the kind is known.</summary>
    public bool Contains(string kind)
    {
        lock (_gate) return _factories.ContainsKey(kind);
    }

    /// <summary>Registered kinds.</summary>
    public IReadOnlyCollection<string> Kinds
    {
        get { lock (_gate) return _factories.Keys.ToArray(); }
    }
}

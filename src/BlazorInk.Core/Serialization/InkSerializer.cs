using System.Buffers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using BlazorInk.Brushes;
using BlazorInk.Geometry;
using BlazorInk.Rendering;

namespace BlazorInk.Serialization;

/// <summary>
/// Reads and writes the BlazorInk document format: versioned JSON with compact binary stroke points.
/// </summary>
/// <remarks>
/// <para>
/// The format is a stable contract, not a projection of the library's classes. Every element carries a
/// <c>type</c> discriminator and only documented fields; unknown fields are ignored on read, and unknown element
/// types are preserved verbatim, so documents survive round trips through other versions.
/// </para>
/// <para>
/// All input is treated as untrusted: sizes, counts and coordinates are validated against
/// <see cref="InkDeserializerOptions.Limits"/> and malformed input produces an <see cref="InkFormatException"/>
/// rather than an unbounded allocation.
/// </para>
/// </remarks>
public static class InkSerializer
{
    /// <summary>The format identifier written into every document.</summary>
    public const string FormatName = "blazorink";

    /// <summary>The current schema version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The oldest schema version this library can read.</summary>
    public const int MinimumSupportedVersion = 1;

    /// <summary>Characters never allowed in a font family, because the value is emitted into CSS and SVG.</summary>
    private static readonly SearchValues<char> UnsafeFontCharacters = SearchValues.Create("<>\"'{}();\\");

    // ---- Writing ---------------------------------------------------------------------------------------------------

    /// <summary>Serializes a document to a JSON string.</summary>
    public static string Serialize(InkDocument document, InkSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var buffer = new ArrayBufferWriter<byte>(EstimateSize(document, options));
        Serialize(document, buffer, options);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Serializes a document as UTF-8 bytes.</summary>
    public static byte[] SerializeToUtf8Bytes(InkDocument document, InkSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var buffer = new ArrayBufferWriter<byte>(EstimateSize(document, options));
        Serialize(document, buffer, options);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Serializes a document into a stream.</summary>
    public static void Serialize(InkDocument document, Stream stream, InkSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);
        options ??= InkSerializerOptions.Default;
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = options.Indented, SkipValidation = true });
        Write(writer, document, options);
    }

    /// <summary>Serializes a document into a buffer writer.</summary>
    public static void Serialize(InkDocument document, IBufferWriter<byte> buffer, InkSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(buffer);
        options ??= InkSerializerOptions.Default;
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = options.Indented, SkipValidation = true });
        Write(writer, document, options);
    }

    /// <summary>Serializes a document into a stream asynchronously.</summary>
    public static async Task SerializeAsync(InkDocument document, Stream stream, InkSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);
        byte[] bytes = SerializeToUtf8Bytes(document, options);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Estimates the encoded size so the output buffer rarely has to grow. Growing an <see cref="ArrayBufferWriter{T}"/>
    /// copies everything written so far, which dominates the cost of saving a large document.
    /// </summary>
    private static int EstimateSize(InkDocument document, InkSerializerOptions? options)
    {
        // Binary points cost roughly 6 bytes each before base64 (4/3 expansion); JSON points cost far more.
        double perPoint = (options ?? InkSerializerOptions.Default).PointEncoding == PointEncoding.Binary ? 8.5 : 34;
        long estimate = 1024 + document.ElementCount * 320L + (long)(document.TotalPointCount * perPoint);
        return (int)Math.Clamp(estimate, 1024, 256L * 1024 * 1024);
    }

    private static void Write(Utf8JsonWriter writer, InkDocument document, InkSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("format", FormatName);
        writer.WriteNumber("version", CurrentVersion);
        writer.WriteString("id", document.Id);

        writer.WritePropertyName("settings");
        writer.WriteStartObject();
        writer.WriteNumber("width", Round(document.Settings.Width, options.Decimals));
        writer.WriteNumber("height", Round(document.Settings.Height, options.Decimals));
        writer.WriteString("background", document.Settings.Background.ToCss());
        writer.WriteEndObject();

        if (document.Metadata.Count > 0)
        {
            writer.WritePropertyName("metadata");
            writer.WriteStartObject();
            foreach ((string key, string value) in document.Metadata.OrderBy(static kv => kv.Key, StringComparer.Ordinal)) writer.WriteString(key, value);
            writer.WriteEndObject();
        }

        writer.WritePropertyName("layers");
        writer.WriteStartArray();
        foreach (Layer layer in document.Layers) WriteLayer(writer, layer, options);
        writer.WriteEndArray();

        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteLayer(Utf8JsonWriter writer, Layer layer, InkSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", layer.Id);
        writer.WriteString("name", layer.Name);
        if (!layer.IsVisible) writer.WriteBoolean("visible", false);
        if (layer.IsLocked) writer.WriteBoolean("locked", true);
        if (layer.Opacity < 1f) writer.WriteNumber("opacity", Round(layer.Opacity, 3));
        WriteMetadata(writer, layer.Properties.Metadata);

        writer.WritePropertyName("elements");
        writer.WriteStartArray();
        foreach (InkElement element in layer.Elements) WriteElement(writer, element, options);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a single element in the document format. Exposed so features that transmit individual elements,
    /// such as collaboration, use exactly the same encoding as a saved document.
    /// </summary>
    /// <param name="writer">Destination. The element is written as one JSON object.</param>
    /// <param name="element">The element to write.</param>
    /// <param name="options">Serialization options.</param>
    public static void WriteElementTo(Utf8JsonWriter writer, InkElement element, InkSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(element);
        WriteElement(writer, element, options ?? InkSerializerOptions.Default);
    }

    /// <summary>
    /// Reads a single element written by <see cref="WriteElementTo"/>. Returns null when the element type is
    /// unknown and <see cref="InkDeserializerOptions.PreserveUnknownElements"/> is disabled.
    /// </summary>
    /// <param name="element">The JSON object.</param>
    /// <param name="options">Deserialization options, including the limits applied to untrusted input.</param>
    /// <exception cref="InkFormatException">The JSON is not a valid element.</exception>
    public static InkElement? ReadElementFrom(JsonElement element, InkDeserializerOptions? options = null)
        => ReadElement(element, options ?? InkDeserializerOptions.Default);

    private static void WriteElement(Utf8JsonWriter writer, InkElement element, InkSerializerOptions options)
    {
        if (element is UnknownElement unknown)
        {
            // Preserved verbatim so a load/save round trip never destroys data written by a newer version.
            using var document = JsonDocument.Parse(unknown.RawJson);
            document.RootElement.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        switch (element)
        {
            case Stroke stroke: WriteStroke(writer, stroke, options); break;
            case RectangleShape rect: WriteRectangle(writer, rect, options); break;
            case EllipseShape ellipse: WriteEllipse(writer, ellipse, options); break;
            case LineShape line: WriteLine(writer, line, options); break;
            case TextElement text: WriteText(writer, text, options); break;
            case ImageElement image: WriteImage(writer, image, options); break;
            case HighlightElement highlight: WriteHighlight(writer, highlight, options); break;
            case CalloutElement callout: WriteCallout(writer, callout, options); break;
            default: throw new InvalidOperationException($"{element.GetType().Name} cannot be serialized. Custom element types must be handled by the application before saving.");
        }
        writer.WriteEndObject();
    }

    private static void WriteCommon(Utf8JsonWriter writer, string type, InkElement element, InkSerializerOptions options)
    {
        writer.WriteString("type", type);
        writer.WriteString("id", element.Id);
        if (element.Version != 0) writer.WriteNumber("v", element.Version);
        if (!element.Transform.IsIdentity)
        {
            writer.WritePropertyName("transform");
            writer.WriteStartArray();
            Matrix3x2 m = element.Transform;
            writer.WriteNumberValue(Round(m.M11, 6));
            writer.WriteNumberValue(Round(m.M12, 6));
            writer.WriteNumberValue(Round(m.M21, 6));
            writer.WriteNumberValue(Round(m.M22, 6));
            writer.WriteNumberValue(Round(m.M31, options.Decimals));
            writer.WriteNumberValue(Round(m.M32, options.Decimals));
            writer.WriteEndArray();
        }
        if (element.Description is { Length: > 0 }) writer.WriteString("description", element.Description);
        WriteMetadata(writer, element.Metadata);
    }

    private static void WriteMetadata(Utf8JsonWriter writer, IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return;
        writer.WritePropertyName("metadata");
        writer.WriteStartObject();
        foreach ((string key, string value) in metadata.OrderBy(static kv => kv.Key, StringComparer.Ordinal)) writer.WriteString(key, value);
        writer.WriteEndObject();
    }

    private static void WriteStroke(Utf8JsonWriter writer, Stroke stroke, InkSerializerOptions options)
    {
        WriteCommon(writer, "stroke", stroke, options);
        WriteBrush(writer, stroke.Brush, options);
        if (stroke.Input.Kind != Input.PointerKind.Unknown)
        {
            writer.WritePropertyName("input");
            writer.WriteStartObject();
            writer.WriteString("device", stroke.Input.Kind.ToString().ToLowerInvariant());
            if (stroke.Input.HasRealPressure) writer.WriteBoolean("pressure", true);
            writer.WriteEndObject();
        }

        writer.WritePropertyName("points");
        writer.WriteStartObject();
        writer.WriteNumber("count", stroke.Points.Count);
        if (options.PointEncoding == PointEncoding.Binary)
        {
            writer.WriteString("encoding", "b64");
            writer.WriteBase64String("data", PointCodec.Encode(stroke.Points, options.Quantization));
        }
        else
        {
            writer.WriteString("encoding", "json");
            WriteFloatArray(writer, "x", stroke.Points.X, options.Decimals);
            WriteFloatArray(writer, "y", stroke.Points.Y, options.Decimals);
            WriteFloatArray(writer, "p", stroke.Points.Pressure, 3);
            WriteFloatArray(writer, "t", stroke.Points.Time, 1);
            if (!stroke.Points.TiltX.IsEmpty)
            {
                WriteFloatArray(writer, "tx", stroke.Points.TiltX, 1);
                WriteFloatArray(writer, "ty", stroke.Points.TiltY, 1);
            }
            if (!stroke.Points.Twist.IsEmpty) WriteFloatArray(writer, "tw", stroke.Points.Twist, 1);
        }
        writer.WriteEndObject();
    }

    private static void WriteFloatArray(Utf8JsonWriter writer, string name, ReadOnlySpan<float> values, int decimals)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (float v in values) writer.WriteNumberValue(Round(v, decimals));
        writer.WriteEndArray();
    }

    private static void WriteBrush(Utf8JsonWriter writer, Brush brush, InkSerializerOptions options)
    {
        writer.WritePropertyName("brush");
        writer.WriteStartObject();
        writer.WriteString("kind", brush.Kind);
        writer.WriteString("color", brush.Color.ToCss());
        writer.WriteNumber("width", Round(brush.Width, 3));
        if (brush.Opacity < 1f) writer.WriteNumber("opacity", Round(brush.Opacity, 3));
        if (brush.BlendMode != BlendMode.Normal) writer.WriteString("blend", BlendName(brush.BlendMode));
        if (brush.Smoothing != 0.5f) writer.WriteNumber("smoothing", Round(brush.Smoothing, 3));
        if (brush.Spacing != 0.25f) writer.WriteNumber("spacing", Round(brush.Spacing, 3));
        if (!brush.SimulatePressure) writer.WriteBoolean("simulatePressure", false);
        if (brush.TaperStart != 0) writer.WriteNumber("taperStart", Round(brush.TaperStart, 3));
        if (brush.TaperEnd != 0) writer.WriteNumber("taperEnd", Round(brush.TaperEnd, 3));

        PressureCurve curve = brush.PressureCurve;
        writer.WritePropertyName("pressure");
        writer.WriteStartObject();
        writer.WriteString("curve", curve.Kind.ToString().ToLowerInvariant());
        writer.WriteNumber("min", Round(curve.MinFactor, 3));
        writer.WriteNumber("max", Round(curve.MaxFactor, 3));
        if (curve.Kind == PressureCurveKind.Custom) WriteFloatArray(writer, "samples", curve.Samples, 4);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteShapeStyle(Utf8JsonWriter writer, ShapeStyle style, InkSerializerOptions options)
    {
        writer.WritePropertyName("style");
        writer.WriteStartObject();
        writer.WriteString("stroke", style.StrokeColor.ToCss());
        writer.WriteNumber("strokeWidth", Round(style.StrokeWidth, 3));
        if (style.FillColor is { } fill) writer.WriteString("fill", fill.ToCss());
        if (style.Opacity < 1f) writer.WriteNumber("opacity", Round(style.Opacity, 3));
        if (style.BlendMode != BlendMode.Normal) writer.WriteString("blend", BlendName(style.BlendMode));
        if (style.Dash is { Length: > 0 } dash) WriteFloatArray(writer, "dash", dash, 2);
        writer.WriteEndObject();
    }

    private static void WriteTextStyle(Utf8JsonWriter writer, TextStyle style, InkSerializerOptions options)
    {
        writer.WritePropertyName("textStyle");
        writer.WriteStartObject();
        writer.WriteString("font", style.FontFamily);
        writer.WriteNumber("size", Round(style.FontSize, 2));
        writer.WriteString("color", style.Color.ToCss());
        if (style.Bold) writer.WriteBoolean("bold", true);
        if (style.Italic) writer.WriteBoolean("italic", true);
        if (style.Align != TextAlign.Left) writer.WriteString("align", style.Align.ToString().ToLowerInvariant());
        if (style.LineHeight != 1.25f) writer.WriteNumber("lineHeight", Round(style.LineHeight, 3));
        writer.WriteEndObject();
    }

    private static void WriteRect(Utf8JsonWriter writer, string name, Rect rect, int decimals)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        writer.WriteNumberValue(Round(rect.X, decimals));
        writer.WriteNumberValue(Round(rect.Y, decimals));
        writer.WriteNumberValue(Round(rect.Width, decimals));
        writer.WriteNumberValue(Round(rect.Height, decimals));
        writer.WriteEndArray();
    }

    private static void WritePoint(Utf8JsonWriter writer, string name, Vector2 point, int decimals)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        writer.WriteNumberValue(Round(point.X, decimals));
        writer.WriteNumberValue(Round(point.Y, decimals));
        writer.WriteEndArray();
    }

    private static void WriteRectangle(Utf8JsonWriter writer, RectangleShape shape, InkSerializerOptions options)
    {
        WriteCommon(writer, "rect", shape, options);
        WriteRect(writer, "rect", shape.Rect, options.Decimals);
        if (shape.CornerRadius > 0) writer.WriteNumber("radius", Round(shape.CornerRadius, 3));
        WriteShapeStyle(writer, shape.Style, options);
    }

    private static void WriteEllipse(Utf8JsonWriter writer, EllipseShape shape, InkSerializerOptions options)
    {
        WriteCommon(writer, "ellipse", shape, options);
        WriteRect(writer, "rect", shape.Rect, options.Decimals);
        WriteShapeStyle(writer, shape.Style, options);
    }

    private static void WriteLine(Utf8JsonWriter writer, LineShape shape, InkSerializerOptions options)
    {
        WriteCommon(writer, "line", shape, options);
        WritePoint(writer, "start", shape.Start, options.Decimals);
        WritePoint(writer, "end", shape.End, options.Decimals);
        if (shape.StartArrow) writer.WriteBoolean("startArrow", true);
        if (shape.EndArrow) writer.WriteBoolean("endArrow", true);
        if (shape.ArrowSize != 5f) writer.WriteNumber("arrowSize", Round(shape.ArrowSize, 3));
        WriteShapeStyle(writer, shape.Style, options);
    }

    private static void WriteText(Utf8JsonWriter writer, TextElement text, InkSerializerOptions options)
    {
        WriteCommon(writer, "text", text, options);
        WritePoint(writer, "position", text.Position, options.Decimals);
        writer.WriteString("text", text.Text);
        if (text.Size is { } size) WritePoint(writer, "size", size, options.Decimals);
        if (text.Opacity < 1f) writer.WriteNumber("opacity", Round(text.Opacity, 3));
        if (text.Background is { } bg) writer.WriteString("background", bg.ToCss());
        if (text.Padding != 4f) writer.WriteNumber("padding", Round(text.Padding, 3));
        WriteTextStyle(writer, text.Style, options);
    }

    private static void WriteImage(Utf8JsonWriter writer, ImageElement image, InkSerializerOptions options)
    {
        WriteCommon(writer, "image", image, options);
        WriteRect(writer, "rect", image.Rect, options.Decimals);
        bool omit = image.Source.IsEmbedded && !options.IncludeEmbeddedImages;
        writer.WriteString("src", omit ? string.Empty : image.Source.Uri);
        if (omit) writer.WriteBoolean("srcOmitted", true);
        if (image.Source.Width > 0) writer.WriteNumber("naturalWidth", image.Source.Width);
        if (image.Source.Height > 0) writer.WriteNumber("naturalHeight", image.Source.Height);
        if (image.Crop is { } crop) WriteRect(writer, "crop", crop, options.Decimals);
        if (image.Opacity < 1f) writer.WriteNumber("opacity", Round(image.Opacity, 3));
    }

    private static void WriteHighlight(Utf8JsonWriter writer, HighlightElement highlight, InkSerializerOptions options)
    {
        WriteCommon(writer, "highlight", highlight, options);
        writer.WriteString("color", highlight.Color.ToCss());
        if (highlight.Opacity != 0.5f) writer.WriteNumber("opacity", Round(highlight.Opacity, 3));
        writer.WritePropertyName("rects");
        writer.WriteStartArray();
        foreach (Rect r in highlight.Rects)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(Round(r.X, options.Decimals));
            writer.WriteNumberValue(Round(r.Y, options.Decimals));
            writer.WriteNumberValue(Round(r.Width, options.Decimals));
            writer.WriteNumberValue(Round(r.Height, options.Decimals));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    private static void WriteCallout(Utf8JsonWriter writer, CalloutElement callout, InkSerializerOptions options)
    {
        WriteCommon(writer, "callout", callout, options);
        WriteRect(writer, "box", callout.Box, options.Decimals);
        WritePoint(writer, "anchor", callout.Anchor, options.Decimals);
        writer.WriteString("text", callout.Text);
        if (callout.Padding != 6f) writer.WriteNumber("padding", Round(callout.Padding, 3));
        WriteShapeStyle(writer, callout.Style, options);
        WriteTextStyle(writer, callout.TextStyle, options);
    }

    private static string BlendName(BlendMode mode) => mode switch
    {
        BlendMode.Multiply => "multiply",
        BlendMode.Screen => "screen",
        BlendMode.DestinationOut => "erase",
        _ => "normal",
    };

    private static double Round(float value, int decimals)
    {
        if (!float.IsFinite(value)) return 0;
        return Math.Round(value, Math.Clamp(decimals, 0, 8), MidpointRounding.AwayFromZero);
    }

    // ---- Reading ---------------------------------------------------------------------------------------------------

    /// <summary>Parses a document from a JSON string.</summary>
    /// <exception cref="InkFormatException">The input is not a valid BlazorInk document.</exception>
    public static InkDocument Deserialize(string json, InkDeserializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Deserialize(Encoding.UTF8.GetBytes(json), options);
    }

    /// <summary>Parses a document from UTF-8 bytes.</summary>
    /// <exception cref="InkFormatException">The input is not a valid BlazorInk document.</exception>
    public static InkDocument Deserialize(ReadOnlySpan<byte> utf8Json, InkDeserializerOptions? options = null)
    {
        options ??= InkDeserializerOptions.Default;
        if (utf8Json.Length > options.MaxInputBytes) throw new InkFormatException($"The document is {utf8Json.Length} bytes; the limit is {options.MaxInputBytes}.");
        JsonDocument json;
        try
        {
            // Parsing from a sequence reader avoids copying the whole document before reading it.
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            json = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException ex)
        {
            throw new InkFormatException("The document is not valid JSON.", ex);
        }
        using (json)
        {
            return ReadDocument(json.RootElement, options);
        }
    }

    /// <summary>Parses a document from a stream.</summary>
    /// <exception cref="InkFormatException">The input is not a valid BlazorInk document.</exception>
    public static async Task<InkDocument> DeserializeAsync(Stream stream, InkDeserializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= InkDeserializerOptions.Default;
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > options.MaxInputBytes) throw new InkFormatException($"The document exceeds the size limit of {options.MaxInputBytes} bytes.");
            memory.Write(buffer, 0, read);
        }
        return Deserialize(memory.GetBuffer().AsSpan(0, (int)memory.Length), options);
    }

    /// <summary>Tries to parse a document, returning false instead of throwing on malformed input.</summary>
    public static bool TryDeserialize(string json, out InkDocument? document, out string? error, InkDeserializerOptions? options = null)
    {
        document = null;
        error = null;
        try
        {
            document = Deserialize(json, options);
            return true;
        }
        catch (InkFormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static InkDocument ReadDocument(JsonElement root, InkDeserializerOptions options)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InkFormatException("The document must be a JSON object.");
        if (!root.TryGetProperty("format", out JsonElement format) || format.GetString() != FormatName)
            throw new InkFormatException($"Not a BlazorInk document (the \"format\" property must be \"{FormatName}\").");
        int version = root.TryGetProperty("version", out JsonElement versionElement) && versionElement.TryGetInt32(out int v) ? v : 0;
        if (version < MinimumSupportedVersion) throw new InkFormatException($"Document version {version} is older than the minimum supported version {MinimumSupportedVersion}.");
        if (version > CurrentVersion) throw new InkFormatException($"Document version {version} was written by a newer version of BlazorInk (this build supports up to {CurrentVersion}). Upgrade the BlazorInk package to open it.");

        Guid id = ReadGuid(root, "id") ?? Guid.NewGuid();
        DocumentSettings settings = DocumentSettings.Default;
        if (root.TryGetProperty("settings", out JsonElement settingsElement) && settingsElement.ValueKind == JsonValueKind.Object)
        {
            settings = new DocumentSettings
            {
                Width = ReadFloat(settingsElement, "width", 800f),
                Height = ReadFloat(settingsElement, "height", 600f),
                Background = ReadColor(settingsElement, "background", InkColor.White),
            };
        }

        var document = new InkDocument(id, settings, options.Limits, withoutLayers: true);

        if (root.TryGetProperty("metadata", out JsonElement metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in metadata.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String) document.SetMetadataCore(property.Name, property.Value.GetString());
            }
        }

        if (!root.TryGetProperty("layers", out JsonElement layers) || layers.ValueKind != JsonValueKind.Array)
            throw new InkFormatException("The document must contain a \"layers\" array.");

        int layerCount = 0;
        var seenIds = new HashSet<Guid>();
        foreach (JsonElement layerElement in layers.EnumerateArray())
        {
            if (++layerCount > options.Limits.MaxLayers) throw new InkFormatException($"The document declares more than {options.Limits.MaxLayers} layers.");
            ReadLayer(document, layerElement, options, seenIds);
        }
        if (document.Layers.Count == 0) document.InsertLayerCore(Guid.NewGuid(), new LayerProperties { Name = "Drawing" }, null, null);
        return document;
    }

    private static void ReadLayer(InkDocument document, JsonElement element, InkDeserializerOptions options, HashSet<Guid> seenIds)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InkFormatException("Each layer must be a JSON object.");
        var properties = new LayerProperties
        {
            Name = ReadString(element, "name") ?? "Layer",
            IsVisible = ReadBool(element, "visible", true),
            IsLocked = ReadBool(element, "locked", false),
            Opacity = Math.Clamp(ReadFloat(element, "opacity", 1f), 0f, 1f),
            Metadata = ReadMetadata(element),
        };
        Guid id = ReadGuid(element, "id") ?? Guid.NewGuid();
        if (document.TryGetLayer(id, out _)) id = Guid.NewGuid();

        var elements = new List<InkElement>();
        if (element.TryGetProperty("elements", out JsonElement elementArray) && elementArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in elementArray.EnumerateArray())
            {
                InkElement? parsed = ReadElement(e, options);
                if (parsed is null) continue;
                // A duplicate id would make the element unaddressable. Give it a fresh one rather than
                // rejecting the whole document: the drawing is still perfectly good.
                if (document.ContainsElement(parsed.Id) || seenIds.Contains(parsed.Id)) parsed = parsed with { Id = Guid.NewGuid() };
                seenIds.Add(parsed.Id);
                elements.Add(parsed);
            }
        }
        try
        {
            document.InsertLayerCore(id, properties, null, elements);
        }
        catch (InvalidOperationException ex)
        {
            // Document limits and validation throw; to a caller loading a file this is a malformed document,
            // which is what TryDeserialize promises to report rather than throw.
            throw new InkFormatException(ex.Message, ex);
        }
    }

    private static InkElement? ReadElement(JsonElement element, InkDeserializerOptions options)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InkFormatException("Each element must be a JSON object.");
        string type = ReadString(element, "type") ?? throw new InkFormatException("Each element must have a \"type\".");

        InkElement? result = type switch
        {
            "stroke" => ReadStroke(element, options),
            "rect" => ReadRectangle(element),
            "ellipse" => ReadEllipse(element),
            "line" => ReadLine(element),
            "text" => ReadText(element),
            "image" => ReadImage(element, options),
            "highlight" => ReadHighlight(element, options.Limits),
            "callout" => ReadCallout(element),
            _ => options.PreserveUnknownElements ? new UnknownElement(type, element.GetRawText(), ReadRect(element, "bounds") ?? Rect.Empty) : null,
        };
        if (result is null) return null;

        Guid id = ReadGuid(element, "id") ?? Guid.NewGuid();
        int version = element.TryGetProperty("v", out JsonElement v) && v.TryGetInt32(out int parsed) ? Math.Max(parsed, 0) : 0;
        Matrix3x2 transform = ReadTransform(element, options.Limits);
        return result with
        {
            Id = id,
            Version = version,
            Transform = transform,
            Description = ReadString(element, "description"),
            Metadata = ReadMetadata(element),
        };
    }

    private static Stroke ReadStroke(JsonElement element, InkDeserializerOptions options)
    {
        Brush brush = ReadBrush(element, options);
        if (!element.TryGetProperty("points", out JsonElement pointsElement) || pointsElement.ValueKind != JsonValueKind.Object)
            throw new InkFormatException("A stroke must have a \"points\" object.");

        StrokePoints points;
        string encoding = ReadString(pointsElement, "encoding") ?? "json";
        if (encoding == "b64")
        {
            if (!pointsElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.String)
                throw new InkFormatException("Binary points require a \"data\" string.");
            byte[] bytes;
            try
            {
                bytes = data.GetBytesFromBase64();
            }
            catch (FormatException ex)
            {
                throw new InkFormatException("Stroke point data is not valid base64.", ex);
            }
            try
            {
                points = PointCodec.Decode(bytes, options.Limits.MaxPointsPerStroke);
            }
            catch (FormatException ex)
            {
                throw new InkFormatException("Stroke point data is malformed.", ex);
            }
        }
        else
        {
            float[] xs = ReadFloatArray(pointsElement, "x", options.Limits.MaxPointsPerStroke);
            float[] ys = ReadFloatArray(pointsElement, "y", options.Limits.MaxPointsPerStroke);
            if (xs.Length != ys.Length) throw new InkFormatException("Stroke x and y arrays must have the same length.");
            float[] ps = ReadFloatArray(pointsElement, "p", options.Limits.MaxPointsPerStroke);
            float[] ts = ReadFloatArray(pointsElement, "t", options.Limits.MaxPointsPerStroke);
            float[] tx = ReadFloatArray(pointsElement, "tx", options.Limits.MaxPointsPerStroke);
            float[] ty = ReadFloatArray(pointsElement, "ty", options.Limits.MaxPointsPerStroke);
            float[] tw = ReadFloatArray(pointsElement, "tw", options.Limits.MaxPointsPerStroke);
            if (ps.Length != 0 && ps.Length != xs.Length) throw new InkFormatException("Stroke pressure array length must match the point count.");
            if (ts.Length != 0 && ts.Length != xs.Length) throw new InkFormatException("Stroke time array length must match the point count.");
            if (tx.Length != ty.Length || (tx.Length != 0 && tx.Length != xs.Length)) throw new InkFormatException("Stroke tilt array lengths must match the point count.");
            if (tw.Length != 0 && tw.Length != xs.Length) throw new InkFormatException("Stroke twist array length must match the point count.");
            points = StrokePoints.FromArrays(xs, ys, ps, ts, tx, ty, tw);
        }

        ValidateCoordinates(points, options.Limits);

        var input = StrokeInputInfo.Unknown;
        if (element.TryGetProperty("input", out JsonElement inputElement) && inputElement.ValueKind == JsonValueKind.Object)
        {
            string? device = ReadString(inputElement, "device");
            var kind = device switch
            {
                "mouse" => Input.PointerKind.Mouse,
                "touch" => Input.PointerKind.Touch,
                "pen" => Input.PointerKind.Pen,
                _ => Input.PointerKind.Unknown,
            };
            input = new StrokeInputInfo(kind, ReadBool(inputElement, "pressure", false));
        }
        return new Stroke(points, brush) { Input = input };
    }

    private static void ValidateCoordinates(StrokePoints points, DocumentLimits limits)
    {
        ReadOnlySpan<float> xs = points.X;
        ReadOnlySpan<float> ys = points.Y;
        float max = limits.MaxCoordinate;
        for (int i = 0; i < xs.Length; i++)
        {
            if (!float.IsFinite(xs[i]) || !float.IsFinite(ys[i]) || MathF.Abs(xs[i]) > max || MathF.Abs(ys[i]) > max)
                throw new InkFormatException($"A stroke point is outside the allowed coordinate range (+/-{max}).");
        }
    }

    private static Brush ReadBrush(JsonElement element, InkDeserializerOptions options)
    {
        if (!element.TryGetProperty("brush", out JsonElement brushElement) || brushElement.ValueKind != JsonValueKind.Object)
            return new PenBrush();

        string kind = ReadString(brushElement, "kind") ?? PenBrush.KindName;
        if (!options.BrushRegistry.TryCreate(kind, out Brush? brush) || brush is null)
        {
            // An unknown brush still renders with its stored appearance rather than being dropped.
            brush = new PenBrush();
        }

        PressureCurve curve = brush.PressureCurve;
        if (brushElement.TryGetProperty("pressure", out JsonElement pressure) && pressure.ValueKind == JsonValueKind.Object)
        {
            string curveName = ReadString(pressure, "curve") ?? "linear";
            float min = Math.Clamp(ReadFloat(pressure, "min", curve.MinFactor), 0f, 4f);
            float max = Math.Clamp(ReadFloat(pressure, "max", curve.MaxFactor), 0f, 4f);
            if (curveName == "custom")
            {
                float[] samples = ReadFloatArray(pressure, "samples", 1024);
                curve = samples.Length >= 2 ? PressureCurve.FromSamples(samples, min, max) : new PressureCurve(PressureCurveKind.Linear, min, max);
            }
            else
            {
                var kindValue = curveName switch
                {
                    "easein" => PressureCurveKind.EaseIn,
                    "easeout" => PressureCurveKind.EaseOut,
                    "easeinout" => PressureCurveKind.EaseInOut,
                    "constant" => PressureCurveKind.Constant,
                    _ => PressureCurveKind.Linear,
                };
                curve = new PressureCurve(kindValue, min, max);
            }
        }

        float width = ReadFloat(brushElement, "width", brush.Width);
        if (!float.IsFinite(width) || width <= 0) width = 1f;
        width = MathF.Min(width, options.Limits.MaxStrokeWidth);

        return brush with
        {
            Color = ReadColor(brushElement, "color", brush.Color),
            Width = width,
            Opacity = Math.Clamp(ReadFloat(brushElement, "opacity", brush.Opacity), 0f, 1f),
            BlendMode = ReadBlend(brushElement, "blend", brush.BlendMode),
            Smoothing = Math.Clamp(ReadFloat(brushElement, "smoothing", brush.Smoothing), 0f, 1f),
            Spacing = Math.Clamp(ReadFloat(brushElement, "spacing", brush.Spacing), 0f, 10f),
            SimulatePressure = ReadBool(brushElement, "simulatePressure", brush.SimulatePressure),
            TaperStart = MathF.Max(ReadFloat(brushElement, "taperStart", brush.TaperStart), 0f),
            TaperEnd = MathF.Max(ReadFloat(brushElement, "taperEnd", brush.TaperEnd), 0f),
            PressureCurve = curve,
        };
    }

    private static ShapeStyle ReadShapeStyle(JsonElement element)
    {
        if (!element.TryGetProperty("style", out JsonElement style) || style.ValueKind != JsonValueKind.Object) return ShapeStyle.Default;
        float[] dash = ReadFloatArray(style, "dash", 32);
        return new ShapeStyle
        {
            StrokeColor = ReadColor(style, "stroke", InkColor.Black),
            StrokeWidth = Math.Clamp(ReadFloat(style, "strokeWidth", 2f), 0f, 10000f),
            FillColor = style.TryGetProperty("fill", out JsonElement fill) && fill.ValueKind == JsonValueKind.String && InkColor.TryParse(fill.GetString(), out InkColor c) ? c : null,
            Opacity = Math.Clamp(ReadFloat(style, "opacity", 1f), 0f, 1f),
            BlendMode = ReadBlend(style, "blend", BlendMode.Normal),
            Dash = dash.Length > 0 ? dash : null,
        };
    }

    private static TextStyle ReadTextStyle(JsonElement element)
    {
        if (!element.TryGetProperty("textStyle", out JsonElement style) || style.ValueKind != JsonValueKind.Object) return TextStyle.Default;
        string font = ReadString(style, "font") ?? TextStyle.Default.FontFamily;
        // Font families are emitted into CSS and SVG; keep them to a safe character set.
        if (font.AsSpan().IndexOfAny(UnsafeFontCharacters) >= 0 || font.Length > 200) font = TextStyle.Default.FontFamily;
        return new TextStyle(
            font,
            Math.Clamp(ReadFloat(style, "size", 16f), 0.1f, 10000f),
            ReadColor(style, "color", InkColor.Black),
            ReadBool(style, "bold", false),
            ReadBool(style, "italic", false),
            ReadString(style, "align") switch { "center" => TextAlign.Center, "right" => TextAlign.Right, _ => TextAlign.Left },
            Math.Clamp(ReadFloat(style, "lineHeight", 1.25f), 0.5f, 10f));
    }

    private static RectangleShape ReadRectangle(JsonElement element)
        => new(ReadRect(element, "rect") ?? Rect.Empty) { Style = ReadShapeStyle(element), CornerRadius = MathF.Max(ReadFloat(element, "radius", 0f), 0f) };

    private static EllipseShape ReadEllipse(JsonElement element)
        => new(ReadRect(element, "rect") ?? Rect.Empty) { Style = ReadShapeStyle(element) };

    private static LineShape ReadLine(JsonElement element)
        => new(ReadVector(element, "start") ?? Vector2.Zero, ReadVector(element, "end") ?? Vector2.Zero)
        {
            Style = ReadShapeStyle(element),
            StartArrow = ReadBool(element, "startArrow", false),
            EndArrow = ReadBool(element, "endArrow", false),
            ArrowSize = Math.Clamp(ReadFloat(element, "arrowSize", 5f), 0f, 100f),
        };

    private static TextElement ReadText(JsonElement element)
    {
        string text = ReadString(element, "text") ?? string.Empty;
        if (text.Length > TextElement.MaxLength) throw new InkFormatException($"A text element exceeds the maximum length of {TextElement.MaxLength} characters.");
        return new TextElement(ReadVector(element, "position") ?? Vector2.Zero, text)
        {
            Style = ReadTextStyle(element),
            Size = ReadVector(element, "size"),
            Opacity = Math.Clamp(ReadFloat(element, "opacity", 1f), 0f, 1f),
            Background = element.TryGetProperty("background", out JsonElement bg) && bg.ValueKind == JsonValueKind.String && InkColor.TryParse(bg.GetString(), out InkColor c) ? c : null,
            Padding = Math.Clamp(ReadFloat(element, "padding", 4f), 0f, 1000f),
        };
    }

    private static ImageElement? ReadImage(JsonElement element, InkDeserializerOptions options)
    {
        string uri = ReadString(element, "src") ?? string.Empty;
        Rect rect = ReadRect(element, "rect") ?? Rect.Empty;
        int naturalWidth = element.TryGetProperty("naturalWidth", out JsonElement nw) && nw.TryGetInt32(out int w) ? w : 0;
        int naturalHeight = element.TryGetProperty("naturalHeight", out JsonElement nh) && nh.TryGetInt32(out int h) ? h : 0;
        if (!ImageSource.TryCreate(uri, naturalWidth, naturalHeight, options.AllowSvgImages, out ImageSource? source) || source is null) return null;
        if (!options.AllowRemoteImages && !source.IsEmbedded) return null;
        return new ImageElement(source, rect)
        {
            Crop = ReadRect(element, "crop"),
            Opacity = Math.Clamp(ReadFloat(element, "opacity", 1f), 0f, 1f),
        };
    }

    private static HighlightElement ReadHighlight(JsonElement element, DocumentLimits limits)
    {
        var rects = new List<Rect>();
        if (element.TryGetProperty("rects", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            // A highlight is a list of rectangles, so it needs its own bound: without one it would be the only
            // element type that can grow without limit.
            int maxRects = Math.Max(limits.MaxPointsPerStroke / 4, 64);
            if (array.GetArrayLength() > maxRects)
            {
                throw new InkFormatException($"A highlight declares {array.GetArrayLength()} rectangles; the limit is {maxRects}.");
            }

            Span<float> values = stackalloc float[4];
            foreach (JsonElement r in array.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Array) continue;
                int i = 0;
                foreach (JsonElement value in r.EnumerateArray())
                {
                    if (i >= 4) break;
                    values[i++] = value.TryGetSingle(out float f) && float.IsFinite(f) ? f : 0f;
                }
                if (i == 4) rects.Add(new Rect(values[0], values[1], values[2], values[3]));
            }
        }
        return new HighlightElement(rects)
        {
            Color = ReadColor(element, "color", new InkColor(255, 235, 59)),
            Opacity = Math.Clamp(ReadFloat(element, "opacity", 0.5f), 0f, 1f),
        };
    }

    private static CalloutElement ReadCallout(JsonElement element)
    {
        string text = ReadString(element, "text") ?? string.Empty;
        if (text.Length > TextElement.MaxLength) throw new InkFormatException($"A callout exceeds the maximum text length of {TextElement.MaxLength} characters.");
        return new CalloutElement(ReadRect(element, "box") ?? Rect.Empty, ReadVector(element, "anchor") ?? Vector2.Zero, text)
        {
            Style = ReadShapeStyle(element),
            TextStyle = ReadTextStyle(element),
            Padding = Math.Clamp(ReadFloat(element, "padding", 6f), 0f, 1000f),
        };
    }

    // ---- JSON helpers ----------------------------------------------------------------------------------------------

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool ReadBool(JsonElement element, string name, bool fallback)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;

    private static float ReadFloat(JsonElement element, string name, float fallback)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number) return fallback;
        return value.TryGetSingle(out float f) && float.IsFinite(f) ? f : fallback;
    }

    private static Guid? ReadGuid(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out Guid guid) ? guid : null;

    private static InkColor ReadColor(JsonElement element, string name, InkColor fallback)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && InkColor.TryParse(value.GetString(), out InkColor color) ? color : fallback;

    private static BlendMode ReadBlend(JsonElement element, string name, BlendMode fallback)
        => ReadString(element, name) switch
        {
            "normal" => BlendMode.Normal,
            "multiply" => BlendMode.Multiply,
            "screen" => BlendMode.Screen,
            "erase" => BlendMode.DestinationOut,
            _ => fallback,
        };

    private static Dictionary<string, string>? ReadMetadata(JsonElement element)
    {
        if (!element.TryGetProperty("metadata", out JsonElement metadata) || metadata.ValueKind != JsonValueKind.Object) return null;
        Dictionary<string, string>? result = null;
        foreach (JsonProperty property in metadata.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            result ??= new Dictionary<string, string>(StringComparer.Ordinal);
            result[property.Name] = property.Value.GetString()!;
        }
        return result;
    }

    private static Rect? ReadRect(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array) return null;
        Span<float> values = stackalloc float[4];
        int i = 0;
        foreach (JsonElement v in value.EnumerateArray())
        {
            if (i >= 4) break;
            values[i++] = v.TryGetSingle(out float f) && float.IsFinite(f) ? f : 0f;
        }
        return i == 4 ? new Rect(values[0], values[1], values[2], values[3]) : null;
    }

    private static Vector2? ReadVector(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array) return null;
        Span<float> values = stackalloc float[2];
        int i = 0;
        foreach (JsonElement v in value.EnumerateArray())
        {
            if (i >= 2) break;
            values[i++] = v.TryGetSingle(out float f) && float.IsFinite(f) ? f : 0f;
        }
        return i == 2 ? new Vector2(values[0], values[1]) : null;
    }

    private static Matrix3x2 ReadTransform(JsonElement element, DocumentLimits limits)
    {
        if (!element.TryGetProperty("transform", out JsonElement value) || value.ValueKind != JsonValueKind.Array) return Matrix3x2.Identity;
        Span<float> m = stackalloc float[6];
        int i = 0;
        foreach (JsonElement v in value.EnumerateArray())
        {
            if (i >= 6) break;
            m[i++] = v.TryGetSingle(out float f) && float.IsFinite(f) ? f : 0f;
        }
        if (i != 6) return Matrix3x2.Identity;
        var matrix = new Matrix3x2(m[0], m[1], m[2], m[3], m[4], m[5]);
        // A singular or wildly scaled transform would make elements un-selectable or explode their bounds.
        float determinant = matrix.GetDeterminant();
        if (!float.IsFinite(determinant) || MathF.Abs(determinant) < 1e-9f) return Matrix3x2.Identity;
        if (MathF.Abs(m[4]) > limits.MaxCoordinate || MathF.Abs(m[5]) > limits.MaxCoordinate) return Matrix3x2.Identity;
        return matrix;
    }

    private static float[] ReadFloatArray(JsonElement element, string name, int maxLength)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array) return [];
        int length = value.GetArrayLength();
        if (length > maxLength) throw new InkFormatException($"The \"{name}\" array has {length} entries; the limit is {maxLength}.");
        float[] result = new float[length];
        int i = 0;
        foreach (JsonElement v in value.EnumerateArray())
        {
            result[i++] = v.TryGetSingle(out float f) && float.IsFinite(f) ? f : 0f;
        }
        return result;
    }
}

/// <summary>Thrown when a document cannot be read because it is malformed, too large or written by a newer version.</summary>
public sealed class InkFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public InkFormatException() : base("The ink document is not valid.") { }

    /// <summary>Creates the exception with a message.</summary>
    public InkFormatException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public InkFormatException(string message, Exception innerException) : base(message, innerException) { }
}

using System.Numerics;
using System.Text;
using System.Text.Json;
using BlazorInk.Brushes;
using BlazorInk.Geometry;
using BlazorInk.Rendering;
using BlazorInk.Serialization;
using Xunit;

namespace BlazorInk.Tests.Core;

public class PointCodecTests
{
    [Fact]
    public void Round_trips_positions_within_the_quantization_step()
    {
        StrokePoints original = TestData.PressureRamp(new Vector2(10.3f, 20.7f), new Vector2(300.15f, 180.9f), 50);
        StrokePoints decoded = PointCodec.Decode(PointCodec.Encode(original));

        Assert.Equal(original.Count, decoded.Count);
        const float step = 1f / PointCodec.DefaultQuantization;
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(original.X[i], decoded.X[i], step);
            Assert.Equal(original.Y[i], decoded.Y[i], step);
            Assert.Equal(original.Pressure[i], decoded.Pressure[i], 0.005f);
            Assert.Equal(original.Time[i], decoded.Time[i], 0.6f);
        }
    }

    [Fact]
    public void Round_trips_tilt_and_twist_when_present()
    {
        var buffer = new StrokePointBuffer();
        for (int i = 0; i < 10; i++)
        {
            buffer.Add(new InkPoint(i, i * 2f, 0.4f, i * 10f) { TiltX = -40 + i * 8, TiltY = 30 - i * 5, Twist = i * 30f });
        }
        StrokePoints original = buffer.ToStrokePoints();
        StrokePoints decoded = PointCodec.Decode(PointCodec.Encode(original));

        Assert.Equal(PointChannels.Tilt | PointChannels.Twist, decoded.Channels);
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(original.TiltX[i], decoded.TiltX[i], 1f);
            Assert.Equal(original.TiltY[i], decoded.TiltY[i], 1f);
            Assert.Equal(original.Twist[i], decoded.Twist[i], 2f);
        }
    }

    [Fact]
    public void Omits_optional_channels_when_the_device_did_not_provide_them()
    {
        StrokePoints original = TestData.Line(new Vector2(0, 0), new Vector2(10, 10), 10);
        StrokePoints decoded = PointCodec.Decode(PointCodec.Encode(original));
        Assert.Equal(PointChannels.None, decoded.Channels);
    }

    [Fact]
    public void Round_trips_strokes_that_move_left_and_up()
    {
        // Negative deltas exercise the zigzag decoding path; ascending-only strokes would not catch a sign bug.
        StrokePoints original = TestData.Line(new Vector2(300f, 250f), new Vector2(-120.5f, -60.25f), 40);
        StrokePoints decoded = PointCodec.Decode(PointCodec.Encode(original));
        const float step = 1f / PointCodec.DefaultQuantization;
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(original.X[i], decoded.X[i], step);
            Assert.Equal(original.Y[i], decoded.Y[i], step);
        }
    }

    [Fact]
    public void Round_trips_a_closed_loop_where_deltas_change_sign()
    {
        StrokePoints original = TestData.Circle(new Vector2(0, 0), 75f, 128);
        StrokePoints decoded = PointCodec.Decode(PointCodec.Encode(original));
        const float step = 1f / PointCodec.DefaultQuantization;
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(original.X[i], decoded.X[i], step);
            Assert.Equal(original.Y[i], decoded.Y[i], step);
        }
    }

    [Fact]
    public void Encoding_is_deterministic()
    {
        StrokePoints points = TestData.Circle(new Vector2(50, 50), 30);
        Assert.Equal(PointCodec.Encode(points), PointCodec.Encode(points));
    }

    [Fact]
    public void Encoding_is_far_smaller_than_a_json_number_array()
    {
        StrokePoints points = TestData.PressureRamp(new Vector2(0, 0), new Vector2(400, 300), 500);
        int binary = PointCodec.Encode(points).Length;
        int json = Encoding.UTF8.GetByteCount(string.Join(',', points.X.ToArray().Select(v => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))));
        Assert.True(binary < json, $"binary was {binary} bytes, a single JSON coordinate array is {json}");
        Assert.True(binary < points.Count * 8, $"expected under {points.Count * 8} bytes but got {binary}");
    }

    [Fact]
    public void Empty_points_round_trip()
    {
        StrokePoints decoded = PointCodec.Decode(PointCodec.Encode(StrokePoints.Empty));
        Assert.Equal(0, decoded.Count);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1 })]
    [InlineData(new byte[] { 99, 0, 64, 5 })]
    [InlineData(new byte[] { 1, 0, 64, 200, 200, 200, 200, 200 })]
    public void Malformed_input_throws_rather_than_allocating(byte[] data)
    {
        Assert.Throws<FormatException>(() => PointCodec.Decode(data));
    }

    [Fact]
    public void A_declared_count_beyond_the_limit_is_rejected()
    {
        // Version 1, no flags, quantization 64, count 1000000 as a varint.
        var bytes = new List<byte> { 1, 0, 64 };
        uint count = 1_000_000;
        while (count >= 0x80) { bytes.Add((byte)(count | 0x80)); count >>= 7; }
        bytes.Add((byte)count);
        Assert.Throws<FormatException>(() => PointCodec.Decode(bytes.ToArray(), maxPoints: 1000));
    }
}

public class SerializerTests
{
    [Fact]
    public void Round_trips_a_full_document()
    {
        InkDocument original = TestData.SampleDocument();
        original.SetMetadata("author", "Test <script>");
        string json = InkSerializer.Serialize(original);
        InkDocument loaded = InkSerializer.Deserialize(json);

        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(original.Layers.Count, loaded.Layers.Count);
        Assert.Equal(original.ElementCount, loaded.ElementCount);
        Assert.Equal(original.Settings.Width, loaded.Settings.Width);
        Assert.Equal(original.Settings.Background, loaded.Settings.Background);
        Assert.Equal("Test <script>", loaded.Metadata["author"]);

        for (int i = 0; i < original.Layers.Count; i++)
        {
            Assert.Equal(original.Layers[i].Name, loaded.Layers[i].Name);
            Assert.Equal(original.Layers[i].Count, loaded.Layers[i].Count);
            for (int j = 0; j < original.Layers[i].Count; j++)
            {
                InkElement a = original.Layers[i].Elements[j];
                InkElement b = loaded.Layers[i].Elements[j];
                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.GetType(), b.GetType());
                Assert.Equal(a.Bounds.X, b.Bounds.X, 0.05f);
                Assert.Equal(a.Bounds.Width, b.Bounds.Width, 0.05f);
            }
        }
    }

    [Fact]
    public void Round_trips_brush_settings()
    {
        var document = new InkDocument();
        var brush = new PencilBrush
        {
            Color = InkColor.Parse("#123456"),
            Width = 7.5f,
            Opacity = 0.42f,
            Smoothing = 0.8f,
            Spacing = 0.05f,
            SimulatePressure = false,
            TaperStart = 3f,
            TaperEnd = 9f,
            PressureCurve = new PressureCurve(PressureCurveKind.EaseInOut, 0.05f, 0.95f),
        };
        document.AddElement(new Stroke(TestData.Line(new Vector2(0, 0), new Vector2(10, 10)), brush));

        InkDocument loaded = InkSerializer.Deserialize(InkSerializer.Serialize(document));
        var reloaded = (Stroke)loaded.Layers[0].Elements[0];

        Assert.IsType<PencilBrush>(reloaded.Brush);
        Assert.Equal(brush.Color, reloaded.Brush.Color);
        Assert.Equal(brush.Width, reloaded.Brush.Width, 3);
        Assert.Equal(brush.Opacity, reloaded.Brush.Opacity, 3);
        Assert.Equal(brush.Smoothing, reloaded.Brush.Smoothing, 3);
        Assert.Equal(brush.Spacing, reloaded.Brush.Spacing, 3);
        Assert.False(reloaded.Brush.SimulatePressure);
        Assert.Equal(brush.TaperEnd, reloaded.Brush.TaperEnd, 3);
        Assert.Equal(PressureCurveKind.EaseInOut, reloaded.Brush.PressureCurve.Kind);
        Assert.Equal(0.95f, reloaded.Brush.PressureCurve.MaxFactor, 3);
    }

    [Fact]
    public void Round_trips_a_custom_pressure_curve()
    {
        var document = new InkDocument();
        float[] samples = [0f, 0.1f, 0.6f, 0.9f, 1f];
        document.AddElement(new Stroke(TestData.Line(new Vector2(0, 0), new Vector2(5, 5)), new PenBrush { PressureCurve = PressureCurve.FromSamples(samples) }));

        InkDocument loaded = InkSerializer.Deserialize(InkSerializer.Serialize(document));
        PressureCurve curve = ((Stroke)loaded.Layers[0].Elements[0]).Brush.PressureCurve;
        Assert.Equal(PressureCurveKind.Custom, curve.Kind);
        Assert.Equal(samples, curve.Samples.ToArray());
    }

    [Fact]
    public void Json_point_encoding_round_trips_exactly_enough()
    {
        var document = new InkDocument();
        document.AddElement(new Stroke(TestData.PressureRamp(new Vector2(1.234f, 5.678f), new Vector2(100f, 50f), 20), new PenBrush()));
        InkDocument loaded = InkSerializer.Deserialize(InkSerializer.Serialize(document, InkSerializerOptions.Readable));

        var a = (Stroke)document.Layers[0].Elements[0];
        var b = (Stroke)loaded.Layers[0].Elements[0];
        for (int i = 0; i < a.Points.Count; i++)
        {
            Assert.Equal(a.Points.X[i], b.Points.X[i], 0.001f);
            Assert.Equal(a.Points.Pressure[i], b.Points.Pressure[i], 0.001f);
        }
    }

    [Fact]
    public void Serialization_is_deterministic()
    {
        InkDocument document = TestData.SampleDocument();
        Assert.Equal(InkSerializer.Serialize(document), InkSerializer.Serialize(document));
    }

    [Fact]
    public void Round_trip_is_stable_across_two_generations()
    {
        InkDocument document = TestData.SampleDocument();
        string first = InkSerializer.Serialize(document);
        string second = InkSerializer.Serialize(InkSerializer.Deserialize(first));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Unknown_element_types_survive_a_round_trip()
    {
        InkDocument document = TestData.SampleDocument();
        string json = InkSerializer.Serialize(document);
        // Inject an element type from a hypothetical future version.
        int at = json.IndexOf("\"elements\":[", StringComparison.Ordinal);
        json = json.Insert(at + "\"elements\":[".Length, "{\"type\":\"future-shape\",\"id\":\"9f1d4d1e-0000-4000-8000-000000000001\",\"custom\":{\"a\":1}},");

        InkDocument loaded = InkSerializer.Deserialize(json);
        Assert.Contains(loaded.AllElements, e => e is UnknownElement);

        string again = InkSerializer.Serialize(loaded);
        Assert.Contains("future-shape", again, StringComparison.Ordinal);
        Assert.Contains("\"custom\"", again, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_element_types_can_be_dropped_on_request()
    {
        InkDocument document = TestData.SampleDocument();
        string source = InkSerializer.Serialize(document);
        int at = source.IndexOf("\"elements\":[", StringComparison.Ordinal);
        string json = source.Insert(at + "\"elements\":[".Length, "{\"type\":\"future-shape\",\"id\":\"9f1d4d1e-0000-4000-8000-000000000001\"},");

        InkDocument loaded = InkSerializer.Deserialize(json, InkDeserializerOptions.Default with { PreserveUnknownElements = false });
        Assert.DoesNotContain(loaded.AllElements, e => e is UnknownElement);
    }

    [Fact]
    public void An_unknown_brush_kind_still_renders_with_its_stored_appearance()
    {
        InkDocument document = TestData.SampleDocument();
        string json = InkSerializer.Serialize(document).Replace("\"kind\":\"pen\"", "\"kind\":\"glitter-2030\"", StringComparison.Ordinal);
        InkDocument loaded = InkSerializer.Deserialize(json);
        var stroke = (Stroke)loaded.Layers[0].Elements[0];
        Assert.Equal(5f, stroke.Brush.Width, 3);
        Assert.False(stroke.Geometry.IsEmpty);
    }

    [Fact]
    public void Duplicate_element_ids_are_repaired_instead_of_failing_the_load()
    {
        // A hand-edited or merged document can repeat an id; the drawing is still usable.
        string json = """
        {"format":"blazorink","version":1,"layers":[{"id":"11111111-1111-4111-8111-111111111111","name":"L","elements":[
          {"type":"rect","id":"22222222-2222-4222-8222-222222222222","rect":[0,0,10,10]},
          {"type":"rect","id":"22222222-2222-4222-8222-222222222222","rect":[20,20,10,10]}
        ]}]}
        """;
        InkDocument document = InkSerializer.Deserialize(json);
        Assert.Equal(2, document.ElementCount);
        Assert.Equal(2, document.AllElements.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void The_output_is_valid_json()
    {
        using JsonDocument parsed = JsonDocument.Parse(InkSerializer.Serialize(TestData.SampleDocument()));
        Assert.Equal("blazorink", parsed.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, parsed.RootElement.GetProperty("version").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"format\":\"other\",\"version\":1}")]
    [InlineData("{\"format\":\"blazorink\",\"version\":1}")]
    [InlineData("{\"format\":\"blazorink\",\"version\":1,\"layers\":{}}")]
    [InlineData("[1,2,3]")]
    public void Malformed_documents_are_rejected_with_a_clear_error(string json)
    {
        Assert.Throws<InkFormatException>(() => InkSerializer.Deserialize(json));
        Assert.False(InkSerializer.TryDeserialize(json, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_newer_schema_version_is_refused_with_an_actionable_message()
    {
        InkFormatException ex = Assert.Throws<InkFormatException>(() => InkSerializer.Deserialize("{\"format\":\"blazorink\",\"version\":99,\"layers\":[]}"));
        Assert.Contains("newer version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Oversized_input_is_rejected_before_parsing()
    {
        string json = InkSerializer.Serialize(TestData.SampleDocument());
        Assert.Throws<InkFormatException>(() => InkSerializer.Deserialize(json, InkDeserializerOptions.Default with { MaxInputBytes = 10 }));
    }

    [Fact]
    public void Coordinates_beyond_the_limit_are_rejected()
    {
        string json = """
        {"format":"blazorink","version":1,"layers":[{"id":"11111111-1111-4111-8111-111111111111","name":"L","elements":[
          {"type":"stroke","id":"22222222-2222-4222-8222-222222222222",
           "brush":{"kind":"pen","color":"#000000","width":2},
           "points":{"count":2,"encoding":"json","x":[0,99999999],"y":[0,0],"p":[0.5,0.5],"t":[0,10]}}
        ]}]}
        """;
        Assert.Throws<InkFormatException>(() => InkSerializer.Deserialize(json));
    }

    [Fact]
    public void A_singular_transform_is_replaced_by_identity()
    {
        string json = """
        {"format":"blazorink","version":1,"layers":[{"id":"11111111-1111-4111-8111-111111111111","name":"L","elements":[
          {"type":"rect","id":"22222222-2222-4222-8222-222222222222","rect":[0,0,10,10],"transform":[0,0,0,0,0,0]}
        ]}]}
        """;
        InkDocument document = InkSerializer.Deserialize(json);
        Assert.Equal(Matrix3x2.Identity, document.Layers[0].Elements[0].Transform);
    }

    [Fact]
    public void Mismatched_point_array_lengths_are_rejected()
    {
        string json = """
        {"format":"blazorink","version":1,"layers":[{"id":"11111111-1111-4111-8111-111111111111","name":"L","elements":[
          {"type":"stroke","id":"22222222-2222-4222-8222-222222222222",
           "brush":{"kind":"pen","color":"#000000","width":2},
           "points":{"count":3,"encoding":"json","x":[0,1,2],"y":[0,1],"p":[0.5],"t":[0]}}
        ]}]}
        """;
        Assert.Throws<InkFormatException>(() => InkSerializer.Deserialize(json));
    }

    [Fact]
    public void Images_with_a_disallowed_scheme_are_dropped()
    {
        string json = """
        {"format":"blazorink","version":1,"layers":[{"id":"11111111-1111-4111-8111-111111111111","name":"L","elements":[
          {"type":"image","id":"22222222-2222-4222-8222-222222222222","rect":[0,0,10,10],"src":"javascript:alert(1)"},
          {"type":"image","id":"33333333-3333-4333-8333-333333333333","rect":[0,0,10,10],"src":"data:text/html;base64,PHNjcmlwdD4="}
        ]}]}
        """;
        InkDocument document = InkSerializer.Deserialize(json);
        Assert.Empty(document.AllElements);
    }

    [Fact]
    public void Remote_images_can_be_refused()
    {
        string json = """
        {"format":"blazorink","version":1,"layers":[{"id":"11111111-1111-4111-8111-111111111111","name":"L","elements":[
          {"type":"image","id":"22222222-2222-4222-8222-222222222222","rect":[0,0,10,10],"src":"https://example.com/x.png"}
        ]}]}
        """;
        Assert.Single(InkSerializer.Deserialize(json).AllElements);
        Assert.Empty(InkSerializer.Deserialize(json, InkDeserializerOptions.Default with { AllowRemoteImages = false }).AllElements);
    }

    [Fact]
    public void A_dangerous_font_family_is_replaced_with_the_default()
    {
        string json = """
        {"format":"blazorink","version":1,"layers":[{"id":"11111111-1111-4111-8111-111111111111","name":"L","elements":[
          {"type":"text","id":"22222222-2222-4222-8222-222222222222","position":[0,0],"text":"hi",
           "textStyle":{"font":"x;} body{display:none} .a{font-family:x","size":12,"color":"#000000"}}
        ]}]}
        """;
        InkDocument document = InkSerializer.Deserialize(json);
        var text = (TextElement)document.Layers[0].Elements[0];
        Assert.Equal(TextStyle.Default.FontFamily, text.Style.FontFamily);
    }

    [Fact]
    public async Task Stream_round_trip_works()
    {
        InkDocument document = TestData.SampleDocument();
        using var stream = new MemoryStream();
        await InkSerializer.SerializeAsync(document, stream);
        stream.Position = 0;
        InkDocument loaded = await InkSerializer.DeserializeAsync(stream);
        Assert.Equal(document.ElementCount, loaded.ElementCount);
    }

    [Fact]
    public void Images_can_be_saved_without_their_payload()
    {
        var document = new InkDocument();
        byte[] png = TestData.CreatePng(4, 4, (_, _) => InkColor.Black);
        document.AddElement(new ImageElement(ImageSource.FromBytes(png, "image/png", 4, 4), new Rect(0, 0, 40, 40)));

        string full = InkSerializer.Serialize(document);
        string stripped = InkSerializer.Serialize(document, InkSerializerOptions.Default with { IncludeEmbeddedImages = false });
        Assert.True(stripped.Length < full.Length);
        Assert.Contains("srcOmitted", stripped, StringComparison.Ordinal);
    }
}

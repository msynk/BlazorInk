using System.Globalization;
using System.Numerics;
using System.Xml;
using BlazorInk.Brushes;
using BlazorInk.Export;
using BlazorInk.Geometry;
using BlazorInk.Raster;
using BlazorInk.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace BlazorInk.Tests.Core;

/// <summary>
/// Regressions for reading untrusted documents and images: malformed input must always surface as an
/// <see cref="InkFormatException"/> or a format error, never as an exception the caller was not told to expect.
/// </summary>
public class SerializationRegressionTests(ITestOutputHelper output)
{
    [Fact]
    public void Invalid_utf8_in_a_string_is_reported_as_a_format_error()
    {
        // JsonElement.GetString throws InvalidOperationException on a string that is not valid UTF-8. Callers were
        // promised InkFormatException, and TryDeserialize only catches that one, so it would escape unhandled.
        string valid = InkSerializer.Serialize(TestData.SampleDocument());
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(valid);
        int at = valid.IndexOf("Drawing", StringComparison.Ordinal);
        Assert.True(at > 0);
        bytes[at] = 0xC3;
        bytes[at + 1] = 0x28;

        Assert.Throws<InkFormatException>(() => InkSerializer.Deserialize(bytes));
        Assert.False(InkSerializer.TryDeserialize(bytes, out InkDocument? parsed, out string? error));
        Assert.Null(parsed);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void An_empty_metadata_key_does_not_fail_the_load()
    {
        const string json = """
            {"format":"blazorink","version":1,"metadata":{"":"x","keep":"y"},"layers":[{"elements":[]}]}
            """;
        InkDocument document = InkSerializer.Deserialize(json);
        Assert.Equal("y", document.Metadata["keep"]);
        Assert.False(document.Metadata.ContainsKey(string.Empty));
    }

    [Fact]
    public void Repeated_round_trips_converge()
    {
        InkDocument document = TestData.SampleDocument();
        string a = InkSerializer.Serialize(document);
        string b = InkSerializer.Serialize(InkSerializer.Deserialize(a));
        string c = InkSerializer.Serialize(InkSerializer.Deserialize(b));
        Assert.Equal(b, c);
    }

    [Fact]
    public void Editing_after_a_round_trip_still_undoes_correctly()
    {
        InkDocument document = InkSerializer.Deserialize(InkSerializer.Serialize(TestData.SampleDocument()));
        using var history = new History.HistoryManager(document);
        int before = document.ElementCount;
        document.AddElement(TestData.Stroke(new Vector2(5, 5), new Vector2(50, 50)));
        Assert.True(history.Undo());
        Assert.Equal(before, document.ElementCount);
        string round = InkSerializer.Serialize(document);
        Assert.Equal(InkSerializer.Serialize(InkSerializer.Deserialize(round)), round);
    }

    [Fact]
    public void Mutated_documents_never_crash_the_reader()
    {
        string valid = InkSerializer.Serialize(TestData.SampleDocument());
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(valid);
        var random = new Random(7);
        var unexpected = new List<string>();

        for (int i = 0; i < 5000; i++)
        {
            byte[] copy = (byte[])bytes.Clone();
            int mutations = 1 + random.Next(6);
            for (int m = 0; m < mutations; m++) copy[random.Next(copy.Length)] = (byte)random.Next(256);
            try
            {
                InkSerializer.Deserialize(copy);
            }
            catch (InkFormatException)
            {
            }
#pragma warning disable CA1031 // Anything other than InkFormatException is exactly what this test is looking for.
            catch (Exception ex)
            {
                unexpected.Add($"{ex.GetType().Name}: {ex.Message}");
            }
#pragma warning restore CA1031
        }
        output.WriteLine($"unexpected: {unexpected.Count}");
        foreach (string u in unexpected.Distinct().Take(15)) output.WriteLine("  " + u);
        Assert.Empty(unexpected);
    }

    [Fact]
    public void Structurally_hostile_documents_are_rejected_cleanly()
    {
        string[] hostile =
        [
            "{}",
            "[]",
            "null",
            "{\"format\":\"blazorink\"}",
            "{\"format\":\"blazorink\",\"version\":1,\"layers\":[]}",
            "{\"format\":\"blazorink\",\"version\":1,\"layers\":[{\"elements\":[{\"type\":\"stroke\",\"points\":{\"encoding\":\"json\",\"x\":[1e39],\"y\":[1]}}]}]}",
            "{\"format\":\"blazorink\",\"version\":1,\"settings\":{\"width\":-5,\"height\":-5},\"layers\":[]}",
            "{\"format\":\"blazorink\",\"version\":1,\"layers\":[{\"elements\":[{\"type\":\"stroke\",\"points\":{\"encoding\":\"b64\",\"data\":\"////////\"}}]}]}",
            "{\"format\":\"blazorink\",\"version\":1,\"layers\":[{\"elements\":[{\"type\":\"text\",\"position\":[0,0],\"text\":\"x\",\"textStyle\":{\"size\":1e30}}]}]}",
            "{\"format\":\"blazorink\",\"version\":1,\"layers\":[{\"elements\":[{\"type\":\"image\",\"src\":\"javascript:alert(1)\",\"rect\":[0,0,10,10]}]}]}",
            "{\"format\":\"blazorink\",\"version\":1,\"layers\":[{\"elements\":[{\"type\":\"ellipse\",\"rect\":[0,0,-1e30,-1e30]}]}]}",
            "{\"format\":\"blazorink\",\"version\":1,\"layers\":[{\"elements\":[{\"type\":\"stroke\",\"transform\":[1000000,0,0,1000000,0,0],\"points\":{\"encoding\":\"json\",\"x\":[0,100],\"y\":[0,100]}}]}]}",
        ];
        foreach (string json in hostile)
        {
            try
            {
                InkDocument d = InkSerializer.Deserialize(json);
                // Whatever survived must still be a coherent, exportable document.
                string svg = SvgExporter.Export(d);
                var xml = new XmlDocument();
                xml.LoadXml(svg);
                output.WriteLine($"accepted: {d.ElementCount} elements, content bounds {d.ContentBounds}");
            }
            catch (InkFormatException ex)
            {
                output.WriteLine($"rejected: {ex.Message}");
            }
        }
    }

    [Fact]
    public void Every_sample_export_is_well_formed_and_has_a_sane_view_box()
    {
        InkDocument document = TestData.SampleDocument();
        document.AddElement(new Stroke(TestData.Line(new Vector2(30, 30), new Vector2(200, 100), 20), new PixelEraserBrush { Width = 20 }));
        foreach (ExportRegion region in Enum.GetValues<ExportRegion>())
        {
            string svg = SvgExporter.Export(document, new SvgExportOptions { Region = region, IncludeXmlDeclaration = true, IncludeElementDescriptions = true });
            var xml = new XmlDocument();
            xml.LoadXml(svg);
            string viewBox = xml.DocumentElement!.GetAttribute("viewBox");
            output.WriteLine($"{region}: viewBox={viewBox} width={xml.DocumentElement.GetAttribute("width")}");
            Assert.False(string.IsNullOrWhiteSpace(viewBox));
            string[] parts = viewBox.Split(' ');
            Assert.Equal(4, parts.Length);
            Assert.True(float.Parse(parts[2], CultureInfo.InvariantCulture) > 0);
            Assert.True(float.Parse(parts[3], CultureInfo.InvariantCulture) > 0);
            Assert.DoesNotContain("NaN", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("Infinity", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("E+", svg, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_fuzzed_png_never_escapes_the_decoder_as_an_unexpected_exception()
    {
        // The decoder runs on embedded data: URIs, which arrive inside untrusted documents.
        byte[] valid = PngExporter.Export(TestData.SampleDocument(), new PngExportOptions { Scale = 0.2f });
        var random = new Random(4242);
        var unexpected = new List<string>();
        int decoded = 0;

        for (int i = 0; i < 4000; i++)
        {
            byte[] copy = (byte[])valid.Clone();
            int mutations = 1 + random.Next(8);
            for (int m = 0; m < mutations; m++) copy[random.Next(copy.Length)] = (byte)random.Next(256);
            try
            {
                PngCodec.DecodedImage image = PngCodec.Decode(copy);
                decoded++;
                Assert.True(image.Width > 0 && image.Height > 0);
                Assert.Equal(image.Width * image.Height * 4, image.Rgba.Length);
            }
            catch (FormatException)
            {
            }
            catch (InvalidDataException)
            {
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                unexpected.Add($"{ex.GetType().Name}: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        output.WriteLine($"decoded {decoded} mutated images, {unexpected.Count} unexpected exceptions");
        foreach (string u in unexpected.Distinct().Take(10)) output.WriteLine("  " + u);
        Assert.Empty(unexpected);
    }

    [Fact]
    public void Truncated_and_absurd_png_headers_are_refused_quickly()
    {
        byte[] valid = PngExporter.Export(TestData.SampleDocument(), new PngExportOptions { Scale = 0.2f });
        var cases = new List<byte[]>
        {
            Array.Empty<byte>(),
            new byte[] { 0 },
            valid[..8],
            valid[..20],
            valid[..(valid.Length / 2)],
        };

        // An IHDR claiming a colossal image must be refused rather than allocating for it.
        byte[] huge = (byte[])valid.Clone();
        huge[16] = 0x7F; huge[17] = 0xFF; huge[18] = 0xFF; huge[19] = 0xF0;
        huge[20] = 0x7F; huge[21] = 0xFF; huge[22] = 0xFF; huge[23] = 0xF0;
        cases.Add(huge);

        foreach (byte[] data in cases)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                PngCodec.Decode(data);
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException)
            {
                output.WriteLine($"{data.Length} bytes rejected: {ex.Message}");
            }
            clock.Stop();
            Assert.True(clock.Elapsed.TotalSeconds < 5, $"decoding {data.Length} bytes took {clock.Elapsed.TotalSeconds:0.0} s");
        }
    }
}

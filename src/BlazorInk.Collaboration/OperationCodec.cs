using System.Buffers;
using System.Text;
using System.Text.Json;
using BlazorInk.Operations;
using BlazorInk.Serialization;

namespace BlazorInk.Collaboration;

/// <summary>
/// Serializes <see cref="DrawingOperation"/> values so they can travel between participants.
/// </summary>
/// <remarks>
/// Operations are encoded with the same element format the document serializer uses, so the wire format is
/// versioned and validated exactly like a saved file. Only the changed elements are sent: drawing a stroke
/// transmits that stroke, never the document.
/// </remarks>
public static class OperationCodec
{
    /// <summary>Serializes an operation to JSON.</summary>
    public static string Serialize(DrawingOperation operation, InkSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true }))
        {
            Write(writer, operation, options ?? InkSerializerOptions.Default);
            writer.Flush();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(Utf8JsonWriter writer, DrawingOperation operation, InkSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (operation)
        {
            case AddElementOperation add:
                writer.WriteString("op", "add");
                writer.WriteString("layer", add.LayerId);
                if (add.Index is { } index) writer.WriteNumber("index", index);
                writer.WritePropertyName("element");
                InkSerializer.WriteElementTo(writer, add.Element, options);
                break;
            case RemoveElementOperation remove:
                writer.WriteString("op", "remove");
                writer.WriteString("element", remove.ElementId);
                break;
            case ReplaceElementOperation replace:
                writer.WriteString("op", "replace");
                writer.WritePropertyName("element");
                InkSerializer.WriteElementTo(writer, replace.Element, options);
                break;
            case ReorderElementOperation reorder:
                writer.WriteString("op", "reorder");
                writer.WriteString("element", reorder.ElementId);
                writer.WriteString("layer", reorder.TargetLayerId);
                if (reorder.Index is { } target) writer.WriteNumber("index", target);
                break;
            case AddLayerOperation addLayer:
                writer.WriteString("op", "addLayer");
                writer.WriteString("layer", addLayer.LayerId);
                if (addLayer.Index is { } layerIndex) writer.WriteNumber("index", layerIndex);
                WriteLayerProperties(writer, addLayer.Properties);
                if (addLayer.Elements is { Count: > 0 })
                {
                    writer.WritePropertyName("elements");
                    writer.WriteStartArray();
                    foreach (InkElement element in addLayer.Elements) InkSerializer.WriteElementTo(writer, element, options);
                    writer.WriteEndArray();
                }
                break;
            case RemoveLayerOperation removeLayer:
                writer.WriteString("op", "removeLayer");
                writer.WriteString("layer", removeLayer.LayerId);
                break;
            case UpdateLayerOperation updateLayer:
                writer.WriteString("op", "updateLayer");
                writer.WriteString("layer", updateLayer.LayerId);
                WriteLayerProperties(writer, updateLayer.Properties);
                break;
            case ReorderLayerOperation reorderLayer:
                writer.WriteString("op", "reorderLayer");
                writer.WriteString("layer", reorderLayer.LayerId);
                writer.WriteNumber("index", reorderLayer.Index);
                break;
            case UpdateSettingsOperation settings:
                writer.WriteString("op", "settings");
                writer.WriteNumber("width", settings.Settings.Width);
                writer.WriteNumber("height", settings.Settings.Height);
                writer.WriteString("background", settings.Settings.Background.ToCss());
                break;
            case SetMetadataOperation metadata:
                writer.WriteString("op", "metadata");
                writer.WriteString("key", metadata.Key);
                if (metadata.Value is null) writer.WriteNull("value");
                else writer.WriteString("value", metadata.Value);
                break;
            case BatchOperation batch:
                writer.WriteString("op", "batch");
                if (batch.Label is { Length: > 0 }) writer.WriteString("label", batch.Label);
                writer.WritePropertyName("operations");
                writer.WriteStartArray();
                foreach (DrawingOperation inner in batch.Operations) Write(writer, inner, options);
                writer.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException($"{operation.GetType().Name} cannot be sent to other participants. Custom operations must be translated by the application.");
        }
        writer.WriteEndObject();
    }

    private static void WriteLayerProperties(Utf8JsonWriter writer, LayerProperties properties)
    {
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WriteString("name", properties.Name);
        writer.WriteBoolean("visible", properties.IsVisible);
        writer.WriteBoolean("locked", properties.IsLocked);
        writer.WriteNumber("opacity", properties.Opacity);
        writer.WriteEndObject();
    }

    /// <summary>Parses an operation previously produced by <see cref="Serialize"/>.</summary>
    /// <exception cref="InkFormatException">The JSON is not a valid operation.</exception>
    public static DrawingOperation Deserialize(string json, InkDeserializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        options ??= InkDeserializerOptions.Default;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            return Read(document.RootElement, options);
        }
        catch (JsonException ex)
        {
            throw new InkFormatException("The operation is not valid JSON.", ex);
        }
    }

    private static DrawingOperation Read(JsonElement element, InkDeserializerOptions options)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InkFormatException("An operation must be a JSON object.");
        string kind = element.TryGetProperty("op", out JsonElement op) ? op.GetString() ?? string.Empty : string.Empty;

        switch (kind)
        {
            case "add":
            {
                InkElement item = ReadElement(element, "element", options);
                return new AddElementOperation(ReadGuid(element, "layer"), item, ReadInt(element, "index"));
            }
            case "remove":
                return new RemoveElementOperation(ReadGuid(element, "element"));
            case "replace":
                return new ReplaceElementOperation(ReadElement(element, "element", options));
            case "reorder":
                return new ReorderElementOperation(ReadGuid(element, "element"), ReadGuid(element, "layer"), ReadInt(element, "index"));
            case "addLayer":
            {
                List<InkElement>? elements = null;
                if (element.TryGetProperty("elements", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
                {
                    elements = new List<InkElement>();
                    foreach (JsonElement e in array.EnumerateArray())
                    {
                        InkElement? parsed = InkSerializer.ReadElementFrom(e, options);
                        if (parsed is not null) elements.Add(parsed);
                    }
                }
                return new AddLayerOperation(ReadGuid(element, "layer"), ReadLayerProperties(element), ReadInt(element, "index"), elements);
            }
            case "removeLayer":
                return new RemoveLayerOperation(ReadGuid(element, "layer"));
            case "updateLayer":
                return new UpdateLayerOperation(ReadGuid(element, "layer"), ReadLayerProperties(element));
            case "reorderLayer":
                return new ReorderLayerOperation(ReadGuid(element, "layer"), ReadInt(element, "index") ?? 0);
            case "settings":
                return new UpdateSettingsOperation(new DocumentSettings
                {
                    Width = ReadFloat(element, "width", 800f),
                    Height = ReadFloat(element, "height", 600f),
                    Background = element.TryGetProperty("background", out JsonElement bg) && Geometry.InkColor.TryParse(bg.GetString(), out Geometry.InkColor color) ? color : Geometry.InkColor.White,
                });
            case "metadata":
            {
                string key = element.TryGetProperty("key", out JsonElement k) ? k.GetString() ?? string.Empty : string.Empty;
                string? value = element.TryGetProperty("value", out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (key.Length == 0) throw new InkFormatException("A metadata operation must have a key.");
                return new SetMetadataOperation(key, value);
            }
            case "batch":
            {
                var operations = new List<DrawingOperation>();
                if (element.TryGetProperty("operations", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement e in array.EnumerateArray()) operations.Add(Read(e, options));
                }
                string? label = element.TryGetProperty("label", out JsonElement l) ? l.GetString() : null;
                return new BatchOperation(operations, label);
            }
            default:
                throw new InkFormatException($"Unknown operation \"{kind}\".");
        }
    }

    private static InkElement ReadElement(JsonElement element, string name, InkDeserializerOptions options)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) throw new InkFormatException($"The operation is missing its \"{name}\".");
        return InkSerializer.ReadElementFrom(value, options) ?? throw new InkFormatException("The operation carries an element this version cannot read.");
    }

    private static LayerProperties ReadLayerProperties(JsonElement element)
    {
        if (!element.TryGetProperty("properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Object) return new LayerProperties();
        return new LayerProperties
        {
            Name = properties.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "Layer" : "Layer",
            IsVisible = !properties.TryGetProperty("visible", out JsonElement v) || v.ValueKind != JsonValueKind.False,
            IsLocked = properties.TryGetProperty("locked", out JsonElement l) && l.ValueKind == JsonValueKind.True,
            Opacity = properties.TryGetProperty("opacity", out JsonElement o) && o.TryGetSingle(out float opacity) ? Math.Clamp(opacity, 0f, 1f) : 1f,
        };
    }

    private static Guid ReadGuid(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out Guid guid)
            ? guid
            : throw new InkFormatException($"The operation is missing a valid \"{name}\" identifier.");

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : null;

    private static float ReadFloat(JsonElement element, string name, float fallback)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float result) && float.IsFinite(result) ? result : fallback;
}

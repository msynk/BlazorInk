# Serialization

## The API

```csharp
string json = InkSerializer.Serialize(document);
InkDocument document = InkSerializer.Deserialize(json);

byte[] utf8 = InkSerializer.SerializeToUtf8Bytes(document);
await InkSerializer.SerializeAsync(document, stream);
InkDocument loaded = await InkSerializer.DeserializeAsync(stream);

if (!InkSerializer.TryDeserialize(json, out InkDocument? d, out string? error))
{
    // report `error` to the user instead of throwing
}
```

## The format

Versioned JSON with compact binary stroke points:

```json
{
  "format": "blazorink",
  "version": 1,
  "id": "0b3f...",
  "settings": { "width": 800, "height": 600, "background": "#ffffff" },
  "layers": [
    {
      "id": "9c21...",
      "name": "Drawing",
      "elements": [
        {
          "type": "stroke",
          "id": "4ad7...",
          "brush": { "kind": "pen", "color": "#111827", "width": 4 },
          "input": { "device": "pen", "pressure": true },
          "points": { "count": 142, "encoding": "b64", "data": "AQBAjgE..." }
        }
      ]
    }
  ]
}
```

It is a stable contract, not a projection of the library's classes. Every element carries a `type` discriminator
and only documented fields.

## How points are stored

`PointCodec` quantizes coordinates to a fixed grid (64 steps per document unit by default) and delta-encodes them
as zigzag varints. Pressure is one byte, time is a varint delta in milliseconds, and tilt and twist are only
present when the device produced them.

A handwriting point costs about 3 to 6 bytes depending on how fast the pen was moving, against 30 or more for a
JSON number array. The encoding is deterministic: the same points always produce the same bytes, so documents can
be compared and diffed.

For a readable file, ask for JSON points:

```csharp
string readable = InkSerializer.Serialize(document, InkSerializerOptions.Readable);
```

## Compatibility

- **Version checks.** A document written by a newer schema version is refused with a message naming the problem,
  rather than being partially read.
- **Unknown elements survive.** An element whose `type` this build does not recognise is preserved verbatim
  through a load and save, so an older build never silently destroys work made by a newer one. Set
  `PreserveUnknownElements = false` to drop them instead.
- **Unknown brushes still render.** A brush kind that is not registered falls back to a pen carrying the stored
  colour, width, opacity and pressure curve, so the drawing still looks approximately right.
- **Unknown fields are ignored** on read, so adding a field is not a breaking change.

## Untrusted input

Loading is a parser attack surface. Everything is validated:

| Check | Limit |
| --- | --- |
| Input size | `MaxInputBytes`, 256 MB by default, checked before parsing |
| Layers, elements | `DocumentLimits.MaxLayers`, `MaxElements` |
| Points per stroke, total points | `MaxPointsPerStroke`, `MaxTotalPoints` |
| Coordinates | `MaxCoordinate`, checked against every element's bounds and rejected rather than clamped |
| Brush width | `MaxStrokeWidth` |
| Image URIs | scheme and media type allowlist; SVG and remote images are opt-in |
| Font families | restricted character set, because the value reaches CSS and SVG |
| Transforms | singular or absurd matrices are replaced with the identity |
| Duplicate ids | repaired by assigning a new id rather than failing the load |

```csharp
var options = new InkDeserializerOptions
{
    Limits = new DocumentLimits { MaxElements = 5_000, MaxPointsPerStroke = 20_000 },
    AllowRemoteImages = false,
    AllowSvgImages = false,
};
InkDocument document = InkSerializer.Deserialize(json, options);
```

Malformed input throws `InkFormatException` with a message describing what is wrong. It never allocates
unboundedly on a hostile size field.

## Size in practice

Measured on a synthetic handwriting document of 10,000 strokes with 60 points each (600,000 points):

| Encoding | Size |
| --- | --- |
| Compact, base64 binary points | about 8 bytes per point including base64 expansion and all JSON overhead |
| Readable, JSON number arrays | several times larger |

The collaboration benchmark asserts the compact figure: a stroke costs under 14 bytes per point on the wire,
including the operation wrapper.

## Custom data

Attach your own information to a document or an element; both survive a round trip.

```csharp
document.SetMetadata("case-id", "2026-114");

document.ReplaceElement(stroke with
{
    Description = "Signature of Jane Doe",
    Metadata = new Dictionary<string, string> { ["signer"] = "jane" },
});
```

`Description` is what the canvas reports to assistive technology, and what SVG export writes as a `title`
element when `IncludeElementDescriptions` is set. Elements without one fall back to a structural summary.

# Drawing, brushes and pressure

## How a stroke is made

A pointer event does not become a point directly. Between the two sits `StrokeBuilder`, which does four things,
all without allocating:

1. **Spacing filter.** Samples closer than `Brush.Spacing * Brush.Width` to the previous stored point are merged
   into it and their pressure averaged. This removes sensor jitter and keeps documents small: a 240 Hz pen
   produces far more samples than the shape needs.
2. **Streamline smoothing.** Each stored position is pulled toward the previous one by `Brush.Smoothing`. This is
   an exponential filter, so it removes hand tremor without adding lag on straight sections.
3. **Pressure.** Real pen pressure is lightly smoothed. When the device has none, and the brush asks for it,
   pressure is derived from drawing speed so fast movements thin out like a real pen.
4. **Timestamps** are recorded relative to the start of the stroke, which is what makes velocity available later.

When the pointer lifts, the last stored point is moved to the exact lift position, so a stroke ends where the pen
did rather than at the last filtered sample.

## Turning points into geometry

`StrokeOutliner` builds a closed, variable-width outline polygon around the points:

- The radius at each point is `Width/2 * PressureCurve(pressure) * taper`.
- Left and right offsets are placed along the perpendicular of the averaged neighbouring directions, with a
  bounded miter correction so the visual width stays constant through gentle corners.
- At sharp corners, where the direction changes by 90 degrees or more, an arc of points is inserted so the
  outline does not pinch.
- Round or flat caps close both ends.
- The polygon is emitted as a smooth quadratic path through segment midpoints.

The result is filled with the non-zero rule, so a tight loop that folds over itself stays solid instead of
punching a hole.

Constant-width brushes such as the marker and highlighter skip all of that and emit a smooth centerline
(Catmull-Rom converted to cubic Béziers) to be stroked at a fixed width. That produces far less geometry.

Geometry is cached per (points, brush) pair on the stroke, so it is built once and reused by every redraw and
every export.

## The built-in brushes

| Brush | Behaviour |
| --- | --- |
| `PenBrush` | Pressure-sensitive variable width, round caps, slight taper at the end. The default. |
| `PencilBrush` | Thin, slightly translucent, strong pressure response, tighter point spacing. |
| `MarkerBrush` | Constant width, round caps, opaque. Rendered as a centerline. |
| `HighlighterBrush` | Wide, translucent, flat caps, multiply blending so text underneath stays legible. |
| `PixelEraserBrush` | Erases what is beneath it within its layer, as a real element that can be undone. |

## Configuring a brush

```csharp
var pen = new PenBrush
{
    Color = InkColor.Parse("#1d4ed8"),
    Width = 6f,                  // maximum width, reached at full pressure
    Opacity = 1f,                // applied to the stroke as one unit, so self-overlaps do not darken
    Smoothing = 0.5f,            // 0 keeps every wobble, 0.9 is very smooth and slightly rounds fast corners
    Spacing = 0.25f,             // minimum point spacing as a fraction of Width
    SimulatePressure = true,     // derive width from speed when the device has no pressure
    TaperStart = 0f,
    TaperEnd = 6f,
    PressureCurve = PressureCurve.Pen,
};
```

For handwriting, prefer less smoothing and closer spacing so small letters keep their shape:

```csharp
var writing = new PenBrush { Width = 3f, Smoothing = 0.35f, Spacing = 0.12f, TaperEnd = 5f };
```

## Pressure curves

A `PressureCurve` maps input pressure (0 to 1) to a width factor between `MinFactor` and `MaxFactor`.

```csharp
PressureCurve.Pen        // S-curve from 20% to 100%: the default pen response
PressureCurve.Linear     // linear from 20% to 100%
PressureCurve.Constant   // ignores pressure entirely

new PressureCurve(PressureCurveKind.EaseIn, minFactor: 0.1f, maxFactor: 1f)
PressureCurve.FromSamples([0f, 0.1f, 0.45f, 0.8f, 1f])   // a custom response curve
```

`PressureCurve.Pen` puts a normal touch (pressure 0.5) at about 60% of the nominal width, so `Width` behaves the
way a user expects while still leaving room to press harder.

## Devices without pressure

A mouse reports a constant 0.5 while a button is down; most touch screens report the same. When
`SimulatePressure` is on, the builder derives pressure from drawing speed instead: slower means heavier, up to a
capped maximum. The result is a line that thins out on quick flicks, which reads as natural even without
hardware support.

`Stroke.Input` records which device drew the stroke and whether the pressure was real, so an application can tell
the two apart after the fact.

## Reading device capabilities

```razor
<InkCanvas @ref="_canvas" Ready="OnReady" />

@code {
    private void OnReady(InputCapabilities capabilities)
    {
        // capabilities.CoalescedEvents, .Tilt, .Twist, .MaxTouchPoints, .DevicePixelRatio, .LowLatencyCanvas
    }
}
```

`Capabilities` also gains `ObservedPen`, `ObservedTouch`, `ObservedPressure` and `ObservedTilt` as input arrives,
which is the only reliable way to know whether a real stylus is in use: feature detection tells you the browser
supports tilt, not that the device produces it.

## Tilt and twist

Tilt and barrel rotation are captured into the stroke when the device reports them, and preserved by
serialization. The built-in brushes do not shape ink from tilt, because a convincing chisel nib needs a brush
model of its own. The data is there for a custom brush to use; see [Extending the framework](extending.md).

## Simplification

When a stroke is committed, `DrawTool` simplifies it:

```csharp
var tool = new DrawTool { SimplifyTolerance = 0.12f };   // document units; 0 disables it
```

Simplification runs on position with the Ramer-Douglas-Peucker algorithm and, separately, on the pressure
channel. Position-only simplification would collapse a straight line drawn with changing pressure into two points
and throw the width variation away, so pressure is treated as its own channel with its own tolerance.

## Quality checklist

The stroke pipeline is covered by tests that assert behaviour rather than pixels:

- A constant-pressure stroke has a constant measured width.
- Pressure changes the measured width.
- A sharp corner does not produce a spike beyond the stroke radius.
- Smoothing reduces jitter without distorting the overall path.
- Fast movement produces lighter ink than slow movement when pressure is simulated.
- A single tap leaves a visible dot.
- Duplicate and degenerate points never produce non-finite geometry.

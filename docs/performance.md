# Performance

Every number on this page was measured with the benchmark suite in `tests/BlazorInk.Benchmarks`. Run it yourself:

```bash
dotnet run --project tests/BlazorInk.Benchmarks -c Release
dotnet run --project tests/BlazorInk.Benchmarks -c Release -- --filter "*Render*"
```

The demo application also has a performance page that measures the engine in your own browser, on your own
hardware, with a document you generate.

## The design decisions that matter

**Two canvases.** Committed elements live on a document canvas; the live stroke and tool previews live on an
overlay. While a stroke is being drawn only the overlay is redrawn, so the thousands of strokes already in the
document are not touched until the pen lifts.

**Input never re-renders Blazor.** Samples arrive from JavaScript once per animation frame and are processed
directly. A component render happens at most once per frame, and only when something bound to the document
actually changed.

**Geometry is cached.** A stroke's outline is built once per (points, brush) pair and reused by every redraw and
every export.

**Bounds never build geometry.** Asking an element for its bounds returns a conservative estimate from the brush
until the exact outline exists. Loading a document reads the bounds of every element, so building every outline up
front made loading far slower and allocated far more; the load figures below are what it costs now.

**Culling.** A redraw records only the elements whose bounds intersect the visible rectangle.

**Structure-of-arrays points.** Positions, pressure and time are parallel float arrays. Reading them allocates
nothing, and optional channels are only allocated when the device produces them.

## Measured results

Taken on .NET 10, x64, a desktop machine. Sizes are binary: MB means 2^20 bytes, as BenchmarkDotNet reports it. Absolute numbers depend on hardware; the ratios and the allocation
figures are the meaningful part.

### Input, the per-frame hot path

| Samples per frame | Time | Allocated |
| --- | --- | --- |
| 4 | 81 ns | 0 B |
| 16 | 299 ns | 0 B |
| 64 | 1.24 us | 0 B |

Sixty-four samples is a 240 Hz pen with coalescing on a slow frame. Processing them costs about one hundredth of
a 16 ms frame budget and allocates nothing, so drawing produces no garbage collection pressure.

### Stroke geometry

| Case | Time | Allocated |
| --- | --- | --- |
| Handwriting stroke, 60 points, variable width | 3.3 us | 2.2 KB |
| Handwriting stroke, 60 points, centerline | 0.7 us | 1.6 KB |
| Continuous stroke, 10,000 points, variable width | 534 us | 333 KB |

This runs once per stroke, not once per frame.

### Redraw

Recording a redraw for a document of handwriting strokes spread over a 1600 by 1000 page, with a 900 by 600
viewport:

| Strokes | Whole document | Visible region only |
| --- | --- | --- |
| 500 | 293 us | 141 us |
| 5,000 | 7.8 ms | 3.6 ms |

Allocation is under 200 bytes either way, because the command buffer is reused between frames.

A redraw happens when the document or the viewport changes, not while drawing. At 5,000 strokes a pan costs about
3.6 ms of recording; the browser then replays the buffer.

### Saving, loading and export

Document of 10,000 strokes, 600,000 points:

| Operation | Time | Allocated |
| --- | --- | --- |
| Save | 28 ms | 22 MB |
| Load | 72 ms | 22 MB |
| Encode 10,000 points | 81 us | 101 KB |
| Decode 10,000 points | 55 us | 156 KB |
| Export SVG | 569 ms | 146 MB |

SVG export is the expensive one, and inherently so: 600,000 points become about 35 million characters of path
data. Most of that memory is the string itself, twice over, because `Export` returns one. Use the streaming
overload for documents this size and the second copy disappears.

### Hit testing

A click with the selection tool over a 10,000-stroke document is a bounding-box rejection for almost every
element, followed by exact geometry for the few that overlap.

## Working with very large documents

- Keep the viewport meaningful. Culling is what makes a 10,000-stroke document interactive; a zoomed-out view of
  everything is the expensive case by definition.
- Use `BeginBatch` when adding many elements. One batch is one change event and one redraw.
- Prefer the streaming export overloads for large SVG files.
- Raise `DocumentLimits` deliberately rather than by default. They exist to turn a runaway document into a clear
  error instead of an unresponsive tab.

## What is not optimized

- The pure .NET rasterizer is written for correctness and portability, not speed. Browser PNG export is faster and
  is what the component uses.
- Recording a full-document redraw is linear in the number of elements. There is no spatial index; culling uses a
  linear scan of bounding boxes. For the document sizes the library targets this is not the bottleneck, and a
  spatial index would add invalidation complexity that is not currently earning its place.

# Hosting models

The same component works in every Blazor hosting model. What differs is where the engine runs, and therefore what
happens between a pen moving and ink appearing.

## Blazor WebAssembly

The engine runs in the browser, in the same process as the UI. This is the model the library is designed for.

Pointer samples arrive from JavaScript once per animation frame. The active tool processes them, the live stroke
is drawn on the overlay canvas, and nothing leaves the machine. Latency is the browser's own input-to-paint
latency and nothing more.

Rendering uses a zero-copy path: the command buffer reaches JavaScript through `[JSImport]` as a view over managed
memory, so a redraw is one call with no serialization. `InkCanvas.UsesFastRenderPath` reports whether it is
active.

No configuration is needed.

## Blazor Server

The engine runs on the server. Pointer samples travel over the SignalR circuit in batches of one animation frame,
and the resulting canvas commands travel back.

That round trip is real. On a local network it is a few milliseconds; over the internet it can be 50 to 150 ms,
which is enough to feel disconnected from the pen. Two things address it:

**Wet ink.** While a batch is in flight, the browser draws a provisional trail: a plain constant-width polyline in
the brush colour. It carries no stroke model and makes no decisions; it exists so the pen feels attached to the
hand. As soon as the engine responds, the real geometry replaces it. This is on by default under interactive
server rendering.

```razor
<InkCanvas WetInk="true" />    <!-- default outside WebAssembly -->
<InkCanvas WetInk="false" />   <!-- to turn it off -->
```

**Batching.** Input is already one message per frame rather than one per event, and the message is a flat array of
numbers rather than an object graph. A 240 Hz pen still costs 60 messages per second, not 240.

Be honest with yourself about the use case. A signature field or a form annotation over Blazor Server is fine.
Sustained handwriting over a high-latency connection is not, whatever the library does; that is what WebAssembly
is for. A Blazor Web App can render exactly the pages that need drawing with `InteractiveWebAssembly` and leave
the rest on the server.

## Blazor Web App

Mark the page or component interactive:

```razor
@rendermode InteractiveWebAssembly
```

Prerendering is handled: the canvas renders its markup on the server, discovers that no browser is available,
stays inert, and attaches when interactivity arrives. `AttachFailed` is null in that case, because nothing failed.

Put the component in the client project so its assembly is available to WebAssembly.

## Blazor Hybrid

MAUI, WPF and Windows Forms hosts run the engine in-process and the canvas in the embedded WebView. Everything
works, over the standard `IJSRuntime` path; the zero-copy render path is WebAssembly-only, and it is not needed
because there is no serialization boundary of consequence.

## When the browser layer cannot start

If the JavaScript module cannot load, because a content security policy blocks it or static web assets are not
served, the canvas stays inert instead of throwing and taking the page down. `InkCanvas.AttachFailed` carries the
reason, so an application can tell the user something useful.

```razor
<InkCanvas @ref="_canvas" Ready="() => StateHasChanged()" />

@if (_canvas?.AttachFailed is { } reason)
{
    <p role="alert">Drawing is unavailable: @reason</p>
}
```

## Content security policy

The component needs no `eval` and no inline script. It loads one module from your own origin. A policy of
`script-src 'self'` is enough for it, and because the Blazor initializer also injects a stylesheet link from
the same origin, so is `style-src 'self'`. If you serve static web assets from a CDN, allow that origin.

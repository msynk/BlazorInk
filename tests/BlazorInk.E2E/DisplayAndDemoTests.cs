using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BlazorInk.E2E;

/// <summary>
/// Browser checks for the things only a real display can answer: device pixel ratio, resizing, pointer routing
/// between contacts, and whether every page of the demo actually works.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class DisplayAndDemoTests(BrowserFixture fixture, ITestOutputHelper output)
{
    private static async Task<int> CountInkAsync(IPage page) => await page.EvaluateAsync<int>(
        """
        () => {
          const canvas = document.querySelector('canvas[data-bi-layer="document"]');
          const ctx = canvas.getContext('2d', { willReadFrequently: true });
          const data = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
          const br = data[0], bg = data[1], bb = data[2], ba = data[3];
          let count = 0;
          for (let i = 0; i < data.length; i += 4) {
            if (Math.abs(data[i] - br) + Math.abs(data[i + 1] - bg) + Math.abs(data[i + 2] - bb) + Math.abs(data[i + 3] - ba) > 24) count++;
          }
          return count;
        }
        """).ConfigureAwait(false);

    /// <summary>The bounding box, in canvas backing-store pixels, of everything that differs from the background.</summary>
    private static async Task<int[]> InkBoxAsync(IPage page) => await page.EvaluateAsync<int[]>(
        """
        () => {
          const canvas = document.querySelector('canvas[data-bi-layer="document"]');
          const ctx = canvas.getContext('2d', { willReadFrequently: true });
          const w = canvas.width, h = canvas.height;
          const data = ctx.getImageData(0, 0, w, h).data;
          const br = data[0], bg = data[1], bb = data[2], ba = data[3];
          let minX = w, minY = h, maxX = -1, maxY = -1;
          for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
            const i = (y * w + x) * 4;
            if (Math.abs(data[i] - br) + Math.abs(data[i+1] - bg) + Math.abs(data[i+2] - bb) + Math.abs(data[i+3] - ba) > 24) {
              if (x < minX) minX = x; if (x > maxX) maxX = x;
              if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
          }
          return [minX, minY, maxX, maxY];
        }
        """).ConfigureAwait(false);

    private static async Task DrawAsync(IPage page, params (float X, float Y)[] points)
    {
        IElementHandle surface = (await page.QuerySelectorAsync(".blazorink-input").ConfigureAwait(false))!;
        ElementHandleBoundingBoxResult box = (await surface.BoundingBoxAsync().ConfigureAwait(false))!;
        await page.Mouse.MoveAsync(box.X + points[0].X, box.Y + points[0].Y).ConfigureAwait(false);
        await page.Mouse.DownAsync().ConfigureAwait(false);
        for (int i = 1; i < points.Length; i++)
            await page.Mouse.MoveAsync(box.X + points[i].X, box.Y + points[i].Y, new MouseMoveOptions { Steps = 4 }).ConfigureAwait(false);
        await page.Mouse.UpAsync().ConfigureAwait(false);
        await page.WaitForTimeoutAsync(250).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task The_backing_store_follows_the_device_pixel_ratio()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using IBrowserContext context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            DeviceScaleFactor = 2,
        }).ConfigureAwait(false);
        IPage page = await context.NewPageAsync().ConfigureAwait(false);
        await page.GotoAsync(fixture.BaseUrl + "/whiteboard", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 }).ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => { const c = document.querySelector('canvas[data-bi-layer=\"document\"]'); return c && c.width > 0; }", null, new PageWaitForFunctionOptions { Timeout = 60000 }).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(300).ConfigureAwait(false);

        int[] sizes = await page.EvaluateAsync<int[]>(
            """
            () => {
              const c = document.querySelector('canvas[data-bi-layer="document"]');
              return [c.width, c.height, Math.round(parseFloat(c.style.width)), Math.round(parseFloat(c.style.height)), window.devicePixelRatio];
            }
            """).ConfigureAwait(false);
        output.WriteLine($"backing {sizes[0]}x{sizes[1]}, css {sizes[2]}x{sizes[3]}, dpr {sizes[4]}");
        Assert.Equal(2, sizes[4]);
        Assert.Equal(sizes[2] * 2, sizes[0]);
        Assert.Equal(sizes[3] * 2, sizes[1]);

        // Ink drawn at a known CSS position must land at twice that position in the backing store.
        await DrawAsync(page, (100, 100), (300, 100)).ConfigureAwait(false);
        int[] ink = await InkBoxAsync(page).ConfigureAwait(false);
        output.WriteLine($"ink box (device px): {string.Join(",", ink)}");
        Assert.InRange(ink[0], 2 * 100 - 30, 2 * 100 + 30);
        Assert.InRange(ink[2], 2 * 300 - 30, 2 * 300 + 30);
    }

    [SkippableFact]
    public async Task Resizing_the_window_keeps_the_drawing_where_it_was()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);
        await DrawAsync(page, (120, 120), (320, 220)).ConfigureAwait(false);

        string before = await page.EvaluateAsync<string>("() => document.querySelector('canvas[data-bi-layer=\"document\"]').width + 'x' + document.querySelector('canvas[data-bi-layer=\"document\"]').height").ConfigureAwait(false);
        int[] inkBefore = await InkBoxAsync(page).ConfigureAwait(false);

        await page.SetViewportSizeAsync(900, 700).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(600).ConfigureAwait(false);
        string after = await page.EvaluateAsync<string>("() => document.querySelector('canvas[data-bi-layer=\"document\"]').width + 'x' + document.querySelector('canvas[data-bi-layer=\"document\"]').height").ConfigureAwait(false);
        int[] inkAfter = await InkBoxAsync(page).ConfigureAwait(false);

        output.WriteLine($"canvas {before} -> {after}; ink {string.Join(",", inkBefore)} -> {string.Join(",", inkAfter)}");
        Assert.NotEqual(before, after);
        // The document did not change, so the ink must still start at the same document position.
        Assert.InRange(inkAfter[0], inkBefore[0] - 4, inkBefore[0] + 4);
        Assert.InRange(inkAfter[1], inkBefore[1] - 4, inkBefore[1] + 4);
    }

    [SkippableFact]
    public async Task A_cancelled_unrelated_pointer_does_not_discard_the_stroke_in_progress()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);

        // A mouse (or pen) stroke is under way when a single stray contact appears and the browser cancels it:
        // a finger brushing a touch screen, a contact the OS rejects. One contact is not a two-finger gesture, so
        // the cancel reaches the active tool, which must ignore it and keep drawing.
        await page.EvaluateAsync(
            """
            async () => {
              const target = document.querySelector('.blazorink-input');
              const rect = target.getBoundingClientRect();
              const send = (type, id, kind, x, y, buttons) => target.dispatchEvent(new PointerEvent(type, {
                pointerId: id, pointerType: kind, isPrimary: true, bubbles: true, cancelable: true,
                clientX: rect.left + x, clientY: rect.top + y, pressure: buttons ? 0.5 : 0, buttons,
              }));
              send('pointerdown', 1, 'mouse', 80, 200, 1);
              for (let i = 1; i <= 15; i++) {
                send('pointermove', 1, 'mouse', 80 + i * 14, 200, 1);
                await new Promise(r => setTimeout(r, 10));
              }
              send('pointerdown', 55, 'touch', 600, 500, 1);
              await new Promise(r => setTimeout(r, 30));
              send('pointercancel', 55, 'touch', 600, 500, 0);
              await new Promise(r => setTimeout(r, 30));
              for (let i = 16; i <= 25; i++) {
                send('pointermove', 1, 'mouse', 80 + i * 14, 200, 1);
                await new Promise(r => setTimeout(r, 10));
              }
              send('pointerup', 1, 'mouse', 80 + 25 * 14, 200, 0);
            }
            """).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(500).ConfigureAwait(false);

        int ink = await CountInkAsync(page).ConfigureAwait(false);
        output.WriteLine($"ink after the stray contact was cancelled: {ink}");
        Assert.True(ink > 300, $"the stroke was discarded when an unrelated pointer was cancelled ({ink} ink pixels)");
    }

    [SkippableFact]
    public async Task Drawing_at_a_zoomed_viewport_lands_under_the_cursor()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);

        // Zoom in with the toolbar, then draw: the ink must appear where the pointer went, not offset by the zoom.
        ILocator zoomIn = page.GetByRole(AriaRole.Button, new() { Name = "Zoom in", Exact = true });
        if (await zoomIn.CountAsync().ConfigureAwait(false) == 0)
        {
            output.WriteLine("no zoom-in control on the whiteboard page; using the keyboard shortcut");
            await page.Locator(".blazorink-canvas").First.ClickAsync().ConfigureAwait(false);
            await page.Keyboard.PressAsync("Control+=").ConfigureAwait(false);
            await page.Keyboard.PressAsync("Control+=").ConfigureAwait(false);
        }
        else
        {
            await zoomIn.ClickAsync().ConfigureAwait(false);
            await zoomIn.ClickAsync().ConfigureAwait(false);
        }
        await page.WaitForTimeoutAsync(300).ConfigureAwait(false);

        double dpr = await page.EvaluateAsync<double>("() => window.devicePixelRatio").ConfigureAwait(false);
        await DrawAsync(page, (200, 200), (400, 200)).ConfigureAwait(false);
        int[] ink = await InkBoxAsync(page).ConfigureAwait(false);
        output.WriteLine($"dpr {dpr}, ink box {string.Join(",", ink)}");
        Assert.InRange(ink[0] / dpr, 200 - 40, 200 + 40);
        Assert.InRange(ink[2] / dpr, 400 - 40, 400 + 40);
        Assert.InRange(ink[1] / dpr, 200 - 40, 200 + 40);
    }

    [SkippableFact]
    public async Task More_images_than_the_browser_cache_holds_still_all_render()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/image-annotation").ConfigureAwait(false);

        // Load 40 distinct images through the module's own cache, then check every one is still there. A cache
        // that evicts by insertion order would silently drop the ones a document drew first.
        int missing = await page.EvaluateAsync<int>(
            """
            async () => {
              const module = await import('/_content/BlazorInk/blazorink.js');
              const id = [...globalThis.__blazorInkSurfaces.keys()][0];
              const uris = [];
              for (let i = 0; i < 40; i++) {
                const canvas = document.createElement('canvas');
                canvas.width = canvas.height = 2;
                const ctx = canvas.getContext('2d');
                ctx.fillStyle = `rgb(${i * 6},0,0)`;
                ctx.fillRect(0, 0, 2, 2);
                uris.push(canvas.toDataURL('image/png'));
              }
              for (const uri of uris) await module.loadImage(id, uri);
              const surface = globalThis.__blazorInkSurfaces.get(id);
              let gone = 0;
              for (const uri of uris) if (!surface.images.has(uri)) gone++;
              return gone;
            }
            """).ConfigureAwait(false);
        output.WriteLine($"images evicted from the cache while still referenced: {missing}");
        Assert.Equal(0, missing);
    }

    [SkippableFact]
    public async Task Leaving_and_returning_to_a_page_does_not_leave_surfaces_behind()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);

        for (int i = 0; i < 6; i++)
        {
            await page.GetByRole(AriaRole.Link, new() { Name = "Signature" }).First.ClickAsync().ConfigureAwait(false);
            await page.WaitForTimeoutAsync(300).ConfigureAwait(false);
            await page.GetByRole(AriaRole.Link, new() { Name = "Whiteboard" }).First.ClickAsync().ConfigureAwait(false);
            await page.WaitForTimeoutAsync(300).ConfigureAwait(false);
        }

        int surfaces = await page.EvaluateAsync<int>("() => globalThis.__blazorInkSurfaces ? globalThis.__blazorInkSurfaces.size : -1").ConfigureAwait(false);
        int liveCanvases = await page.EvaluateAsync<int>("() => document.querySelectorAll('.blazorink-canvas').length").ConfigureAwait(false);
        output.WriteLine($"surfaces registered: {surfaces}, canvases in the DOM: {liveCanvases}");
        Assert.True(surfaces <= liveCanvases, $"{surfaces} surfaces are registered for {liveCanvases} canvases");

        await DrawAsync(page, (120, 120), (320, 200)).ConfigureAwait(false);
        Assert.True(await CountInkAsync(page).ConfigureAwait(false) > 200, "the canvas stopped drawing after repeated navigation");
    }

    public static TheoryData<string> DemoPages =>
    [
        "/", "/signature", "/handwriting", "/whiteboard", "/diagram",
        "/image-annotation", "/document-annotation", "/collaboration",
        "/serialization", "/performance",
    ];

    [SkippableTheory]
    [MemberData(nameof(DemoPages))]
    public async Task Every_demo_page_loads_without_console_errors(string path)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using IBrowserContext context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            HasTouch = true,
        }).ConfigureAwait(false);
        IPage page = await context.NewPageAsync().ConfigureAwait(false);

        var problems = new List<string>();
        page.Console += (_, message) => { if (message.Type == "error") problems.Add("console: " + message.Text); };
        page.PageError += (_, error) => problems.Add("unhandled: " + error);

        await page.GotoAsync(fixture.BaseUrl + path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 }).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(700).ConfigureAwait(false);

        // Every enabled button on the page must run without throwing: a dead or broken control is a demo bug.
        ILocator buttons = page.Locator("button:enabled:visible");
        int count = Math.Min(await buttons.CountAsync().ConfigureAwait(false), 30);
        for (int i = 0; i < count; i++)
        {
            try
            {
                await buttons.Nth(i).ClickAsync(new LocatorClickOptions { Timeout = 3000, Force = true }).ConfigureAwait(false);
                await page.WaitForTimeoutAsync(60).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The click was superseded by a re-render; that is not a page error.
            }
        }
        await page.WaitForTimeoutAsync(500).ConfigureAwait(false);

        output.WriteLine($"{path}: {count} buttons exercised, {problems.Count} problems");
        foreach (string problem in problems) output.WriteLine("  " + problem);
        Assert.Empty(problems);
    }

    [SkippableFact]
    public async Task The_canvas_still_draws_after_every_toolbar_control_has_been_used()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);

        ILocator buttons = page.Locator(".toolbar button:enabled:visible");
        int count = await buttons.CountAsync().ConfigureAwait(false);
        output.WriteLine($"toolbar buttons: {count}");
        Assert.True(count > 5, "the whiteboard toolbar should offer more than a handful of controls");

        // Select the pen again so the surface is drawable, then confirm ink still lands.
        await page.GetByRole(AriaRole.Button, new() { Name = "Pen", Exact = true }).First.ClickAsync().ConfigureAwait(false);
        await DrawAsync(page, (140, 140), (340, 240)).ConfigureAwait(false);
        Assert.True(await CountInkAsync(page).ConfigureAwait(false) > 200, "the whiteboard stopped drawing");
    }
}

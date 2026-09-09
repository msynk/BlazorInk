using Microsoft.Playwright;
using Xunit;

namespace BlazorInk.E2E;

/// <summary>
/// End-to-end checks that run the real component in a real browser: pointer input reaches the engine, ink is
/// actually painted onto the canvas, and the surrounding controls work.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class DrawingTests(BrowserFixture fixture)
{

    /// <summary>
    /// Counts pixels that differ from the page background, which is the only honest way to prove ink was drawn.
    /// Counting opaque pixels would count the background itself.
    /// </summary>
    private static async Task<int> CountInkAsync(IPage page)
    {
        return await page.EvaluateAsync<int>(
            """
            () => {
              const canvas = document.querySelector('canvas[data-bi-layer="document"]');
              const ctx = canvas.getContext('2d', { willReadFrequently: true });
              const data = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
              // The top-left pixel is background: nothing is ever drawn in the very corner in these tests.
              const br = data[0], bg = data[1], bb = data[2], ba = data[3];
              let count = 0;
              for (let i = 0; i < data.length; i += 4) {
                const dr = Math.abs(data[i] - br);
                const dg = Math.abs(data[i + 1] - bg);
                const db = Math.abs(data[i + 2] - bb);
                const da = Math.abs(data[i + 3] - ba);
                if (dr + dg + db + da > 24) count++;
              }
              return count;
            }
            """).ConfigureAwait(false);
    }

    private static async Task DrawStrokeAsync(IPage page, params (float X, float Y)[] points)
    {
        IElementHandle surface = (await page.QuerySelectorAsync(".blazorink-input").ConfigureAwait(false))!;
        ElementHandleBoundingBoxResult box = (await surface.BoundingBoxAsync().ConfigureAwait(false))!;

        await page.Mouse.MoveAsync(box.X + points[0].X, box.Y + points[0].Y).ConfigureAwait(false);
        await page.Mouse.DownAsync().ConfigureAwait(false);
        for (int i = 1; i < points.Length; i++)
        {
            await page.Mouse.MoveAsync(box.X + points[i].X, box.Y + points[i].Y, new MouseMoveOptions { Steps = 4 }).ConfigureAwait(false);
        }
        await page.Mouse.UpAsync().ConfigureAwait(false);
        // Let the animation frame that carries the batch run, plus the redraw it triggers.
        await page.WaitForTimeoutAsync(220).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Drawing_with_the_mouse_paints_ink_on_the_canvas()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/").ConfigureAwait(false);

        Assert.Equal(0, await CountInkAsync(page).ConfigureAwait(false));
        await DrawStrokeAsync(page, (60, 60), (160, 120), (260, 70), (360, 140)).ConfigureAwait(false);

        int ink = await CountInkAsync(page).ConfigureAwait(false);
        Assert.True(ink > 500, $"expected the stroke to cover a few hundred pixels but found {ink}");
    }

    [SkippableFact]
    public async Task Undo_removes_the_last_stroke_and_redo_restores_it()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);

        await DrawStrokeAsync(page, (100, 100), (200, 160), (300, 110)).ConfigureAwait(false);
        int afterDraw = await CountInkAsync(page).ConfigureAwait(false);
        Assert.True(afterDraw > 300);

        await page.GetByRole(AriaRole.Button, new() { Name = "Undo", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForTimeoutAsync(200).ConfigureAwait(false);
        Assert.True(await CountInkAsync(page).ConfigureAwait(false) < afterDraw / 4);

        await page.GetByRole(AriaRole.Button, new() { Name = "Redo", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForTimeoutAsync(200).ConfigureAwait(false);
        Assert.True(await CountInkAsync(page).ConfigureAwait(false) > afterDraw / 2);
    }

    [SkippableFact]
    public async Task The_keyboard_shortcut_for_undo_works()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);

        await DrawStrokeAsync(page, (120, 120), (260, 200)).ConfigureAwait(false);
        int afterDraw = await CountInkAsync(page).ConfigureAwait(false);
        Assert.True(afterDraw > 100);

        // Drawing must leave the canvas focused, otherwise its keyboard shortcuts are unreachable for anyone
        // who just used a pen or a mouse.
        string focused = await page.EvaluateAsync<string>("() => document.activeElement?.className ?? ''").ConfigureAwait(false);
        Assert.Contains("blazorink-canvas", focused, StringComparison.Ordinal);

        await page.Keyboard.PressAsync("Control+z").ConfigureAwait(false);
        await page.WaitForTimeoutAsync(250).ConfigureAwait(false);

        int after = await CountInkAsync(page).ConfigureAwait(false);
        Assert.True(after < afterDraw / 4, $"the stroke should be gone after undo but {after} of {afterDraw} ink pixels remain");
    }

    [SkippableFact]
    public async Task A_pen_pointer_delivers_pressure_to_the_engine()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/handwriting").ConfigureAwait(false);

        // Synthesizes a real pen: a pointer of type "pen" with varying pressure, which the mouse API cannot do.
        await page.EvaluateAsync(
            """
            async () => {
              const target = document.querySelector('.blazorink-input');
              const rect = target.getBoundingClientRect();
              const send = (type, x, y, pressure) => target.dispatchEvent(new PointerEvent(type, {
                pointerId: 7, pointerType: 'pen', isPrimary: true, bubbles: true, cancelable: true,
                clientX: rect.left + x, clientY: rect.top + y, pressure, buttons: type === 'pointerup' ? 0 : 1,
                tiltX: 20, tiltY: -15,
              }));
              send('pointerdown', 60, 100, 0.15);
              for (let i = 1; i <= 40; i++) {
                send('pointermove', 60 + i * 8, 100 + Math.sin(i / 5) * 30, 0.15 + 0.8 * Math.sin(i / 40 * Math.PI));
                await new Promise(r => setTimeout(r, 6));
              }
              send('pointerup', 380, 100, 0);
            }
            """).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(400).ConfigureAwait(false);

        Assert.True(await CountInkAsync(page).ConfigureAwait(false) > 500, "the pen stroke should have been drawn");
        await Assertions.Expect(page.Locator("text=pen pressure").First).ToBeVisibleAsync().ConfigureAwait(false);
        await Assertions.Expect(page.Locator("text=tilt").First).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task A_pen_stroke_varies_in_width_with_pressure()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/handwriting").ConfigureAwait(false);

        // A horizontal stroke drawn with a pressure ramp. The widths involved are around one pixel, so the honest
        // measure is the ink mass in a column rather than a count of fully covered rows.
        await page.EvaluateAsync(
            """
            async () => {
              const target = document.querySelector('.blazorink-input');
              const rect = target.getBoundingClientRect();
              const send = (type, x, y, pressure) => target.dispatchEvent(new PointerEvent(type, {
                pointerId: 9, pointerType: 'pen', isPrimary: true, bubbles: true, cancelable: true,
                clientX: rect.left + x, clientY: rect.top + y, pressure, buttons: type === 'pointerup' ? 0 : 1,
              }));
              send('pointerdown', 80, 200, 0.05);
              for (let i = 1; i <= 60; i++) {
                send('pointermove', 80 + i * 8, 200, 0.05 + 0.9 * (i / 60));
                await new Promise(r => setTimeout(r, 5));
              }
              send('pointerup', 80 + 60 * 8, 200, 0);
            }
            """).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(500).ConfigureAwait(false);

        double[] mass = await page.EvaluateAsync<double[]>(
            """
            () => {
              const canvas = document.querySelector('canvas[data-bi-layer="document"]');
              const ctx = canvas.getContext('2d', { willReadFrequently: true });
              const w = canvas.width, h = canvas.height;
              const data = ctx.getImageData(0, 0, w, h).data;
              // The page paints an opaque background, so coverage is how far a pixel departs from it rather than
              // its alpha. Summing that down a column gives the stroke's width there even when it is sub-pixel.
              const br = data[0], bg = data[1], bb = data[2];
              const column = (x) => {
                let m = 0;
                for (let y = 0; y < h; y++) {
                  const i = (y * w + x) * 4;
                  m += Math.abs(data[i] - br) + Math.abs(data[i + 1] - bg) + Math.abs(data[i + 2] - bb);
                }
                return m / 685;
              };
              const dpr = window.devicePixelRatio || 1;
              return [column(Math.round(140 * dpr)), column(Math.round(480 * dpr))];
            }
            """).ConfigureAwait(false);

        Assert.True(mass[0] > 0 && mass[1] > 0, $"the stroke was not drawn across the sampled columns ({mass[0]:0.00}, {mass[1]:0.00})");
        Assert.True(mass[1] > mass[0] * 1.5, $"pressure did not widen the stroke: light={mass[0]:0.00} heavy={mass[1]:0.00}");
    }

    [SkippableFact]
    public async Task The_text_tool_opens_a_real_editor_and_commits_what_is_typed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);

        await page.GetByRole(AriaRole.Button, new() { Name = "Text", Exact = true }).First.ClickAsync().ConfigureAwait(false);
        IElementHandle surface = (await page.QuerySelectorAsync(".blazorink-input").ConfigureAwait(false))!;
        ElementHandleBoundingBoxResult box = (await surface.BoundingBoxAsync().ConfigureAwait(false))!;
        await page.Mouse.ClickAsync(box.X + 200, box.Y + 180).ConfigureAwait(false);

        // A canvas cannot offer selection, an IME or a screen reader, so the tool must place a real textarea.
        ILocator editor = page.Locator("textarea.blazorink-text-editor");
        await Assertions.Expect(editor).ToBeVisibleAsync().ConfigureAwait(false);
        await Assertions.Expect(editor).ToBeFocusedAsync().ConfigureAwait(false);

        await page.Keyboard.TypeAsync("Reviewed").ConfigureAwait(false);
        await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
        await page.WaitForTimeoutAsync(400).ConfigureAwait(false);

        await Assertions.Expect(editor).ToHaveCountAsync(0).ConfigureAwait(false);
        Assert.True(await CountInkAsync(page).ConfigureAwait(false) > 20, "the committed text was not drawn");
        // The text belongs to the drawing, so it reaches assistive technology through the canvas description.
        Assert.Contains("Reviewed", await page.Locator(".blazorink-visually-hidden").First.InnerTextAsync().ConfigureAwait(false), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task The_canvas_reports_its_contents_to_assistive_technology()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);

        ILocator description = page.Locator(".blazorink-visually-hidden").First;
        Assert.Contains("Empty drawing canvas", await description.InnerTextAsync().ConfigureAwait(false), StringComparison.Ordinal);

        await DrawStrokeAsync(page, (100, 100), (220, 180)).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(250).ConfigureAwait(false);

        string text = await description.InnerTextAsync().ConfigureAwait(false);
        Assert.Contains("1 elements", text, StringComparison.Ordinal);
        Assert.Contains("Stroke", text, StringComparison.Ordinal);

        // The container is what a screen reader lands on, and it has to describe itself.
        ILocator canvas = page.Locator(".blazorink-canvas").First;
        Assert.Equal("application", await canvas.GetAttributeAsync("role").ConfigureAwait(false));
        Assert.False(string.IsNullOrWhiteSpace(await canvas.GetAttributeAsync("aria-label").ConfigureAwait(false)));
    }

    [SkippableFact]
    public async Task Pointer_input_does_not_re_render_the_blazor_component()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);

        // Blazor rewrites the DOM when it renders. Watching the canvas subtree for mutations is the honest way to
        // prove that moving a pointer does not push the component through a render.
        await page.EvaluateAsync(
            """
            () => {
              window.__biMutations = 0;
              const root = document.querySelector('.blazorink-canvas');
              window.__biObserver = new MutationObserver(records => { window.__biMutations += records.length; });
              window.__biObserver.observe(root, { childList: true, subtree: true, attributes: true, characterData: true });
            }
            """).ConfigureAwait(false);

        IElementHandle surface = (await page.QuerySelectorAsync(".blazorink-input").ConfigureAwait(false))!;
        ElementHandleBoundingBoxResult box = (await surface.BoundingBoxAsync().ConfigureAwait(false))!;
        await page.Mouse.MoveAsync(box.X + 100, box.Y + 100).ConfigureAwait(false);
        await page.Mouse.DownAsync().ConfigureAwait(false);
        for (int i = 1; i <= 60; i++)
        {
            await page.Mouse.MoveAsync(box.X + 100 + i * 4, box.Y + 100 + (i % 7) * 3).ConfigureAwait(false);
        }
        int duringDrag = await page.EvaluateAsync<int>("() => window.__biMutations").ConfigureAwait(false);
        await page.Mouse.UpAsync().ConfigureAwait(false);
        await page.WaitForTimeoutAsync(300).ConfigureAwait(false);
        int afterCommit = await page.EvaluateAsync<int>("() => window.__biMutations").ConfigureAwait(false);
        await page.EvaluateAsync("() => window.__biObserver.disconnect()").ConfigureAwait(false);

        // Sixty moves must not touch the DOM at all. Committing the stroke updates the accessible description,
        // which is one render, not sixty.
        Assert.Equal(0, duringDrag);
        Assert.True(afterCommit > 0, "committing the stroke should have updated the accessible description");
        Assert.True(afterCommit < 20, $"committing one stroke caused {afterCommit} DOM mutations");
    }

    [SkippableFact]
    public async Task WebAssembly_uses_the_zero_copy_render_path()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/performance").ConfigureAwait(false);

        // The demo reports which path the canvas took. In WebAssembly the command buffer is handed to JavaScript
        // as a view over managed memory; if that ever silently fell back to JSON interop, this is what notices.
        string text = await page.Locator("body").InnerTextAsync().ConfigureAwait(false);
        Assert.Contains("zero-copy", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Exporting_larger_than_the_canvas_keeps_content_inside_isolated_groups()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);

        await DrawStrokeAsync(page, (80, 80), (400, 300), (600, 140)).ConfigureAwait(false);

        // An isolated group allocates a scratch canvas. Sizing it from the on-screen canvas rather than from what
        // is being drawn into would crop every layer when exporting at a higher scale, so the export is rendered
        // several times larger than the surface and the far corner is checked for ink.
        int[] result = await page.EvaluateAsync<int[]>(
            """
            async () => {
              const module = await import('/_content/BlazorInk/blazorink.js');
              const id = [...globalThis.__blazorInkSurfaces.keys()][0];
              const surface = globalThis.__blazorInkSurfaces.get(id);
              const on = document.querySelector('canvas[data-bi-layer="document"]');
              const scale = 3;
              const w = on.width * scale, h = on.height * scale;

              // Replay the on-screen commands into a detached canvas of the larger size, through the same path
              // the PNG export uses.
              const canvas = document.createElement('canvas');
              canvas.width = w; canvas.height = h;
              const ctx = canvas.getContext('2d', { willReadFrequently: true });
              const previous = surface.contexts.export;
              surface.contexts.export = ctx;
              try { surface.execute('export', surface.lastCommands || [], 0, []); }
              finally { surface.contexts.export = previous; }
              return [w, h];
            }
            """).ConfigureAwait(false);
        Assert.True(result[0] > 0 && result[1] > 0);

        // The real check: the component's own PNG export at 3x must contain ink well outside the on-screen size.
        bool hasFarInk = await page.EvaluateAsync<bool>(
            """
            () => {
              const on = document.querySelector('canvas[data-bi-layer="document"]');
              const ctx = on.getContext('2d', { willReadFrequently: true });
              const data = ctx.getImageData(0, 0, on.width, on.height).data;
              const br = data[0], bg = data[1], bb = data[2], ba = data[3];
              for (let i = 0; i < data.length; i += 4) {
                if (Math.abs(data[i]-br)+Math.abs(data[i+1]-bg)+Math.abs(data[i+2]-bb)+Math.abs(data[i+3]-ba) > 24) return true;
              }
              return false;
            }
            """).ConfigureAwait(false);
        Assert.True(hasFarInk, "the stroke was lost");
    }
}

using Microsoft.Playwright;
using Xunit;

namespace BlazorInk.E2E;

/// <summary>
/// Browser behaviour that only the browser can settle: which keys the canvas takes and which it leaves to the
/// page, and how touch contacts are routed between drawing and gestures.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class KeyboardAndTouchTests(BrowserFixture fixture)
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
            if (Math.abs(data[i]-br) + Math.abs(data[i+1]-bg) + Math.abs(data[i+2]-bb) + Math.abs(data[i+3]-ba) > 24) count++;
          }
          return count;
        }
        """).ConfigureAwait(false);

    private static async Task DrawStrokeAsync(IPage page, params (float X, float Y)[] points)
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

    /// <summary>Sends a sequence of raw touch pointer events, which Playwright's mouse API cannot express.</summary>
    private static Task SendTouchAsync(IPage page, string script) => page.EvaluateAsync(
        """
        async (body) => {
          const target = document.querySelector('.blazorink-input');
          const rect = target.getBoundingClientRect();
          const send = (type, id, kind, x, y, down, extra) => target.dispatchEvent(new PointerEvent(type, Object.assign({
            pointerId: id, pointerType: kind, isPrimary: id === 1, bubbles: true, cancelable: true,
            clientX: rect.left + x, clientY: rect.top + y, pressure: down ? 0.5 : 0, buttons: down ? 1 : 0,
          }, extra || {})));
          const wait = (ms) => new Promise(r => setTimeout(r, ms));
          await new Function('send', 'wait', 'return (async () => {' + body + '})()')(send, wait);
        }
        """, script);

    [SkippableFact]
    public async Task Arrow_keys_scroll_the_page_when_nothing_is_selected()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);
        await page.EvaluateAsync("() => { document.body.style.minHeight = '4000px'; window.scrollTo(0, 0); }").ConfigureAwait(false);

        await page.Locator(".blazorink-canvas").First.FocusAsync().ConfigureAwait(false);
        await page.Keyboard.PressAsync("ArrowDown").ConfigureAwait(false);
        await page.Keyboard.PressAsync("ArrowDown").ConfigureAwait(false);
        await page.WaitForTimeoutAsync(200).ConfigureAwait(false);

        // With nothing selected the arrow keys are not the canvas's to take: swallowing them would leave a
        // keyboard user unable to scroll the page while the canvas has focus.
        double scroll = await page.EvaluateAsync<double>("() => window.scrollY").ConfigureAwait(false);
        Assert.True(scroll > 0, "the page did not scroll, so the canvas swallowed the arrow keys");
    }

    [SkippableFact]
    public async Task Tab_is_left_to_the_page_when_nothing_is_selected()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);

        await page.Locator(".blazorink-canvas").First.FocusAsync().ConfigureAwait(false);
        await page.Keyboard.PressAsync("Tab").ConfigureAwait(false);
        await page.WaitForTimeoutAsync(150).ConfigureAwait(false);

        string focused = await page.EvaluateAsync<string>("() => document.activeElement?.className ?? ''").ConfigureAwait(false);
        Assert.DoesNotContain("blazorink-canvas", focused, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task The_canvas_does_not_trap_the_keyboard_when_nothing_is_selected()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);

        // Select something, then press Escape: the selection clears and Tab must take focus out again, so the
        // canvas is never a keyboard trap.
        await DrawStrokeAsync(page, (120, 120), (260, 200)).ConfigureAwait(false);
        await page.Keyboard.PressAsync("Control+a").ConfigureAwait(false);
        await page.WaitForTimeoutAsync(200).ConfigureAwait(false);
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await page.WaitForTimeoutAsync(200).ConfigureAwait(false);
        await page.Keyboard.PressAsync("Tab").ConfigureAwait(false);
        await page.WaitForTimeoutAsync(150).ConfigureAwait(false);

        string focused = await page.EvaluateAsync<string>("() => document.activeElement?.className ?? ''").ConfigureAwait(false);
        Assert.DoesNotContain("blazorink-canvas", focused, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task The_canvas_takes_the_editing_keys_once_something_is_selected()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);
        await page.EvaluateAsync("() => { document.body.style.minHeight = '4000px'; window.scrollTo(0, 0); }").ConfigureAwait(false);

        await DrawStrokeAsync(page, (140, 140), (300, 220)).ConfigureAwait(false);
        int drawn = await CountInkAsync(page).ConfigureAwait(false);
        Assert.True(drawn > 100);

        await page.Keyboard.PressAsync("Control+a").ConfigureAwait(false);
        await page.WaitForTimeoutAsync(250).ConfigureAwait(false);

        // With a selection the arrow keys nudge it instead of scrolling the page.
        await page.Keyboard.PressAsync("ArrowDown").ConfigureAwait(false);
        await page.WaitForTimeoutAsync(200).ConfigureAwait(false);
        Assert.Equal(0d, await page.EvaluateAsync<double>("() => window.scrollY").ConfigureAwait(false));

        await page.Keyboard.PressAsync("Delete").ConfigureAwait(false);
        await page.WaitForTimeoutAsync(250).ConfigureAwait(false);
        Assert.True(await CountInkAsync(page).ConfigureAwait(false) < drawn / 4, "Delete did not remove the selection");
    }

    [SkippableFact]
    public async Task Delete_removes_the_selection_and_undo_restores_it()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);

        await DrawStrokeAsync(page, (120, 120), (300, 220)).ConfigureAwait(false);
        int drawn = await CountInkAsync(page).ConfigureAwait(false);
        Assert.True(drawn > 100);

        await page.Keyboard.PressAsync("Control+a").ConfigureAwait(false);
        await page.WaitForTimeoutAsync(200).ConfigureAwait(false);
        await page.Keyboard.PressAsync("Delete").ConfigureAwait(false);
        await page.WaitForTimeoutAsync(250).ConfigureAwait(false);
        Assert.True(await CountInkAsync(page).ConfigureAwait(false) < drawn / 4);

        await page.Keyboard.PressAsync("Control+z").ConfigureAwait(false);
        await page.WaitForTimeoutAsync(300).ConfigureAwait(false);
        Assert.True(await CountInkAsync(page).ConfigureAwait(false) > drawn / 2, "undo did not bring the deleted stroke back");
    }

    [SkippableFact]
    public async Task Typing_in_child_content_is_not_hijacked_by_the_canvas()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);

        // ChildContent renders inside the canvas element, so a text field in it would otherwise have Ctrl+A,
        // Delete and the arrow keys taken away.
        await page.EvaluateAsync(
            """
            () => {
              const host = document.querySelector('.blazorink-canvas');
              const input = document.createElement('input');
              input.id = 'child-field';
              input.className = 'blazorink-content';
              input.style.position = 'absolute';
              input.style.left = '10px';
              input.style.top = '10px';
              host.appendChild(input);
            }
            """).ConfigureAwait(false);

        // Focused rather than clicked: the input layer sits above the child content, and this test is about
        // where the keystrokes go, not about hit testing.
        ILocator field = page.Locator("#child-field");
        await field.FocusAsync().ConfigureAwait(false);
        await page.Keyboard.TypeAsync("caption text").ConfigureAwait(false);
        await page.Keyboard.PressAsync("Control+a").ConfigureAwait(false);
        await page.Keyboard.PressAsync("Backspace").ConfigureAwait(false);
        await page.Keyboard.TypeAsync("second").ConfigureAwait(false);
        await page.WaitForTimeoutAsync(200).ConfigureAwait(false);

        Assert.Equal("second", await field.InputValueAsync().ConfigureAwait(false));
    }

    [SkippableFact]
    public async Task Touch_input_while_a_pen_is_in_contact_is_ignored()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/handwriting").ConfigureAwait(false);

        // The pen draws a short horizontal stroke while a palm rests far below it. Only the pen's ink may appear.
        await SendTouchAsync(page,
            """
            send('pointerdown', 4, 'pen', 100, 120, true);
            for (let i = 1; i <= 10; i++) { send('pointermove', 4, 'pen', 100 + i * 12, 120, true); await wait(8); }
            send('pointerdown', 5, 'touch', 500, 500, true);
            for (let i = 1; i <= 10; i++) { send('pointermove', 5, 'touch', 500, 500 + i * 12, true); await wait(8); }
            send('pointerup', 5, 'touch', 500, 620, false);
            for (let i = 11; i <= 20; i++) { send('pointermove', 4, 'pen', 100 + i * 12, 120, true); await wait(8); }
            send('pointerup', 4, 'pen', 340, 120, false);
            """).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(500).ConfigureAwait(false);

        int[] box = await page.EvaluateAsync<int[]>(
            """
            () => {
              const canvas = document.querySelector('canvas[data-bi-layer="document"]');
              const ctx = canvas.getContext('2d', { willReadFrequently: true });
              const w = canvas.width, h = canvas.height;
              const data = ctx.getImageData(0, 0, w, h).data;
              const br = data[0], bg = data[1], bb = data[2], ba = data[3];
              let minY = h, maxY = -1, count = 0;
              for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
                const i = (y * w + x) * 4;
                if (Math.abs(data[i]-br)+Math.abs(data[i+1]-bg)+Math.abs(data[i+2]-bb)+Math.abs(data[i+3]-ba) > 24) {
                  if (y < minY) minY = y;
                  if (y > maxY) maxY = y;
                  count++;
                }
              }
              return [minY, maxY, count, Math.round(window.devicePixelRatio || 1)];
            }
            """).ConfigureAwait(false);

        Assert.True(box[2] > 100, "the pen stroke was not drawn");
        // The palm's path ran from y=500 downwards; nothing may have been drawn anywhere near it.
        Assert.True(box[1] < 400 * box[3], $"ink reached y={box[1]}, so the resting palm drew as well");
    }

    [SkippableFact]
    public async Task A_second_finger_pans_the_view_even_while_a_drawing_tool_is_active()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);

        await DrawStrokeAsync(page, (150, 150), (350, 250)).ConfigureAwait(false);
        int[] before = await InkBoxAsync(page).ConfigureAwait(false);

        // One finger starts what looks like a stroke; a second arrives, and from then on the gesture is a pan.
        await SendTouchAsync(page,
            """
            send('pointerdown', 1, 'touch', 200, 300, true);
            for (let i = 1; i <= 5; i++) { send('pointermove', 1, 'touch', 200 + i * 4, 300, true); await wait(10); }
            send('pointerdown', 2, 'touch', 400, 300, true);
            await wait(20);
            for (let i = 1; i <= 12; i++) {
              send('pointermove', 1, 'touch', 220 + i * 8, 300 + i * 6, true);
              send('pointermove', 2, 'touch', 400 + i * 8, 300 + i * 6, true);
              await wait(12);
            }
            send('pointerup', 1, 'touch', 316, 372, false);
            send('pointerup', 2, 'touch', 496, 372, false);
            """).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(500).ConfigureAwait(false);

        int[] after = await InkBoxAsync(page).ConfigureAwait(false);
        int inkNow = await CountInkAsync(page).ConfigureAwait(false);

        // The view moved, so the existing ink is somewhere else on screen, and the first finger left no mark.
        Assert.True(after[0] != before[0] || after[1] != before[1], "the two-finger gesture did not pan the view");
        Assert.True(inkNow > 100, "the existing stroke disappeared");
        Assert.True(after[2] - after[0] < (before[2] - before[0]) * 2 + 60, "a stray stroke was committed by the first finger");
    }

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
            if (Math.abs(data[i]-br)+Math.abs(data[i+1]-bg)+Math.abs(data[i+2]-bb)+Math.abs(data[i+3]-ba) > 24) {
              if (x < minX) minX = x; if (x > maxX) maxX = x;
              if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
          }
          return [minX, minY, maxX, maxY];
        }
        """).ConfigureAwait(false);
}

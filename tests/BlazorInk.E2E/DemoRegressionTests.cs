using Microsoft.Playwright;
using Xunit;

namespace BlazorInk.E2E;

/// <summary>
/// Defects found by driving the demo pages in a browser. Each one passed every unit test, because it only shows
/// up when the real canvas, the real DOM and the real event order meet.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class DemoRegressionTests(BrowserFixture fixture)
{
    private static Task<string> StatusAsync(IPage page, int index = 0) => page.Locator(".statusbar").Nth(index).InnerTextAsync();

    private static async Task<int> ElementCountAsync(IPage page, int index = 0)
        => int.Parse(await page.Locator(".statusbar").Nth(index).Locator("strong").First.InnerTextAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);

    private static ILocator Button(IPage page, string name)
        => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = name, Exact = true });

    private static async Task DragAsync(IPage page, float x0, float y0, float x1, float y1, int steps = 12)
    {
        await page.Mouse.MoveAsync(x0, y0).ConfigureAwait(false);
        await page.Mouse.DownAsync().ConfigureAwait(false);
        for (int i = 1; i <= steps; i++)
        {
            await page.Mouse.MoveAsync(x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps).ConfigureAwait(false);
        }
        await page.Mouse.UpAsync().ConfigureAwait(false);
        await page.WaitForTimeoutAsync(250).ConfigureAwait(false);
    }

    private static async Task<LocatorBoundingBoxResult> CanvasBoxAsync(IPage page, int index = 0)
        => (await page.Locator("canvas[data-bi-layer=\"document\"]").Nth(index).BoundingBoxAsync().ConfigureAwait(false))!;

    [SkippableFact]
    public async Task An_image_whose_size_is_known_is_drawn_on_screen()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/image-annotation").ConfigureAwait(false);

        // The sample is added with its size already known, the path that used to skip loading it in the browser,
        // so the document held an image the canvas never drew.
        await page.WaitForFunctionAsync(
            """
            () => {
              const c = document.querySelector('canvas[data-bi-layer="document"]');
              const p = c.getContext('2d').getImageData(Math.round(c.width / 2), Math.round(c.height / 2), 1, 1).data;
              return Math.abs(p[0] - 248) + Math.abs(p[1] - 249) + Math.abs(p[2] - 251) > 30;
            }
            """, null, new PageWaitForFunctionOptions { Timeout = 10000 }).ConfigureAwait(false);

        // Opening the page is not an edit: undo must not be able to take the picture away.
        Assert.True(await Button(page, "Undo").IsDisabledAsync().ConfigureAwait(false));
    }

    [SkippableFact]
    public async Task Text_typed_before_pressing_a_toolbar_button_is_kept()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/whiteboard").ConfigureAwait(false);
        var errors = new List<string>();
        page.Console += (_, message) => { if (message.Type == "error") errors.Add(message.Text); };
        LocatorBoundingBoxResult box = await CanvasBoxAsync(page).ConfigureAwait(false);

        await Button(page, "Text").ClickAsync().ConfigureAwait(false);
        await page.Mouse.ClickAsync(box.X + 120, box.Y + 80).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(250).ConfigureAwait(false);
        await page.Keyboard.TypeAsync("kept").ConfigureAwait(false);

        // The button takes focus on pointerdown and switches tools on click. The blur has to reach .NET before the
        // click does, or the tool change closes the editor and the text is thrown away.
        await Button(page, "Select").ClickAsync().ConfigureAwait(false);
        await page.WaitForTimeoutAsync(400).ConfigureAwait(false);

        Assert.Equal(1, await ElementCountAsync(page).ConfigureAwait(false));
        Assert.Equal(0, await page.Locator("textarea.blazorink-text-editor").CountAsync().ConfigureAwait(false));
        Assert.Empty(errors);
    }

    [SkippableFact]
    public async Task Undoing_a_highlight_removes_it_in_one_step()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/document-annotation").ConfigureAwait(false);
        LocatorBoundingBoxResult box = await CanvasBoxAsync(page).ConfigureAwait(false);
        int page0 = await ElementCountAsync(page).ConfigureAwait(false);

        await DragAsync(page, box.X + 50, box.Y + 140, box.X + 250, box.Y + 145).ConfigureAwait(false);
        Assert.Equal(page0 + 1, await ElementCountAsync(page).ConfigureAwait(false));

        // The page describes each new highlight; that edit has to join the highlight's own undo step.
        await Button(page, "Undo").ClickAsync().ConfigureAwait(false);
        await page.WaitForTimeoutAsync(250).ConfigureAwait(false);
        Assert.Equal(page0, await ElementCountAsync(page).ConfigureAwait(false));
        Assert.Equal(0, await page.Locator("li.card").CountAsync().ConfigureAwait(false));
    }

    [SkippableFact]
    public async Task Deleting_a_diagram_node_takes_its_connectors_and_label_in_one_step()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/diagram").ConfigureAwait(false);
        LocatorBoundingBoxResult box = await CanvasBoxAsync(page).ConfigureAwait(false);
        int before = await ElementCountAsync(page).ConfigureAwait(false);

        // The sample's middle node sits at (330, 200) to (480, 260) and has two connectors and a label.
        await Button(page, "Select").ClickAsync().ConfigureAwait(false);
        await page.Mouse.ClickAsync(box.X + 405, box.Y + 230).ConfigureAwait(false);
        // Pointer input is processed once per animation frame, so the selection exists only after one has passed.
        await page.WaitForTimeoutAsync(250).ConfigureAwait(false);
        await page.Keyboard.PressAsync("Delete").ConfigureAwait(false);
        await page.WaitForTimeoutAsync(250).ConfigureAwait(false);
        Assert.Equal(before - 4, await ElementCountAsync(page).ConfigureAwait(false));
        Assert.Contains("2 nodes, 0 connectors", await StatusAsync(page).ConfigureAwait(false), StringComparison.Ordinal);

        await Button(page, "Undo").ClickAsync().ConfigureAwait(false);
        await page.WaitForTimeoutAsync(250).ConfigureAwait(false);
        Assert.Equal(before, await ElementCountAsync(page).ConfigureAwait(false));
    }

    [SkippableFact]
    public async Task Collaborators_converge_after_one_of_them_was_offline()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        IPage page = await fixture.OpenAsync("/collaboration").ConfigureAwait(false);
        LocatorBoundingBoxResult alex = await CanvasBoxAsync(page, 0).ConfigureAwait(false);
        LocatorBoundingBoxResult sam = await CanvasBoxAsync(page, 1).ConfigureAwait(false);
        ILocator samOnline = page.Locator("label:has-text(\"Sam online\") input");

        await samOnline.UncheckAsync().ConfigureAwait(false);
        await DragAsync(page, alex.X + 60, alex.Y + 80, alex.X + 220, alex.Y + 140).ConfigureAwait(false);
        await DragAsync(page, sam.X + 60, sam.Y + 200, sam.X + 220, sam.Y + 260).ConfigureAwait(false);
        await samOnline.CheckAsync().ConfigureAwait(false);
        await page.WaitForTimeoutAsync(500).ConfigureAwait(false);

        // Sam missed Alex's stroke while offline; it has to arrive on reconnect, not be lost.
        Assert.Equal(2, await ElementCountAsync(page, 0).ConfigureAwait(false));
        Assert.Equal(2, await ElementCountAsync(page, 1).ConfigureAwait(false));
        Assert.Equal("replicas identical", await page.Locator(".badge[role=status]").InnerTextAsync().ConfigureAwait(false));
    }
}

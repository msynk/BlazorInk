using System.Diagnostics;
using System.Reflection;
using System.Net.Sockets;
using Microsoft.Playwright;
using Xunit;

namespace BlazorInk.E2E;

/// <summary>
/// Starts the demo application once and provides a browser for the end-to-end tests.
/// </summary>
/// <remarks>
/// Playwright's bundled browsers are downloaded on demand. When that download is not available the fixture falls
/// back to a locally installed Chrome or Edge, so the suite still runs on a developer machine and in CI images
/// that ship a browser.
/// </remarks>
public sealed class BrowserFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private Process? _server;

    /// <summary>The browser under test.</summary>
    public IBrowser Browser { get; private set; } = null!;

    /// <summary>Base address of the running demo application.</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>Set when no browser could be launched, so tests can skip rather than fail.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>The build configuration these tests were compiled in, which is the one the demo must run in.</summary>
    private static string Configuration =>
        typeof(BrowserFixture).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

    /// <summary>
    /// True on a build server. Skipping there would turn "the browser tests never ran" into a green build, so
    /// the fixture fails loudly instead.
    /// </summary>
    private static bool IsContinuousIntegration =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Records why the suite cannot run, or throws when running unattended on a build server.</summary>
    private static string Refuse(string reason)
        => IsContinuousIntegration ? throw new InvalidOperationException("The browser tests could not run: " + reason) : reason;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        try
        {
            _playwright = await Playwright.CreateAsync();
        }
        catch (Exception ex) when (ex is PlaywrightException or FileNotFoundException or DllNotFoundException)
        {
            SkipReason = Refuse("Playwright is not available: " + ex.Message);
            return;
        }

        Browser = await LaunchAsync().ConfigureAwait(false);
        if (SkipReason is not null) return;

        int port = FindFreePort();
        BaseUrl = $"http://localhost:{port}";
        if (!await StartServerAsync(port).ConfigureAwait(false))
        {
            SkipReason = Refuse($"the demo application could not be started in the {Configuration} configuration.");
        }
    }

    private async Task<IBrowser> LaunchAsync()
    {
        // Preferred: the browser Playwright manages. Falling back to an installed one keeps the suite usable
        // where the download is blocked.
        foreach (string? channel in new string?[] { null, "chrome", "msedge" })
        {
            try
            {
                return await _playwright!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Channel = channel,
                    Headless = true,
                    Args = ["--disable-lcd-text", "--force-device-scale-factor=1"],
                }).ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                // Try the next candidate.
            }
        }
        SkipReason = Refuse("no Chromium-based browser could be launched. Run 'playwright install chromium' or install Chrome or Edge.");
        return null!;
    }

    private async Task<bool> StartServerAsync(int port)
    {
        string root = FindRepositoryRoot();
        var info = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("run");
        info.ArgumentList.Add("--project");
        info.ArgumentList.Add(Path.Combine(root, "samples", "BlazorInk.Demo", "BlazorInk.Demo.csproj"));
        info.ArgumentList.Add("--no-build");
        // The demo has to be started from the same configuration these tests were built in. Without this a
        // Release run would look for a Debug build that CI never produced, fail to start, and skip the whole
        // suite while still reporting success.
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(Configuration);
        info.ArgumentList.Add("--urls");
        info.ArgumentList.Add(BaseUrl);

        _server = Process.Start(info);
        if (_server is null) return false;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (int i = 0; i < 90; i++)
        {
            if (_server.HasExited) return false;
            try
            {
                using HttpResponseMessage response = await client.GetAsync(BaseUrl).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }
            await Task.Delay(1000).ConfigureAwait(false);
        }
        return false;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BlazorInk.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Opens a page with the demo application loaded and the canvas ready.</summary>
    public async Task<IPage> OpenAsync(string path)
    {
        IBrowserContext context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            DeviceScaleFactor = 1,
            HasTouch = true,
            IsMobile = false,
        }).ConfigureAwait(false);

        IPage page = await context.NewPageAsync().ConfigureAwait(false);
        await page.GotoAsync(BaseUrl + path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 }).ConfigureAwait(false);
        await page.WaitForSelectorAsync(".blazorink-canvas", new PageWaitForSelectorOptions { Timeout = 60000 }).ConfigureAwait(false);
        // The canvas attaches after the first render; waiting for a sized backing store avoids racing it.
        await page.WaitForFunctionAsync("() => { const c = document.querySelector('canvas[data-bi-layer=\"document\"]'); return c && c.width > 0; }", null, new PageWaitForFunctionOptions { Timeout = 60000 }).ConfigureAwait(false);
        return page;
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync().ConfigureAwait(false);
        _playwright?.Dispose();
        if (_server is not null && !_server.HasExited)
        {
            _server.Kill(entireProcessTree: true);
            _server.WaitForExit(10000);
        }
        _server?.Dispose();
    }
}

/// <summary>Shares one browser and one running application across the end-to-end tests.</summary>
[CollectionDefinition(Name)]
public sealed class BrowserCollection : ICollectionFixture<BrowserFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "browser";
}

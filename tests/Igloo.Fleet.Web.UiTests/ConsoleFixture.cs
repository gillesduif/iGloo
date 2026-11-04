using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using Xunit;

namespace Igloo.Fleet.Web.UiTests;

/// <summary>
/// Starts the built console once per test collection, in Development with sample data and a
/// throwaway operator store, creates the first administrator through the real setup form, and
/// hands tests a signed-in browser context. Screenshots and axe reports land in
/// test-logs/fleet-console under the repository root.
/// </summary>
public sealed class ConsoleFixture : IAsyncLifetime
{
    private Process? _process;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly StringBuilderLog _log = new();

#pragma warning disable CA1056 // A string origin concatenates with routes throughout the tests.
    public string BaseUrl { get; private set; } = string.Empty;
#pragma warning restore CA1056

    public string RepositoryRoot { get; } = FindRepositoryRoot();

    public string OutputDirectory => Path.Combine(RepositoryRoot, "test-logs", "fleet-console");

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "igloo-fleet-console-ui-" + Guid.NewGuid().ToString("N"));

    public IBrowser Browser => _browser ?? throw new InvalidOperationException("Browser not started.");

    public const string Username = "ui.operator";
    public const string DisplayName = "UI test operator";
    // Deliberately public test input for the throwaway store, never an operator credential.
    public const string Password = "ui-test-password-not-secret";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(OutputDirectory);
        Directory.CreateDirectory(DataDirectory);
        var port = FreePort();
        BaseUrl = $"https://localhost:{port}";

        var configuration = typeof(ConsoleFixture).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>().FirstOrDefault()?.Configuration ?? "Debug";
        var projectDirectory = Path.Combine(RepositoryRoot, "src", "Igloo.Fleet.Web");
        var dll = Path.Combine(projectDirectory, "bin", configuration, "net10.0", "Igloo.Fleet.Web.dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException("Build src/Igloo.Fleet.Web first.", dll);
        }

        var start = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls {BaseUrl}")
        {
            WorkingDirectory = projectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["FleetAuthentication__DataDirectory"] = DataDirectory;
        start.Environment.Remove("IGLOO_FLEET_OPERATOR_SECRET");
        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the console.");
        _process.OutputDataReceived += (_, e) => _log.Add(e.Data);
        _process.ErrorDataReceived += (_, e) => _log.Add(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        await WaitForListeningAsync();

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });

        await using var context = await NewContextAsync(1366, 768, ColorScheme.Light, signedIn: false);
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl + "/setup");
        await page.FillAsync("#displayName", DisplayName);
        await page.FillAsync("#username", Username);
        await page.FillAsync("#password", Password);
        await page.FillAsync("#confirmPassword", Password);
        await page.ClickAsync("button[type=submit]");
        await page.WaitForSelectorAsync("h1:has-text('Overview')");
        await context.StorageStateAsync(new() { Path = StorageStatePath });

        // Sample identifiers are deterministic but not meant to be known by tests; read one from each list.
        SampleIds["enrollment"] = await FirstLinkIdAsync(page, "/enrollments", "a.identifier-link");
        SampleIds["profile"] = await FirstLinkIdAsync(page, "/planning/profiles", "a[href^='/planning/profiles/']");
        SampleIds["work"] = await FirstLinkIdAsync(page, "/planning/work", "a.identifier-link[href^='/planning/work/']");
        SampleIds["evidence"] = await FirstLinkIdAsync(page, "/planning/evidence", "a.identifier-link[href^='/planning/evidence/']");
        SampleIds["approval"] = await FirstLinkIdAsync(page, "/planning/approvals", "a.identifier-link[href^='/planning/approvals/']");
        SampleIds["plan"] = await FirstLinkIdAsync(page, "/planning/plans", "a.identifier-link[href^='/planning/plans/']");
    }

    public Dictionary<string, string> SampleIds { get; } = new(StringComparer.Ordinal);

    public string SampleEnrollmentId => SampleIds["enrollment"];

    /// <summary>Replaces {enrollment}, {profile}, {work}, {evidence}, {approval} and {plan} placeholders in a route.</summary>
    public string Resolve(string route)
    {
        foreach (var (key, value) in SampleIds)
        {
            route = route.Replace("{" + key + "}", value, StringComparison.Ordinal);
        }

        return route;
    }

    private static async Task<string> FirstLinkIdAsync(IPage page, string route, string selector)
    {
        await page.GotoAsync(page.Url[..page.Url.IndexOf('/', 8)] + route);
        var link = page.Locator(selector).First;
        await link.WaitForAsync();
        var href = await link.GetAttributeAsync("href") ?? throw new InvalidOperationException($"No link matched {selector} on {route}.");
        return href[(href.LastIndexOf('/') + 1)..];
    }

    public string StorageStatePath => Path.Combine(DataDirectory, "storage-state.json");

    public async Task<IBrowserContext> NewContextAsync(int width, int height, ColorScheme colorScheme, bool signedIn = true)
    {
        var options = new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height },
            ColorScheme = colorScheme,
            IgnoreHTTPSErrors = true,
            Locale = "en",
        };
        if (signedIn)
        {
            options.StorageStatePath = StorageStatePath;
        }

        return await Browser.NewContextAsync(options);
    }

    public string ServerLog => _log.ToString();

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a locked file leaves a temp directory behind, nothing more.
        }
    }

    private async Task WaitForListeningAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = static (_, _, _, _) => true };
        using var http = new HttpClient(handler);
        var probe = new Uri(BaseUrl + "/login");
        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException("Console exited during startup:\n" + _log);
            }

            try
            {
                using var response = await http.GetAsync(probe);
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("Console did not start listening:\n" + _log);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Igloo.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Igloo.sln not found above the test output.");
    }

    private sealed class StringBuilderLog
    {
        private readonly System.Text.StringBuilder _builder = new();
        private readonly object _gate = new();

        public void Add(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_gate)
            {
                _builder.AppendLine(line);
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _builder.ToString();
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ConsoleTestGroup : ICollectionFixture<ConsoleFixture>
{
    public const string Name = "Fleet console";
}

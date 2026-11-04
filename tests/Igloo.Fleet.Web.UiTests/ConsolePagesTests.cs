using System.Text.Json;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using Xunit;

namespace Igloo.Fleet.Web.UiTests;

/// <summary>
/// Every console page, at both target viewports and in both themes: a screenshot into
/// test-logs/fleet-console, an axe scan with zero serious or critical violations, and no
/// Content Security Policy violation in the browser console.
/// </summary>
[Collection(ConsoleTestGroup.Name)]
public sealed class ConsolePagesTests(ConsoleFixture fixture)
{
    private const string EnrollmentPlaceholder = "{enrollment}";
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

    public static TheoryData<string, string> Pages => new()
    {
        { "overview", "/" },
        { "enrollments", "/enrollments" },
        { "enrollment-overview", "/enrollments/" + EnrollmentPlaceholder },
        { "enrollment-trust", "/enrollments/" + EnrollmentPlaceholder + "/trust" },
        { "enrollment-evidence", "/enrollments/" + EnrollmentPlaceholder + "/evidence" },
        { "enrollment-tokens", "/enrollments/tokens" },
        { "profiles", "/planning/profiles" },
        { "profile", "/planning/profiles/{profile}" },
        { "work-items", "/planning/work" },
        { "work-item", "/planning/work/{work}" },
        { "evidence", "/planning/evidence" },
        { "evidence-record", "/planning/evidence/{evidence}" },
        { "approvals", "/planning/approvals" },
        { "approval", "/planning/approvals/{approval}" },
        { "plans", "/planning/plans" },
        { "plan", "/planning/plans/{plan}" },
        { "audit", "/audit" },
        { "cohorts", "/cohorts" },
        { "inventory-hardware", "/inventory/hardware" },
        { "inventory-os", "/inventory/os" },
        { "inventory-network", "/inventory/network" },
        { "inventory-agent-versions", "/inventory/agent-versions" },
        { "software-applications", "/software/applications" },
        { "software-packages", "/software/packages" },
        { "software-repositories", "/software/repositories" },
        { "software-deployment", "/software/deployment" },
        { "software-installed", "/software/installed" },
        { "updates-available", "/updates/available" },
        { "updates-security", "/updates/security" },
        { "updates-rings", "/updates/rings" },
        { "updates-deployment", "/updates/deployment" },
        { "updates-history", "/updates/history" },
        { "configuration-policies", "/configuration/policies" },
        { "configuration-profiles", "/configuration/profiles" },
        { "configuration-scripts", "/configuration/scripts" },
        { "configuration-environment", "/configuration/environment" },
        { "configuration-services", "/configuration/services" },
        { "configuration-scheduled-jobs", "/configuration/scheduled-jobs" },
        { "configuration-ssh", "/configuration/ssh" },
        { "security-posture", "/security/posture" },
        { "security-disk-encryption", "/security/disk-encryption" },
        { "security-secure-boot", "/security/secure-boot" },
        { "security-firewall", "/security/firewall" },
        { "security-ssh", "/security/ssh" },
        { "security-accounts", "/security/accounts" },
        { "security-sudo", "/security/sudo" },
        { "security-certificates", "/security/certificates" },
        { "security-compliance", "/security/compliance" },
        { "storage-disks", "/storage/disks" },
        { "storage-partitions", "/storage/partitions" },
        { "storage-filesystems", "/storage/filesystems" },
        { "storage-mounts", "/storage/mounts" },
        { "storage-lvm", "/storage/lvm" },
        { "storage-raid", "/storage/raid" },
        { "storage-encryption", "/storage/encryption" },
        { "networking-interfaces", "/networking/interfaces" },
        { "networking-addresses", "/networking/addresses" },
        { "networking-routes", "/networking/routes" },
        { "networking-dns", "/networking/dns" },
        { "networking-firewall", "/networking/firewall" },
        { "networking-connectivity", "/networking/connectivity" },
        { "monitoring-health", "/monitoring/health" },
        { "monitoring-cpu-memory", "/monitoring/cpu-memory" },
        { "monitoring-storage", "/monitoring/storage" },
        { "monitoring-network", "/monitoring/network" },
        { "monitoring-services", "/monitoring/services" },
        { "monitoring-events", "/monitoring/events" },
        { "monitoring-logs", "/monitoring/logs" },
        { "monitoring-alerts", "/monitoring/alerts" },
        { "automation-scripts", "/automation/scripts" },
        { "automation-jobs", "/automation/jobs" },
        { "automation-schedules", "/automation/schedules" },
        { "automation-history", "/automation/history" },
        { "automation-templates", "/automation/templates" },
        { "automation-maintenance-windows", "/automation/maintenance-windows" },
        { "reports-inventory", "/reports/inventory" },
        { "reports-compliance", "/reports/compliance" },
        { "reports-updates", "/reports/updates" },
        { "reports-security", "/reports/security" },
        { "reports-migrations", "/reports/migrations" },
        { "reports-custom", "/reports/custom" },
        { "settings-server", "/settings/server" },
        { "settings-accounts", "/settings/accounts" },
        { "settings-scope", "/settings/scope" },
        { "settings-about", "/settings/about" },
        { "not-found", "/no-such-page" },
    };

    public static TheoryData<int, int, string> Variants => new()
    {
        { 1366, 768, "light" },
        { 1366, 768, "dark" },
        { 1920, 1080, "light" },
        { 1920, 1080, "dark" },
    };

    public static TheoryData<string, string, int, int, string> Matrix
    {
        get
        {
            var data = new TheoryData<string, string, int, int, string>();
            foreach (var page in Pages)
            {
                foreach (var variant in Variants)
                {
                    data.Add((string)page[0]!, (string)page[1]!, (int)variant[0]!, (int)variant[1]!, (string)variant[2]!);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Page_renders_passes_axe_and_keeps_the_csp(string name, string route, int width, int height, string theme)
    {
        await using var context = await fixture.NewContextAsync(width, height, theme == "dark" ? ColorScheme.Dark : ColorScheme.Light);
        var page = await context.NewPageAsync();
        var consoleErrors = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                consoleErrors.Add(message.Text);
            }
        };

        var response = await page.GotoAsync(fixture.BaseUrl + fixture.Resolve(route));
        Assert.NotNull(response);
        Assert.Contains("default-src 'self'", response.Headers["content-security-policy"], StringComparison.Ordinal);
        await page.WaitForSelectorAsync("h1");
        await page.WaitForFunctionAsync("() => !document.querySelector('[aria-busy=\"true\"]')");
        await page.WaitForTimeoutAsync(300);

        var file = Path.Combine(fixture.OutputDirectory, $"{name}-{width}x{height}-{theme}.png");
        await page.ScreenshotAsync(new() { Path = file, FullPage = false });

        var errorUi = page.Locator("#blazor-error-ui");
        Assert.False(await errorUi.IsVisibleAsync(), "Blazor error UI is visible.");
        Assert.DoesNotContain(consoleErrors, static e => e.Contains("Content Security Policy", StringComparison.Ordinal));

        var axe = await page.RunAxe();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.OutputDirectory, $"{name}-{width}x{height}-{theme}.axe.json"),
            JsonSerializer.Serialize(axe.Violations.Select(static v => new { v.Id, v.Impact, v.Description, v.Help, Nodes = v.Nodes.Select(static n => n.Html).ToArray() }), ReportJson));
        var blocking = axe.Violations.Where(static v => v.Impact is "serious" or "critical").ToArray();
        Assert.True(blocking.Length == 0, "axe violations:\n" + string.Join("\n", blocking.Select(static v => $"{v.Impact} {v.Id}: {v.Help}\n  " + string.Join("\n  ", v.Nodes.Select(static n => n.Html)))));

        var h1Count = await page.Locator("h1").CountAsync();
        Assert.Equal(1, h1Count);
    }

    public static TheoryData<string, string> ModuleListPages => new()
    {
        { "cohorts", "/cohorts" }, { "inventory", "/inventory/hardware" }, { "software", "/software/applications" }, { "updates", "/updates/available" },
        { "configuration", "/configuration/policies" }, { "security", "/security/certificates" }, { "storage", "/storage/disks" }, { "networking", "/networking/interfaces" },
        { "monitoring", "/monitoring/alerts" }, { "automation", "/automation/jobs" }, { "reports", "/reports/custom" },
    };

    [Theory]
    [MemberData(nameof(ModuleListPages))]
    public async Task Module_details_page_opens_from_the_list_and_passes_axe(string name, string route)
    {
        await using var context = await fixture.NewContextAsync(1366, 768, ColorScheme.Light);
        var page = await context.NewPageAsync();
        await page.GotoAsync(fixture.BaseUrl + route);
        await page.WaitForSelectorAsync("h1");
        await page.WaitForSelectorAsync($"a[href^='{route}/']");
        var href = await page.Locator($"a[href^='{route}/']").First.GetAttributeAsync("href");
        Assert.NotNull(href);
        await page.GotoAsync(fixture.BaseUrl + href);
        await page.WaitForSelectorAsync("h1");
        await page.WaitForSelectorAsync("dl.property-list");
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.OutputDirectory, $"{name}-details-1366x768-light.png"), FullPage = false });
        var axe = await page.RunAxe();
        Assert.DoesNotContain(axe.Violations, static v => v.Impact is "serious" or "critical");
        Assert.Equal(1, await page.Locator("h1").CountAsync());
    }

    [Fact]
    public async Task Module_create_dialog_adds_a_record_to_the_list()
    {
        await using var context = await fixture.NewContextAsync(1366, 768, ColorScheme.Light);
        var page = await context.NewPageAsync();
        await page.GotoAsync(fixture.BaseUrl + "/configuration/policies");
        await page.WaitForSelectorAsync("h1");
        await page.WaitForSelectorAsync("a[href^='/configuration/policies/']");
        var create = page.Locator("fluent-button", new() { HasText = "Create policy" }).First;
        for (var attempt = 0; attempt < 10 && await page.Locator("fluent-dialog").CountAsync() == 0; attempt++)
        {
            await create.ClickAsync(new() { Timeout = 5000 });
            await page.WaitForTimeoutAsync(300);
        }

        await page.WaitForSelectorAsync("fluent-dialog fluent-text-input input");
        var nameInput = page.Locator("fluent-dialog fluent-text-input input").First;
        await nameInput.FillAsync("Playwright policy");
        await nameInput.PressAsync("Tab");
        var submit = page.Locator("fluent-dialog fluent-button", new() { HasTextString = "Create" }).Filter(new() { HasNotText = "Cancel" }).First;
        await page.WaitForFunctionAsync("() => [...document.querySelectorAll('fluent-dialog fluent-button')].some(b => b.textContent.trim() === 'Create' && !b.disabled)");
        for (var attempt = 0; attempt < 5 && await page.Locator("fluent-dialog").CountAsync() > 0; attempt++)
        {
            await submit.ClickAsync(new() { Timeout = 5000 });
            await page.WaitForTimeoutAsync(500);
        }

        try
        {
            await page.WaitForSelectorAsync("a:has-text('Playwright policy')");
        }
        catch (TimeoutException)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.OutputDirectory, "configuration-policies-create-failed.png"), FullPage = false });
            await File.WriteAllTextAsync(Path.Combine(fixture.OutputDirectory, "configuration-policies-create-failed.log"), fixture.ServerLog);
            throw;
        }
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.OutputDirectory, "configuration-policies-created.png"), FullPage = false });
    }

    [Fact]
    public async Task Theme_menu_switches_to_light_and_the_choice_survives_a_reload()
    {
        await using var context = await fixture.NewContextAsync(1366, 768, ColorScheme.Dark);
        var page = await context.NewPageAsync();
        await page.GotoAsync(fixture.BaseUrl + "/configuration/environment");
        await page.WaitForSelectorAsync("h1");
        await page.WaitForFunctionAsync("() => typeof Blazor !== 'undefined' && !!Blazor.theme");
        Assert.Equal("#292929", await page.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).getPropertyValue('--colorNeutralBackground1').trim()"));

        var themeButton = page.Locator("#console-theme-button");
        var lightItem = page.GetByRole(AriaRole.Menuitemradio, new() { Name = "Light" });
        for (var attempt = 0; attempt < 10 && !await lightItem.IsVisibleAsync(); attempt++)
        {
            await themeButton.ClickAsync();
            await page.WaitForTimeoutAsync(300);
        }

        await lightItem.ClickAsync();
        await page.WaitForFunctionAsync("() => getComputedStyle(document.documentElement).getPropertyValue('--colorNeutralBackground1').trim() === '#ffffff'");
        Assert.Null(await page.EvaluateAsync<string?>("() => document.body.getAttribute('data-theme')"));
        await page.Keyboard.PressAsync("Escape");
        await page.WaitForTimeoutAsync(600);
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.OutputDirectory, "theme-light-on-dark-system-1366x768.png"), FullPage = false });

        await page.ReloadAsync();
        await page.WaitForSelectorAsync("h1");
        await page.WaitForFunctionAsync("() => getComputedStyle(document.documentElement).getPropertyValue('--colorNeutralBackground1').trim() === '#ffffff'");
        // Fluent removes the attribute again for light mode; what matters is that the stored choice beat the dark system setting.
        Assert.NotEqual("dark", await page.EvaluateAsync<string?>("() => document.body.getAttribute('data-theme')"));
        Assert.Equal("light", await page.EvaluateAsync<string?>("() => JSON.parse(localStorage.getItem('fluentui-blazor:theme-settings') ?? '{}').mode"));
        await page.WaitForTimeoutAsync(300);
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.OutputDirectory, "theme-light-after-reload-1366x768.png"), FullPage = false });
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task Sign_in_page_renders_and_passes_axe(int width, int height, string theme)
    {
        await using var context = await fixture.NewContextAsync(width, height, theme == "dark" ? ColorScheme.Dark : ColorScheme.Light, signedIn: false);
        var page = await context.NewPageAsync();
        var response = await page.GotoAsync(fixture.BaseUrl + "/enrollments");
        Assert.NotNull(response);
        await page.WaitForURLAsync("**/login?*");
        await page.WaitForSelectorAsync("h1");
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.OutputDirectory, $"sign-in-{width}x{height}-{theme}.png"), FullPage = false });
        var axe = await page.RunAxe();
        Assert.DoesNotContain(axe.Violations, static v => v.Impact is "serious" or "critical");
    }

    [Fact]
    public async Task Keyboard_reaches_the_skip_link_first_and_the_main_landmark_second()
    {
        await using var context = await fixture.NewContextAsync(1366, 768, ColorScheme.Light);
        var page = await context.NewPageAsync();
        await page.GotoAsync(fixture.BaseUrl + "/enrollments");
        await page.WaitForSelectorAsync("h1");
        await page.WaitForFunctionAsync("() => document.querySelectorAll('[role=row]').length > 2");
        // Let FocusOnNavigate move focus to the h1 first, so the reset below is not raced by the circuit connecting.
        try { await page.WaitForFunctionAsync("() => document.activeElement?.tagName === 'H1'", null, new() { Timeout = 10000 }); } catch (TimeoutException) { }
        // blur() keeps the sequential focus start point at the h1; a focused-then-removed anchor at the top of body resets it.
        await page.EvaluateAsync("() => { const a = document.createElement('span'); a.tabIndex = -1; document.body.prepend(a); a.focus(); a.remove(); window.scrollTo(0, 0); }");
        await page.Keyboard.PressAsync("Tab");
        var focused = await page.EvaluateAsync<string>("() => { const e = document.activeElement; return e ? e.tagName + '.' + e.className : ''; }");
        Assert.Contains("skip-link", focused, StringComparison.Ordinal);
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("() => document.activeElement?.id === 'main-content' || location.hash === '#main-content'", null, new() { Timeout = 10000 });
    }

    [Fact]
    public async Task Search_filters_the_enrollment_list_as_you_type()
    {
        await using var context = await fixture.NewContextAsync(1366, 768, ColorScheme.Light);
        var page = await context.NewPageAsync();
        await page.GotoAsync(fixture.BaseUrl + "/enrollments");
        await page.WaitForSelectorAsync("h1");
        await page.WaitForFunctionAsync("() => document.querySelectorAll('[role=row]').length > 2");
        var before = await page.Locator("[role=row]").CountAsync();
        await page.Locator("input[placeholder^='Search by enrollment']").FillAsync(fixture.SampleEnrollmentId);
        await page.WaitForFunctionAsync($"() => document.querySelectorAll('[role=row]').length < {before}");
        var after = await page.Locator("[role=row]").CountAsync();
        Assert.True(after < before, $"rows before {before}, after {after}");
    }

    [Fact]
    public async Task Creating_an_enrollment_token_shows_it_once_and_never_again()
    {
        await using var context = await fixture.NewContextAsync(1366, 768, ColorScheme.Light);
        var page = await context.NewPageAsync();
        var consoleErrors = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Type is "error" or "warning")
            {
                consoleErrors.Add(message.Text);
            }
        };
        await page.GotoAsync(fixture.BaseUrl + "/enrollments/tokens");
        await page.WaitForSelectorAsync("h1");
        // Fluent buttons are custom elements; match them by tag and text rather than by computed role.
        // The page is prerendered, so the first clicks may land before the circuit is connected: retry until the dialog opens.
        // The fluent-dialog host has no box of its own (its content is in the top layer), so visibility is checked on the title.
        var dialog = page.Locator("fluent-dialog");
        var title = page.GetByText("Create enrollment token", new() { Exact = true });
        for (var attempt = 0; attempt < 15 && await dialog.CountAsync() == 0; attempt++)
        {
            await page.Locator("fluent-button", new() { HasText = "Create token" }).First.ClickAsync();
            await page.WaitForTimeoutAsync(1000);
        }

        await title.WaitForAsync();
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.OutputDirectory, "dialog-create-token.png") });
        await dialog.Locator("fluent-button", new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"^\s*Create\s*$") }).ClickAsync();
        var tokenInput = dialog.Locator("fluent-text-input[readonly]");
        await tokenInput.WaitForAsync(new() { State = WaitForSelectorState.Attached });
        var token = await tokenInput.EvaluateAsync<string>("e => e.value");
        Assert.Equal(64, token.Length);
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.OutputDirectory, "dialog-token-shown-once.png") });
        await dialog.Locator("fluent-button", new() { HasText = "Done" }).ClickAsync();
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        var html = await page.ContentAsync();
        Assert.DoesNotContain(token, html, StringComparison.Ordinal);
        Assert.DoesNotContain(token, fixture.ServerLog, StringComparison.Ordinal);
    }
}

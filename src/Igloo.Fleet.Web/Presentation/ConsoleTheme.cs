using Microsoft.JSInterop;

namespace Igloo.Fleet.Web.Presentation;

/// <summary>
/// Applies the operator's theme choice through the Fluent UI script. In Fluent UI Blazor 5.0.1 the
/// <c>IThemeService.SetThemeAsync(ThemeMode)</c> call left the page unchanged (observed 2026-11-01), while the
/// script's <c>Blazor.theme.setThemeMode</c> switches the tokens, keeps the body attribute and persists the choice.
/// </summary>
internal static class ConsoleTheme
{
    public static readonly string[] Modes = ["system", "light", "dark"];

    public static ValueTask ApplyAsync(IJSRuntime js, string theme)
    {
        ArgumentNullException.ThrowIfNull(js);
        return js.InvokeVoidAsync("Blazor.theme.setThemeMode", theme is "light" or "dark" ? theme : null);
    }
}

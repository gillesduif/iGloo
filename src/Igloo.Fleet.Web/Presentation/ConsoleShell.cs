namespace Igloo.Fleet.Web.Presentation;

/// <summary>
/// Per-circuit shell state shared by the header and the current page: the in-page search box
/// and the navigation collapse. There is no cross-object search endpoint, so the header's search
/// field only ever filters the list page that attached to it.
/// </summary>
internal sealed class ConsoleShell
{
    public string Query { get; private set; } = string.Empty;

    public string? SearchPlaceholder { get; private set; }

    public bool SearchAvailable => SearchPlaceholder is not null;

    public bool NavigationCollapsed { get; private set; }

    public event Action? Changed;

    public void AttachSearch(string placeholder)
    {
        SearchPlaceholder = placeholder;
        Query = string.Empty;
        Changed?.Invoke();
    }

    public void DetachSearch()
    {
        SearchPlaceholder = null;
        Query = string.Empty;
        Changed?.Invoke();
    }

    public void SetQuery(string? query)
    {
        Query = query ?? string.Empty;
        Changed?.Invoke();
    }

    public void ToggleNavigation()
    {
        NavigationCollapsed = !NavigationCollapsed;
        Changed?.Invoke();
    }
}

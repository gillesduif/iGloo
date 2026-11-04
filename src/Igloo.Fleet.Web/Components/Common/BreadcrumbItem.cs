namespace Igloo.Fleet.Web.Components.Common;

// Public because Razor components are public and take it as a parameter.
public sealed record BreadcrumbItem(string Text, string Href);

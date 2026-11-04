using Igloo.Fleet.Web.Presentation;
using Igloo.Fleet.Web.Services;

namespace Igloo.Fleet.Web;

/// <summary>CSV export of module pages, one file per page, unfiltered.</summary>
internal static class ModuleExportEndpoints
{
    internal static IEndpointRouteBuilder MapModuleExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/export/modules/{module}/{slug}.csv", Export).RequireAuthorization();
        return endpoints;
    }

    private static IResult Export(string module, string slug, IModuleDataSource source)
    {
        var page = ModuleCatalog.Find(module, slug == "all" ? string.Empty : slug);
        if (page is null)
        {
            return Results.NotFound();
        }

        var rows = source.Rows(page);
        if (rows is null)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var header = page.Fields.Select(static f => f.Key).Prepend("Id").ToArray();
        var lines = rows.Select(r => page.Fields.Select(f => r.Export(f.Key)).Prepend(r.Id.ToString("D")).ToArray());
        return ExportEndpoints.Csv($"{module}-{(slug == "all" ? module : slug)}.csv", header, lines);
    }
}

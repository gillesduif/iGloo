namespace Igloo.Fleet.Web.Components.Pages.Planning;

/// <summary>Resource keys for the three distribution identifiers the Server accepts.</summary>
internal static class PlanningLabels
{
    public static readonly string[] DistributionIds = ["debian", "fedora-kde", "linuxmint-cinnamon"];

    public static string DistributionKey(string distroId) => distroId switch
    {
        "debian" => "DistributionDebian",
        "fedora-kde" => "DistributionFedoraKde",
        "linuxmint-cinnamon" => "DistributionLinuxMintCinnamon",
        _ => distroId,
    };

    public const long Gibibyte = 1024L * 1024 * 1024;
}

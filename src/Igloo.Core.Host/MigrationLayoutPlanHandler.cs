using System.Text.Json;
using Igloo.Command.Contracts;
using Igloo.Core.Plugins;
using Igloo.Core.Runtime;
using Igloo.Core.Runtime.Execution;

namespace Igloo.Core.Host;

// Command-kind Plan writes only the existing PreparedLayoutStore checkpoint.
// It never receives an installer service or a storage writer.
internal sealed class MigrationLayoutPlanHandler(IReviewedInstallationPlanning planning,
    FileArtifactRegistry artifacts, DistroLoader loader, DistroRegistry providers,
    string? managedStagingRoot = null)
    : ICommandHandler
{
    public CapabilityDescriptor Descriptor => new("migration.layout.plan", 1, 1, 1,
        CommandKind.Plan, RiskClass.Low, false, AuthorityRequirement.User,
        loader.LoadedDistros.Where(d => d.IsAvailable &&
                d.SupportedInstallModes.Contains("dual-boot", StringComparer.Ordinal) &&
                providers.TryGet(d.Id, out var provider) &&
                ReviewedInstallationProviderSupport.SupportsPlanning(provider))
            .Select(d => d.Id).Order(StringComparer.Ordinal).ToArray());

    public async Task<object> ExecuteAsync(CommandRequest request,
        IProgress<CommandProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = request.Input.Deserialize<MigrationLayoutPlanInput>(
            CommandProtocol.JsonOptions) ?? throw new ArgumentException("Layout input is required.");
        if (string.IsNullOrWhiteSpace(input.DistroId) ||
            input.PlanId == Guid.Empty || input.TargetId == Guid.Empty ||
            input.AllocationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(input.ArtifactId) ||
            input.RequestedLinuxBytes <= 0)
            throw new ArgumentException("A bound plan, allocation and artifact are required.");
        RequireManagedStage(input.StagingDirectory, input.DistroId,
            managedStagingRoot);
        var acquired = await artifacts.ResolveVerifiedAsync(input.ArtifactId,
            input.DistroId, cancellationToken).ConfigureAwait(false);
        var result = await planning.PlanAsync(input.DistroId, input.PlanId,
            input.TargetId, input.AllocationId, input.RequestedLinuxBytes,
            acquired, input.StagingDirectory, cancellationToken).ConfigureAwait(false);
        var intent = result.Plan.ReviewedIntent ??
            throw new InvalidDataException("The reviewed layout lost its durable allocation binding.");
        if (intent.PlanId != input.PlanId || intent.TargetId != input.TargetId ||
            intent.AllocationId != input.AllocationId ||
            !string.Equals(intent.ArtifactSha256, acquired.VerifiedSha256,
                StringComparison.OrdinalIgnoreCase) ||
            result.Checkpoint.GenerationId != result.Plan.GenerationId)
            throw new StateChangedException();
        return new MigrationLayoutPlanResult(input.PlanId, input.TargetId,
            input.AllocationId, result.Plan.GenerationId, input.ArtifactId,
            intent.ArtifactSha256, checked((long)intent.ShrinkBytes),
            checked((long)result.Plan.Space.RequiredBytes), result.StagedBytes,
            result.Plan.Space.Allocations.Select(a => new MigrationLayoutExtent(
                a.Role.ToString(), checked((long)a.OffsetBytes),
                checked((long)a.SizeBytes), a.FileSystem, a.CreateInWindows)).ToArray(),
            "Planned", false, "AdvisoryOnly");
    }

    private static void RequireManagedStage(string stagingDirectory, string distroId,
        string? managedStagingRoot)
    {
        if (string.IsNullOrWhiteSpace(stagingDirectory) ||
            !Path.IsPathFullyQualified(stagingDirectory) ||
            distroId.Length is < 1 or > 80 ||
            !distroId.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            throw new ArgumentException("A service-owned staging generation is required.");
        var root = Path.GetFullPath(Path.Join(managedStagingRoot ?? Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Igloo", "staging"), distroId));
        var selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory));
        if (!string.Equals(Path.GetDirectoryName(selected), root,
                StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(selected), "N", out _) ||
            !Directory.Exists(selected))
            throw new InvalidDataException("The selected staging generation is outside the managed migration location.");
        for (var current = new DirectoryInfo(selected); current is not null;
             current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The selected staging generation is redirected.");
            if (string.Equals(current.FullName, root, StringComparison.OrdinalIgnoreCase))
                break;
        }
    }
}

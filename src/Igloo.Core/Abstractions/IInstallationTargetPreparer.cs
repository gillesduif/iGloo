using Igloo.Core.Models;

namespace Igloo.Core.Abstractions;

/// <summary>
/// Explicit creation and durable identity capture, separate from legacy installer boot staging.
/// A caller must review/authorize the snapshot and exact free extent before PrepareAsync.
/// This capability does not resize, format, install or select a partition heuristically.
/// </summary>
public interface IInstallationTargetPreparer
{
    Task<GptDiskLayout> ReadLayoutAsync(Guid diskGuid, CancellationToken ct = default);

    Task<InstallationTargetClaim> PrepareAsync(
        InstallationTargetRequest request, string manifestPath, CancellationToken ct = default);
}

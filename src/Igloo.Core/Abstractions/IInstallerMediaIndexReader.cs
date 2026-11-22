namespace Igloo.Core.Abstractions;

public interface IInstallerMediaIndexReader
{
    /// <summary>Returns the declaration with the hashes its signed index names; one without an index comes back as it is.</summary>
    Task<InstallerBootSpec> ResolveAsync(InstallerBootSpec declared, CancellationToken ct = default);
}

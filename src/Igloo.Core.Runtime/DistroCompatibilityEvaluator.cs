using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;

namespace Igloo.Core.Runtime;

/// <summary>One provider compatibility evaluation for CLI, Community and Fleet local callers.</summary>
public sealed class DistroCompatibilityEvaluator(DistroRegistry registry, DistroLoader? loader = null)
{
    public IReadOnlyList<PreflightFinding> Evaluate(string distroId, PreflightReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distroId);
        ArgumentNullException.ThrowIfNull(report);
        if (!registry.TryGet(distroId, out var plugin)) throw new KeyNotFoundException();
        var manifest = loader?.LoadedDistros.SingleOrDefault(d => string.Equals(d.Id, distroId,
            StringComparison.OrdinalIgnoreCase));
        var tags = manifest?.Tags ?? plugin.Metadata.Tags;
        var findings = new List<PreflightFinding>();
        if (report.SecureBootEnabled && !tags.Contains("secure-boot-supported",
                StringComparer.OrdinalIgnoreCase))
            findings.Add(new(FindingSeverity.Blocker, "SecureBootUnsupported",
                "Turn off Secure Boot in firmware settings before installing this distribution."));
        findings.AddRange(plugin.CheckCompatibility(report));
        return findings;
    }
}

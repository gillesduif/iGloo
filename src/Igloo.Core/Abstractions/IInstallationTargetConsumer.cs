using Igloo.Core.Models;

namespace Igloo.Core.Abstractions;

/// <summary>
/// Opt-in contract for plugins requiring a verified creation receipt before installation.
/// It does not enable installation or replace validation against the live GPT inventory.
/// </summary>
public interface IInstallationTargetConsumer
{
    InstallationTargetRequirement TargetRequirement { get; }
}

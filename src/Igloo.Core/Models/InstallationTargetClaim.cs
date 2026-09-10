using System.Text.Json.Serialization;

namespace Igloo.Core.Models;

/// <summary>Portable GPT facts. Device names and partition numbers are deliberately absent.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GptPartitionIdentity
{
    [JsonPropertyName("partitionGuid")] public required Guid PartitionGuid { get; init; }
    [JsonPropertyName("gptType")] public required Guid GptType { get; init; }
    [JsonPropertyName("offsetBytes")] public required long OffsetBytes { get; init; }
    [JsonPropertyName("lengthBytes")] public required long LengthBytes { get; init; }
}

/// <summary>The complete partition identity set detects changes beyond the installation root.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GptDiskLayout
{
    [JsonPropertyName("diskGuid")] public required Guid DiskGuid { get; init; }
    [JsonPropertyName("logicalSectorSize")] public required int LogicalSectorSize { get; init; }
    [JsonPropertyName("diskSizeBytes")] public required long DiskSizeBytes { get; init; }
    [JsonPropertyName("partitions")] public required IReadOnlyList<GptPartitionIdentity> Partitions { get; init; }
}

/// <summary>
/// A verified creation receipt, published only after reading the resulting layout.
/// This is not permission to format a partition merely because this JSON exists.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InstallationTargetClaim
{
    [JsonPropertyName("version")] public required int Version { get; init; }
    [JsonPropertyName("installationId")] public required Guid InstallationId { get; init; }
    [JsonPropertyName("ownership")] public required string Ownership { get; init; }
    [JsonPropertyName("disk")] public required GptDiskLayout Disk { get; init; }
    [JsonPropertyName("rootPartitionGuid")] public required Guid RootPartitionGuid { get; init; }
    [JsonPropertyName("espPartitionGuid")] public required Guid? EspPartitionGuid { get; init; }
}

/// <summary>
/// The caller explicitly authorizes this exact free extent in this reviewed snapshot.
/// Existing partitions, implicit reuse, shrinking, and choosing free space are not supported.
/// </summary>
public sealed record InstallationTargetRequest
{
    public required Guid InstallationId { get; init; }
    public required GptDiskLayout ExpectedLayout { get; init; }
    public required long RootOffsetBytes { get; init; }
    public required long RootLengthBytes { get; init; }
    public required Guid? EspPartitionGuid { get; init; }
}

public enum InstallationTargetRequirement
{
    None,
    CreatedRoot,
    CreatedRootAndEsp,
}

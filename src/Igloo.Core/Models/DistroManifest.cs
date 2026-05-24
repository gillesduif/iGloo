using System.Text.Json.Serialization;

namespace Igloo.Core.Models;

/// <summary>Deserialised representation of a <c>distro.json</c> manifest file.</summary>
public sealed record DistroManifest
{
    [JsonPropertyName("id")]                      public required string Id                    { get; init; }
    [JsonPropertyName("displayName")]             public required string DisplayName           { get; init; }
    [JsonPropertyName("description")]             public required string Description           { get; init; }
    [JsonPropertyName("defaultDesktopEnvironment")] public string? DefaultDesktopEnvironment  { get; init; }
    [JsonPropertyName("installerType")]           public string? InstallerType                 { get; init; }
    [JsonPropertyName("iso")]                     public required DistroIsoSpec Iso            { get; init; }
    [JsonPropertyName("tags")]                    public IReadOnlyList<string> Tags            { get; init; } = [];
    [JsonPropertyName("screenshots")]             public IReadOnlyList<string> Screenshots     { get; init; } = [];
    [JsonPropertyName("minimumRequirements")]     public DistroRequirements? MinimumRequirements { get; init; }
    [JsonPropertyName("maintainer")]              public DistroMaintainer? Maintainer          { get; init; }
}

public sealed record DistroIsoSpec
{
    [JsonPropertyName("downloadUrl")]     public required string DownloadUrl    { get; init; }
    [JsonPropertyName("sha256")]          public required string Sha256         { get; init; }
    [JsonPropertyName("gpgSignatureUrl")] public string? GpgSignatureUrl        { get; init; }
    [JsonPropertyName("gpgKeyUrl")]       public string? GpgKeyUrl              { get; init; }
}

public sealed record DistroRequirements
{
    [JsonPropertyName("minRamBytes")]   public long MinRamBytes   { get; init; }
    [JsonPropertyName("minDiskBytes")]  public long MinDiskBytes  { get; init; }
    [JsonPropertyName("requiresUefi")]  public bool RequiresUefi  { get; init; }
    [JsonPropertyName("requires64Bit")] public bool Requires64Bit { get; init; }
}

public sealed record DistroMaintainer
{
    [JsonPropertyName("github")] public string? Github { get; init; }
    [JsonPropertyName("note")]   public string? Note   { get; init; }
}

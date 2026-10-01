using Igloo.Distro.Debian.Deployment;

namespace CanonicalImportQualification;

// Exact retained external-pin schema. Strict decoding must recognize the source
// identities even though the authority authenticates their bound descriptor.
internal readonly record struct UserDataExternalPin(string Use, string ProductionAuthentication,
    Guid BuildId, string DescriptorSha256, string PolicySha256, DateTimeOffset NotBeforeUtc,
    DateTimeOffset NotAfterUtc, string ManifestSha256, string ContentSha256)
{
    internal DebianRootDevelopmentPinV1 Authenticate(Guid build, string descriptor, string manifest,
        string content, DateTimeOffset now)
    {
        static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
        if (Use != "DevelopmentImportOnly" || ProductionAuthentication != "Unsupported" ||
            BuildId == Guid.Empty || BuildId != build || !Hash(DescriptorSha256) || DescriptorSha256 != descriptor ||
            !Hash(ManifestSha256) || ManifestSha256 != manifest || !Hash(ContentSha256) || ContentSha256 != content ||
            !Hash(PolicySha256) || NotBeforeUtc == default || NotAfterUtc <= NotBeforeUtc ||
            now < NotBeforeUtc || now >= NotAfterUtc)
            throw new InvalidDataException("UserDataExternalPinAuthenticationRejected");
        return new(BuildId, DescriptorSha256, PolicySha256, NotBeforeUtc, NotAfterUtc);
    }
}

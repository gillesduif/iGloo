namespace Igloo.Fleet.Web.Services;

/// <summary>Console presentation settings. None of these change what the Fleet Server does.</summary>
internal sealed class ConsoleOptions
{
    public const string SectionName = "Fleet:Console";

    /// <summary>Cultures with resource files. A culture is only offered to operators once every key is translated; see OfferedCultures.</summary>
    public static readonly string[] ResourceCultures = ["en", "nl", "fr", "de"];

    /// <summary>Cultures an operator can choose. Partial translations stay out so a page is never half in one language, half in another.</summary>
    public static readonly string[] OfferedCultures = ["en"];

    public int StaleContactDays { get; set; } = 7;

    public int CertificateExpiryWarningDays { get; set; } = 14;
}

/// <summary>How the browser host reaches the Fleet Server. The operator secret is read from the environment, never from configuration files.</summary>
internal sealed class FleetServerOptions
{
    public const string SectionName = "Fleet:Server";
    public const string OperatorSecretVariable = "IGLOO_FLEET_OPERATOR_SECRET";
    public const string ServerUriVariable = "IGLOO_FLEET_SERVER_URI";
    public const string CaCertificateVariable = "IGLOO_FLEET_CA_CERT";

    public string Uri { get; set; } = string.Empty;

    public string CaCertificatePath { get; set; } = string.Empty;

    public Uri? ResolveUri()
    {
        var candidate = string.IsNullOrWhiteSpace(Uri)
            ? Environment.GetEnvironmentVariable(ServerUriVariable)
            : Uri;
        return System.Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && uri.Scheme == System.Uri.UriSchemeHttps
            && uri.AbsolutePath == "/"
            && uri.UserInfo.Length == 0
            ? uri
            : null;
    }

    public string? ResolveCaCertificatePath()
    {
        var candidate = string.IsNullOrWhiteSpace(CaCertificatePath)
            ? Environment.GetEnvironmentVariable(CaCertificateVariable)
            : CaCertificatePath;
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }

    public static bool HasOperatorSecret() =>
        Environment.GetEnvironmentVariable(OperatorSecretVariable) is { Length: >= 32 };
}

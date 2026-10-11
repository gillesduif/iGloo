using System.Diagnostics;

namespace Igloo.Cli.Client;

/// <summary>Resolves only packaged or repository-build CLI locations; never searches PATH.</summary>
public static class CliExecutableLocator
{
    public const string ExecutableName = "Igloo.Cli.exe";

    public static string Resolve(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        var applicationRoot = Path.GetFullPath(applicationDirectory);
        foreach (var candidate in new[]
                 {
                     Path.Join(applicationRoot, "cli", ExecutableName),
                     Path.Join(applicationRoot, ExecutableName),
                 })
            if (File.Exists(candidate)) return Validate(candidate);

        var current = new DirectoryInfo(applicationRoot);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "Igloo.sln")))
            {
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    var candidate = Path.Join(current.FullName, "src", "Igloo.Cli", "Igloo.Cli",
                        "bin", configuration, "net10.0-windows10.0.19041.0", ExecutableName);
                    if (File.Exists(candidate)) return Validate(candidate);
                }
                break;
            }
            current = current.Parent;
        }
        throw new CliTransportException(CliTransportError.ExecutableUnavailable,
            "The packaged iGloo CLI executable is unavailable.");
    }

    public static string Validate(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath) ||
            !string.Equals(Path.GetFileName(executablePath), ExecutableName,
                StringComparison.OrdinalIgnoreCase))
            throw new CliTransportException(CliTransportError.IdentityMismatch,
                "The configured CLI location is not the expected executable.");
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
            throw new CliTransportException(CliTransportError.ExecutableUnavailable,
                "The iGloo CLI executable is unavailable.");
        if (!string.Equals(FileVersionInfo.GetVersionInfo(fullPath).ProductName, "Igloo.Cli",
                StringComparison.Ordinal))
            throw new CliTransportException(CliTransportError.IdentityMismatch,
                "The configured executable has an unexpected product identity.");
        return fullPath;
    }
}

using System.Diagnostics;

namespace Igloo.Cli.Client;

/// <summary>Resolves packaged CLI locations, or a build only for an explicit repository-root caller; never searches PATH.</summary>
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
                // An app/test running beneath the checkout still needs its own
                // packaged CLI. Only an explicit repository-root development
                // request may resolve the build output.
                if (!string.Equals(Path.TrimEndingDirectorySeparator(applicationRoot),
                        Path.TrimEndingDirectorySeparator(current.FullName),
                        StringComparison.OrdinalIgnoreCase))
                    break;
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    var candidate = Path.Join(current.FullName, "src", "Igloo.Cli", "Igloo.Cli",
                        "bin", configuration, "portable", "net10.0-windows10.0.19041.0", ExecutableName);
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
        try
        {
            var file = new FileInfo(fullPath);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw RedirectedPath();
            for (var directory = file.Directory; directory is not null;
                 directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw RedirectedPath();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw RedirectedPath();
        }
        if (!string.Equals(FileVersionInfo.GetVersionInfo(fullPath).ProductName, "Igloo.Cli",
                StringComparison.Ordinal))
            throw new CliTransportException(CliTransportError.IdentityMismatch,
                "The configured executable has an unexpected product identity.");
        return fullPath;
    }

    private static CliTransportException RedirectedPath() =>
        new(CliTransportError.IdentityMismatch,
            "The configured CLI path is redirected or could not be inspected.");
}

namespace Igloo.Core.Abstractions;

/// <summary>
/// Staging failed and its new, incomplete generation could not be removed.
/// The directory is exposed so the caller can show the exact location to the user.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1032:Implement standard exception constructors",
    Justification = "An exact orphan directory and both failures are required for a valid instance.")]
public sealed class IncompleteFileStagingCleanupException : IOException
{
    public IncompleteFileStagingCleanupException(
        string stagingDirectory,
        Exception originalError,
        Exception cleanupError)
        : base(
            $"Incomplete migration files could not be removed from {stagingDirectory}. " +
            "Review that folder before retrying staging.",
            new AggregateException(originalError, cleanupError))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(originalError);
        ArgumentNullException.ThrowIfNull(cleanupError);
        StagingDirectory = stagingDirectory;
        OriginalError = originalError;
        CleanupError = cleanupError;
    }

    public string StagingDirectory { get; }
    public Exception OriginalError { get; }
    public Exception CleanupError { get; }
}

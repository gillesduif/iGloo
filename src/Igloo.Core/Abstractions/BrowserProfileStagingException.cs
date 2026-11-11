namespace Igloo.Core.Abstractions;

/// <summary>A selected browser file could not be captured consistently.</summary>
public sealed class BrowserProfileStagingException : IOException
{
    public BrowserProfileStagingFailure Reason { get; }

    /// <summary>The browsers this failure is about, by the name the wizard lists; empty when unknown.</summary>
    public IReadOnlyList<string> Browsers { get; init; } = [];

    public BrowserProfileStagingException() : base("Close the selected browsers and retry staging.") { }

    public BrowserProfileStagingException(string message) : base(message) { }

    public BrowserProfileStagingException(string message, Exception innerException)
        : base(message, innerException) { }

    public BrowserProfileStagingException(string message, Exception? innerException,
        BrowserProfileStagingFailure reason) : base(message, innerException)
    {
        Reason = reason;
    }
}

/// <summary><c>BrowserRunning</c> is raised before anything is copied; the others during the copy.</summary>
public enum BrowserProfileStagingFailure { SnapshotUnavailable, RecoveryRequired, BrowserInUse, BrowserRunning }

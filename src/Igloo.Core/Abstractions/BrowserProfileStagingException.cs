namespace Igloo.Core.Abstractions;

/// <summary>A selected browser file could not be captured consistently.</summary>
public sealed class BrowserProfileStagingException : IOException
{
    public BrowserProfileStagingFailure Reason { get; }

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

public enum BrowserProfileStagingFailure { SnapshotUnavailable, RecoveryRequired, BrowserInUse }

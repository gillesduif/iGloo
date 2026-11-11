namespace Igloo.Core.Abstractions;

/// <summary>A refusal raised before Windows is resized. The message tells the user what to do next.</summary>
public sealed class InstallationRefusedException : InvalidOperationException
{
    public InstallationRefusedException() { }

    public InstallationRefusedException(string message) : base(message) { }

    public InstallationRefusedException(string message, Exception innerException) : base(message, innerException) { }
}

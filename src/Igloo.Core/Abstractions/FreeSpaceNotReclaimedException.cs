namespace Igloo.Core.Abstractions;

/// <summary>Unallocated space directly behind the Windows volume that Windows did not add to it. The message tells the user what to do next.</summary>
public sealed class FreeSpaceNotReclaimedException : InvalidOperationException
{
    public FreeSpaceNotReclaimedException() { }

    public FreeSpaceNotReclaimedException(string message) : base(message) { }

    public FreeSpaceNotReclaimedException(string message, Exception innerException) : base(message, innerException) { }
}

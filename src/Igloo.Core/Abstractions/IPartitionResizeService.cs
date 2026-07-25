namespace Igloo.Core.Abstractions;

public interface IPartitionResizeService
{
    Task<long> GetShrinkableSpaceAsync(int diskNumber, CancellationToken ct = default);

    Task ShrinkAsync(int diskNumber, long linuxSizeBytes,
        IProgress<string>? progress = null, CancellationToken ct = default);
}

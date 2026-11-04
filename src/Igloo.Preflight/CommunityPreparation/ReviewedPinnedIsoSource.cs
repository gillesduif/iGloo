using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Igloo.Preflight.CommunityPreparation;

internal sealed record ReviewedExtractedIsoMemberV1(string RelativePath,
    string TemporaryPath, long SizeBytes, string Sha256);

// Reads declared ISO9660 members directly from the same read-locked image whose
// complete bytes match the reviewed ISO pin. An unsupported ISO layout stops the
// reviewed path; it must never fall back to a different mounted drive letter.
internal sealed class ReviewedPinnedIsoSource : IDisposable
{
    private const int BlockBytes = 2048;
    private const uint MaximumDirectoryBytes = 16 * 1024 * 1024;
    private const uint MaximumMemberBytes = 2U * 1024 * 1024 * 1024;
    private readonly FileStream _image;
    private readonly long _expectedLength;
    private readonly string _expectedSha256;
    private bool _disposed;

    internal ReviewedPinnedIsoSource(string isoPath, long expectedLength,
        string expectedSha256, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(isoPath) || expectedLength <= 0 ||
            expectedSha256 is not { Length: 64 } || !expectedSha256.All(Uri.IsHexDigit) ||
            (File.GetAttributes(isoPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The reviewed ISO source has no exact file and hash pin.");
        _image = new FileStream(isoPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, FileOptions.RandomAccess);
        _expectedLength = expectedLength;
        _expectedSha256 = expectedSha256;
        try { VerifyPin(ct); }
        catch { _image.Dispose(); throw; }
    }

    internal void VerifyPin(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_image.Length != _expectedLength)
            throw new InvalidDataException("The reviewed ISO size changed.");
        _image.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int count;
        while ((count = _image.Read(buffer)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        if (_image.Position != _expectedLength ||
            !string.Equals(Convert.ToHexString(hash.GetHashAndReset()),
                _expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The reviewed ISO bytes differ from the media pin.");
    }

    internal string? FirstAvailable(IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        foreach (var candidate in candidates)
            if (Resolve(candidate) is not null)
                return candidate;
        return null;
    }

    internal (string Shim, string Grub)? FirstAvailablePair(
        IReadOnlyList<(string Shim, string Grub)> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        foreach (var candidate in candidates)
            if (Resolve(candidate.Shim) is not null && Resolve(candidate.Grub) is not null)
                return candidate;
        return null;
    }

    internal ReviewedExtractedIsoMemberV1 Extract(string relativePath,
        long? expectedMemberBytes = null, CancellationToken ct = default)
    {
        var entry = Resolve(relativePath) ??
            throw new FileNotFoundException("A declared file is absent from the pinned ISO.", relativePath);
        if (entry.SizeBytes is 0 or > MaximumMemberBytes ||
            (expectedMemberBytes is not null && entry.SizeBytes != expectedMemberBytes))
            throw new InvalidDataException("The ISO member size differs from reviewed media measurements.");
        var temporary = Path.Join(Path.GetTempPath(),
            "igloo-reviewed-iso-member-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var output = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1024 * 1024);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            _image.Position = checked((long)entry.ExtentLba * BlockBytes);
            var remaining = (long)entry.SizeBytes;
            var buffer = new byte[1024 * 1024];
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                var count = _image.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count <= 0)
                    throw new EndOfStreamException("The pinned ISO member ended before its extent.");
                hash.AppendData(buffer, 0, count);
                output.Write(buffer, 0, count);
                remaining -= count;
            }
            output.Flush(flushToDisk: true);
            return new(relativePath, temporary, entry.SizeBytes,
                Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    internal (long SizeBytes, string Sha256) HashMember(string relativePath,
        long? expectedMemberBytes = null, CancellationToken ct = default)
    {
        var entry = Resolve(relativePath) ??
            throw new FileNotFoundException("A declared file is absent from the pinned ISO.", relativePath);
        if (entry.SizeBytes is 0 or > MaximumMemberBytes ||
            (expectedMemberBytes is not null && entry.SizeBytes != expectedMemberBytes))
            throw new InvalidDataException("The ISO member size differs from reviewed media measurements.");
        _image.Position = checked((long)entry.ExtentLba * BlockBytes);
        var remaining = (long)entry.SizeBytes;
        var buffer = new byte[1024 * 1024];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            var count = _image.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count <= 0)
                throw new EndOfStreamException("The pinned ISO member ended before its extent.");
            hash.AppendData(buffer, 0, count);
            remaining -= count;
        }
        return (entry.SizeBytes, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private IsoEntry? Resolve(string relativePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(relativePath) ||
            !ReviewedSeedPathRules.TrySegments("\\" + relativePath.Replace('/', '\\'),
                out var segments))
            throw new InvalidDataException("The ISO member path is unsafe.");
        var pvd = ReadBlock(16);
        if (pvd[0] != 1 || !pvd.AsSpan(1, 5).SequenceEqual("CD001"u8) ||
            pvd[6] != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(pvd.AsSpan(128, 2)) != BlockBytes ||
            BinaryPrimitives.ReadUInt16BigEndian(pvd.AsSpan(130, 2)) != BlockBytes)
            throw new InvalidDataException("The pinned ISO has no supported ISO9660 primary volume.");
        var current = ParseEntry(pvd, 156, pvd[156]);
        if (!current.IsDirectory)
            throw new InvalidDataException("The pinned ISO root is not a directory.");
        for (var i = 0; i < segments.Length; i++)
        {
            var next = Find(current, segments[i], i < segments.Length - 1);
            if (next is null) return null;
            current = next;
        }
        return current;
    }

    private IsoEntry? Find(IsoEntry directory, string name, bool wantDirectory)
    {
        if (!directory.IsDirectory || directory.SizeBytes is 0 or > MaximumDirectoryBytes)
            throw new InvalidDataException("A pinned ISO directory has an unsupported extent.");
        IsoEntry? match = null;
        var blocks = ((ulong)directory.SizeBytes + BlockBytes - 1) / BlockBytes;
        for (ulong blockIndex = 0; blockIndex < blocks; blockIndex++)
        {
            var block = ReadBlock(checked((uint)(directory.ExtentLba + blockIndex)));
            var limit = (int)Math.Min(BlockBytes,
                (ulong)directory.SizeBytes - blockIndex * BlockBytes);
            for (var offset = 0; offset < limit;)
            {
                var length = block[offset];
                if (length == 0) break;
                if (length < 34 || offset + length > limit ||
                    block[offset + 32] == 0 || offset + 33 + block[offset + 32] > offset + length)
                    throw new InvalidDataException("A pinned ISO directory record is malformed.");
                var idLength = block[offset + 32];
                if (idLength == 1 && block[offset + 33] is 0 or 1)
                {
                    offset += length;
                    continue;
                }
                var id = Encoding.ASCII.GetString(block, offset + 33, idLength);
                var version = id.IndexOf(';', StringComparison.Ordinal);
                if (version >= 0) id = id[..version];
                if (string.Equals(id, name, StringComparison.OrdinalIgnoreCase))
                {
                    var entry = ParseEntry(block, offset, length);
                    if (entry.IsDirectory == wantDirectory)
                    {
                        if (match is not null)
                            throw new InvalidDataException("A pinned ISO path has ambiguous directory entries.");
                        match = entry;
                    }
                }
                offset += length;
            }
        }
        return match;
    }

    private IsoEntry ParseEntry(byte[] block, int offset, int length)
    {
        if (length < 34 || offset + length > block.Length ||
            (block[offset + 25] & 0x80) != 0)
            throw new InvalidDataException("A pinned ISO entry is malformed or multi-extent.");
        var lba = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(offset + 2, 4));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(offset + 10, 4));
        if (lba != BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(offset + 6, 4)) ||
            size != BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(offset + 14, 4)) ||
            (ulong)lba * BlockBytes > (ulong)_expectedLength ||
            size > (ulong)_expectedLength - (ulong)lba * BlockBytes)
            throw new InvalidDataException("A pinned ISO extent is outside the reviewed image.");
        return new(lba, size, (block[offset + 25] & 0x02) != 0);
    }

    private byte[] ReadBlock(uint lba)
    {
        var offset = checked((long)lba * BlockBytes);
        if (offset > _expectedLength - BlockBytes)
            throw new InvalidDataException("A pinned ISO descriptor is outside the reviewed image.");
        _image.Position = offset;
        var block = new byte[BlockBytes];
        _image.ReadExactly(block);
        return block;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _image.Dispose();
    }

    private sealed record IsoEntry(uint ExtentLba, uint SizeBytes, bool IsDirectory);
}

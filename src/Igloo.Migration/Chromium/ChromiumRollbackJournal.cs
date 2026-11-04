using System.Buffers.Binary;
using Igloo.Core.Abstractions;

namespace Igloo.Migration.Chromium;

/// <summary>Admits only self-contained rollback journals for recovery of a private copy.</summary>
internal static class ChromiumRollbackJournal
{
    private static ReadOnlySpan<byte> Magic => [0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7];

    public static bool NeedsRecovery(FileStream journal, long mainLength)
    {
        journal.Position = 0;
        Span<byte> header = stackalloc byte[28];
        var count = journal.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        // SQLite initializes an unsynced journal with zero magic but nonzero
        // metadata. No protected main pages can have been written at that point.
        // PERSIST invalidates the magic on commit, too. Neither is a hot journal.
        if (header[..Math.Min(count, 8)].IndexOfAnyExcept((byte)0) < 0) return false;
        if (count < header.Length || !header[..8].SequenceEqual(Magic)) throw UnsafeJournal();

        // SQLite consults a super-journal pathname BEFORE replaying any pages.
        // Reject its footer marker even if the name/length/checksum is malformed;
        // a private snapshot must never cause access to an external pathname.
        Span<byte> footer = stackalloc byte[8];
        journal.Position = journal.Length - footer.Length;
        journal.ReadExactly(footer);
        if (footer.SequenceEqual(Magic)) throw UnsafeJournal();

        var pageSize = BinaryPrimitives.ReadUInt32BigEndian(header[24..]);
        var sectorSize = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
        var originalPages = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        if (!IsPowerOfTwoInRange(pageSize, 512, 65536) ||
            !IsPowerOfTwoInRange(sectorSize, 32, 65536) ||
            (long)originalPages * pageSize > checked(mainLength + journal.Length)) throw UnsafeJournal();
        var record = new byte[checked((int)pageSize + 8)];
        var seenPages = new HashSet<uint>();
        long offset = 0;
        while (true)
        {
            journal.Position = offset;
            count = journal.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            if (count >= 8 && header[..8].IndexOfAnyExcept((byte)0) < 0) return true;
            if (count < header.Length || !header[..8].SequenceEqual(Magic) ||
                BinaryPrimitives.ReadUInt32BigEndian(header[24..]) != pageSize ||
                BinaryPrimitives.ReadUInt32BigEndian(header[20..]) != sectorSize ||
                BinaryPrimitives.ReadUInt32BigEndian(header[16..]) != originalPages ||
                offset + sectorSize > journal.Length) throw UnsafeJournal();
            var records = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
            var nonce = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
            journal.Position = offset + sectorSize;
            if (records == uint.MaxValue)
            {
                // SQLite's no-sync/safe-append form consumes the remaining file.
                if (offset != 0 || (journal.Length - journal.Position) % record.Length != 0) throw UnsafeJournal();
                records = checked((uint)((journal.Length - journal.Position) / record.Length));
            }
            if ((long)records * record.Length > journal.Length - journal.Position) throw UnsafeJournal();
            for (uint index = 0; index < records; index++)
            {
                journal.ReadExactly(record);
                var page = BinaryPrimitives.ReadUInt32BigEndian(record);
                if (page == 0 || page > originalPages || page == 0x40000000 / pageSize + 1 ||
                    !seenPages.Add(page)) throw UnsafeJournal();
                var checksum = nonce;
                for (var position = (int)pageSize - 200; position > 0; position -= 200)
                    checksum = unchecked(checksum + record[4 + position]);
                if (checksum != BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(4 + (int)pageSize)))
                    throw UnsafeJournal();
            }
            if (journal.Position == journal.Length) return true;
            offset = (journal.Position + sectorSize - 1) & ~((long)sectorSize - 1);
            if (offset >= journal.Length)
            {
                // Only zero sector padding may remain after the final record.
                while (journal.Position < journal.Length)
                    if (journal.ReadByte() != 0) throw UnsafeJournal();
                return true;
            }
        }
    }

    private static bool IsPowerOfTwoInRange(uint value, uint minimum, uint maximum) =>
        value >= minimum && value <= maximum && (value & (value - 1)) == 0;

    private static BrowserProfileStagingException UnsafeJournal() => new(
        "Open and close the selected browser so it can recover its database, then retry.",
        null, BrowserProfileStagingFailure.RecoveryRequired);
}

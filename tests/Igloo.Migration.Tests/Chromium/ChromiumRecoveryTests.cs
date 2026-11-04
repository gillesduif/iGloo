using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Igloo.Core.Abstractions;
using Igloo.Migration.Chromium;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class ChromiumRecoveryTests
{
    [Theory]
    [InlineData("magic")]
    [InlineData("checksum")]
    [InlineData("truncated-record")]
    [InlineData("page-number")]
    [InlineData("duplicate-page")]
    [InlineData("sector-size")]
    [InlineData("oversized-original")]
    [InlineData("super-journal")]
    public void Unsafe_journal_is_refused_without_source_or_external_file_changes(string corruption)
    {
        var root = Path.Join(Path.GetTempPath(), $"igloo-journal-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = Path.Join(root, "Login Data");
        var external = Path.Join(root, "external-mj123456789");
        try
        {
            ChromiumRollbackFixture.CreateHotDatabase(source);
            File.WriteAllText(external, "external file must not be accessed or changed");
            var journalPath = source + "-journal";
            var journal = File.ReadAllBytes(journalPath);
            var sectorSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(journal.AsSpan(20)));
            var pageSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(journal.AsSpan(24)));
            switch (corruption)
            {
                case "magic": journal[0] ^= 0x80; break;
                case "checksum": journal[sectorSize + 4 + pageSize] ^= 0x80; break;
                case "truncated-record": Array.Resize(ref journal, sectorSize + pageSize); break;
                case "page-number": BinaryPrimitives.WriteUInt32BigEndian(journal.AsSpan(sectorSize), 0); break;
                case "duplicate-page":
                    Assert.True(BinaryPrimitives.ReadUInt32BigEndian(journal.AsSpan(8)) >= 2);
                    BinaryPrimitives.WriteUInt32BigEndian(journal.AsSpan(sectorSize + pageSize + 8),
                        BinaryPrimitives.ReadUInt32BigEndian(journal.AsSpan(sectorSize)));
                    break;
                case "sector-size": BinaryPrimitives.WriteUInt32BigEndian(journal.AsSpan(20), 513); break;
                case "oversized-original": BinaryPrimitives.WriteUInt32BigEndian(journal.AsSpan(16), uint.MaxValue); break;
                case "super-journal":
                    var name = Encoding.UTF8.GetBytes(external);
                    var footer = new byte[name.Length + 20];
                    BinaryPrimitives.WriteUInt32BigEndian(footer, 0x40000000 / (uint)pageSize + 1);
                    name.CopyTo(footer, 4);
                    BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(4 + name.Length), (uint)name.Length);
                    uint checksum = 0;
                    foreach (var value in name) checksum = unchecked(checksum + (uint)(sbyte)value);
                    BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(8 + name.Length), checksum);
                    Convert.FromHexString("D9D505F920A163D7").CopyTo(footer, 12 + name.Length);
                    journal = [.. journal, .. footer];
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(corruption));
            }
            File.WriteAllBytes(journalPath, journal);
            var mainHash = SHA256.HashData(File.ReadAllBytes(source));
            var journalHash = SHA256.HashData(journal);
            var externalHash = SHA256.HashData(File.ReadAllBytes(external));
            var originalEntries = Directory.GetFiles(root);
            using (var denyExternalAccess = new FileStream(external, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var error = Assert.Throws<BrowserProfileStagingException>(() => LoginDataReader.Read(source));
                Assert.Equal(BrowserProfileStagingFailure.RecoveryRequired, error.Reason);
                Assert.Null(error.InnerException); // rejected by validation before opening SQLite on the private copy
            }
            Assert.Equal(mainHash, SHA256.HashData(File.ReadAllBytes(source)));
            Assert.Equal(journalHash, SHA256.HashData(File.ReadAllBytes(journalPath)));
            Assert.Equal(externalHash, SHA256.HashData(File.ReadAllBytes(external)));
            Assert.Equal(originalEntries, Directory.GetFiles(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Zero_record_header_does_not_bypass_validation_of_following_segments(bool corruptFollowingSegment)
    {
        var root = Path.Join(Path.GetTempPath(), $"igloo-journal-segments-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = Path.Join(root, "Login Data");
        try
        {
            ChromiumRollbackFixture.CreateHotDatabase(source);
            var journalPath = source + "-journal";
            var original = File.ReadAllBytes(journalPath);
            var sectorSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(20)));
            var pageSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(24)));
            var emptyHeader = original[..sectorSize];
            BinaryPrimitives.WriteUInt32BigEndian(emptyHeader.AsSpan(8), 0);
            byte[] journal = [.. emptyHeader, .. original];
            if (corruptFollowingSegment) journal[2 * sectorSize + 4 + pageSize] ^= 0x80;
            File.WriteAllBytes(journalPath, journal);
            var mainHash = SHA256.HashData(File.ReadAllBytes(source));
            if (corruptFollowingSegment)
            {
                var error = Assert.Throws<BrowserProfileStagingException>(() => LoginDataReader.Read(source));
                Assert.Equal(BrowserProfileStagingFailure.RecoveryRequired, error.Reason);
            }
            else
            {
                Assert.Equal("alice", Assert.Single(LoginDataReader.Read(source)).Username);
            }
            Assert.Equal(mainHash, SHA256.HashData(File.ReadAllBytes(source)));
            Assert.Equal(SHA256.HashData(journal), SHA256.HashData(File.ReadAllBytes(journalPath)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

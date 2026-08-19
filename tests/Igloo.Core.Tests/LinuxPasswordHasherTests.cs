using CryptSharp;
using FluentAssertions;
using Igloo.Core.Services;
using Xunit;

namespace Igloo.Core.Tests;

public class LinuxPasswordHasherTests
{
    // The published vectors from Ulrich Drepper's SHA-crypt specification. A wrong hash
    // locks the user out of the machine they just migrated to, so the implementation is
    // pinned against the spec rather than trusted.
    [Theory]
    [InlineData("$6$saltstring", "Hello world!",
        "$6$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1")]
    [InlineData("$6$rounds=10000$saltstringsaltstring", "Hello world!",
        "$6$rounds=10000$saltstringsaltst$OW1/O6BYHV6BcXZu8QVeXbDWra3Oeqh0sbHbbMCVNSnCM/UrjmM0Dp8vOuZeHBy/YTBmSK6H9qs/y3RnOaw5v.")]
    [InlineData("$6$rounds=5000$toolongsaltstring", "This is just a test",
        "$6$rounds=5000$toolongsaltstrin$lQ8jolhgVRVhY4b5pZKaysCLi0QBxGoNeKQzQ3glMhwllF7oGDZxUhx1yxdYcz/e1JSbq3y6JMxxl8audkUEm0")]
    [InlineData("$6$rounds=1400$anotherlongsaltstring",
        "a very much longer text to encrypt.  This one even stretches over morethan one line.",
        "$6$rounds=1400$anotherlongsalts$POfYwTEok97VWcjxIiSOjiykti.o/pQs.wPvMxQ6Fm7I6IoYN3CmLs66x9t0oSwbtEW7o7UmJEiDwGqd8p4ur1")]
    [InlineData("$6$rounds=77777$short", "we have a short salt string but not a short password",
        "$6$rounds=77777$short$WuQyW2YR.hBNpjjRhpYD/ifIw05xdfeEyQoMxIXbkvr0gge1a1x3yRULJ5CCaUeOxFmtlcGZelFl5CxtgfiAc0")]
    [InlineData("$6$rounds=123456$asaltof16chars..", "a short string",
        "$6$rounds=123456$asaltof16chars..$BtCwjqMJGx5hrJhZywWvt0RLE8uZ4oPwcelCjmw2kSYu.Ec6ycULevoBK25fs2xXgMNrCzIMVcgEJAstJeonj1")]
    [InlineData("$6$rounds=10$roundstoolow", "the minimum number is still observed",
        "$6$rounds=1000$roundstoolow$kUMsbe306n21p9R.FRkW3IGn.S9NPN0x50YhH1xhLsPuWGsUSklZt58jaTfF4ZEQpyUNGc0dqbpBYYBaHHrsX.")]
    public void Matches_the_specification_vectors(string salt, string password, string expected)
    {
        Crypter.Sha512.Crypt(password, salt).Should().Be(expected);
    }

    [Fact]
    public void Produces_a_sha512_crypt_string_that_verifies()
    {
        var hash = LinuxPasswordHasher.Sha512Crypt("correct horse battery staple");

        hash.Should().StartWith("$6$");
        Crypter.CheckPassword("correct horse battery staple", hash).Should().BeTrue();
        Crypter.CheckPassword("wrong password", hash).Should().BeFalse();
    }

    [Fact]
    public void Salts_every_call_separately()
    {
        var a = LinuxPasswordHasher.Sha512Crypt("same password");
        var b = LinuxPasswordHasher.Sha512Crypt("same password");

        a.Should().NotBe(b, "an unsalted hash would leak that two accounts share a password");
    }

    [Fact]
    public void Keeps_the_salt_within_the_16_characters_glibc_reads()
    {
        var hash = LinuxPasswordHasher.Sha512Crypt("x")!;

        // $6$rounds=<n>$<salt>$<checksum>
        hash.Split('$')[3].Should().HaveLength(16);
    }

    [Fact]
    public void Asks_for_more_rounds_than_the_glibc_default()
    {
        var hash = LinuxPasswordHasher.Sha512Crypt("x")!;

        hash.Should().StartWith("$6$rounds=200000$",
            "5000 rounds is glibc's default and far too cheap on current hardware");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Returns_null_when_there_is_no_password(string? password)
    {
        LinuxPasswordHasher.Sha512Crypt(password).Should().BeNull();
    }

    [Fact]
    public void Handles_passwords_the_shell_would_choke_on()
    {
        const string awkward = "p@ss'w\"o$rd\\`|;&#";
        var hash = LinuxPasswordHasher.Sha512Crypt(awkward)!;

        Crypter.CheckPassword(awkward, hash).Should().BeTrue();
        hash.Should().MatchRegex(@"^\$6\$rounds=\d+\$[./A-Za-z0-9]{16}\$[./A-Za-z0-9]+$",
            "the hash must be safe to drop into a preseed or kickstart line verbatim");
    }
}

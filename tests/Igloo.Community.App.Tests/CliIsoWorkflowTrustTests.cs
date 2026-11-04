using System;
using System.IO;
using System.Security.Cryptography;
using Igloo.Command.Contracts;
using Igloo.Community.App;
using Igloo.Core.Models;
using Xunit;

namespace Igloo.Community.App.Tests;

public sealed class CliIsoWorkflowTrustTests
{
    [Theory]
    [InlineData("debian")]
    [InlineData("linuxmint-cinnamon")]
    [InlineData("fedora-kde")]
    public void SignedDistributionRejectsSuccessfulCliPayloadWithoutSignatureProof(string distroId)
    {
        var path = Path.GetTempFileName();
        try
        {
            var contents = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(path, contents);
            var digest = Convert.ToHexString(SHA256.HashData(contents));
            var distro = Distro(distroId, digest, signatureDeclared: true);
            var acquired = new IsoAcquiredV2(distroId, path, digest, true, false, contents.Length);

            Assert.Throws<InvalidDataException>(() => CliIsoWorkflow.ValidateAcquired(distro, acquired));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SignatureRequirementMatchesSelectedManifest(bool signatureDeclared)
    {
        var path = Path.GetTempFileName();
        try
        {
            var contents = new byte[] { 5, 6, 7, 8 };
            File.WriteAllBytes(path, contents);
            var digest = Convert.ToHexString(SHA256.HashData(contents));
            var distro = Distro("fixture", digest, signatureDeclared);
            var acquired = new IsoAcquiredV2(distro.Id, path, digest, true,
                signatureDeclared, contents.Length);

            var verified = CliIsoWorkflow.ValidateAcquired(distro, acquired);

            Assert.Equal(signatureDeclared, verified.GpgVerified);
            Assert.Equal(path, verified.LocalPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static DistroManifest Distro(string id, string digest, bool signatureDeclared) => new()
    {
        Id = id,
        DisplayName = id,
        Description = "Fixture",
        Iso = new DistroIsoSpec
        {
            DownloadUrl = new Uri("https://example.test/fixture.iso"),
            Sha256 = digest,
            GpgSignatureUrl = signatureDeclared ? new Uri("https://example.test/CHECKSUM") : null,
            GpgKeyUrl = signatureDeclared ? new Uri("https://example.test/key") : null,
            GpgKeyFingerprint = signatureDeclared ? new string('A', 40) : null,
        },
    };
}

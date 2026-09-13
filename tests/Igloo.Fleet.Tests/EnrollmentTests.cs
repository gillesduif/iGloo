using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Igloo.Fleet.Contracts;
using Igloo.Fleet.Domain;
using Igloo.Fleet.Persistence;
using Igloo.Fleet.Server;
using Xunit;

namespace Igloo.Fleet.Tests;

public sealed class EnrollmentTests
{
    [Fact]
    public void TokenLifecycleAndCertificateMappingSurviveRestart()
    {
        using var fixture = new PlanningFixture();
        using var root = FleetCertificateAuthority.CreateRoot();
        using var ca = new FleetCertificateAuthority(root);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=untrusted-hostname", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var token = fixture.Service.CreateToken(10, "operator");
        Assert.Equal(64, token.Token.Length);
        var enrolled = fixture.Service.Enroll(token.Token, id => ca.Issue(request.CreateSigningRequestPem(), id));
        using var certificate = X509Certificate2.CreateFromPem(enrolled.CertificatePem);
        Assert.True(ca.Validate(certificate));
        Assert.DoesNotContain("untrusted-hostname", certificate.Subject, StringComparison.Ordinal);
        var hash = FleetCertificateAuthority.Hash(certificate);
        Assert.Equal(enrolled.Identity, fixture.Service.Authenticate(hash).Identity);
        Assert.Equal(FleetErrorCode.EnrollmentTokenConsumed,
            Assert.Throws<PlanningException>(() => fixture.Service.Enroll(token.Token, id => ca.Issue(request.CreateSigningRequestPem(), id))).Code);
        var restarted = new PlanningService(new SqlitePlanningStore(fixture.Path), fixture.Clock);
        Assert.Equal(enrolled.Identity, restarted.Authenticate(hash).Identity);
        restarted.SetTrust(enrolled.Identity.DeviceId, AgentTrustStatus.Disabled, "operator");
        Assert.Equal(FleetErrorCode.AgentDisabled, Assert.Throws<PlanningException>(() => restarted.Authenticate(hash)).Code);
        restarted.SetTrust(enrolled.Identity.DeviceId, AgentTrustStatus.Active, "operator");
        Assert.Equal(enrolled.Identity, restarted.Authenticate(hash).Identity);
        restarted.SetTrust(enrolled.Identity.DeviceId, AgentTrustStatus.Revoked, "operator");
        Assert.Equal(FleetErrorCode.CertificateRevoked, Assert.Throws<PlanningException>(() => restarted.Authenticate(hash)).Code);
        Assert.DoesNotContain(token.Token, File.ReadAllText(fixture.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidExpiredRevokedTokensAndIssuerFailureAreSafe()
    {
        using var fixture = new PlanningFixture();
        IssuedAgentCertificate Issue(DeviceIdentity _) => new("public certificate", Guid.NewGuid().ToString(), fixture.Clock.GetUtcNow().AddDays(1));
        Assert.Equal(FleetErrorCode.EnrollmentTokenInvalid,
            Assert.Throws<PlanningException>(() => fixture.Service.Enroll(new string('0', 64), Issue)).Code);
        var expired = fixture.Service.CreateToken(1, "operator");
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(FleetErrorCode.EnrollmentTokenExpired,
            Assert.Throws<PlanningException>(() => fixture.Service.Enroll(expired.Token, Issue)).Code);
        var revoked = fixture.Service.CreateToken(10, "operator");
        fixture.Service.RevokeToken(revoked.TokenId, "operator");
        Assert.Equal(FleetErrorCode.EnrollmentTokenRevoked,
            Assert.Throws<PlanningException>(() => fixture.Service.Enroll(revoked.Token, Issue)).Code);
        var valid = fixture.Service.CreateToken(10, "operator");
        Assert.Throws<CryptographicException>(() => fixture.Service.Enroll(valid.Token, _ => throw new CryptographicException()));
        Assert.NotEqual(Guid.Empty, fixture.Service.Enroll(valid.Token, Issue).Identity.DeviceId);
    }

    [Fact]
    public async Task ConcurrentTokenReplayEnrollsExactlyOnce()
    {
        using var fixture = new PlanningFixture();
        var token = fixture.Service.CreateToken(10, "operator");
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            try
            {
                fixture.Service.Enroll(token.Token, _ => new("certificate", Guid.NewGuid().ToString(), fixture.Clock.GetUtcNow().AddDays(1)));
                return true;
            }
            catch (PlanningException) { return false; }
        })));
        Assert.Single(outcomes.Where(x => x));
    }
}

internal sealed class PlanningFixture : IDisposable
{
    private readonly string _directory = System.IO.Path.Join(System.IO.Path.GetTempPath(), "igloo-planning-" + Guid.NewGuid());
    public string Path => System.IO.Path.Join(_directory, "fleet.db");
    public TestClock Clock { get; } = new();
    public PlanningService Service { get; }
    public PlanningFixture() => Service = new(new SqlitePlanningStore(Path), Clock);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan span) => _now += span;
}

using System.Collections.Immutable;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Web;

internal interface IFleetDeviceDataSource
{
    bool IsSampleData { get; }

    Task<TrustedDeviceView[]> GetTrustedDevicesAsync(CancellationToken cancellationToken = default);
}

internal sealed class LiveFleetDeviceDataSource(FleetStatusClient client) : IFleetDeviceDataSource
{
    public bool IsSampleData => false;

    public async Task<TrustedDeviceView[]> GetTrustedDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        return await client.GetTrustedDevicesAsync(cancellationToken).ConfigureAwait(false) ?? [];
    }
}

internal sealed class SampleFleetDeviceDataSource : IFleetDeviceDataSource
{
    private static readonly TrustedDeviceView[] Devices =
    [
        CreateDevice(
            "10000000-0000-0000-0000-000000000001",
            "20000000-0000-0000-0000-000000000001",
            "30000000-0000-0000-0000-000000000001",
            AgentTrustStatus.Active,
            new DateTimeOffset(2026, 9, 23, 16, 42, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 8, 31, 0, 0, 0, TimeSpan.Zero),
            "1.4.0",
            "0.2-alpha",
            PlanningCapability.PreflightV1,
            PlanningCapability.MigrationDryRunV1,
            PlanningCapability.EvidenceV1,
            PlanningCapability.ProfileSchemaV1),

        CreateDevice(
            "10000000-0000-0000-0000-000000000002",
            "20000000-0000-0000-0000-000000000002",
            "30000000-0000-0000-0000-000000000002",
            AgentTrustStatus.Active,
            new DateTimeOffset(2026, 9, 23, 16, 37, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 4, 15, 0, 0, 0, TimeSpan.Zero),
            "1.4.0",
            "0.2-alpha",
            PlanningCapability.PreflightV1,
            PlanningCapability.EvidenceV1),

        CreateDevice(
            "10000000-0000-0000-0000-000000000003",
            "20000000-0000-0000-0000-000000000003",
            "30000000-0000-0000-0000-000000000003",
            AgentTrustStatus.Active,
            new DateTimeOffset(2026, 9, 23, 15, 58, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero),
            "1.3.2",
            "0.2-alpha",
            PlanningCapability.PreflightV1,
            PlanningCapability.MigrationDryRunV1),

        CreateDevice(
            "10000000-0000-0000-0000-000000000004",
            "20000000-0000-0000-0000-000000000004",
            "30000000-0000-0000-0000-000000000004",
            AgentTrustStatus.Disabled,
            new DateTimeOffset(2026, 9, 20, 8, 12, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 20, 0, 0, 0, TimeSpan.Zero),
            "1.3.2",
            "0.2-alpha",
            PlanningCapability.PreflightV1),

        CreateDevice(
            "10000000-0000-0000-0000-000000000005",
            "20000000-0000-0000-0000-000000000005",
            "30000000-0000-0000-0000-000000000005",
            AgentTrustStatus.Revoked,
            new DateTimeOffset(2026, 8, 30, 19, 5, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            "1.2.0",
            "0.2-alpha",
            PlanningCapability.PreflightV1),

        CreateDeviceWithoutHeartbeat(
            "10000000-0000-0000-0000-000000000006",
            "20000000-0000-0000-0000-000000000006",
            "30000000-0000-0000-0000-000000000006",
            AgentTrustStatus.Active,
            new DateTimeOffset(2027, 1, 12, 0, 0, 0, TimeSpan.Zero)),

        CreateDevice(
            "10000000-0000-0000-0000-000000000007",
            "20000000-0000-0000-0000-000000000007",
            "30000000-0000-0000-0000-000000000007",
            AgentTrustStatus.Active,
            new DateTimeOffset(2026, 9, 22, 6, 45, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
            "1.4.0",
            "0.2-alpha"),

        CreateDevice(
            "10000000-0000-0000-0000-000000000008",
            "20000000-0000-0000-0000-000000000008",
            "30000000-0000-0000-0000-000000000008",
            AgentTrustStatus.Disabled,
            new DateTimeOffset(2026, 7, 10, 10, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            "1.1.5",
            "0.1-alpha",
            PlanningCapability.PreflightV1)
    ];

    public bool IsSampleData => true;

    public Task<TrustedDeviceView[]> GetTrustedDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult((TrustedDeviceView[])Devices.Clone());
    }

    private static TrustedDeviceView CreateDevice(
        string deviceId,
        string agentId,
        string enrollmentId,
        AgentTrustStatus status,
        DateTimeOffset lastHeartbeatUtc,
        DateTimeOffset certificateExpiresAtUtc,
        string agentVersion,
        string iglooVersion,
        params PlanningCapability[] capabilities)
    {
        return new TrustedDeviceView(
            new DeviceIdentity(new Guid(deviceId), new Guid(agentId)),
            new Guid(enrollmentId),
            CertificateHash(deviceId),
            certificateExpiresAtUtc,
            status,
            lastHeartbeatUtc,
            new TrustedHeartbeat(
                PlanningProtocol.Version,
                agentVersion,
                iglooVersion,
                capabilities.ToImmutableArray()));
    }

    private static TrustedDeviceView CreateDeviceWithoutHeartbeat(
        string deviceId,
        string agentId,
        string enrollmentId,
        AgentTrustStatus status,
        DateTimeOffset certificateExpiresAtUtc)
    {
        return new TrustedDeviceView(
            new DeviceIdentity(new Guid(deviceId), new Guid(agentId)),
            new Guid(enrollmentId),
            CertificateHash(deviceId),
            certificateExpiresAtUtc,
            status,
            null,
            null);
    }

    private static string CertificateHash(string seed)
    {
        var compact = seed.Replace("-", string.Empty, StringComparison.Ordinal);
        return ("A1B2C3D4E5F60718293A4B5C6D7E8F90" + compact).PadRight(64, '0')[..64];
    }
}
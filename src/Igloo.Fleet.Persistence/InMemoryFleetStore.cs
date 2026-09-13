using Igloo.Fleet.Contracts;
using Igloo.Fleet.Domain;

namespace Igloo.Fleet.Persistence;

/// <summary>Bounded, process-local development storage. Restarting the server loses evidence.</summary>
public sealed class InMemoryFleetStore : IDeviceRepository, IAssessmentRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, RegisteredDevice> _devices = [];
    private readonly Dictionary<Guid, MigrationEvidence> _assessments = [];
    private const int Capacity = 1000;

    public bool Register(AgentRegistrationRequest registration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate)
        {
            if (_devices.TryGetValue(registration.Identity.DeviceId, out var existing))
            {
                if (existing.Registration.Identity != registration.Identity)
                    return false;
            }
            else if (_devices.Count >= Capacity ||
                _devices.Values.Any(d => d.Registration.Identity.AgentId == registration.Identity.AgentId))
                return false;
            _devices[registration.Identity.DeviceId] = new(registration, now);
            return true;
        }
    }

    public RegisteredDevice? Find(Guid deviceId)
    {
        lock (_gate)
            return _devices.GetValueOrDefault(deviceId);
    }

    public bool Heartbeat(AgentHeartbeat heartbeat, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        lock (_gate)
        {
            if (!_devices.TryGetValue(heartbeat.Identity.DeviceId, out var device) ||
                device.Registration.Identity != heartbeat.Identity)
                return false;
            _devices[heartbeat.Identity.DeviceId] = device with
            {
                LastHeartbeat = now,
                Registration = device.Registration with { Capabilities = heartbeat.Capabilities },
            };
            return true;
        }
    }

    public bool Add(MigrationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        lock (_gate)
            return _assessments.Count < Capacity && _assessments.TryAdd(evidence.Assessment.AssessmentId, evidence);
    }

    MigrationEvidence? IAssessmentRepository.Find(Guid assessmentId)
    {
        lock (_gate)
            return _assessments.GetValueOrDefault(assessmentId);
    }
}

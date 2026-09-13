using System.Text.Json;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Agent;

/// <summary>Replace with certificate-backed enrollment before production deployment.</summary>
public interface IDeviceIdentityProvider
{
    DeviceIdentity GetIdentity();
}

public sealed class DevelopmentDeviceIdentityProvider(string path) : IDeviceIdentityProvider
{
    public DeviceIdentity GetIdentity()
    {
        if (File.Exists(path))
        {
            var stored = JsonSerializer.Deserialize<DeviceIdentity>(File.ReadAllText(path));
            if (stored is null || stored.DeviceId == Guid.Empty || stored.AgentId == Guid.Empty)
                throw new InvalidDataException("Invalid development identity; restore the identity file.");
            return stored;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var identity = new DeviceIdentity(Guid.NewGuid(), Guid.NewGuid());
        // Never overwrite a concurrent writer. A partial file fails closed on the next run.
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, identity);
        stream.Flush(flushToDisk: true);
        return identity;
    }
}

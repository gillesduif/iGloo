using System.Runtime.CompilerServices;
using Igloo.Core.Safety;

namespace Igloo.TestData;

internal static class HostProtectionTestDefaults
{
    // The suite gives the same result on a build machine that carries the protected-host marker.
    [ModuleInitializer]
    internal static void Apply() => HostProtection.UseForTestProcess(HostProtectionState.NotProtected);
}

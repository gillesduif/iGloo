using Igloo.Core.Abstractions;

namespace Igloo.Preflight.CommunityPreparation;

// The immutable model is not a supported installer recipe. Keep this gate BEFORE ISO mounting,
// shell suppression, label-based leftover cleanup, shrink, create, format and staging.
internal static class DedicatedEspPreparationSupport
{
    internal static Observation<bool> Production => Observations.Failure<bool>(ObservationAvailability.Unsupported,
        "DedicatedEspInstallerBindingNotImplemented");

    internal static void RequireSupported()
    {
        var support = Production;
        if (support.Availability != ObservationAvailability.Available || !support.Value)
            throw new NotSupportedException("Dedicated iGloo/Linux ESP preparation is blocked: supported exact installer ESP binding, " +
                "owned partition acquisition and stable payload/loader discovery are not implemented. No preparation changes were started.");
    }
}

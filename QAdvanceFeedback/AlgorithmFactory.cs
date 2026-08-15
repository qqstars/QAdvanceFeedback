using System.Reflection;
using QAdvanceFeedback.Core;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Resolves the withheld <c>Private\QAdvanceFeedback\</c> implementations of Layer 2
    /// (<see cref="ITelemetryAdapter"/>) and Layer 3 (<see cref="ILegacyWheelLockSlipEngine"/>) at
    /// runtime, by looking for each one's well-known type name IN THIS ASSEMBLY via
    /// <see cref="PrivateTypeResolver"/> - never a compile-time reference to the concrete type.
    /// <para/>
    /// THIS IS THE WHOLE POINT of the Layer 2/3 split: <c>..\Private\*.cs</c> is gitignored (see
    /// <c>..\Private\.gitignore</c>/<c>..\Private\README.md</c>), so a fresh open-source clone simply
    /// does not have <c>Private\QAdvanceFeedback\SimHubTelemetryAdapter.cs</c>/
    /// <c>Private\QAdvanceFeedback\LegacyWheelLockSlipEngine.cs</c> on disk. This project's own csproj
    /// has an explicit, wildcard <c>&lt;Compile Include&gt;</c> for that folder (needed because it now
    /// lives OUTSIDE this project's own directory, so the SDK's default glob no longer reaches it) -
    /// that wildcard simply evaluates to nothing when the folder is absent, so on that clone those
    /// two source files simply do not exist - if anything OUTSIDE <c>Private\</c> referenced
    /// <c>SimHubTelemetryAdapter</c> or <c>LegacyWheelLockSlipEngine</c> BY NAME (a field type, a
    /// `new SimHubTelemetryAdapter()`, anything the compiler has to resolve at compile time), the
    /// build would fail with "type or namespace not found" the moment those files are absent. Routing
    /// through <c>Assembly.GetType("QAdvanceFeedback.SimHubTelemetryAdapter")</c> - a plain string,
    /// resolved at RUN time - is what lets <c>QAdvanceFeedback.cs</c> compile identically whether or
    /// not <c>Private\</c> exists; the only difference at run time is which branch
    /// <see cref="PrivateTypeResolver.CreateOrFallback{T}"/> takes.
    /// <para/>
    /// Each fallback is logged exactly ONCE per plugin instance (not once per frame - this factory is
    /// only even called from <c>QAdvanceFeedback</c>'s field initialisers, so "once" is automatic here,
    /// but the guard is kept explicit in case that ever changes) so a user who never supplies a
    /// Private implementation gets one clear explanation in the SimHub log, not a flood.
    /// </summary>
    internal static class AlgorithmFactory
    {
        private const string TelemetryAdapterTypeName = "QAdvanceFeedback.SimHubTelemetryAdapter";
        private const string LegacyEngineTypeName = "QAdvanceFeedback.Core.LegacyWheelLockSlipEngine";

        public static ITelemetryAdapter CreateTelemetryAdapter()
        {
            return PrivateTypeResolver.CreateOrFallback<ITelemetryAdapter>(
                CurrentAssembly, TelemetryAdapterTypeName,
                () =>
                {
                    LogMissing("telemetry adapter", TelemetryAdapterTypeName);
                    return new InertTelemetryAdapter();
                });
        }

        public static ILegacyWheelLockSlipEngine CreateLegacyEngine()
        {
            return PrivateTypeResolver.CreateOrFallback<ILegacyWheelLockSlipEngine>(
                CurrentAssembly, LegacyEngineTypeName,
                () =>
                {
                    LogMissing("legacy wheel lock/slip algorithm", LegacyEngineTypeName);
                    return new InertLegacyWheelLockSlipEngine();
                });
        }

        private static Assembly CurrentAssembly => typeof(AlgorithmFactory).Assembly;

        private static void LogMissing(string what, string typeName)
        {
            SimHub.Logging.Current.Warn(
                "QAdvanceFeedback: no Private implementation of the " + what + " (" + typeName + ") was " +
                "found in this build - this channel will be inert (no wheel lock/slip output) until a " +
                "QAdvanceFeedback\\Private\\ implementation is supplied. See Private\\README.md.");
        }
    }
}

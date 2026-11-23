using System;
using QAdvanceFeedback.Core.MotorsExport;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Core.ShakeItEffects
{
    /// <summary>Where one channel (Wheel Lock or Wheel Slip) reads its four Layer-4 inputs from this frame.</summary>
    public enum ChannelSourceRoute
    {
        /// <summary>The channel's own configured per-wheel sources (Raw, Viper, Custom).</summary>
        Configured = 0,
        /// <summary>ShakeIt's built-in values through our native effect, learned under the ShakeIt identity.</summary>
        ShakeItBuiltIn = 1,
        /// <summary>The channel's own Layer-3 Raw properties, learned under the Raw identity.</summary>
        RawFallback = 2
    }

    /// <summary>One channel's resolved inputs: the route, the four values and the learning key.</summary>
    public struct ChannelSourceResolution
    {
        public ChannelSourceResolution(ChannelSourceRoute route, Corners values, string identity)
        {
            Route = route;
            Values = values;
            Identity = identity;
        }

        public ChannelSourceRoute Route { get; }
        public Corners Values { get; }
        public string Identity { get; }
    }

    /// <summary>
    /// The per-channel source decision (docs\shakeit-source-design.md §4). Pure; the plugin supplies the
    /// readers and the configured / Raw identities.
    /// <list type="bullet">
    /// <item>A blocked Viper channel reads Raw (unchanged behaviour).</item>
    /// <item>A ShakeIt channel reads the built-in values only when its state is
    /// <see cref="ShakeItSourceState.Supported"/> AND the tap is fresh, learned under the canonical
    /// <see cref="ShakeItIdentity"/> - never under whatever the channel's stored rows happen to say;
    /// otherwise (not set up, set up but idle, not supported, stale) it reads Raw for all four wheels and
    /// learns under the Raw identity.</item>
    /// <item>Decided per CHANNEL, never per wheel, so ShakeIt learning never receives a Raw value.</item>
    /// <item>Every other mode reads its configured sources under the configured identity.</item>
    /// </list>
    /// </summary>
    public static class ShakeItSourceResolution
    {
        // The legacy export property names (spec §5): today's ShakeIt identity, byte for byte.
        private static readonly string LockShakeItIdentity = ComputeShakeItIdentity(true);
        private static readonly string SlipShakeItIdentity = ComputeShakeItIdentity(false);

        /// <summary>
        /// The learning identity of the ShakeIt Built-in source: the four legacy export property names as
        /// Plain sources, exactly as the ShakeIt preset's rows produce it, so everything learned under it so
        /// far carries over.
        /// <para/>
        /// THE ONE SEAM FOR THE ALGORITHM FINGERPRINT (spec §6, Task D): <paramref name="algorithmFingerprint"/>
        /// is appended here and nowhere else; null/empty (the defaults) leaves the identity unchanged.
        /// </summary>
        public static string ShakeItIdentity(bool isLockChannel, string algorithmFingerprint = null)
        {
            string identity = isLockChannel ? LockShakeItIdentity : SlipShakeItIdentity;
            return string.IsNullOrEmpty(algorithmFingerprint) ? identity : identity + algorithmFingerprint;
        }

        /// <summary>
        /// Whether <paramref name="tap"/> was computed under <paramref name="current"/> - the snapshot whose
        /// fingerprint this frame's ShakeIt identity carries. False for a tap computed under any OTHER
        /// snapshot: right after a setting change (mid-session, or made at the menu) the latest values can
        /// still be the old algorithm's, up to the tap's staleness window, and learning them under the new
        /// key would put another algorithm's evidence in it. The caller then treats the tap as not fresh,
        /// so the channel reads Raw under the Raw identity for those frames. Compared by reference: the
        /// plugin reuses one snapshot instance while nothing changes. A tap that carries no snapshot
        /// matches only "no snapshot" (null).
        /// </summary>
        public static bool TapComputedUnder(ShakeItTapValues tap, ShakeItAlgorithmSnapshot current)
            => tap != null && ReferenceEquals(tap.Algorithm, current);

        public static ChannelSourceRoute Route(bool isShakeItMode, bool viperBlocked, ShakeItSourceState state, bool tapFresh)
        {
            if (viperBlocked) return ChannelSourceRoute.RawFallback;
            if (!isShakeItMode) return ChannelSourceRoute.Configured;
            return state == ShakeItSourceState.Supported && tapFresh
                ? ChannelSourceRoute.ShakeItBuiltIn
                : ChannelSourceRoute.RawFallback;
        }

        /// <summary>
        /// Whether a ShakeIt channel shows and keys its data as Raw while not driving (the settings page's
        /// "reading Raw" status): not set up, or the game was found not supported. <see cref="ShakeItSourceState.SetUpIdle"/>
        /// is not a fallback verdict - at the menu nothing is read, and claiming Raw there would re-key the
        /// page's key data to Raw while the driver configures ShakeIt (the Viper "no game" lesson).
        /// </summary>
        public static bool FallsBackToRaw(ShakeItSourceState state) =>
            state == ShakeItSourceState.NotSetUp || state == ShakeItSourceState.NotSupported;

        /// <summary>
        /// Resolves one channel. <paramref name="tap"/> is this frame's built-in values (read only on the
        /// ShakeIt route); <paramref name="readRaw"/> / <paramref name="readConfigured"/> map
        /// (<paramref name="state"/>, wheel index 0 FL, 1 FR, 2 RL, 3 RR) to that wheel's value - static
        /// delegates plus caller state, so the frame loop allocates nothing. <paramref name="configuredIdentity"/>
        /// is used only on the Configured route; the ShakeIt route always uses <see cref="ShakeItIdentity"/>.
        /// </summary>
        public static ChannelSourceResolution Resolve<TState>(ChannelSourceRoute route, bool isLockChannel, ShakeItTapValues tap,
            TState state, Func<TState, int, double> readRaw, Func<TState, int, double> readConfigured,
            string configuredIdentity, string rawIdentity, string shakeItAlgorithmFingerprint = null)
        {
            switch (route)
            {
                case ChannelSourceRoute.ShakeItBuiltIn when tap != null:
                    return new ChannelSourceResolution(route, isLockChannel ? tap.Lock : tap.Slip,
                        ShakeItIdentity(isLockChannel, shakeItAlgorithmFingerprint));
                case ChannelSourceRoute.ShakeItBuiltIn:
                case ChannelSourceRoute.RawFallback:
                    return new ChannelSourceResolution(ChannelSourceRoute.RawFallback, Read(state, readRaw), rawIdentity);
                default:
                    return new ChannelSourceResolution(route, Read(state, readConfigured), configuredIdentity);
            }
        }

        private static Corners Read<TState>(TState state, Func<TState, int, double> read) =>
            read == null ? Corners.Zero : new Corners(read(state, 0), read(state, 1), read(state, 2), read(state, 3));

        private static string ComputeShakeItIdentity(bool isLockChannel) => SourceIdentity.Compute(
            MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, MotorsExportPropertyNames.FrontLeft), "Plain",
            MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, MotorsExportPropertyNames.FrontRight), "Plain",
            MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, MotorsExportPropertyNames.RearLeft), "Plain",
            MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, MotorsExportPropertyNames.RearRight), "Plain");
    }
}

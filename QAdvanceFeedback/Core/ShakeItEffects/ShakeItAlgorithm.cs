using System;
using System.Globalization;

namespace QAdvanceFeedback.Core.ShakeItEffects
{
    /// <summary>
    /// The ShakeIt algorithm settings' shipped defaults (docs\shakeit-source-design.md §6). Verified against
    /// SimHub's own <c>WheelsLockContainer</c> / <c>WheelsSlipContainer</c> and their editors (Task D report):
    /// <list type="bullet">
    /// <item>Use legacy iRacing algorithm: on for both (the owner's export profile; SimHub's own constructor
    /// leaves it off).</item>
    /// <item>Lock sensibility: 50 (SimHub's constructor value; editor slider 0-100, tick 5, snapping).
    /// SimHub's <c>LockSensibilityEx</c> is the same backing value under a second name, not a setting.</item>
    /// <item>Lock only: on (<c>WheelsLockContainer.LoadDefaultSettings</c> sets <c>IsLock = true</c>; the
    /// editor's "Use lock only" / "Use lock and slip" radio pair).</item>
    /// <item>Mute slip while lock is active: off (SimHub's default).</item>
    /// </list>
    /// </summary>
    public static class ShakeItAlgorithmDefaults
    {
        public const bool UseLegacyIracingAlgorithm = true;
        public const double LockSensibility = 50.0;
        public const double LockSensibilityMin = 0.0;
        public const double LockSensibilityMax = 100.0;
        /// <summary>SimHub's editor slider snaps to ticks of 5.</summary>
        public const double LockSensibilityStep = 5.0;
        public const bool LockOnly = true;
        public const bool MuteWhileLockActive = false;

        /// <summary>
        /// A Lock sensibility as SimHub's editor can produce it: clamped to 0-100 and snapped to the
        /// nearest multiple of 5 (midpoint away from zero: 52.5 -&gt; 55), so a value a hair off a tick never
        /// makes a new learning key. A non-finite value is the default.
        /// </summary>
        public static double NormaliseLockSensibility(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return LockSensibility;
            double clamped = ClampMath.Clamp(value, LockSensibilityMin, LockSensibilityMax);
            return Math.Round(clamped / LockSensibilityStep, MidpointRounding.AwayFromZero) * LockSensibilityStep;
        }
    }

    /// <summary>
    /// Our pedal thresholds as the hidden containers' filters (docs\shakeit-source-design.md §6: the
    /// filters are not configurable; they follow our own thresholds). Pure.
    /// <para/>
    /// SimHub's gates (decompiled): Lock <c>Brake &gt; BrakeFilter || !caps.Brake</c>; Slip, when either filter
    /// is on, <c>(UseBrakeFilter &amp;&amp; (Brake &gt; BrakeFilter || !caps.Brake)) || (UseThrottleFilter &amp;&amp;
    /// (Throttle &gt; ThrottleFilter || !caps.Throttle))</c>, and always allowed when both are off. Ours:
    /// Lock <c>brake &gt;= LockBrakeThresholdPercent</c>; Slip <c>brake &gt;= SlipBrake || throttle &gt;= SlipThrottle</c>.
    /// Same units (0-100), direction and OR; SimHub's comparison is exclusive, ours inclusive (an edge at
    /// exactly the threshold only).
    /// <para/>
    /// ROUNDING: <see cref="MidpointRounding.AwayFromZero"/> to SimHub's whole numbers (20.5 -&gt; 21), clamped
    /// 0-100; a non-finite threshold maps to 0.
    /// <para/>
    /// THROTTLE 0 TURNS BOTH SLIP FILTERS OFF. Our throttle threshold 0 means "throttle &gt;= 0", which is
    /// always true, so our Slip gate is always open whatever the brake threshold. SimHub reaches "always
    /// allowed" only with both filters off - leaving the brake filter on would gate Slip by brake alone.
    /// </summary>
    public static class ShakeItFilterMapping
    {
        /// <summary>One threshold as a ShakeIt filter value: rounded half away from zero, clamped 0-100.</summary>
        public static int ToFilterPercent(double thresholdPercent)
        {
            if (double.IsNaN(thresholdPercent) || double.IsInfinity(thresholdPercent)) return 0;
            double rounded = Math.Round(ClampMath.Clamp(thresholdPercent, 0.0, 100.0), MidpointRounding.AwayFromZero);
            return (int)rounded;
        }

        /// <summary>Lock's brake filter (always on in SimHub): round(<paramref name="lockBrakeThresholdPercent"/>).</summary>
        public static int LockBrakeFilter(double lockBrakeThresholdPercent) => ToFilterPercent(lockBrakeThresholdPercent);

        /// <summary>Slip's throttle filter is on when the threshold is above 0.</summary>
        public static bool SlipUseThrottleFilter(double slipThrottleThresholdPercent) => slipThrottleThresholdPercent > 0.0;

        /// <summary>Slip's brake filter is on when the threshold is below 100 - and the throttle filter is on
        /// (see the class remarks: throttle 0 means always allowed).</summary>
        public static bool SlipUseBrakeFilter(double slipBrakeThresholdPercent, double slipThrottleThresholdPercent) =>
            slipBrakeThresholdPercent < 100.0 && SlipUseThrottleFilter(slipThrottleThresholdPercent);
    }

    /// <summary>
    /// The ShakeIt source's learning-key fingerprint (docs\shakeit-source-design.md §6): a pure function of
    /// one channel's ALGORITHM settings - never the pedal thresholds, which gate when, not the scale. Empty
    /// at the defaults, so the key there is today's string; otherwise the channel's full tuple, so every
    /// distinct setting combination gets a distinct key. Contains neither the learner key separator
    /// (<c>|#|</c>) nor the wheel separator (<c>~</c>), so the identity still splits into four wheels and
    /// still classifies as the ShakeIt source for its cold start.
    /// </summary>
    public static class ShakeItAlgorithmFingerprint
    {
        /// <summary>Every non-empty fingerprint starts with this; nothing else in a source identity contains it.</summary>
        public const string Marker = "#alg=";

        /// <summary>
        /// <paramref name="sourceIdentity"/> without its algorithm fingerprint (unchanged when it has none).
        /// For classifying the source TYPE (shipped defaults are per source type, not per algorithm
        /// setting); learning and manual slots stay keyed WITH the fingerprint.
        /// </summary>
        public static string BaseIdentity(string sourceIdentity)
        {
            if (string.IsNullOrEmpty(sourceIdentity)) return sourceIdentity;
            int at = sourceIdentity.IndexOf(Marker, StringComparison.Ordinal);
            return at < 0 ? sourceIdentity : sourceIdentity.Substring(0, at);
        }

        /// <summary>Lock: legacy, sensibility, lock-only. Empty at the defaults.</summary>
        public static string ForLock(bool useLegacyIracingAlgorithm, double lockSensibility, bool lockOnly)
        {
            double sensibility = ShakeItAlgorithmDefaults.NormaliseLockSensibility(lockSensibility);
            if (useLegacyIracingAlgorithm == ShakeItAlgorithmDefaults.UseLegacyIracingAlgorithm
                && sensibility == ShakeItAlgorithmDefaults.LockSensibility
                && lockOnly == ShakeItAlgorithmDefaults.LockOnly)
                return string.Empty;

            return Marker + "legacy:" + Flag(useLegacyIracingAlgorithm)
                + ",sens:" + sensibility.ToString("R", CultureInfo.InvariantCulture)
                + ",lockOnly:" + Flag(lockOnly);
        }

        /// <summary>Slip: legacy, mute (mute belongs to Slip's key only). Empty at the defaults.</summary>
        public static string ForSlip(bool useLegacyIracingAlgorithm, bool muteWhileLockActive)
        {
            if (useLegacyIracingAlgorithm == ShakeItAlgorithmDefaults.UseLegacyIracingAlgorithm
                && muteWhileLockActive == ShakeItAlgorithmDefaults.MuteWhileLockActive)
                return string.Empty;

            return Marker + "legacy:" + Flag(useLegacyIracingAlgorithm) + ",mute:" + Flag(muteWhileLockActive);
        }

        private static string Flag(bool value) => value ? "1" : "0";
    }

    /// <summary>
    /// "Mute slip while lock is active", emulated by us (docs\shakeit-source-design.md §6). SimHub's own
    /// implementation (<c>WheelSlipEffect.IsMuted</c>) walks the slip container's <c>ParentProfile</c> for a
    /// Wheels Lock container with ANY wheel's output active in the last 100 ms, and then mutes ALL FOUR slip
    /// wheels; the hidden containers have no parent profile. Emulated per frame: when on, Slip reads 0 on
    /// any frame where any wheel of the Lock built-in value is above 0 (no 100 ms hold). Pure.
    /// </summary>
    public static class ShakeItMuteEmulation
    {
        /// <summary>Whether Slip is muted this frame.</summary>
        public static bool IsMuted(bool muteWhileLockActive, Corners lockValues) =>
            muteWhileLockActive
            && (lockValues.FrontLeft > 0.0 || lockValues.FrontRight > 0.0
                || lockValues.RearLeft > 0.0 || lockValues.RearRight > 0.0);

        /// <summary>The Slip values after muting.</summary>
        public static Corners Apply(bool muteWhileLockActive, Corners lockValues, Corners slipValues) =>
            IsMuted(muteWhileLockActive, lockValues) ? Corners.Zero : slipValues;
    }

    /// <summary>The plain inputs of a <see cref="ShakeItAlgorithmSnapshot"/>: both channels' algorithm
    /// settings and our pedal thresholds. A value type, so comparing a frame's inputs allocates nothing.</summary>
    public readonly struct ShakeItAlgorithmInputs : IEquatable<ShakeItAlgorithmInputs>
    {
        public ShakeItAlgorithmInputs(
            bool lockUseLegacyIracingAlgorithm, double lockSensibility, bool lockOnly, double lockBrakeThresholdPercent,
            bool slipUseLegacyIracingAlgorithm, bool slipMuteWhileLockActive,
            double slipBrakeThresholdPercent, double slipThrottleThresholdPercent)
        {
            LockUseLegacyIracingAlgorithm = lockUseLegacyIracingAlgorithm;
            LockSensibility = ShakeItAlgorithmDefaults.NormaliseLockSensibility(lockSensibility);
            LockOnly = lockOnly;
            LockBrakeThresholdPercent = lockBrakeThresholdPercent;
            SlipUseLegacyIracingAlgorithm = slipUseLegacyIracingAlgorithm;
            SlipMuteWhileLockActive = slipMuteWhileLockActive;
            SlipBrakeThresholdPercent = slipBrakeThresholdPercent;
            SlipThrottleThresholdPercent = slipThrottleThresholdPercent;
        }

        public bool LockUseLegacyIracingAlgorithm { get; }
        public double LockSensibility { get; }
        public bool LockOnly { get; }
        public double LockBrakeThresholdPercent { get; }
        public bool SlipUseLegacyIracingAlgorithm { get; }
        public bool SlipMuteWhileLockActive { get; }
        public double SlipBrakeThresholdPercent { get; }
        public double SlipThrottleThresholdPercent { get; }

        /// <summary>The shipped algorithm defaults with the shipped thresholds (Lock brake 20, Slip brake 100,
        /// Slip throttle 40 - <see cref="LegacyThresholds.Defaults"/>).</summary>
        public static ShakeItAlgorithmInputs Defaults => new ShakeItAlgorithmInputs(
            ShakeItAlgorithmDefaults.UseLegacyIracingAlgorithm, ShakeItAlgorithmDefaults.LockSensibility,
            ShakeItAlgorithmDefaults.LockOnly, LegacyThresholds.Defaults.LockBrakeThresholdPercent,
            ShakeItAlgorithmDefaults.UseLegacyIracingAlgorithm, ShakeItAlgorithmDefaults.MuteWhileLockActive,
            LegacyThresholds.Defaults.SlipBrakeThresholdPercent, LegacyThresholds.Defaults.SlipThrottleThresholdPercent);

        public bool Equals(ShakeItAlgorithmInputs other) =>
            LockUseLegacyIracingAlgorithm == other.LockUseLegacyIracingAlgorithm
            && LockSensibility.Equals(other.LockSensibility)
            && LockOnly == other.LockOnly
            && LockBrakeThresholdPercent.Equals(other.LockBrakeThresholdPercent)
            && SlipUseLegacyIracingAlgorithm == other.SlipUseLegacyIracingAlgorithm
            && SlipMuteWhileLockActive == other.SlipMuteWhileLockActive
            && SlipBrakeThresholdPercent.Equals(other.SlipBrakeThresholdPercent)
            && SlipThrottleThresholdPercent.Equals(other.SlipThrottleThresholdPercent);

        public override bool Equals(object obj) => obj is ShakeItAlgorithmInputs other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = LockSensibility.GetHashCode();
                h = h * 31 + LockBrakeThresholdPercent.GetHashCode();
                h = h * 31 + SlipBrakeThresholdPercent.GetHashCode();
                h = h * 31 + SlipThrottleThresholdPercent.GetHashCode();
                h = h * 31 + (LockUseLegacyIracingAlgorithm ? 1 : 0) + (LockOnly ? 2 : 0)
                    + (SlipUseLegacyIracingAlgorithm ? 4 : 0) + (SlipMuteWhileLockActive ? 8 : 0);
                return h;
            }
        }
    }

    /// <summary>
    /// Everything the hidden WheelsLock/WheelsSlip containers are configured from, plus the Slip mute and
    /// both channels' learning-key fingerprints - immutable, built on the data thread and handed to
    /// ShakeIt's thread (docs\shakeit-source-design.md §7). <see cref="Reuse"/> returns the previous instance
    /// while nothing changed, so an unchanged frame allocates nothing and the tap reconfigures only on a
    /// new reference.
    /// </summary>
    public sealed class ShakeItAlgorithmSnapshot
    {
        public static readonly ShakeItAlgorithmSnapshot Defaults = new ShakeItAlgorithmSnapshot(ShakeItAlgorithmInputs.Defaults);

        public ShakeItAlgorithmSnapshot(ShakeItAlgorithmInputs inputs)
        {
            Inputs = inputs;
            LockBrakeFilter = ShakeItFilterMapping.LockBrakeFilter(inputs.LockBrakeThresholdPercent);
            SlipUseBrakeFilter = ShakeItFilterMapping.SlipUseBrakeFilter(inputs.SlipBrakeThresholdPercent, inputs.SlipThrottleThresholdPercent);
            SlipBrakeFilter = ShakeItFilterMapping.ToFilterPercent(inputs.SlipBrakeThresholdPercent);
            SlipUseThrottleFilter = ShakeItFilterMapping.SlipUseThrottleFilter(inputs.SlipThrottleThresholdPercent);
            SlipThrottleFilter = ShakeItFilterMapping.ToFilterPercent(inputs.SlipThrottleThresholdPercent);
            LockFingerprint = ShakeItAlgorithmFingerprint.ForLock(inputs.LockUseLegacyIracingAlgorithm, inputs.LockSensibility, inputs.LockOnly);
            SlipFingerprint = ShakeItAlgorithmFingerprint.ForSlip(inputs.SlipUseLegacyIracingAlgorithm, inputs.SlipMuteWhileLockActive);
        }

        public ShakeItAlgorithmInputs Inputs { get; }

        public bool LockUseLegacyIracingAlgorithm => Inputs.LockUseLegacyIracingAlgorithm;
        public double LockSensibility => Inputs.LockSensibility;
        /// <summary>SimHub's <c>WheelsLockContainer.IsLock</c>.</summary>
        public bool LockOnly => Inputs.LockOnly;
        public int LockBrakeFilter { get; }

        public bool SlipUseLegacyIracingAlgorithm => Inputs.SlipUseLegacyIracingAlgorithm;
        /// <summary>Emulated by <see cref="ShakeItMuteEmulation"/>; SimHub's own flag stays off on the hidden copy.</summary>
        public bool SlipMuteWhileLockActive => Inputs.SlipMuteWhileLockActive;
        public bool SlipUseBrakeFilter { get; }
        public int SlipBrakeFilter { get; }
        public bool SlipUseThrottleFilter { get; }
        public int SlipThrottleFilter { get; }

        /// <summary>Lock's learning-key fingerprint (empty at the defaults).</summary>
        public string LockFingerprint { get; }
        /// <summary>Slip's learning-key fingerprint (empty at the defaults).</summary>
        public string SlipFingerprint { get; }

        public string Fingerprint(bool isLockChannel) => isLockChannel ? LockFingerprint : SlipFingerprint;

        /// <summary><paramref name="previous"/> when it was built from the same inputs, else a new snapshot.</summary>
        public static ShakeItAlgorithmSnapshot Reuse(ShakeItAlgorithmSnapshot previous, ShakeItAlgorithmInputs inputs) =>
            previous != null && previous.Inputs.Equals(inputs) ? previous : new ShakeItAlgorithmSnapshot(inputs);
    }
}

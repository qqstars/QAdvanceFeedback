namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// Owner-requested, driver-configurable pedal-pressed thresholds that gate Layer 3's legacy
    /// lock/slip detection (<see cref="LegacySlipAlgorithm"/>, withheld under <c>Private\</c>).
    /// <para/>
    /// THIS IS A DELIBERATE, OWNER-REQUESTED DEVIATION from SimHub's own hard-coded gates
    /// (<c>Brake &gt; 20</c>, <c>Throttle &gt; 40</c>, confirmed by decompiling
    /// <c>WheelSlipEffect.GetRpmSpeedSlipLegacy</c> - see <c>docs\reference\SimHub.WheelSlipEffect.decompiled.cs</c>)
    /// - this plugin now exposes those same two numbers (plus a THIRD, new one: a brake threshold that
    /// also gates the Slip channel) as settings, so the driver can tune them instead of being stuck
    /// with SimHub's own constants. The shipped DEFAULTS below happen to equal SimHub's own hard-coded
    /// values for Lock's brake threshold and Slip's throttle threshold (20/40) - this is intentional
    /// (matches today's shipped behaviour exactly until a driver changes something), not a coincidence.
    /// <para/>
    /// PRIORITY ORDERING (confirmed by decompilation - see <see cref="LegacySlipAlgorithm"/>'s own
    /// remarks): SimHub's real <c>GetRpmSpeedSlipLegacy</c> is a sequential if/return - if the brake
    /// condition is satisfied it returns immediately with the brake-driven term, and the throttle
    /// condition is never even evaluated. This plugin's Slip channel reproduces that same priority:
    /// brake is checked FIRST using <see cref="SlipBrakeThresholdPercent"/>, and only if that fails is
    /// throttle checked using <see cref="SlipThrottleThresholdPercent"/>. Shipping
    /// <see cref="SlipBrakeThresholdPercent"/> at 100 (its default) means that branch can never fire in
    /// practice (BrakePercent cannot exceed 100), so Slip ends up driven by throttle alone - "brake at
    /// 100% effectively means only throttle triggers slip" is the OWNER'S OWN INTENT, not a bug; do not
    /// "simplify" this to 0, which would make the brake branch fire constantly and suppress throttle
    /// entirely (the opposite of what a low brake threshold would sound like it should do).
    /// <para/>
    /// The Lock channel is unaffected by any of Slip's thresholds - it only ever reads
    /// <see cref="LockBrakeThresholdPercent"/>, independently.
    /// <para/>
    /// NOT included here (deliberately): the throttle branch's own additional
    /// <c>Clutch &lt; 5</c> guard (SimHub's own decompiled condition - a driver riding the clutch gets
    /// no slip feedback, which is SimHub's behaviour, not a bug here) stays a fixed constant inside
    /// <see cref="LegacySlipAlgorithm"/> - the owner's brief only asked for brake/throttle thresholds to
    /// become configurable, and making a third, rarely-touched guard configurable for no requested
    /// benefit would just be more surface area to get wrong.
    /// </summary>
    public struct LegacyThresholds
    {
        /// <summary>Wheel Lock's own brake-pedal-pressed threshold, 0-100. Default 20 (matches
        /// SimHub's own hard-coded constant).</summary>
        public double LockBrakeThresholdPercent;

        /// <summary>Wheel Slip's brake-pedal-pressed threshold, 0-100 - checked BEFORE throttle (see
        /// this struct's own remarks on priority ordering). Default 100 (effectively disables this
        /// branch, so Slip is throttle-only by default - the owner's explicit intent).</summary>
        public double SlipBrakeThresholdPercent;

        /// <summary>Wheel Slip's throttle-pedal-pressed threshold, 0-100 - only checked when
        /// <see cref="SlipBrakeThresholdPercent"/>'s own condition is NOT satisfied. Default 40
        /// (matches SimHub's own hard-coded constant).</summary>
        public double SlipThrottleThresholdPercent;

        /// <summary>
        /// Wheel Lock's own sensitivity, 0-100, matching SimHub's own <c>WheelsLockContainer.LockSensibility</c>
        /// exactly (name, range, default) - consumed by the withheld Layer 3 Lock algorithm (see
        /// <c>Private\README.md</c>) and docs\lock-and-animation-report.md for why Lock's own algorithm
        /// was changed to need it. Higher values make Lock more sensitive (reads a nonzero value sooner);
        /// default 50 matches SimHub's own shipped default AND the exact value the owner's own driver
        /// reported using, unchanged.
        /// <para/>
        /// NOTE: the default below (50.0) is a plain literal, NOT a reference to the withheld Layer 3
        /// algorithm's own constant - this struct is public and always compiled (clean-clone included),
        /// so it must not depend on a type that only exists when <c>Private\QAdvanceFeedback\</c> is
        /// present (see <see cref="Defaults"/>'s own remarks).
        /// </summary>
        public double LockSensibility;

        /// <summary>The shipped defaults - equal to SimHub's own hard-coded gates for Lock's brake and
        /// Slip's throttle, plus the new Slip brake threshold defaulted to 100 (effectively off), plus
        /// Lock's sensitivity at SimHub's own default (50, duplicated as a literal here rather than
        /// referencing the withheld algorithm's own constant - see <see cref="LockSensibility"/>'s
        /// remarks on why).</summary>
        public static LegacyThresholds Defaults => new LegacyThresholds
        {
            LockBrakeThresholdPercent = 20.0,
            SlipBrakeThresholdPercent = 100.0,
            SlipThrottleThresholdPercent = 40.0,
            LockSensibility = 50.0
        };
    }
}

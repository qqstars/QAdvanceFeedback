namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// Driver-configurable pedal-pressed thresholds that gate the Raw-layer pedal+speed formula
    /// (<c>QAdvanceFeedback.Core.RawCalculator.BrakeSpeedSlipModel</c>). Exposes what would otherwise be
    /// fixed internal gates (brake/throttle percentages) as settings, so a driver can tune them instead
    /// of being stuck with one fixed pair of numbers.
    /// <para/>
    /// PRIORITY ORDERING: Slip checks brake FIRST - if the brake condition is satisfied it uses the
    /// brake-driven reading immediately, and the throttle condition is never even evaluated. Only when
    /// the brake condition is NOT satisfied is throttle checked using
    /// <see cref="SlipThrottleThresholdPercent"/>.
    /// <para/>
    /// The Lock channel is unaffected by any of Slip's thresholds - it only ever reads
    /// <see cref="LockBrakeThresholdPercent"/>, independently.
    /// <para/>
    /// <see cref="SlipBrakeThresholdPercent"/> SHIPS AT 100 (effectively disabled) BY DELIBERATE PRODUCT
    /// CHOICE, not because it mirrors any external reference: Wheel Slip is throttle-only out of the box
    /// ("percentage of Brake Pedal Pressed (default 100%) OR throttle Pedal Pressed (default 40%)... by
    /// default, set the brake pedal presses as 100%, which means only throttle pedal pressed will
    /// trigger Wheel Slip"), confirmed after driving it ("feels good, reasonable - you can remain the
    /// current Wheel Slip"). A driver who prefers Slip to also respond to braking (the same way Lock
    /// does) can lower this threshold toward 20 themselves - the setting stays fully configurable either
    /// way.
    /// <para/>
    /// NOT included here (deliberately): the throttle branch's own additional clutch-engagement guard
    /// stays a fixed constant inside <c>BrakeSpeedSlipModel</c> - only brake/throttle thresholds are
    /// meant to be driver-configurable; making a third, rarely-touched guard configurable for no
    /// requested benefit would just be more surface area to get wrong.
    /// </summary>
    public struct LegacyThresholds
    {
        /// <summary>Wheel Lock's own brake-pedal-pressed threshold, 0-100. Default 20.</summary>
        public double LockBrakeThresholdPercent;

        /// <summary>Wheel Slip's brake-pedal-pressed threshold, 0-100 - checked BEFORE throttle (see
        /// this struct's own remarks on priority ordering). Default 100 - the deliberate choice that
        /// effectively disables Slip's own brake path so that ONLY throttle triggers Wheel Slip by
        /// default (see this struct's own remarks). A driver who wants brake-responsive Slip can lower
        /// this value toward 20 themselves.</summary>
        public double SlipBrakeThresholdPercent;

        /// <summary>Wheel Slip's throttle-pedal-pressed threshold, 0-100 - only checked when
        /// <see cref="SlipBrakeThresholdPercent"/>'s own condition is NOT satisfied. Default 40.</summary>
        public double SlipThrottleThresholdPercent;

        /// <summary>
        /// Wheel Lock's own sensitivity, 0-100, consumed by the Raw-layer Lock algorithm - higher values
        /// make Lock more sensitive (reads a nonzero value sooner); default 50.
        /// <para/>
        /// NOTE: the default below (50.0) is a plain literal, not a reference to the Raw-layer
        /// algorithm's own constant of the same value - this struct is public and always compiled, so it
        /// must not depend on a type from a different layer for its own default.
        /// </summary>
        public double LockSensibility;

        /// <summary>The shipped defaults - Lock's brake threshold and Slip's throttle threshold at their
        /// ordinary values, but Slip's OWN brake threshold ships at 100 (effectively disabled) per this
        /// struct's own remarks. Plus Lock's sensitivity at its own default (50).</summary>
        public static LegacyThresholds Defaults => new LegacyThresholds
        {
            LockBrakeThresholdPercent = 20.0,
            SlipBrakeThresholdPercent = 100.0,
            SlipThrottleThresholdPercent = 40.0,
            LockSensibility = 50.0
        };
    }
}

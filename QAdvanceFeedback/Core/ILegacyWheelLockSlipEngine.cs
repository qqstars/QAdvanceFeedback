namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// PUBLIC CONTRACT for Layer 3 - the withheld "legacy iRacing algorithm" implementation. This
    /// interface, this file, and everything else outside <c>Private\QAdvanceFeedback\</c> ship in the
    /// open-source repository; the concrete implementation that actually reproduces SimHub's
    /// decompiled <c>WheelSlipEffect.GetRpmSpeedSlipLegacy</c> arithmetic lives in
    /// <c>Private\QAdvanceFeedback\</c>, which is gitignored (see <c>..\Private\README.md</c> for what
    /// a third party must supply to restore real output).
    /// <para/>
    /// CONTRACT:
    /// <list type="bullet">
    /// <item><b>Input</b>: one <see cref="ITelemetrySample"/> - the current ("New") and previous
    /// ("Old") game-agnostic telemetry frames plus how much time elapsed between them. Every reading
    /// on <see cref="ITelemetryFrame"/> is independently nullable; a correct implementation must treat
    /// "missing" as "cannot tell", never silently substitute 0 for a genuinely absent reading (see
    /// that interface's own remarks - this is load-bearing, not a style note).</item>
    /// <item><b>Output</b>: one <see cref="LegacyWheelLockSlipResult"/> - Lock and Slip, each as four
    /// per-wheel values (<see cref="Corners"/>: FrontLeft/FrontRight/RearLeft/RearRight) plus the
    /// Front/Rear/Left/Right/All aggregates <see cref="PublishedPropertyNames"/> expects. EVERY one of
    /// those 18 numbers must already be on the plugin's published 0-100 scale, clamped (see
    /// <see cref="ClampMath.To0100"/>) - this is the publish boundary; nothing downstream re-clamps
    /// Layer 3's own output.</item>
    /// <item><b>Per-wheel semantics</b>: <see cref="Corners"/>' own remarks fix the wheel-index order
    /// (FrontLeft=0, FrontRight=1, RearLeft=2, RearRight=3) that any left/right-dependent arithmetic
    /// (e.g. a lateral-G halving term) must use - getting this order wrong silently swaps which side
    /// of the car reports which value.</item>
    /// <item><b>Guards</b>: a real implementation is expected to gate its own output on the same class
    /// of "do we actually have enough data this frame" checks the decompiled original does (minimum
    /// ground speed, both frames' RPM/speed present, no mid-comparison gear change) rather than
    /// producing a number from partial/absent telemetry - <see cref="ITelemetryFrame"/>'s own remarks
    /// on nullable-means-absent apply here directly.</item>
    /// <item><b>Never throw</b>: <see cref="Compute"/> is called once per SimHub telemetry frame
    /// (dozens of times a second) from <c>QAdvanceFeedback.DataUpdate</c>, which already wraps the
    /// whole per-frame pipeline in a catch-and-log - but an implementation that throws routinely turns
    /// every other channel's own per-frame work (Layers 4/5, G-force) into dropped frames too, so a
    /// well-behaved implementation should not rely on that outer catch as its own error handling.</item>
    /// </list>
    /// <para/>
    /// Resolved at runtime by <c>AlgorithmFactory.CreateLegacyEngine</c>, which looks for the Private
    /// implementation by type name via reflection and falls back to
    /// <see cref="InertLegacyWheelLockSlipEngine"/> (logging once) when it is absent - see that
    /// factory's own remarks for why a compile-time reference to the concrete type is impossible by
    /// design.
    /// </summary>
    public interface ILegacyWheelLockSlipEngine
    {
        /// <param name="sample">The current + previous telemetry frame.</param>
        /// <param name="thresholds">The owner-configurable pedal-pressed thresholds that gate Lock
        /// (brake) and Slip (brake-then-throttle, brake takes priority - see
        /// <see cref="LegacyThresholds"/>'s own remarks). Null (the default) means
        /// <see cref="LegacyThresholds.Defaults"/> - every pre-existing caller that does not yet pass
        /// this keeps compiling and behaving exactly as before.</param>
        LegacyWheelLockSlipResult Compute(ITelemetrySample sample, LegacyThresholds? thresholds = null);
    }
}

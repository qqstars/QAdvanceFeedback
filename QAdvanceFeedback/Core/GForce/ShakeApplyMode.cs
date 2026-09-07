namespace QAdvanceFeedback.Core.GForce
{
    /// <summary>
    /// How the "Integrate Wheel Lock and Slip" shake is distributed across the eight G-force pads
    /// (v1.0.8). All three modes produce the SAME left/right alternating wave with the same band
    /// arithmetic (see <see cref="GForceShake"/>) - they differ only in what each channel's band is
    /// computed FROM.
    /// <para/>
    /// TWO THINGS ARE IDENTICAL IN EVERY MODE, and both are load-bearing for how the shake feels:
    /// <list type="number">
    /// <item>THE DRIVE IS ALWAYS <c>Math.Max(lockContribution, slipContribution)</c>, computed once in
    /// <see cref="GForceEngine.Compute"/> BEFORE any mode branching. No mode routes lock to one set of
    /// pads and slip to another, and no mode weighs the two differently.</item>
    /// <item>THERE IS EXACTLY ONE OSCILLATOR - a single <c>_shakePhaseSeconds</c> shared by all eight
    /// pads and by both wheel signals. Lock-driven and slip-driven shaking are therefore in phase BY
    /// CONSTRUCTION: when one is at its maximum so is the other, and likewise at the minimum. There is
    /// no separate lock wave and slip wave that could drift apart, and switching which signal dominates
    /// changes only the band's WIDTH, never the wave's position. Anything that gave a channel or a
    /// signal its own phase would break the feel - see <c>GForceEngineShakeModeTests</c>' phase
    /// tests.</item>
    /// </list>
    /// </summary>
    public enum ShakeApplyMode
    {
        /// <summary>
        /// THE SHIPPED DEFAULT, and the only behaviour that existed before v1.0.8. Every pad pair shakes
        /// around ITS OWN current G-force level: <c>band = thatChannelLevel * contribution</c>.
        /// <para/>
        /// A channel sitting at level 0 therefore cannot shake at all (a zero band), which is why the
        /// shake appears to "route" itself - under braking the active chain is BackLow -> BottomRear ->
        /// BottomFront so BackTop stays still, and under acceleration it is BottomRear -> BackLow ->
        /// BackTop so BottomFront stays still. That is an emergent consequence of the multiply, NOT any
        /// per-channel lock/slip assignment: all pads receive the same combined contribution.
        /// </summary>
        PerChannel = 0,

        /// <summary>
        /// One band for every pad, computed from the ACTIVE CHAIN'S TERMINAL channel - the strongest,
        /// last-stage pad - and then applied identically to all eight outputs.
        /// <para/>
        /// The terminal channel is chosen by G-FORCE DIRECTION (owner's decision): braking uses
        /// BottomFront, accelerating uses BackTop. Picking it by whichever of lock/slip is larger was
        /// considered and rejected - slip while braking would select BackTop, whose level is 0 during
        /// braking, silencing the shake exactly when it was asked for.
        /// <para/>
        /// NOTE that this selects only WHICH G-FORCE LEVEL sets the band width. It is not a lock/slip
        /// routing decision: the drive is still <c>Math.Max(lock, slip)</c> exactly as in every other
        /// mode - see this enum's own remarks.
        /// <para/>
        /// So the terminal channel behaves exactly as it does under <see cref="PerChannel"/>, while every
        /// other pad - including the ones the active chain leaves at 0 - now shakes with that same
        /// strength instead of a scaled-down one or none at all. Still G-force-proportional: a light
        /// brake produces a small band on all eight, a hard one a large band.
        /// </summary>
        AllChannelsGForce = 1,

        /// <summary>
        /// One band for every pad, computed from the wheel lock/slip value ALONE:
        /// <c>band = 100 * contribution</c>, swinging between 0 and <c>lockOrSlipValue * scale</c>
        /// regardless of how much G-force there is - or whether there is any at all.
        /// <para/>
        /// The lateral left/right cornering bias is deliberately NOT applied in this mode (owner's
        /// decision): the whole point is an output that depends on nothing but how hard the wheel is
        /// locking or slipping, and a cornering multiplier would contradict that.
        /// </summary>
        AllChannelsLockSlip = 2,

        /// <summary>
        /// BOTH CUES, WHICHEVER IS STRONGER AT THIS INSTANT (v1.0.8). The wheel lock/slip wave is built
        /// exactly as in <see cref="AllChannelsLockSlip"/> - one wave from
        /// <c>Math.Max(lock, slip)</c> after scaling, no lateral bias - and then EACH pad publishes the
        /// greater of that wave and ITS OWN G-force value.
        /// <para/>
        /// The point is that neither cue can mask the other. Under
        /// <see cref="AllChannelsLockSlip"/> a hard braking load produces nothing at all unless a wheel
        /// is misbehaving; under <see cref="PerChannel"/> a lock while coasting is multiplied by a
        /// near-zero level and vanishes. Here a high-G moment is felt through the G-force side, a
        /// lock/slip moment is felt through the wave, and when both happen the louder one wins on each
        /// pad independently.
        /// <para/>
        /// The G-force half is each channel's own POST-LATERAL value - the same number
        /// <see cref="PerChannel"/> would publish with the shake silent - so the cornering cue survives
        /// on any pad the wave is not currently louder than. The lock/slip half keeps
        /// <see cref="AllChannelsLockSlip"/>'s deliberate lack of lateral bias, for that mode's own
        /// stated reason.
        /// <para/>
        /// Because the oscillation comes from the wheel value rather than the G-force level, this mode
        /// shares <see cref="AllChannelsLockSlip"/>'s shake-start scoring and its no-G-force fallback -
        /// see <c>GForceEngine.UsesLockSlipWave</c>.
        /// </summary>
        HigherOfGForceOrLockSlip = 3,
    }
}

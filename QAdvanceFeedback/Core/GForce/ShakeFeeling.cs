namespace QAdvanceFeedback.Core.GForce
{
    /// <summary>
    /// HOW THE TWO PADS OF A PAIR RELATE TO EACH OTHER while shaking (v1.0.8, owner's specification).
    /// <para/>
    /// Replaces the old free "Both-sides blend (%)" number. That slider was a continuous pan/common-mode
    /// mix, and it had two problems the driver could not see: only three points on it were actually
    /// distinct to feel, and the sustain setting silently cancelled itself as the slider approached the
    /// middle (see the old <c>EffectiveSustain</c>). Three named choices say what each one does and let
    /// the hold behave the same way in every one of them.
    /// </summary>
    public enum ShakeFeeling
    {
        /// <summary>
        /// THE SHIPPED DEFAULT. The two pads run half a cycle apart: one is at its maximum while the
        /// other is at its minimum, and they cross in the middle. Reads as the vibration travelling
        /// side to side.
        /// <para/>
        /// The pair's TOTAL output is very nearly constant, which is why this is the quietest of the
        /// three to feel even though each pad is travelling its whole band - two uncorrelated
        /// transducers sum closer to <c>sqrt(L^2 + R^2)</c> than to <c>L + R</c>.
        /// </summary>
        OppositePhase = 0,

        /// <summary>
        /// Both pads do exactly the same thing at the same moment - a pure common-mode pulse with no
        /// left/right movement at all. The total output swings the full band, so this is the strongest
        /// of the three to feel, at the cost of carrying no directional information.
        /// </summary>
        SamePhase = 1,

        /// <summary>
        /// A quarter-turn between the two: both start at maximum, one begins its descent immediately
        /// while the other holds. The pair keeps some side-to-side movement AND a swelling total, which
        /// is the combination that reads as a real shake rather than a pan.
        /// <para/>
        /// Uses a FIXED hold of <see cref="GForceShake.BlendingHoldFraction"/>; the hold setting is
        /// hidden for this feeling because the shape is defined by the offset instead.
        /// </summary>
        Blending = 2,
    }
}

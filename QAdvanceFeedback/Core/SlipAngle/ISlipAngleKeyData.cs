using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// What <see cref="SlipAngleFrameProcessor"/> needs from the persisted Slip Angle key data
    /// (<c>Settings.SlipAngleKeyDataSettings</c> implements it), so Core keeps no dependency on Settings.
    /// </summary>
    public interface ISlipAngleKeyData
    {
        /// <summary>True when the learned window is applied (Auto) for this source.</summary>
        bool GetAutoGenerate(string sourceIdentity);

        /// <summary>One-time seed of the slot from the learned window; false when already seeded or invalid.</summary>
        bool TrySeed(string gameId, string sourceIdentity, SlipAngleWindow learned);

        /// <summary>The window to apply (learned under Auto, or until the gate is ready; else the slot).</summary>
        SlipAngleWindow Resolve(string gameId, string sourceIdentity, SlipAngleWindow learned,
            ManualOverrideGate gate, string gateKey, double maturity);
    }
}

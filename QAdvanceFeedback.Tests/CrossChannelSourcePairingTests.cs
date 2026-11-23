using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Task G round 3: Slip's cross-channel cold-start borrow pairs only the SAME source type across the
    /// two channels - no source ever feeds another source's learning.
    /// </summary>
    public class CrossChannelSourcePairingTests
    {
        private const string Game = "G", Car = "C";
        private static string LockRaw => RawSourceFallback.RawIdentity(true);
        private static string SlipRaw => RawSourceFallback.RawIdentity(false);
        private static string LockShakeIt => ShakeItSourceResolution.ShakeItIdentity(true, ShakeItAlgorithmFingerprint.ForLock(false, 50, true));
        private static string SlipShakeIt => ShakeItSourceResolution.ShakeItIdentity(false, ShakeItAlgorithmFingerprint.ForSlip(false, true));
        private static string LockViper => KnownSourceColdStartReference.ViperSourceIdentity(true);
        private static string SlipViper => KnownSourceColdStartReference.ViperSourceIdentity(false);
        private const string SlipCustom = "Plain:Some.Plugin.SlipFL~Plain:Some.Plugin.SlipFR~Plain:Some.Plugin.SlipRL~Plain:Some.Plugin.SlipRR";
        private const string LockCustom = "Plain:Some.Plugin.LockFL~Plain:Some.Plugin.LockFR~Plain:Some.Plugin.LockRL~Plain:Some.Plugin.LockRR";

        private static string Pair(string slip, string activeLock)
            => CrossChannelSourcePairing.LockIdentityFor(slip, activeLock, SlipRaw, LockRaw);

        [Fact]
        public void Slip_internal_always_pairs_with_lock_internal()
        {
            Assert.Equal(LockRaw, Pair(SlipRaw, LockRaw));
            Assert.Equal(LockRaw, Pair(SlipRaw, LockShakeIt));
            Assert.Equal(LockRaw, Pair(SlipRaw, LockViper));
            Assert.Equal(LockRaw, Pair(SlipRaw, LockCustom));
        }

        [Fact]
        public void Shakeit_and_viper_pair_only_with_their_own_type_whatever_the_fingerprints()
        {
            Assert.Equal(LockShakeIt, Pair(SlipShakeIt, LockShakeIt));
            Assert.Equal(LockShakeIt, Pair(ShakeItSourceResolution.ShakeItIdentity(false), LockShakeIt));
            Assert.Equal(LockViper, Pair(SlipViper, LockViper));

            Assert.Null(Pair(SlipShakeIt, LockViper));
            Assert.Null(Pair(SlipShakeIt, LockRaw));
            Assert.Null(Pair(SlipShakeIt, LockCustom));
            Assert.Null(Pair(SlipViper, LockShakeIt));
            Assert.Null(Pair(SlipViper, LockRaw));
        }

        [Fact]
        public void A_custom_slip_source_never_borrows()
        {
            Assert.Null(Pair(SlipCustom, LockCustom));
            Assert.Null(Pair(SlipCustom, LockRaw));
            Assert.Null(Pair(SlipCustom, LockShakeIt));
            Assert.Null(Pair(null, LockShakeIt));
        }

        /// <summary>Slip's ceiling for <paramref name="slipIdentity"/> after Lock has confident evidence
        /// under <paramref name="lockIdentity"/> and Slip only non-qualifying frames (so its cold anchor is
        /// exactly where the borrow shows), with the engine's pairing installed by one Compute.</summary>
        private static double SlipCeiling(string slipIdentity, string lockIdentity, bool lockHasEvidence)
        {
            var engine = new NormalizedWheelLockSlipEngine();
            engine.Compute(TestFrames.BrakingSampleFor(0.1), Corners.Zero, Corners.Zero, Game, Car,
                lockSourceIdentity: lockIdentity, slipSourceIdentity: slipIdentity);
            if (lockHasEvidence)
                for (int i = 0; i < 400; i++)
                    engine.LockScaleLearner.ObserveAtPhysicalLimit(Game, Car, lockIdentity, 30.0, 1.0);
            for (int i = 0; i < 400; i++)
                engine.SlipScaleLearner.ObserveNonQualifyingAtPhysicalLimit(Game, Car, slipIdentity);
            return engine.SlipScaleLearner.LearnedCeiling(Game, Car, slipIdentity, out _) ?? double.NaN;
        }

        [Fact]
        public void Slip_shakeit_borrows_from_lock_shakeit()
        {
            Assert.NotEqual(SlipCeiling(SlipShakeIt, LockShakeIt, lockHasEvidence: false),
                SlipCeiling(SlipShakeIt, LockShakeIt, lockHasEvidence: true));
        }

        [Fact]
        public void Slip_shakeit_does_not_borrow_from_lock_viper()
        {
            Assert.Equal(SlipCeiling(SlipShakeIt, LockViper, lockHasEvidence: false),
                SlipCeiling(SlipShakeIt, LockViper, lockHasEvidence: true));
        }

        [Fact]
        public void Slip_custom_does_not_borrow_even_from_a_custom_lock()
        {
            Assert.Equal(SlipCeiling(SlipCustom, LockCustom, lockHasEvidence: false),
                SlipCeiling(SlipCustom, LockCustom, lockHasEvidence: true));
        }
    }
}

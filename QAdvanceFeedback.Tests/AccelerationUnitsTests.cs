using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Pins the m/s^2-to-G conversion factor (docs\field-fixes-report.md, defect A) - the actual
    /// <c>SimHubTelemetryAdapter.cs</c> call site that uses this cannot be unit-tested directly in
    /// this project (it needs a live <c>GameReaderCommon.GameData</c>, so it is SimHub-dependent -
    /// see docs\architecture.md's own remarks on that boundary), so the conversion is pinned here
    /// instead, at the one place it is actually computed.
    /// <para/>
    /// MUTATION (a) in the report: replacing <see cref="AccelerationUnits.Ms2ToG"/>'s body with a
    /// no-op (<c>return ms2;</c>) - the exact real-world bug (SimHub's raw m/s^2 AccelerationSurge
    /// assigned straight into a G-named field with no conversion) - fails
    /// <see cref="A_known_forza_horizon_6_session_spike_converts_to_a_plausible_g_value"/> below.
    /// </summary>
    public class AccelerationUnitsTests
    {
        [Fact]
        public void Ms2ToG_uses_standard_gravity()
        {
            Assert.Equal(1.0, AccelerationUnits.Ms2ToG(9.80665), 9);
            Assert.Equal(0.0, AccelerationUnits.Ms2ToG(0.0), 9);
            Assert.Equal(2.0, AccelerationUnits.Ms2ToG(19.6133), 3);
        }

        [Fact]
        public void Ms2ToG_and_GToMs2_are_exact_inverses()
        {
            foreach (double g in new[] { 0.0, 0.5, 1.0, 2.0, 8.0, 19.83 })
            {
                double ms2 = AccelerationUnits.GToMs2(g);
                Assert.Equal(g, AccelerationUnits.Ms2ToG(ms2), 9);
            }
        }

        [Fact]
        public void Ms2ToG_preserves_sign()
        {
            Assert.True(AccelerationUnits.Ms2ToG(-9.80665) < 0.0);
            Assert.Equal(-1.0, AccelerationUnits.Ms2ToG(-9.80665), 9);
        }

        /// <summary>
        /// This session's own captured evidence (docs\field-fixes-report.md, defect A): the current
        /// (buggy) build's Diag.MotionMagnitudeG - which, before this fix, was the raw SimHub
        /// AccelerationSurge value with NO conversion applied - reached a maximum of 194.5 (labelled
        /// "G", which would be lethal). Converted through this fix, it becomes a plausible, still
        /// notably large but survivable spike (consistent with a heavy Forza Horizon 6 impact/kerb
        /// strike), not a literal 194.5g reading.
        /// </summary>
        [Fact]
        public void A_known_forza_horizon_6_session_spike_converts_to_a_plausible_g_value()
        {
            double loggedRawMagnitude = 194.5487060546875; // Diag.MotionMagnitudeG max, captured session
            double convertedG = AccelerationUnits.Ms2ToG(loggedRawMagnitude);

            Assert.True(convertedG < 25.0, $"converted value {convertedG} is still implausibly large for a G reading");
            Assert.True(convertedG > 15.0, $"converted value {convertedG} should still reflect a genuine large spike");
        }
    }
}

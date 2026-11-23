using System;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>Ruling R29: <c>SlipAngle.CrossingGateEnabled</c> off bypasses the crossing gate (Lock/Slip parity).</summary>
    public class SlipAngleCrossingGateSwitchTests
    {
        private const double Dt = 1.0 / 60.0;
        private const double PeakG = 1.5;
        private static readonly ImpactVerdict Allowed = new ImpactVerdict(true, false, false, 0.0, 0.0);
        private static readonly ImpactVerdict Excluded = new ImpactVerdict(false, false, false, 1.0, 0.0);

        /// <summary>Peaks at 8 deg (1.5 g), then falls 0.15 g/deg.</summary>
        private static double TrueCurve(double a) =>
            a <= 8.0 ? PeakG * Math.Sin(Math.PI / 2.0 * a / 8.0) : Math.Max(0.3, PeakG - 0.15 * (a - 8.0));

        private static SlipAngleWindowLearner WithCurve(bool gateEnabled)
        {
            var bins = new GripCurveBins();
            for (int k = 0; k < 40; k++)
                for (int i = 0; GripCurveBins.BinCenterDeg(i) < 15.0; i++)
                {
                    double a = (i + (k + 0.5) / 40) * GripCurveBins.BinWidthDeg;
                    bins.Observe(a, TrueCurve(a));
                }
            var l = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            l.Import(new SlipAngleLearnerState { Bins = bins.Export() });
            l.CrossingGateEnabled = gateEnabled;
            return l;
        }

        /// <summary>A held slide at 9 deg: candidate frames (G at 90% of peak, on the curve) but no
        /// crossing (alpha and G never move), then an exit below 40% of peak.</summary>
        private static void HeldSlide(SlipAngleWindowLearner l, ImpactVerdict verdict, double alpha = 9.0)
        {
            for (int i = 0; i < 120; i++) l.Observe(alpha, TrueCurve(alpha), verdict, true, Dt);
            for (int i = 0; i < 72; i++) l.Observe(1.0, 0.2, Allowed, true, Dt);
        }

        [Fact]
        public void Default_is_enabled_everywhere()
        {
            Assert.True(new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault).CrossingGateEnabled);
            Assert.True(new KeyedSlipAngleLearner().CrossingGateEnabled);
        }

        [Fact]
        public void Enabled_a_held_slide_without_a_crossing_teaches_no_max()
        {
            SlipAngleWindowLearner l = WithCurve(true);
            for (int i = 0; i < 3; i++) HeldSlide(l, Allowed);

            Assert.Equal(0, l.CrossingCount);
            Assert.False(l.PeakConfirmed);
            Assert.Equal(SlipAngleWindow.ShippedDefault, l.Published);
        }

        [Fact]
        public void Disabled_max_learns_from_candidate_frames_without_crossings_and_confirms()
        {
            SlipAngleWindowLearner l = WithCurve(false);
            for (int i = 0; i < 3; i++) HeldSlide(l, Allowed);

            Assert.Equal(3, l.CrossingCount);   // each candidate run is one event
            Assert.True(l.PeakConfirmed);
            SlipAngleWindow w = l.Published;
            Assert.True(w.IsValid, w.ToString());
            Assert.InRange(w.Max, 8.5, 9.5);
        }

        [Fact]
        public void Disabled_excluded_frames_still_teach_nothing()
        {
            SlipAngleWindowLearner l = WithCurve(false);
            for (int i = 0; i < 3; i++) HeldSlide(l, Excluded);

            Assert.Equal(0, l.CrossingCount);
            Assert.False(l.PeakConfirmed);
            Assert.Equal(SlipAngleWindow.ShippedDefault, l.Published);
        }

        private static readonly ImpactVerdict RetroImpact = new ImpactVerdict(false, true, true, 1.5, 0.0);

        private static double MaxLevel(SlipAngleWindowLearner l) => l.Export().Max.Level;

        private static void Slide(SlipAngleWindowLearner l, double alpha, int frames)
        {
            for (int i = 0; i < frames; i++) l.Observe(alpha, TrueCurve(alpha), Allowed, true, Dt);
        }

        [Fact]
        public void Toggling_mid_run_does_not_corrupt_state()
        {
            SlipAngleWindowLearner l = WithCurve(false);
            Slide(l, 9.0, 60);
            l.CrossingGateEnabled = true;    // the live run is dropped, nothing recorded
            for (int i = 0; i < 72; i++) l.Observe(1.0, 0.2, Allowed, true, Dt);
            Assert.Equal(0, l.CrossingCount);
            Assert.Equal(0.0, MaxLevel(l));
            Assert.NotNull(l.Export());

            l.CrossingGateEnabled = false;
            for (int i = 0; i < 3; i++) HeldSlide(l, Allowed);
            Assert.True(l.PeakConfirmed);
            Assert.True(l.Published.IsValid);
        }

        [Fact]
        public void Toggle_on_off_on_mid_run_restores_max()
        {
            SlipAngleWindowLearner l = WithCurve(true);
            l.CrossingGateEnabled = false;
            HeldSlide(l, Allowed);                    // one committed run at 9 deg
            double committed = MaxLevel(l);
            Assert.True(committed > 0.0);

            l.CrossingGateEnabled = true;
            l.CrossingGateEnabled = false;
            Slide(l, 6.0, 120);                       // a new run teaching toward 6 deg
            l.CrossingGateEnabled = true;             // cut by the switch

            Assert.Equal(committed, MaxLevel(l));
            Assert.Equal(1, l.CrossingCount);
        }

        [Fact]
        public void A_held_slide_ending_in_an_impact_teaches_no_max()
        {
            SlipAngleWindowLearner fresh = WithCurve(false);
            Slide(fresh, 9.0, 120);
            fresh.Observe(9.0, TrueCurve(9.0), RetroImpact, true, Dt);
            Assert.Equal(0.0, MaxLevel(fresh));
            Assert.Equal(0, fresh.CrossingCount);
            Assert.Equal(SlipAngleWindow.ShippedDefault, fresh.Published);

            SlipAngleWindowLearner l = WithCurve(false);
            HeldSlide(l, Allowed);
            double committed = MaxLevel(l);
            SlipAngleWindow published = l.Published;
            Slide(l, 6.0, 120);                       // a slide into a wall...
            l.Observe(6.0, TrueCurve(6.0), RetroImpact, true, Dt);
            Assert.Equal(committed, MaxLevel(l));
            Assert.Equal(published.Max, l.Published.Max);

            // ...and the next ordinary run does not inherit its teaching.
            HeldSlide(l, Allowed);
            Assert.Equal(2, l.CrossingCount);
        }

        [Fact]
        public void Shape_check_alone_needs_two_committed_runs()
        {
            SlipAngleWindowLearner l = WithCurve(false);
            HeldSlide(l, Allowed);
            Assert.Equal(1, l.CrossingCount);
            Assert.False(l.PeakConfirmed);

            HeldSlide(l, Allowed);
            Assert.Equal(2, l.CrossingCount);
            Assert.True(l.PeakConfirmed);
        }

        [Fact]
        public void Processor_configure_applies_the_switch_to_the_keyed_learner()
        {
            var learner = new KeyedSlipAngleLearner();
            var p = new SlipAngleFrameProcessor(learner, null);
            var projector = new OutputProjector(ProjectorSettings.CreateShippedDefault(ProjectionChannel.SlipAngle));

            p.Configure(projector, new PulseSettings(), new DriftSettings(), crossingGateEnabled: false);
            Assert.False(learner.CrossingGateEnabled);
            p.Configure(projector, new PulseSettings(), new DriftSettings(), crossingGateEnabled: true);
            Assert.True(learner.CrossingGateEnabled);
        }
    }
}

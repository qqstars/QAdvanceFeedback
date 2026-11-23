using System;
using System.Collections.Generic;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>The composition-root pipeline (Task 20) as a pure unit: order, sources, gating, key data, persistence.</summary>
    public class SlipAngleFrameProcessorTests
    {
        private const double Dt = 1.0 / 60.0;
        private const double RadToDeg = 180.0 / Math.PI;

        private static TelemetryFrame Frame(double vlat = 2.0, double vfwd = 27.8, double latG = 1.0, double brake = 50,
            bool airborne = false, double? speedKmh = 100.0, double longG = 0.0)
            => new TelemetryFrame(groundSpeedKmh: speedKmh, brakePercent: brake, throttlePercent: 0,
                lateralLocalVelocity: vlat, forwardLocalVelocity: vfwd, lateralG: latG, longitudinalG: longG,
                yawRateRadPerSec: 0.3, isAirborne: airborne);

        private static SlipAngleFrameRequest Request(TelemetryFrame f, SlipAngleInput input = null, ISlipAngleKeyData keyData = null)
            => new SlipAngleFrameRequest { GameId = "G", CarId = "C", Frame = f, Dt = Dt, Input = input ?? new SlipAngleInput("G"), KeyData = keyData };

        private static SlipAngleFrameProcessor Processor(Action<Dictionary<string, SlipAngleKeyState>> save = null)
            => new SlipAngleFrameProcessor(new KeyedSlipAngleLearner(), save);

        private static long BinSamples(KeyedSlipAngleLearner learner) =>
            learner.Export().Values.Sum(s => s.Learner?.Curve?.TotalSamples ?? 0L);

        [Fact]
        public void No_data_gives_null_raw_zero_outputs_and_teaches_nothing()
        {
            var p = Processor();
            SlipAngleFrameResult r = p.Process(Request(new TelemetryFrame(groundSpeedKmh: 100, brakePercent: 50, lateralG: 1.0)));

            Assert.Null(r.RawAll);
            Assert.Equal(SourceNativeness.Unknown, r.Tier);
            Assert.Equal(0.0, r.NormalizedAll);
            Assert.Equal(0.0, r.ProjectedAll);
            Assert.Empty(p.Learner.Export());
        }

        [Fact]
        public void Fallback_frame_uses_body_slip_under_the_fallback_identity_and_shipped_window()
        {
            var p = Processor();
            SlipAngleFrameResult r = p.Process(Request(Frame()));

            double expected = Math.Atan2(2.0, 27.8) * RadToDeg;
            Assert.Equal(expected, r.RawAll.Value, 9);
            Assert.Equal(SourceNativeness.Fallback, r.Tier);
            Assert.Equal(SlipAngleFrameProcessor.FallbackIdentity, r.SourceIdentity);
            Assert.True(r.SourceIsPluginInternal);
            Assert.Equal(SlipAngleNormalizer.Normalize(expected, SlipAngleWindow.ShippedDefault), r.NormalizedAll, 9);
            Assert.True(r.ProjectedAll > 0.0);
            Assert.True(r.PedalGateOpen);
            Assert.Equal(1.0, r.K);
            Assert.Null(r.Front);
        }

        [Fact]
        public void Native_input_combines_axles_under_the_native_identity()
        {
            var p = Processor();
            var input = new SlipAngleInput("G", true, new Corners(-4, 4, 6, -6), true, SlipAngleUnit.Degrees);
            SlipAngleFrameResult r = p.Process(Request(Frame(), input));

            Assert.Equal(6 * 0.85 + 4 * 0.15, r.RawAll.Value, 9);
            Assert.Equal(SourceNativeness.Native, r.Tier);
            Assert.Equal(SlipAngleFrameProcessor.NativeIdentity, r.SourceIdentity);
            Assert.Equal(4.0, r.WheelFrontLeft.Value, 9);
            Assert.Equal(6.0, r.WheelRearRight.Value, 9);
            Assert.Equal(4.0, r.Front.Value, 9);
            Assert.Equal(6.0, r.Rear.Value, 9);
        }

        [Fact]
        public void Not_yet_promoted_native_values_still_use_the_fallback()
        {
            var p = Processor();
            var input = new SlipAngleInput("G", true, new Corners(4, 4, 6, 6), isNative: false, unit: SlipAngleUnit.Degrees);
            SlipAngleFrameResult r = p.Process(Request(Frame(), input));

            Assert.Equal(SourceNativeness.Fallback, r.Tier);
            Assert.Equal(SlipAngleFrameProcessor.FallbackIdentity, r.SourceIdentity);
        }

        [Fact]
        public void Custom_source_clamps_to_45_and_drops_unreadable_wheels()
        {
            var p = Processor();
            SlipAngleFrameRequest req = Request(Frame());
            req.UseCustomSource = true;
            req.CustomSourceIdentity = "custom-id";
            req.CustomWheelsDeg = new double?[] { null, null, -8.0, 50.0 };

            SlipAngleFrameResult r = p.Process(req);

            Assert.Null(r.Front);
            Assert.Equal((8.0 + 45.0) / 2, r.Rear.Value, 9);
            Assert.Equal(r.Rear.Value, r.RawAll.Value, 9);
            Assert.Equal("custom-id", r.SourceIdentity);
            Assert.False(r.SourceIsPluginInternal);
        }

        [Fact]
        public void Custom_source_publishes_tier_unknown_but_never_the_no_data_status()
        {
            var p = Processor();
            SlipAngleFrameRequest req = Request(Frame());
            req.UseCustomSource = true;
            req.CustomWheelsDeg = new double?[] { 3, 3, 4, 4 };

            SlipAngleFrameResult r = p.Process(req);
            SlipAngleStatus s = SlipAngleStatus.From(r, r.Tier, "G");

            Assert.Equal(SourceNativeness.Unknown, r.Tier);
            Assert.NotNull(r.RawAll);
            Assert.False(s.NoSlipAngleData);
            Assert.False(s.ShowFallbackWarning);
        }

        [Fact]
        public void Fault_status_shows_neither_no_data_nor_the_warning()
        {
            SlipAngleStatus s = SlipAngleStatus.Fault("G", sourceIsPluginInternal: true);
            Assert.True(s.Faulted);
            Assert.False(s.NoSlipAngleData);
            Assert.False(s.ShowFallbackWarning);
        }

        [Fact]
        public void A_missing_axis_holds_its_last_value_so_a_dropout_is_not_an_impact()
        {
            var p = Processor();
            p.Process(Request(Frame(longG: 3.5)));
            SlipAngleFrameResult dropout = p.Process(Request(new TelemetryFrame(groundSpeedKmh: 100, brakePercent: 50,
                lateralLocalVelocity: 2.0, forwardLocalVelocity: 27.8, lateralG: 1.0, longitudinalG: null)));

            Assert.False(dropout.ImpactFlag);   // substituting 0 would be a 3.5 g jump
        }

        [Fact]
        public void Game_switch_reset_returns_the_surface_classifier_to_sealed()
        {
            var p = Processor();
            p.Process(Request(Frame()));
            p.Reset();
            SlipAngleFrameResult r = p.Process(Request(Frame()));
            Assert.Equal(SurfaceBucket.Sealed, r.SurfaceBucket);
            Assert.Equal(1.0, r.SurfaceWeightSealed, 9);
        }

        [Fact]
        public void Custom_source_with_no_readable_wheel_is_no_data()
        {
            var p = Processor();
            SlipAngleFrameRequest req = Request(Frame());
            req.UseCustomSource = true;
            req.CustomWheelsDeg = new double?[] { null, null, null, double.NaN };

            SlipAngleFrameResult r = p.Process(req);

            Assert.Null(r.RawAll);
            Assert.Equal(SourceNativeness.Unknown, r.Tier);
            Assert.Equal(0.0, r.ProjectedAll);
        }

        [Fact]
        public void Closed_pedal_gate_zeroes_the_output_but_raw_is_still_published()
        {
            var p = Processor();
            SlipAngleFrameResult r = p.Process(Request(Frame(brake: 0)));

            Assert.NotNull(r.RawAll);
            Assert.False(r.PedalGateOpen);
            Assert.Equal(0.0, r.ProjectedAll);
        }

        [Fact]
        public void Ground_frames_teach_and_airborne_frames_do_not()
        {
            var ground = Processor();
            var air = Processor();
            for (int i = 0; i < 120; i++)
            {
                ground.Process(Request(Frame()));
                air.Process(Request(Frame(airborne: true)));
            }

            Assert.True(BinSamples(ground.Learner) > 0);
            Assert.Equal(0, BinSamples(air.Learner));
        }

        /// <summary>CAR SWITCH (owner, 2026-11-22): the frame state - the surface classifier above all - starts again,
        /// as on a game switch. Before, a new car inherited the previous car's last surface verdict and, having no
        /// curve of its own to judge against, never left it: it learned only into that bucket.</summary>
        [Fact]
        public void A_car_switch_starts_the_surface_classifier_again()
        {
            var p = Processor();
            var gravel = new SlipAngleInput("RFactor2", rawWheelSurface: new[] { "4", "4", "4", "4" });
            SlipAngleFrameResult r = null;
            for (int i = 0; i < 300; i++)
                r = p.Process(new SlipAngleFrameRequest { GameId = "RFactor2", CarId = "A", Frame = Frame(), Dt = Dt, Input = gravel });
            Assert.Equal(SurfaceBucket.Loose, r.SurfaceBucket);

            // The same car without a surface report keeps its verdict (nothing changed)...
            var unreported = new SlipAngleInput("RFactor2");
            r = p.Process(new SlipAngleFrameRequest { GameId = "RFactor2", CarId = "A", Frame = Frame(), Dt = Dt, Input = unreported });
            Assert.Equal(SurfaceBucket.Loose, r.SurfaceBucket);

            // ...another car starts on Sealed, as after a restart, and learns there.
            for (int i = 0; i < 120; i++)
                r = p.Process(new SlipAngleFrameRequest { GameId = "RFactor2", CarId = "B", Frame = Frame(), Dt = Dt, Input = unreported });
            Assert.Equal(SurfaceBucket.Sealed, r.SurfaceBucket);
            Assert.True(p.Learner.LearnedWindow("RFactor2", "B", SlipAngleFrameProcessor.FallbackIdentity, SlipAngleTarget.All, SurfaceBucket.Sealed).HasValue);
            Assert.False(p.Learner.LearnedWindow("RFactor2", "B", SlipAngleFrameProcessor.FallbackIdentity, SlipAngleTarget.All, SurfaceBucket.Loose).HasValue);
        }

        [Fact]
        public void Pit_frames_do_not_teach()
        {
            var p = Processor();
            var pit = new TelemetryFrame(groundSpeedKmh: 100, brakePercent: 50, lateralLocalVelocity: 2, forwardLocalVelocity: 27.8,
                lateralG: 1.0, longitudinalG: 0, isInPit: true);
            for (int i = 0; i < 120; i++) p.Process(Request(pit));
            Assert.Equal(0, BinSamples(p.Learner));
        }

        [Fact]
        public void A_lateral_g_jump_flags_an_impact_and_opens_the_exclusion_window()
        {
            var p = Processor();
            p.Process(Request(Frame(latG: 0.5)));
            SlipAngleFrameResult r = p.Process(Request(Frame(latG: 4.5)));

            Assert.True(r.ImpactFlag);
            Assert.True(r.ExclusionRemaining > 0.0);
        }

        [Fact]
        public void Key_data_resolution_drives_the_normalized_value()
        {
            var keyData = new FakeKeyData { Applied = new SlipAngleWindow(1.0, 2.0, 3.0) };
            var p = Processor();
            SlipAngleFrameResult r = p.Process(Request(Frame(), keyData: keyData));

            Assert.Equal(SlipAngleNormalizer.Normalize(r.RawAll.Value, keyData.Applied), r.NormalizedAll, 9);
            Assert.Equal(keyData.Applied.Best, r.AppliedWindow.Best);
            Assert.Equal(SlipAngleFrameProcessor.FallbackIdentity, keyData.LastSource);
            Assert.Equal(ColdStartTierResolver.Key("G", "C", SlipAngleFrameProcessor.FallbackIdentity), keyData.LastGateKey);
            Assert.Equal(0, keyData.SeedCalls);   // gate not ready (no 30 s of driving yet)
            Assert.False(r.KeyDataSeeded);
        }

        /// <summary>MANUAL MODE (owner, 2026-11-22): learning never stops - the learned window keeps learning and is what
        /// is saved - while the driver's window drives the output once the warm-up is done; the log tells them apart
        /// (LearnedWindow.* vs AppliedWindow.* and ManualApplied).</summary>
        [Fact]
        public void Manual_mode_keeps_learning_and_reports_the_applied_window_apart_from_the_learned_one()
        {
            var keyData = new SlipAngleKeyDataSettings();
            string id = SlipAngleFrameProcessor.FallbackIdentity;
            keyData.SetAutoGenerate(id, false);
            var manual = new SlipAngleWindow(2.0, 3.0, 4.0);
            Assert.True(keyData.TryCommitManual("G", id, manual.Min, manual.Best, manual.Max));
            Dictionary<string, SlipAngleKeyState> saved = null;
            var p = Processor(d => saved = d);

            SlipAngleFrameResult r = p.Process(Request(Frame(), keyData: keyData));
            Assert.False(r.ManualApplied);                      // warm-up first: the learned window applies
            Assert.Equal(r.LearnedWindow.Max, r.AppliedWindow.Max);

            for (int i = 0; i < 60 * 120 && !r.ManualApplied; i++)
            {
                double v = 1.0 + 3.0 * Math.Abs(Math.Sin(i / 90.0));   // corners in and out
                r = p.Process(Request(Frame(vlat: v, latG: 0.05 + 1.5 * Math.Abs(Math.Sin(i / 90.0))), keyData: keyData));
            }
            Assert.True(r.ManualApplied);
            Assert.Equal(manual.Max, r.AppliedWindow.Max);
            Assert.NotEqual(manual.Max, r.LearnedWindow.Max);
            Assert.True(BinSamples(p.Learner) > 0);              // still learning under Manual
            Assert.NotNull(saved);
            Assert.DoesNotContain(saved.Values, s => s.PublishedMax == manual.Max);   // the learned window is saved, never the manual one
        }

        [Fact]
        public void Real_key_data_in_auto_applies_the_learned_window()
        {
            var keyData = new SlipAngleKeyDataSettings();
            var p = Processor();
            SlipAngleFrameResult r = p.Process(Request(Frame(), keyData: keyData));

            Assert.Equal(r.LearnedWindow.Best, r.AppliedWindow.Best);
            Assert.Equal(SlipAngleWindow.ShippedDefault.Best, r.LearnedWindow.Best);
            Assert.False(r.ManualGateReady);
        }

        [Fact]
        public void Learner_state_is_saved_on_the_calling_thread_after_five_seconds()
        {
            int thread = Environment.CurrentManagedThreadId;
            int saves = 0;
            int savedOn = -1;
            Dictionary<string, SlipAngleKeyState> saved = null;
            var p = Processor(d => { saves++; savedOn = Environment.CurrentManagedThreadId; saved = d; });

            bool any = false;
            for (int i = 0; i < 60 * 6; i++) any |= p.Process(Request(Frame())).LearnerSaved;

            Assert.True(any);
            Assert.Equal(1, saves);
            Assert.Equal(thread, savedOn);
            Assert.NotEmpty(saved);
        }

        [Fact]
        public void Null_request_and_empty_frame_never_throw()
        {
            var p = Processor();
            Assert.Same(SlipAngleFrameResult.Empty, p.Process(null));
            SlipAngleFrameResult r = p.Process(new SlipAngleFrameRequest { Dt = double.NaN });
            Assert.Null(r.RawAll);
            p.NoteGap();
            p.Reset();
        }

        [Fact]
        public void NoteGap_resets_the_drift_attenuator_like_Configure()
        {
            var p = Processor();
            SlipAngleFrameResult r = null;
            for (int i = 0; i < 90; i++) r = p.Process(Request(Frame(vlat: 20.0))); // ~36°: held far past the window
            Assert.Equal(DriftState.Drifting, r.DriftState);
            Assert.True(r.DriftFactor < 1.0);

            p.NoteGap(); // paused / menu
            r = p.Process(Request(Frame(vlat: 20.0)));
            Assert.Equal(DriftState.Normal, r.DriftState);
            Assert.Equal(1.0, r.DriftFactor);
        }

        [Fact]
        public void Native_support_diagnostics_are_captured()
        {
            var native = new SlipAngleNativeSupport();
            var p = Processor();
            SlipAngleFrameRequest req = Request(Frame());
            req.NativeSupport = native;
            SlipAngleFrameResult r = p.Process(req);

            Assert.Equal(native.IsBlocked("G"), r.NativeBlocked);
            Assert.Equal(native.IsUnitUndetermined("G"), r.UnitUndetermined);
        }

        [Fact]
        public void Status_shows_the_fallback_warning_only_for_plugin_internal_fallback_R19()
        {
            var p = Processor();
            SlipAngleFrameResult fallback = p.Process(Request(Frame()));
            Assert.True(SlipAngleStatus.From(fallback, SourceNativeness.Fallback, "G").ShowFallbackWarning);
            Assert.False(SlipAngleStatus.From(fallback, SourceNativeness.Native, "G").ShowFallbackWarning);

            SlipAngleFrameRequest req = Request(Frame());
            req.UseCustomSource = true;
            req.CustomWheelsDeg = new double?[] { 3, 3, 4, 4 };
            SlipAngleFrameResult custom = p.Process(req);
            Assert.False(SlipAngleStatus.From(custom, SourceNativeness.Fallback, "G").ShowFallbackWarning);
        }

        [Fact]
        public void Status_reports_no_slip_angle_data_for_unknown_tier_with_null_raw_R19()
        {
            var p = Processor();
            SlipAngleFrameResult none = p.Process(Request(new TelemetryFrame(groundSpeedKmh: 100, lateralG: 1.0)));
            SlipAngleStatus s = SlipAngleStatus.From(none, SourceNativeness.Unknown, "G");
            Assert.True(s.NoSlipAngleData);
            Assert.False(s.ShowFallbackWarning);
            Assert.False(SlipAngleStatus.None.NoSlipAngleData);   // no game: no status text
            Assert.Equal(SlipAngleWindow.ShippedDefault.Best, s.LearnedWindow.Best);
        }

        private sealed class FakeKeyData : ISlipAngleKeyData
        {
            public SlipAngleWindow Applied;
            public string LastSource;
            public string LastGateKey;
            public int SeedCalls;

            public bool GetAutoGenerate(string sourceIdentity) => false;

            public bool TrySeed(string gameId, string sourceIdentity, SlipAngleWindow learned)
            {
                SeedCalls++;
                return true;
            }

            public SlipAngleWindow Resolve(string gameId, string sourceIdentity, SlipAngleWindow learned,
                ManualOverrideGate gate, string gateKey, double maturity)
            {
                LastSource = sourceIdentity;
                LastGateKey = gateKey;
                return Applied;
            }
        }
    }
}

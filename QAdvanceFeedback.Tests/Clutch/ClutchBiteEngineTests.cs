using System;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Clutch;
using Xunit;

namespace QAdvanceFeedback.Tests.Clutch
{
    /// <summary>The per-frame Clutch Bite pipeline: clutch source, output, learning keys
    /// (docs\clutch-bite-design.md).</summary>
    public class ClutchBiteEngineTests
    {
        private static readonly ClutchBiteParameters Defaults =
            new ClutchBiteParameters(ClutchApplyDirection.PressAndRelease, auto: true, bitePoint: 50, range: 30, gamma: 2.5);

        // ---- Source ------------------------------------------------------------------------------

        [Fact]
        public void Null_game_clutch_uses_the_axis()
        {
            var r = new ClutchSourceResolver();
            ClutchReading reading = r.Resolve("g", gameClutch: null, axis01: 0.4, axisName: "JoystickPlugin.P_Z", invert: false);
            Assert.Equal(ClutchSourceKind.Axis, reading.Kind);
            Assert.Equal(40.0, reading.Clutch0100.Value, 9);
            Assert.Equal("Axis:JoystickPlugin.P_Z", reading.Identity);
        }

        [Fact]
        public void Constant_zero_game_clutch_uses_the_axis_until_the_game_has_shown_a_clutch()
        {
            var r = new ClutchSourceResolver();
            Assert.Equal(ClutchSourceKind.Axis, r.Resolve("g", 0, 0.2, "A", false).Kind);
            Assert.Equal(ClutchSourceKind.Game, r.Resolve("g", 30, 0.2, "A", false).Kind);    // the game reports a clutch
            ClutchReading after = r.Resolve("g", 0, 0.2, "A", false);
            Assert.Equal(ClutchSourceKind.Game, after.Kind);                                 // latched: a released pedal is 0
            Assert.Equal("GameClutch", after.Identity);
        }

        [Fact]
        public void Latch_resets_on_game_change()
        {
            var r = new ClutchSourceResolver();
            r.Resolve("g1", 30, 0.2, "A", false);
            Assert.Equal(ClutchSourceKind.Axis, r.Resolve("g2", 0, 0.2, "A", false).Kind);
        }

        [Fact]
        public void Inverted_axis_and_no_source()
        {
            var r = new ClutchSourceResolver();
            Assert.Equal(75.0, r.Resolve(null, null, 0.25, "A", invert: true).Clutch0100.Value, 9);
            ClutchReading none = r.Resolve(null, null, null, null, false);
            Assert.Equal(ClutchSourceKind.None, none.Kind);
            Assert.Null(none.Clutch0100);
        }

        [Fact]
        public void Game_value_without_axis_before_the_latch_is_reported_but_not_a_source()
        {
            var r = new ClutchSourceResolver();
            ClutchReading reading = r.Resolve("g", 0, null, null, false);
            Assert.Equal(ClutchSourceKind.None, reading.Kind);   // review fix M1
            Assert.Equal(0.0, reading.Clutch0100.Value);
        }

        // ---- Engine ------------------------------------------------------------------------------

        private static ClutchFrame Frame(double t, double clutch, double speed = 0, double rpm = 900) =>
            new ClutchFrame(t, clutch, rpm, speed, 10, inGear: true, looseSurface: false, validForLearning: true);

        private static ClutchReading Game(double clutch) => new ClutchReading(clutch, "GameClutch", ClutchSourceKind.Game);
        private static ClutchReading Axis(double clutch) => new ClutchReading(clutch, "Axis:A", ClutchSourceKind.Axis);

        [Fact]
        public void Output_follows_curve_and_direction()
        {
            var engine = new ClutchBiteEngine();
            var learner = new ClutchBiteLearner();
            var release = Defaults.With(apply: ClutchApplyDirection.ReleaseOnly);

            double atBite = 0;
            for (double c = 100; c >= 50; c -= 1) atBite = engine.Step(Game(c), Frame(0, c), "g", "car", release, learner, out _);
            Assert.Equal(100.0, atBite, 6);
            Assert.Equal(ClutchBiteCurve.Output(40, 50, 30, 2.5), engine.Step(Game(40), Frame(0, 40), "g", "car", release, learner, out _), 9);

            // Pressing back in: Release Only goes quiet.
            double pressing = 0;
            for (double c = 40; c <= 50; c += 1) pressing = engine.Step(Game(c), Frame(0, c), "g", "car", release, learner, out _);
            Assert.Equal(0.0, pressing);

            Assert.Equal(0.0, engine.Step(new ClutchReading(null, "None", ClutchSourceKind.None), Frame(0, 50), "g", "car", Defaults, learner, out _));
        }

        [Fact]
        public void Output_uses_the_learned_final_bite_under_auto_and_the_configured_one_under_manual()
        {
            var learner = new ClutchBiteLearner();
            string key = ClutchBiteLearner.Key("g", "car", "GameClutch");
            for (int i = 0; i < 9; i++) learner.AddSample(key, 40);   // matured at 40

            var engine = new ClutchBiteEngine();
            Assert.Equal(100.0, engine.Step(Game(40), Frame(0, 40), "g", "car", Defaults, learner, out _), 6);
            Assert.Equal(40.0, engine.CurrentStatus.Final, 9);

            var manual = new ClutchBiteEngine();
            Assert.Equal(ClutchBiteCurve.Output(40, 50, 30, 2.5),
                manual.Step(Game(40), Frame(0, 40), "g", "car", Defaults.With(auto: false), learner, out _), 9);
        }

        [Fact]
        public void A_standing_start_is_learned_under_the_current_key()
        {
            var engine = new ClutchBiteEngine();
            var learner = new ClutchBiteLearner();
            bool accepted = RunStart(engine, learner, "g", "car", reading: Game);
            Assert.True(accepted);
            Assert.Equal(1, learner.Status(ClutchBiteLearner.Key("g", "car", "GameClutch"), 50, true).Count);
            Assert.Equal(ClutchBiteLearner.Key("g", "car", "GameClutch"), engine.CurrentKey);
        }

        [Fact]
        public void Source_change_mid_start_drops_the_sample()
        {
            var engine = new ClutchBiteEngine();
            var learner = new ClutchBiteLearner();
            // The source switches from the axis to the game halfway through the release.
            bool accepted = RunStart(engine, learner, "g", "car", reading: (c, t) => t < 2.5 ? Axis(c) : Game(c));
            Assert.False(accepted);
            Assert.Empty(learner.ExportAll());
        }

        [Fact]
        public void Car_switch_mid_start_drops_the_sample()
        {
            var engine = new ClutchBiteEngine();
            var learner = new ClutchBiteLearner();
            bool accepted = RunStart(engine, learner, "g", "car", reading: Game, carAt: t => t < 2.5 ? "car" : "other");
            Assert.False(accepted);
            Assert.Empty(learner.ExportAll());
        }

        /// <summary>A slow standing start (true bite 45) through the engine; true if a sample was accepted.</summary>
        private static bool RunStart(ClutchBiteEngine engine, ClutchBiteLearner learner, string game, string car,
            Func<double, double, ClutchReading> reading, Func<double, string> carAt = null)
        {
            const double dt = 1.0 / 60.0;
            bool any = false;
            double speed = 0;
            var clutch = new System.Collections.Generic.List<double>();
            for (int i = 0; i < 60; i++) clutch.Add(100);
            for (int i = 0; i < 60; i++) clutch.Add(90);
            for (int i = 1; i <= 360; i++) clutch.Add(90 - 60.0 * i / 360);
            for (int i = 0; i < 360; i++) clutch.Add(30);
            for (int i = 0; i < clutch.Count; i++)
            {
                double t = i * dt, delayed = clutch[Math.Max(0, i - 2)];
                double engaged = Math.Max(0, 45 - delayed);
                speed += dt * 0.5 * Math.Max(0, engaged - 2);
                var f = Frame(t, clutch[i], speed, 900 - 20 * engaged);
                engine.Step(reading(clutch[i], t), f, game, carAt?.Invoke(t) ?? car,
                    Defaults, learner, out bool accepted);
                any |= accepted;
            }
            return any;
        }

        private static ClutchReading Game(double c, double t) => Game(c);

        // ---- Published ---------------------------------------------------------------------------

        [Fact]
        public void Clutch_product_name_follows_the_62_lock_slip_gforce_names_and_slip_angle_follows_clutch()
        {
            var names = AllPublishedProperties.ProductNames().ToList();
            // Slip Angle (docs\slip-angle-design.md 9.8) appended its names after Clutch's.
            int slipAngle = Core.SlipAngle.SlipAnglePublishedNames.ProductNames.Length;
            int slipAngleDiag = Core.SlipAngle.SlipAnglePublishedNames.DiagnosticNames.Length;
            Assert.Equal(63 + slipAngle, names.Count);
            Assert.Equal("Clutch.Bite.Output", names[62]);
            Assert.Equal(AllPublishedProperties.ClutchDiagnosticNames, AllPublishedProperties.DiagnosticNames().Skip(AllPublishedProperties.DiagnosticNames().Count() - 5 - slipAngleDiag).Take(5).ToArray());
        }

        [Fact]
        public void Csv_row_carries_the_clutch_values_in_header_order()
        {
            var publisher = new PropertyPublisher();
            publisher.UpdateClutch(73.5);
            publisher.UpdateClutchDiagnostics("GameClutch", 44.0, 45.0, 45.5, 3);
            var names = AllPublishedProperties.AllNames(true).ToList();
            object[] row = publisher.SnapshotAllValuesForCsv();
            Assert.Equal(names.Count, row.Length);
            Assert.Equal(73.5, row[names.IndexOf("Clutch.Bite.Output")]);
            Assert.Equal("GameClutch", row[names.IndexOf("Diag.Clutch.Source")]);
            Assert.Equal(3, row[names.IndexOf("Diag.Clutch.BiteSamples")]);
        }
    }
}

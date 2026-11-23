using System;
using System.Collections.Generic;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>
    /// The slip-angle raw-source table against docs\slip-angle-spike-report.md, row by row. Every
    /// expected value below is transcribed from the spike's per-game table, with SimHub's own array
    /// naming applied (an array member <c>name[i]</c> is published as <c>name01</c>..<c>name04</c>,
    /// 1-based, two digits - DataCorePlugin.DeclareObject). Paths are listed in FL, FR, RL, RR order.
    /// </summary>
    public class SlipAngleRawSourcesTableTests
    {
        private static string[] Arr(string prefix, string suffix, params int[] rawIndices)
            => rawIndices.Select(i => prefix + (i + 1).ToString("00") + suffix).ToArray();

        // Raw RL,RR,FL,FR (F1 23+ and LFS): FL is raw 2, FR raw 3, RL raw 0, RR raw 1.
        private static readonly int[] F1Remap = { 2, 3, 0, 1 };
        private static readonly int[] Identity = { 0, 1, 2, 3 };

        private sealed class Row
        {
            public string Id;
            public string[] Paths;
            public string[] LongitudinalPaths;
            public SlipAngleUnit Unit;
            public SlipAngleTransform Transform;
            public SlipAngleWheelOrderBasis Order;
            public string[] Surface;
            public bool Provisional;
        }

        private static IEnumerable<Row> SpikeRows()
        {
            // ---- Native (direct) ----
            yield return new Row { Id = "ACC", Paths = Arr("Physics.slipAngle", "", Identity), Unit = SlipAngleUnit.Unverified, Transform = SlipAngleTransform.Direct, Order = SlipAngleWheelOrderBasis.FeedbackDataIndex };
            yield return new Row { Id = "AssettoCorsaRally", Paths = Arr("Physics.slipAngle", "", Identity), Unit = SlipAngleUnit.Unverified, Transform = SlipAngleTransform.Direct, Order = SlipAngleWheelOrderBasis.Unverified };
            yield return new Row { Id = "AssettoCorsaEVO", Paths = Arr("Physics.slipAngle", "", Identity), Unit = SlipAngleUnit.Unverified, Transform = SlipAngleTransform.Direct, Order = SlipAngleWheelOrderBasis.Unverified };
            foreach (string f1 in new[] { "F12023", "F12024", "F12025" })
            {
                yield return new Row
                {
                    Id = f1, Paths = Arr("PacketMotionExData.m_wheelSlipAngle", "", F1Remap), Unit = SlipAngleUnit.Unverified,
                    Transform = SlipAngleTransform.Direct, Order = SlipAngleWheelOrderBasis.ReaderRemap,
                    Surface = Arr("PlayerCarTelemetryData.m_surfaceType", "", F1Remap),
                };
            }
            foreach (string forza in new[] { "FM7", "FM8", "FH4", "FH5", "FH6" })
            {
                yield return new Row
                {
                    Id = forza,
                    Paths = new[] { "TireSlipAngleFrontLeft", "TireSlipAngleFrontRight", "TireSlipAngleRearLeft", "TireSlipAngleRearRight" },
                    Unit = SlipAngleUnit.Unverified, Transform = SlipAngleTransform.Direct, Order = SlipAngleWheelOrderBasis.Named,
                };
            }
            yield return new Row { Id = "LFS", Paths = Arr("OutSim2.OSWheels", ".TanSlipAngle", F1Remap), Unit = SlipAngleUnit.Radians, Transform = SlipAngleTransform.AtanOfTangent, Order = SlipAngleWheelOrderBasis.ReaderRemap };
            yield return new Row { Id = "Wreckfest2", Paths = Arr("Main.carPlayer.tires", ".slipAngle", Identity), Unit = SlipAngleUnit.Unverified, Transform = SlipAngleTransform.Direct, Order = SlipAngleWheelOrderBasis.Named, Surface = Arr("Main.carPlayer.tires", ".surfaceType", Identity) };
            yield return new Row { Id = "ProjectMotorRacing", Paths = Arr("PlayerTelemetry.m_wheels", ".m_slipAngle", Identity), Unit = SlipAngleUnit.Unverified, Transform = SlipAngleTransform.Direct, Order = SlipAngleWheelOrderBasis.FeedbackDataIndex, Surface = Arr("PlayerTelemetry.m_wheels", ".m_contactMaterialHash", Identity) };
            yield return new Row
            {
                Id = "Drift21",
                Paths = new[] { "RaceTelemetry.TireSlipAngleRadians_FrontLeft", "RaceTelemetry.TireSlipAngleRadians_FrontRight", "RaceTelemetry.TireSlipAngleRadians_BackLeft", "RaceTelemetry.TireSlipAngleRadians_BackRight" },
                Unit = SlipAngleUnit.Radians, Transform = SlipAngleTransform.Direct, Order = SlipAngleWheelOrderBasis.Named,
            };
            yield return new Row { Id = "KartKraft", Paths = Arr("Motion.WheelsArray", ".SlipAngle", Identity), Unit = SlipAngleUnit.Unverified, Transform = SlipAngleTransform.Direct, Order = SlipAngleWheelOrderBasis.Unverified, Surface = Arr("Motion.WheelsArray", ".Surface", Identity) };
            yield return new Row { Id = "BusBound", Paths = Arr("Telemetry.Bus.Wheels", ".SlipAngle", Identity), Unit = SlipAngleUnit.Unverified, Transform = SlipAngleTransform.Direct, Order = SlipAngleWheelOrderBasis.Unverified, Surface = Arr("Telemetry.Bus.Wheels", ".Ground", Identity) };
            yield return new Row
            {
                Id = "Rennsport",
                Paths = new[] { "TelemetryPoint.LF.SlipAngle", "TelemetryPoint.RF.SlipAngle", "TelemetryPoint.LR.SlipAngle", "TelemetryPoint.RR.SlipAngle" },
                Unit = SlipAngleUnit.Unverified, Transform = SlipAngleTransform.Direct, Order = SlipAngleWheelOrderBasis.Named,
                Surface = new[] { "TelemetryPoint.LF.ContactSurface", "TelemetryPoint.RF.ContactSurface", "TelemetryPoint.LR.ContactSurface", "TelemetryPoint.RR.ContactSurface" },
            };

            // ---- Native (computed): atan2(lateral patch velocity, |longitudinal ground velocity|) ----
            yield return new Row
            {
                Id = "RFactor2", Paths = Arr("CurrentPlayerTelemetry.mWheels", ".mLateralPatchVel", Identity),
                LongitudinalPaths = Arr("CurrentPlayerTelemetry.mWheels", ".mLongitudinalGroundVel", Identity),
                Unit = SlipAngleUnit.Radians, Transform = SlipAngleTransform.Atan2PatchVelocity, Order = SlipAngleWheelOrderBasis.FeedbackDataIndex,
                Surface = Arr("CurrentPlayerTelemetry.mWheels", ".mSurfaceType", Identity),
            };
            yield return new Row
            {
                Id = "LMU", Paths = Arr("PlayerNativeTelemetry.mWheel", ".mLateralPatchVel", Identity),
                LongitudinalPaths = Arr("PlayerNativeTelemetry.mWheel", ".mLongitudinalGroundVel", Identity),
                Unit = SlipAngleUnit.Radians, Transform = SlipAngleTransform.Atan2PatchVelocity, Order = SlipAngleWheelOrderBasis.FeedbackDataIndex,
                Surface = Arr("PlayerNativeTelemetry.mWheel", ".mSurfaceType", Identity),
            };
            foreach (string nascar in new[] { "NASCARHeat4", "NASCARHeat5", "TonyStewartsAllAmericanRacing", "TonyStewartsSprintCarRacing", "SRXTheGame" })
            {
                yield return new Row
                {
                    Id = nascar,
                    Paths = new[] { "LCarPacket.wheel_lf.lat_terrain_speed", "LCarPacket.wheel_rf.lat_terrain_speed", "LCarPacket.wheel_lr.lat_terrain_speed", "LCarPacket.wheel_rr.lat_terrain_speed" },
                    LongitudinalPaths = new[] { "LCarPacket.wheel_lf.lon_terrain_speed", "LCarPacket.wheel_rf.lon_terrain_speed", "LCarPacket.wheel_lr.lon_terrain_speed", "LCarPacket.wheel_rr.lon_terrain_speed" },
                    Unit = SlipAngleUnit.Radians, Transform = SlipAngleTransform.Atan2PatchVelocity, Order = SlipAngleWheelOrderBasis.Named,
                    Surface = new[] { "LCarPacket.wheel_lf.surface", "LCarPacket.wheel_rf.surface", "LCarPacket.wheel_lr.surface", "LCarPacket.wheel_rr.surface" },
                    Provisional = true,
                };
            }
        }

        public static IEnumerable<object[]> SpikeIds() => SpikeRows().Select(r => new object[] { r.Id });

        [Theory]
        [MemberData(nameof(SpikeIds))]
        public void Row_matches_the_spike_report_exactly(string id)
        {
            Row expected = SpikeRows().Single(r => r.Id == id);
            Assert.True(SlipAngleRawSourceTable.TryGet(id, out SlipAngleRawSourceEntry e));
            Assert.Equal(id, e.GameId);
            Assert.Equal(expected.Paths, e.PropertyPaths);
            if (expected.LongitudinalPaths == null) Assert.Null(e.LongitudinalPaths);
            else Assert.Equal(expected.LongitudinalPaths, e.LongitudinalPaths);
            Assert.Equal(expected.Unit, e.Unit);
            Assert.Equal(expected.Transform, e.Transform);
            Assert.Equal(expected.Order, e.WheelOrderBasis);
            Assert.Equal(expected.Order != SlipAngleWheelOrderBasis.Unverified, e.WheelOrderVerified);
            Assert.False(string.IsNullOrWhiteSpace(e.WheelOrder));
            if (expected.Surface == null) Assert.Null(e.SurfacePaths);
            else Assert.Equal(expected.Surface, e.SurfacePaths);
            Assert.Equal(expected.Provisional, e.Provisional);
        }

        [Fact]
        public void Table_has_exactly_the_spike_native_rows()
        {
            var expected = SpikeRows().Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var actual = SlipAngleRawSourceTable.NativeGameIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData("AssettoCorsa")]
        [InlineData("Automobilista2")]
        [InlineData("RRRE")]
        [InlineData("IRacing")]
        [InlineData("F12022")]
        [InlineData("EAWRC23")]
        [InlineData("BeamNgDrive")]
        [InlineData("")]
        [InlineData(null)]
        public void Fallback_unverified_and_unknown_games_have_no_native_row(string id)
        {
            Assert.False(SlipAngleRawSourceTable.TryGet(id, out SlipAngleRawSourceEntry e));
            Assert.Null(e);
        }

        [Fact]
        public void Lookup_is_case_insensitive_like_SimHub_property_names()
        {
            Assert.True(SlipAngleRawSourceTable.TryGet("rfactor2", out SlipAngleRawSourceEntry e));
            Assert.Equal("RFactor2", e.GameId);
        }

        [Fact]
        public void ACC_is_also_reachable_by_SimHubs_long_game_name()
        {
            // The spike lists ACC as "ACC"; this repo's own transcription of a decompiled plugin
            // (ViperSupportedGames) shows SimHub's GameName for it is "AssettoCorsaCompetizione".
            Assert.True(SlipAngleRawSourceTable.TryGet("AssettoCorsaCompetizione", out SlipAngleRawSourceEntry e));
            Assert.True(SlipAngleRawSourceTable.TryGet("ACC", out SlipAngleRawSourceEntry acc));
            Assert.Same(acc, e);
        }

        [Fact]
        public void Only_Rennsport_is_flagged_as_an_unconfirmed_game_id_and_only_LMU_as_unconfirmed_exposure()
        {
            foreach (string id in SlipAngleRawSourceTable.NativeGameIds)
            {
                SlipAngleRawSourceTable.TryGet(id, out SlipAngleRawSourceEntry e);
                Assert.Equal(id == "Rennsport", e.GameIdUnverified);
                Assert.Equal(id == "LMU", e.ExposureUnconfirmed);
            }
        }

        [Fact]
        public void Every_native_row_has_four_paths()
        {
            foreach (string id in SlipAngleRawSourceTable.NativeGameIds)
            {
                SlipAngleRawSourceTable.TryGet(id, out SlipAngleRawSourceEntry e);
                Assert.Equal(4, e.PropertyPaths.Count);
                Assert.All(e.PropertyPaths, p => Assert.False(string.IsNullOrWhiteSpace(p)));
                Assert.Equal(e.Transform == SlipAngleTransform.Atan2PatchVelocity, e.LongitudinalPaths != null);
                if (e.SurfacePaths != null) Assert.Equal(4, e.SurfacePaths.Count);
            }
        }

        [Fact]
        public void Full_property_name_has_the_raw_data_prefix()
        {
            Assert.Equal("DataCorePlugin.GameRawData.Physics.slipAngle01", SlipAngleRawSourceTable.FullPropertyName("Physics.slipAngle01"));
        }

        // ---- Surface-only rows (fallback games; ruling R13 - raw value only, no mapping) ----

        [Theory]
        [InlineData("Automobilista2", "mTerrain01", "mTerrain02", "mTerrain03", "mTerrain04", SlipAngleWheelOrderBasis.FeedbackDataIndex)]
        [InlineData("PCars", "mTerrain01", "mTerrain02", "mTerrain03", "mTerrain04", SlipAngleWheelOrderBasis.Unverified)]
        [InlineData("PCars2", "mTerrain01", "mTerrain02", "mTerrain03", "mTerrain04", SlipAngleWheelOrderBasis.Unverified)]
        [InlineData("PCars3", "mTerrain01", "mTerrain02", "mTerrain03", "mTerrain04", SlipAngleWheelOrderBasis.Unverified)]
        [InlineData("RFactor1", "Data.wheel01.surfaceType", "Data.wheel02.surfaceType", "Data.wheel03.surfaceType", "Data.wheel04.surfaceType", SlipAngleWheelOrderBasis.Unverified)]
        [InlineData("StockCarExtreme", "Data.wheel01.surfaceType", "Data.wheel02.surfaceType", "Data.wheel03.surfaceType", "Data.wheel04.surfaceType", SlipAngleWheelOrderBasis.Unverified)]
        [InlineData("CopaPetrobrasDeMarcas", "Data.wheel01.surfaceType", "Data.wheel02.surfaceType", "Data.wheel03.surfaceType", "Data.wheel04.surfaceType", SlipAngleWheelOrderBasis.Unverified)]
        [InlineData("FormulaTruck2013", "Data.wheel01.surfaceType", "Data.wheel02.surfaceType", "Data.wheel03.surfaceType", "Data.wheel04.surfaceType", SlipAngleWheelOrderBasis.Unverified)]
        [InlineData("Automobilista", "Data.wheel01.surfaceType", "Data.wheel02.surfaceType", "Data.wheel03.surfaceType", "Data.wheel04.surfaceType", SlipAngleWheelOrderBasis.Unverified)]
        [InlineData("SIMBINGTR2", "Data.wheel01.surfaceType", "Data.wheel02.surfaceType", "Data.wheel03.surfaceType", "Data.wheel04.surfaceType", SlipAngleWheelOrderBasis.Unverified)]
        [InlineData("GranTurismo7", "surfaceType01", "surfaceType02", "surfaceType03", "surfaceType04", SlipAngleWheelOrderBasis.FeedbackDataIndex)]
        [InlineData("BeamNgDrive", "wheel_contactMaterial_fl", "wheel_contactMaterial_fr", "wheel_contactMaterial_rl", "wheel_contactMaterial_rr", SlipAngleWheelOrderBasis.Named)]
        [InlineData("F12019", "PlayerCarTelemetryData.m_surfaceType01", "PlayerCarTelemetryData.m_surfaceType02", "PlayerCarTelemetryData.m_surfaceType03", "PlayerCarTelemetryData.m_surfaceType04", SlipAngleWheelOrderBasis.Unverified)]
        [InlineData("F12020", "PlayerCarTelemetryData.m_surfaceType01", "PlayerCarTelemetryData.m_surfaceType02", "PlayerCarTelemetryData.m_surfaceType03", "PlayerCarTelemetryData.m_surfaceType04", SlipAngleWheelOrderBasis.Unverified)]
        [InlineData("F12021", "PlayerCarTelemetryData.m_surfaceType01", "PlayerCarTelemetryData.m_surfaceType02", "PlayerCarTelemetryData.m_surfaceType03", "PlayerCarTelemetryData.m_surfaceType04", SlipAngleWheelOrderBasis.Unverified)]
        [InlineData("F12022", "PlayerCarTelemetryData.m_surfaceType01", "PlayerCarTelemetryData.m_surfaceType02", "PlayerCarTelemetryData.m_surfaceType03", "PlayerCarTelemetryData.m_surfaceType04", SlipAngleWheelOrderBasis.Unverified)]
        public void Surface_only_rows_match_the_spike(string id, string fl, string fr, string rl, string rr, SlipAngleWheelOrderBasis order)
        {
            Assert.False(SlipAngleRawSourceTable.TryGet(id, out _));
            Assert.True(SlipAngleRawSourceTable.TryGetSurfacePaths(id, out IReadOnlyList<string> paths, out SlipAngleWheelOrderBasis basis));
            Assert.Equal(new[] { fl, fr, rl, rr }, paths);
            Assert.Equal(order, basis);
        }

        [Fact]
        public void Native_row_surface_paths_are_reachable_through_the_surface_lookup()
        {
            Assert.True(SlipAngleRawSourceTable.TryGetSurfacePaths("F12024", out IReadOnlyList<string> paths, out SlipAngleWheelOrderBasis basis));
            Assert.Equal(Arr("PlayerCarTelemetryData.m_surfaceType", "", F1Remap), paths);
            Assert.Equal(SlipAngleWheelOrderBasis.ReaderRemap, basis);
            Assert.False(SlipAngleRawSourceTable.TryGetSurfacePaths("ACC", out _, out _)); // spike: none (searched)
            Assert.False(SlipAngleRawSourceTable.TryGetSurfacePaths("AssettoCorsa", out _, out _));
        }

        [Theory]
        [InlineData("IRacing", true)]
        [InlineData("Rennsport", true)]
        [InlineData("iracing", true)]
        [InlineData("AssettoCorsaCompetizione", false)]
        [InlineData("RFactor2", false)]
        [InlineData(null, false)]
        public void Vertical_acceleration_is_only_populated_by_iRacing_and_Rennsport(string id, bool expected)
            => Assert.Equal(expected, SlipAngleRawSourceTable.ReportsVerticalAcceleration(id));
    }

    /// <summary>The per-frame reader: transform, unit, wheel-order remap to FL/FR/RL/RR, path-missing
    /// verdict, never-throw.</summary>
    public class SlipAngleRawSourceReaderTests
    {
        private const double Dt = 0.1;

        private static Func<string, object> Props(IDictionary<string, object> values, Action<string> onRead = null)
            => name =>
            {
                onRead?.Invoke(name);
                return values.TryGetValue(name, out object v) ? v : null;
            };

        private static string P(string relative) => SlipAngleRawSourceTable.FullPropertyName(relative);

        private static TelemetryFrame Moving(double latG = 0.0, bool[] kerbs = null)
            => new TelemetryFrame(groundSpeedKmh: 100, speedKmh: 100, lateralG: latG, wheelsOnKerbs: kerbs);

        [Fact]
        public void F1_raw_RL_RR_FL_FR_is_remapped_to_FL_FR_RL_RR()
        {
            var values = new Dictionary<string, object>
            {
                [P("PacketMotionExData.m_wheelSlipAngle01")] = 1.0f, // raw RL
                [P("PacketMotionExData.m_wheelSlipAngle02")] = 2.0f, // raw RR
                [P("PacketMotionExData.m_wheelSlipAngle03")] = 3.0f, // raw FL
                [P("PacketMotionExData.m_wheelSlipAngle04")] = 4.0f, // raw FR
            };
            SlipAngleRawSourceTable.TryGet("F12024", out SlipAngleRawSourceEntry e);
            Corners? c = SlipAngleRawSourceReader.ReadCorners(e, Props(values));
            Assert.Equal(new Corners(3, 4, 1, 2), c);
        }

        [Fact]
        public void Drift21_radians_round_trip_to_degrees_without_waiting_for_detection()
        {
            var values = new Dictionary<string, object>
            {
                [P("RaceTelemetry.TireSlipAngleRadians_FrontLeft")] = 0.1,
                [P("RaceTelemetry.TireSlipAngleRadians_FrontRight")] = 0.2,
                [P("RaceTelemetry.TireSlipAngleRadians_BackLeft")] = -0.1,
                [P("RaceTelemetry.TireSlipAngleRadians_BackRight")] = 0.0,
            };
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            SlipAngleInput input = reader.Read("Drift21", Props(values), Moving(), Dt);
            Assert.True(input.HasNativeSource);
            Assert.Equal(SlipAngleUnit.Radians, input.Unit);
            Assert.NotNull(input.NativeWheelDeg);
            Assert.Equal(0.1 * 180 / Math.PI, input.NativeWheelDeg.Value.FrontLeft, 9);
            Assert.Equal(0.2 * 180 / Math.PI, input.NativeWheelDeg.Value.FrontRight, 9);
            Assert.Equal(-0.1 * 180 / Math.PI, input.NativeWheelDeg.Value.RearLeft, 9);
            Assert.Equal(0.0, input.NativeWheelDeg.Value.RearRight, 9);
            Assert.False(input.IsNative); // one frame is not 5 s of evidence
        }

        [Fact]
        public void LFS_tangent_is_converted_with_atan_and_remapped()
        {
            var values = new Dictionary<string, object>
            {
                [P("OutSim2.OSWheels01.TanSlipAngle")] = 0.0f,  // raw RL
                [P("OutSim2.OSWheels02.TanSlipAngle")] = -1.0f, // raw RR
                [P("OutSim2.OSWheels03.TanSlipAngle")] = 1.0f,  // raw FL
                [P("OutSim2.OSWheels04.TanSlipAngle")] = 0.5f,  // raw FR
            };
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            SlipAngleInput input = reader.Read("LFS", Props(values), Moving(), Dt);
            Corners deg = input.NativeWheelDeg.Value;
            Assert.Equal(45.0, deg.FrontLeft, 9);
            Assert.Equal(Math.Atan(0.5) * 180 / Math.PI, deg.FrontRight, 9);
            Assert.Equal(0.0, deg.RearLeft, 9);
            Assert.Equal(-45.0, deg.RearRight, 9);
        }

        [Fact]
        public void RFactor2_computes_atan2_of_lateral_patch_velocity_over_abs_longitudinal_ground_velocity()
        {
            var values = new Dictionary<string, object>();
            double[] lat = { 1.0, -2.0, 0.5, 0.0 };
            double[] lon = { -1.0, 20.0, 10.0, 30.0 };
            for (int i = 0; i < 4; i++)
            {
                values[P($"CurrentPlayerTelemetry.mWheels{i + 1:00}.mLateralPatchVel")] = lat[i];
                values[P($"CurrentPlayerTelemetry.mWheels{i + 1:00}.mLongitudinalGroundVel")] = lon[i];
            }
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            SlipAngleInput input = reader.Read("RFactor2", Props(values), Moving(), Dt);
            Corners deg = input.NativeWheelDeg.Value;
            for (int i = 0; i < 4; i++)
                Assert.Equal(Math.Atan2(lat[i], Math.Abs(lon[i])) * 180 / Math.PI, deg[i], 9);
            Assert.False(input.IsProvisionalSource);
        }

        [Fact]
        public void NASCAR_rows_are_computed_and_flagged_provisional()
        {
            var values = new Dictionary<string, object>();
            string[] w = { "lf", "rf", "lr", "rr" };
            for (int i = 0; i < 4; i++)
            {
                values[P($"LCarPacket.wheel_{w[i]}.lat_terrain_speed")] = (double)(i + 1);
                values[P($"LCarPacket.wheel_{w[i]}.lon_terrain_speed")] = -40.0;
            }
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            SlipAngleInput input = reader.Read("NASCARHeat5", Props(values), Moving(), Dt);
            Assert.True(input.IsProvisionalSource);
            Assert.Equal(Math.Atan2(3.0, 40.0) * 180 / Math.PI, input.NativeWheelDeg.Value.RearLeft, 9);
        }

        [Fact]
        public void An_unlisted_unverified_row_reads_radians_from_the_first_frame()
        {
            // owner, 2026-11-20: radians is the default for a native field the table and the lists leave open.
            var native = new SlipAngleNativeSupport();
            var reader = new SlipAngleRawSourceReader(native);
            var values = new Dictionary<string, object>();
            SlipAngleInput input = null;
            for (int f = 0; f < 70; f++)
            {
                double v = 0.03 + 0.02 * (f % 5); // radians-scale values
                for (int i = 0; i < 4; i++) values[P($"Physics.slipAngle{i + 1:00}")] = (float)v;
                // cornering episodes: 0.5 s at 1.0 g, 0.2 s at 0.2 g (ruling R16 episode voting)
                input = reader.Read("AssettoCorsaCompetizione", Props(values), Moving(latG: f % 7 < 5 ? 1.0 : 0.2), Dt);
                if (f == 0)
                {
                    Assert.Equal(SlipAngleUnit.Radians, input.Unit);
                    Assert.Equal((float)v * 180 / Math.PI, input.NativeWheelDeg.Value.FrontLeft, 4);
                    Assert.False(input.IsNative);   // promotion still needs its 5 s of evidence
                }
            }
            Assert.True(input.IsNative);
            Assert.Equal(SlipAngleUnit.Radians, input.Unit);
            double expectedRad = 0.03 + 0.02 * (69 % 5);
            Assert.Equal((float)expectedRad * 180 / Math.PI, input.NativeWheelDeg.Value.FrontLeft, 4);
        }

        [Fact]
        public void A_partially_missing_frame_gives_no_wheels()
        {
            var values = new Dictionary<string, object>
            {
                [P("Physics.slipAngle01")] = 1.0f,
                [P("Physics.slipAngle02")] = 1.0f,
                [P("Physics.slipAngle03")] = "not a number",
            };
            SlipAngleRawSourceTable.TryGet("ACC", out SlipAngleRawSourceEntry e);
            Assert.Null(SlipAngleRawSourceReader.ReadCorners(e, Props(values)));
        }

        [Fact]
        public void Missing_paths_are_cached_as_a_per_game_verdict_and_no_longer_read()
        {
            int reads = 0;
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            Func<string, object> props = Props(new Dictionary<string, object>(), _ => reads++);
            for (int f = 0; f < SlipAngleRawSourceReader.MissingVerdictFrames; f++)
                reader.Read("LMU", props, Moving(), Dt);
            Assert.True(reader.IsSourceMissing("LMU"));
            Assert.False(reader.IsSourceMissing("RFactor2"));
            int before = reads;
            SlipAngleInput input = reader.Read("LMU", props, Moving(), Dt);
            Assert.True(reads - before <= 4); // surface paths only; slip paths no longer read
            Assert.False(input.HasNativeSource);
            Assert.Null(input.NativeWheelDeg);
        }

        [Fact]
        public void Nulls_while_not_moving_never_rule_the_source_missing()
        {
            // Review IMPORTANT 2: 10 s+ of nulls at standstill (menus, garage, loading), then real values.
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            var empty = Props(new Dictionary<string, object>());
            var standstill = new TelemetryFrame(groundSpeedKmh: 0, speedKmh: 0);
            for (int f = 0; f < SlipAngleRawSourceReader.MissingVerdictFrames + 100; f++)
                reader.Read("Drift21", empty, standstill, Dt);
            for (int f = 0; f < SlipAngleRawSourceReader.MissingVerdictFrames - 1; f++)
                reader.Read("Drift21", empty, f % 100 == 0 ? standstill : Moving(), Dt); // never 600 moving in a row
            Assert.False(reader.IsSourceMissing("Drift21"));

            var values = new Dictionary<string, object>();
            SlipAngleInput input = null;
            for (int f = 0; f < 60; f++)
            {
                double v = 0.03 + 0.02 * (f % 5);
                values[P("RaceTelemetry.TireSlipAngleRadians_FrontLeft")] = v;
                values[P("RaceTelemetry.TireSlipAngleRadians_FrontRight")] = v;
                values[P("RaceTelemetry.TireSlipAngleRadians_BackLeft")] = v;
                values[P("RaceTelemetry.TireSlipAngleRadians_BackRight")] = v;
                input = reader.Read("Drift21", Props(values), Moving(), Dt);
            }
            Assert.True(input.IsNative);
        }

        [Fact]
        public void Unverified_radian_game_with_an_early_spin_is_scaled_as_radians()
        {
            // Review CRITICAL 1 end to end: the first high-G episode is a 1 s spin through 3 rad at 0.9 g
            // (it votes degrees), then ordinary radian cornering episodes outvote it.
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            var values = new Dictionary<string, object>();
            SlipAngleInput input = null;
            for (int f = 0; f < 90; f++)
            {
                bool spin = f < 10;
                double v = spin ? 0.3 * (f + 1) : 0.03 + 0.02 * (f % 5);
                double latG = spin ? 0.9 : f < 12 ? 0.2 : (f - 12) % 7 < 5 ? 1.0 : 0.2;
                for (int i = 0; i < 4; i++) values[P($"Physics.slipAngle{i + 1:00}")] = v;
                input = reader.Read("ACC", Props(values), Moving(latG: latG), Dt);
                if (f == 15) Assert.False(input.IsNative);
            }
            Assert.True(input.IsNative);
            Assert.Equal(SlipAngleUnit.Radians, input.Unit);
            Assert.Equal((0.03 + 0.02 * (89 % 5)) * 180 / Math.PI, input.NativeWheelDeg.Value.FrontLeft, 9);
        }

        [Fact]
        public void A_single_hit_resets_the_missing_count()
        {
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            var empty = Props(new Dictionary<string, object>());
            var hit = Props(new Dictionary<string, object> { [P("Physics.slipAngle01")] = 1.0f });
            for (int f = 0; f < SlipAngleRawSourceReader.MissingVerdictFrames - 1; f++) reader.Read("ACC", empty, Moving(), Dt);
            reader.Read("ACC", hit, Moving(), Dt);
            for (int f = 0; f < SlipAngleRawSourceReader.MissingVerdictFrames - 1; f++) reader.Read("ACC", empty, Moving(), Dt);
            Assert.False(reader.IsSourceMissing("ACC"));
        }

        [Fact]
        public void A_throwing_property_reader_never_throws_and_degrades_that_game()
        {
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            SlipAngleInput input = reader.Read("Drift21", _ => throw new InvalidOperationException("boom"), Moving(), Dt);
            Assert.Null(input.NativeWheelDeg);
            Assert.True(reader.IsSourceMissing("Drift21"));
            Assert.Contains(Core.Health.HealthRegistry.Snapshot(), h => h.Subsystem == Core.Health.HealthSubsystems.SlipAngleSource);
        }

        [Fact]
        public void Game_without_a_row_has_no_native_source()
        {
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            SlipAngleInput input = reader.Read("AssettoCorsa", Props(new Dictionary<string, object>()), Moving(), Dt);
            Assert.False(input.HasNativeSource);
            Assert.Null(input.NativeWheelDeg);
            Assert.False(input.IsNative);
            Assert.Equal("AssettoCorsa", input.GameId);
        }

        [Fact]
        public void Kerbs_are_copied_from_the_frame_and_raw_surface_is_remapped_but_not_interpreted()
        {
            var values = new Dictionary<string, object>
            {
                [P("PlayerCarTelemetryData.m_surfaceType01")] = (byte)1, // raw RL
                [P("PlayerCarTelemetryData.m_surfaceType02")] = (byte)2, // raw RR
                [P("PlayerCarTelemetryData.m_surfaceType03")] = (byte)3, // raw FL
                [P("PlayerCarTelemetryData.m_surfaceType04")] = (byte)4, // raw FR
            };
            var kerbs = new[] { true, false, false, true };
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            SlipAngleInput input = reader.Read("F12025", Props(values), Moving(kerbs: kerbs), Dt);
            Assert.Equal(new[] { true, false, false, true }, input.KerbWheels);
            Assert.NotSame(kerbs, input.KerbWheels);
            Assert.Equal(new[] { "3", "4", "1", "2" }, input.RawWheelSurface);
        }

        [Fact]
        public void Surface_only_game_reports_raw_surface_with_no_native_source()
        {
            var values = new Dictionary<string, object>
            {
                [P("mTerrain01")] = 0u, [P("mTerrain02")] = 5u, [P("mTerrain04")] = 7u,
            };
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            SlipAngleInput input = reader.Read("Automobilista2", Props(values), Moving(), Dt);
            Assert.False(input.HasNativeSource);
            Assert.Equal(new[] { "0", "5", null, "7" }, input.RawWheelSurface);
            Assert.Null(input.KerbWheels);
        }

        [Fact]
        public void No_surface_row_gives_null_surface()
        {
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            SlipAngleInput input = reader.Read("AssettoCorsa", Props(new Dictionary<string, object>()), Moving(), Dt);
            Assert.Null(input.RawWheelSurface);
        }

        [Fact]
        public void Null_game_and_null_frame_never_throw()
        {
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            SlipAngleInput a = reader.Read(null, null, null, Dt);
            Assert.NotNull(a);
            Assert.Null(a.NativeWheelDeg);
            SlipAngleInput b = reader.Read("Drift21", null, null, double.NaN);
            Assert.NotNull(b);
            Assert.Null(b.NativeWheelDeg);
        }
    }
}

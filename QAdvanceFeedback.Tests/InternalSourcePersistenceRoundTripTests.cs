using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Task G (docs\internal-source-learning-design.md 3.3): saved learning keeps the full key - game, car,
    /// source, surface (Slip Angle also target) - and every key's own data survives a save and a reload
    /// through the runtime parameter file, with no pooled or source-less entry written.
    /// </summary>
    public class InternalSourcePersistenceRoundTripTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "qaf-internal-roundtrip-" + Guid.NewGuid() + ".json");

        public void Dispose()
        {
            if (File.Exists(_path)) File.Delete(_path);
        }

        private static ITelemetrySample Braking(double g)
            => new TelemetrySample(new TelemetryFrame(groundSpeedKmh: 100.0, longitudinalG: -g, brakePercent: 80.0),
                new TelemetryFrame(groundSpeedKmh: 101.0), DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

        private static ITelemetrySample Accelerating(double g)
            => new TelemetrySample(new TelemetryFrame(groundSpeedKmh: 101.0, longitudinalG: g, throttlePercent: 90.0),
                new TelemetryFrame(groundSpeedKmh: 100.0), DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

        private static void Drive(NormalizedWheelLockSlipEngine engine, string car, string lockSource, string slipSource, double scale)
        {
            for (int c = 0; c < 25; c++)
            {
                for (int i = 0; i < 8; i++)
                {
                    double t = i / 7.0;
                    var active = Corners.Uniform(scale * (30.0 + 10.0 * t));
                    var raw = new Corners(60 + 15 * t, 62 + 14 * t, 50, 52);
                    engine.Compute(Braking(3.0 * (1.0 - 0.006 * i)), active, Corners.Zero, "G", car,
                        lockSourceIdentity: lockSource, slipSourceIdentity: slipSource, layer3RawLockWheels: raw);
                    engine.Compute(Accelerating(1.2 * (1.0 - 0.006 * i)), Corners.Zero, active, "G", car,
                        lockSourceIdentity: lockSource, slipSourceIdentity: slipSource, layer3RawSlipWheels: raw);
                }
                for (int i = 0; i < 70; i++)
                {
                    engine.Compute(Braking(3.0), Corners.Uniform(scale * 40.0), Corners.Zero, "G", car,
                        lockSourceIdentity: lockSource, slipSourceIdentity: slipSource, layer3RawLockWheels: Corners.Uniform(75.0));
                    engine.Compute(Accelerating(1.2), Corners.Zero, Corners.Uniform(scale * 40.0), "G", car,
                        lockSourceIdentity: lockSource, slipSourceIdentity: slipSource, layer3RawSlipWheels: Corners.Uniform(75.0));
                }
            }
        }

        private static string Json<T>(Dictionary<string, T> d)
            => JsonConvert.SerializeObject(d.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList());

        private static string GripCore(Dictionary<string, GripLearnerState> d)
            => string.Join("\n", d.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + " " + kv.Value.PeakG.ToString("R") + " " + kv.Value.Samples));

        private static string[] Segments(string key) => key.Split(new[] { "|#|" }, StringSplitOptions.None);

        [Fact]
        public void A_multi_source_state_survives_save_and_reload_with_its_full_keys()
        {
            string lockRaw = RawSourceFallback.RawIdentity(true);
            string slipRaw = RawSourceFallback.RawIdentity(false);

            var engine = new NormalizedWheelLockSlipEngine();
            Drive(engine, "Car1", lockRaw, slipRaw, 2.0);                       // Plugin Internal selected
            Drive(engine, "Car1", "ShakeIt.Lock", "ShakeIt.Slip", 1.0);         // ShakeIt active, internal alongside
            Drive(engine, "Car2", "Custom.Lock", "Viper.Slip", 0.5);            // another car, other sources

            var slipAngle = new SlipAngleFrameProcessor(new KeyedSlipAngleLearner(), null);
            for (int i = 0; i < 900; i++)
            {
                double phase = (i % 240) / 240.0;
                var frame = new TelemetryFrame(groundSpeedKmh: 100.0, brakePercent: 50, throttlePercent: 0,
                    lateralLocalVelocity: 0.5 + 4.5 * Math.Sin(Math.PI * phase), forwardLocalVelocity: 27.8,
                    lateralG: 0.3 + 1.1 * Math.Sin(Math.PI * phase), longitudinalG: 0.0, yawRateRadPerSec: 0.3, isAirborne: false);
                double a = 2.0 + 9.0 * Math.Sin(Math.PI * phase);
                slipAngle.Process(new SlipAngleFrameRequest
                {
                    GameId = "G", CarId = "Car1", Frame = frame, Dt = 1.0 / 60.0, Input = new SlipAngleInput("G"),
                });
            }

            var lockGrip = engine.LockLearners.ExportAll();
            var slipGrip = engine.SlipLearners.ExportAll();
            var lockScale = engine.LockScaleLearner.ExportAll();
            var slipScale = engine.SlipScaleLearner.ExportAll();
            var anchors = engine.LockAnchors.ExportAll();
            var lockPhysical = engine.LockPhysicalReference.ExportAll();
            var slipAngleState = slipAngle.Learner.Export();

            // Every source keeps its own entries - nothing pooled.
            foreach (string source in new[] { lockRaw, "ShakeIt.Lock", "Custom.Lock" })
            {
                Assert.Contains(lockGrip.Keys, k => Segments(k)[2] == source);
                Assert.Contains(lockScale.Keys, k => Segments(k)[2] == source);
            }
            foreach (string source in new[] { slipRaw, "ShakeIt.Slip", "Viper.Slip" })
                Assert.Contains(slipGrip.Keys, k => Segments(k)[2] == source);
            Assert.Contains(lockScale.Keys, k => Segments(k)[1] == "Car2" && Segments(k)[2] == lockRaw);

            // Full key shape: game | car | source | surface, and never the retired hidden key.
            foreach (string key in lockGrip.Keys.Concat(slipGrip.Keys).Concat(lockScale.Keys).Concat(slipScale.Keys).Concat(anchors.Keys))
            {
                string[] s = Segments(key);
                Assert.Equal(4, s.Length);
                Assert.Equal("G", s[0]);
                Assert.False(string.IsNullOrEmpty(s[1]));
                Assert.False(string.IsNullOrEmpty(s[2]), "a source-less entry: " + key);
                Assert.False(InternalSourceKeyMigration.IsHiddenKey(key));
            }
            foreach (string key in lockGrip.Keys.Concat(slipGrip.Keys))
                Assert.Contains(Segments(key)[3], new[] { NormalizedWheelLockSlipEngine.SealedSurfaceBucket, NormalizedWheelLockSlipEngine.LooseSurfaceBucket });

            // Slip Angle: game | car | source/target | surface, Plugin Internal present.
            var parsedSources = new HashSet<string>();
            foreach (string key in slipAngleState.Keys)
            {
                Assert.True(KeyedSlipAngleLearner.TryParseKey(key, out string g, out string c, out string source, out SlipAngleTarget target, out _), key);
                Assert.Equal(("G", "Car1"), (g, c));
                parsedSources.Add(source + "/" + target);
            }
            Assert.Contains(SlipAngleFrameProcessor.FallbackIdentity + "/All", parsedSources);
            Assert.Contains(SlipAngleFrameProcessor.FallbackIdentity + "/Rear", parsedSources);

            using (var store = new RuntimeStore(_path, flushInterval: TimeSpan.FromHours(1)))
            {
                store.SaveLockLearners(lockGrip);
                store.SaveSlipLearners(slipGrip);
                store.SaveLockScaleLearners(lockScale);
                store.SaveSlipScaleLearners(slipScale);
                store.SaveLockAnchors(anchors);
                store.SaveLockPhysicalReference(lockPhysical);
                store.SaveSlipAngleLearners(slipAngleState);
                store.Flush();
            }

            // 1. The FILE holds every key's own data, byte for byte.
            using (var store = new RuntimeStore(_path, flushInterval: TimeSpan.FromHours(1)))
            {
                store.LoadLockLearners(out var lg);
                store.LoadSlipLearners(out var sg);
                store.LoadLockScaleLearners(out var ls);
                store.LoadSlipScaleLearners(out var ss);
                store.LoadLockAnchors(out var la);
                store.LoadLockPhysicalReference(out var lp);
                store.LoadSlipAngleLearners(out var sa);
                Assert.Equal(Json(lockGrip), Json(lg));
                Assert.Equal(Json(slipGrip), Json(sg));
                Assert.Equal(Json(lockScale), Json(ls));
                Assert.Equal(Json(slipScale), Json(ss));
                Assert.Equal(Json(anchors), Json(la));
                Assert.Equal(Json(lockPhysical), Json(lp));
                Assert.Equal(Json(slipAngleState), Json(sa));
            }

            // 2. Loaded back into fresh learners, every key comes back under the same full key with its
            // own data.
            var reloaded = new NormalizedWheelLockSlipEngine();
            var reloadedSlipAngle = new KeyedSlipAngleLearner();
            using (var store = new RuntimeStore(_path, flushInterval: TimeSpan.FromHours(1)))
            {
                // The same load path the plugin takes (migration included - a no-op for a file with no hidden key).
                store.LoadLockLearners(out var lg);
                reloaded.LockLearners.ImportAll(InternalSourceKeyMigration.MigrateGrip(GripLearnerKeyMigration.MigrateLegacyKeys(lg), lockRaw));
                store.LoadSlipLearners(out var sg);
                reloaded.SlipLearners.ImportAll(InternalSourceKeyMigration.MigrateGrip(GripLearnerKeyMigration.MigrateLegacyKeys(sg), slipRaw));
                store.LoadLockScaleLearners(out var ls);
                reloaded.LockScaleLearner.ImportAll(InternalSourceKeyMigration.MigrateScale(ls, lockRaw));
                store.LoadSlipScaleLearners(out var ss);
                reloaded.SlipScaleLearner.ImportAll(InternalSourceKeyMigration.MigrateScale(ss, slipRaw));
                store.LoadLockAnchors(out var la);
                reloaded.LockAnchors.ImportAll(InternalSourceKeyMigration.MigrateAnchors(la, lockRaw));
                store.LoadLockPhysicalReference(out var lp);
                reloaded.LockPhysicalReference.ImportAll(lp);
                store.LoadSlipAngleLearners(out var sa);
                reloadedSlipAngle.Import(sa);
            }

            // Grip learners: the learned peak and evidence per key. (A pending raise/lower CANDIDATE with
            // zero hits is not restored by GripLearner.Load by design - it carries no evidence - so the
            // transient candidate fields are not compared.)
            Assert.Equal(GripCore(lockGrip), GripCore(reloaded.LockLearners.ExportAll()));
            Assert.Equal(GripCore(slipGrip), GripCore(reloaded.SlipLearners.ExportAll()));
            Assert.Equal(Json(lockScale), Json(reloaded.LockScaleLearner.ExportAll()));
            Assert.Equal(Json(slipScale), Json(reloaded.SlipScaleLearner.ExportAll()));
            Assert.Equal(Json(anchors), Json(reloaded.LockAnchors.ExportAll()));
            Assert.Equal(GripCore(lockPhysical), GripCore(reloaded.LockPhysicalReference.ExportAll()));
            Assert.Equal(Json(slipAngleState), Json(reloadedSlipAngle.Export()));
        }
    }
}

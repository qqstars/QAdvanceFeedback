using System;
using System.Collections.Generic;
using System.Linq;
using GameReaderCommon;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Clutch;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.ShakeItEffects;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Clutch Bite (docs\clutch-bite-design.md):
    /// <list type="bullet">
    /// <item>resolves the clutch (the game's, falling back to a pedal axis);</item>
    /// <item>computes and publishes <c>Clutch.Bite.Output</c>, plus the snapshot the native effect plays;</item>
    /// <item>learns the bite point per (game, car, clutch source) and saves every accepted sample at once.</item>
    /// </list>
    /// The Test Effect publishes through <see cref="PublishClutchTest"/>.
    /// </summary>
    public sealed partial class QAdvanceFeedback
    {
        private readonly object _clutchLock = new object();
        private readonly ClutchSourceResolver _clutchResolver = new ClutchSourceResolver();
        private readonly ClutchBiteEngine _clutchEngine = new ClutchBiteEngine();
        private readonly ClutchBiteLearner _clutchLearner = new ClutchBiteLearner();
        private string _lastClutchKey;
        private volatile bool _testClutchActive;
        private FrameSnapshot<double?> _latestClutch;
        private volatile ClutchUiState _clutchUi = new ClutchUiState();

        /// <summary>What the Clutch view models show, refreshed by the data thread.</summary>
        public sealed class ClutchUiState
        {
            public ClutchSourceKind Kind { get; set; }
            public double? Input { get; set; }
            public string Key { get; set; }
            public ClutchBiteStatus Status { get; set; } = new ClutchBiteStatus { Final = 50 };
        }

        /// <summary>The latest Clutch.Bite.Output (live or test) - what the native effect plays.</summary>
        public FrameSnapshot<double?> LatestClutch => System.Threading.Volatile.Read(ref _latestClutch);

        /// <summary>True while a Clutch Test Effect drives Clutch.Bite.Output.</summary>
        public bool TestClutchActive => _testClutchActive;

        public ClutchUiState ClutchUi => _clutchUi;

        /// <summary>The bite point the Test Effect uses: the most recent in-game key's final bite, else
        /// the configured value.</summary>
        public double FinalBiteForTest
        {
            get
            {
                var s = _settings?.Clutch;
                if (s == null) return 50.0;
                lock (_clutchLock)
                    return _clutchLearner.Status(_lastClutchKey, s.BitePoint, s.ToParameters().Auto, s.ToParameters().Learning.MatureSamples).Final;
            }
        }

        /// <summary>The car the Test Effect's bite belongs to (game | car), or null.</summary>
        public string FinalBiteForTestKey => _lastClutchKey;

        private void LoadClutchLearning()
        {
            _runtimeStore.LoadClutchBiteLearners(out Dictionary<string, ClutchBiteLearnerState> data);
            lock (_clutchLock)
            {
                _clutchLearner.Import(data);
                _lastClutchKey = _runtimeStore.LoadLastClutchKey();
            }
        }

        /// <summary>
        /// Puts the learning into the runtime cache. The store's background timer writes it to disk
        /// within seconds, and <c>End</c> flushes synchronously on shutdown. A synchronous flush here would
        /// block SimHub's data thread on a multi-MB write at every car or source switch (review fix I1).
        /// Saved under the lock, so a concurrent Forget can never be undone by an older export.
        /// </summary>
        private void SaveClutchLearningNow()
        {
            lock (_clutchLock)
            {
                _runtimeStore?.SaveClutchBiteLearners(_clutchLearner.ExportAll());
                _runtimeStore?.SaveLastClutchKey(_lastClutchKey);
            }
        }

        /// <summary>Controller axes SimHub publishes (<c>JoystickPlugin.*</c>), for the fallback picker.</summary>
        public IReadOnlyList<string> ControllerAxes()
        {
            try
            {
                List<string> names = PluginManager?.GetAllPropertiesNames();
                if (names == null) return new string[0];
                return names.Where(n => n != null && n.StartsWith("JoystickPlugin.", StringComparison.Ordinal))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { return new string[0]; }
        }

        /// <summary>A controller axis, 0..1 (SimHub publishes them as 0..65535), or null when absent.</summary>
        public double? ReadAxis01(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                object raw = PluginManager?.GetPropertyValue(name);
                if (raw == null) return null;
                double v = Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture);
                return ClampMath.IsFinite(v) ? ClampMath.To01(v / 65535.0) : (double?)null;
            }
            catch { return null; }
        }

        /// <summary>Out of a game session: the live output is 0 (unless a test owns it).</summary>
        private void UpdateClutchOutOfSession()
        {
            // A standing start cannot continue across a pause or menu (review fix I4).
            lock (_clutchLock) _clutchEngine.Interrupt();
            if (_testClutchActive) return;
            PublishClutchValue(0.0);
        }

        /// <summary>One in-session frame.</summary>
        private void UpdateClutch(GameData data, TelemetrySample sample, string gameId, string carId)
        {
            try
            {
                var s = _settings.Clutch;
                ITelemetryFrame nf = sample.New;
                double? axis = ReadAxis01(s.FallbackAxis);
                string gear = nf?.Gear;
                bool inGear = !string.IsNullOrEmpty(gear) && gear != "N" && gear != "R" && gear != "0";
                bool loose = nf?.WheelOnLooseSurfaceFrontLeft == true || nf?.WheelOnLooseSurfaceFrontRight == true
                    || nf?.WheelOnLooseSurfaceRearLeft == true || nf?.WheelOnLooseSurfaceRearRight == true;
                bool valid = data.NewData?.Spectating != true && nf?.IsReplay != true;
                var frame = new ClutchFrame(sample.FrameTime.Ticks / (double)TimeSpan.TicksPerSecond, nf?.ClutchPercent,
                    nf?.Rpm, nf?.GroundSpeedKmh ?? nf?.SpeedKmh, nf?.ThrottlePercent, inGear, loose, valid);

                double output;
                bool accepted, keyChanged;
                ClutchReading reading;
                lock (_clutchLock)
                {
                    reading = _clutchResolver.Resolve(gameId, nf?.ClutchPercent, axis, s.FallbackAxis, s.InvertAxis);
                    string before = _clutchEngine.CurrentKey;
                    output = _clutchEngine.Step(reading, frame, gameId, carId, s.ToParameters(), _clutchLearner, out accepted);
                    keyChanged = !string.Equals(before, _clutchEngine.CurrentKey, StringComparison.Ordinal);
                    // ONE key for the readout, the graph, the Test bite and Forget (review fix I2): the car
                    // whose clutch is really in use (game or axis), kept after the session ends.
                    if (reading.Kind != ClutchSourceKind.None) _lastClutchKey = _clutchEngine.CurrentKey;
                    _clutchUi = new ClutchUiState
                    {
                        Kind = reading.Kind,
                        Input = reading.Clutch0100,
                        Key = _lastClutchKey,
                        Status = _clutchLearner.Status(_lastClutchKey, s.BitePoint, s.ToParameters().Auto, s.ToParameters().Learning.MatureSamples),
                    };
                }

                // Every accepted sample is saved at once (standing starts are rare); a car or source
                // switch saves too, so the next session continues where this one stopped.
                if (accepted || keyChanged) SaveClutchLearningNow();

                if (!_testClutchActive) PublishClutchValue(output);
                ClutchBiteStatus st = _clutchUi.Status;
                _publisher.UpdateClutchDiagnostics(reading.Kind.ToString(), reading.Clutch0100, st.Final, st.Learned, st.Count);
            }
            catch (Exception e)
            {
                // Never into SimHub's data loop: the output reads 0 and the health panel says why.
                HealthRegistry.Report(HealthSubsystems.ClutchBite, HealthSeverity.Degraded, "Health.Impact.ClutchBite", e.ToString());
                if (!_testClutchActive) PublishClutchValue(0.0);
            }
        }

        private void PublishClutchValue(double output)
        {
            _publisher?.UpdateClutch(output);
            System.Threading.Volatile.Write(ref _latestClutch, new FrameSnapshot<double?>(output, FrameSnapshot.NowTicks()));
        }

        /// <summary>
        /// One Clutch Test Effect frame. The native effect plays only <see cref="LatestClutch"/>, so it
        /// plays exactly the published value. Null releases the output: stop claiming it, then publish 0.
        /// </summary>
        public void PublishClutchTest(double? output0100)
        {
            if (output0100.HasValue)
            {
                _testClutchActive = true;
                PublishClutchValue(output0100.Value);
            }
            else
            {
                _testClutchActive = false;
                PublishClutchValue(0.0);
            }
        }

        /// <summary>Clears the learned bite of the current car (or, out of game, the last one) and saves.</summary>
        public void ForgetClutchBite()
        {
            lock (_clutchLock)
            {
                // The car the readout shows (review fix I2).
                _clutchLearner.Forget(_lastClutchKey);
            }
            SaveClutchLearningNow();
        }
    }
}

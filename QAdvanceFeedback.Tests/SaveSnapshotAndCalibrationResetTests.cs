using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.RawCalculator.Calibration;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// The save takes the snapshot (owner, 2026-11-25): the data thread hands its learners over only when a save asks
    /// (or on a key change), never per frame; and the slip / lock calibration of the current track and car can be reset.
    /// </summary>
    public sealed class SaveSnapshotAndCalibrationResetTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "qaf-snapshot-" + Guid.NewGuid().ToString("N") + ".json");

        public void Dispose()
        {
            try { File.Delete(_path); } catch (IOException) { }
        }

        private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < timeout)
            {
                if (condition()) return true;
                Thread.Sleep(5);
            }
            return condition();
        }

        private static Dictionary<string, GripLearnerState> Learners(double peak) =>
            new Dictionary<string, GripLearnerState> { ["G|#|C"] = new GripLearnerState { PeakG = peak, Samples = 10 } };

        [Fact]
        public void A_tick_asks_the_data_thread_for_its_learners_only_while_frames_are_learning()
        {
            using (var store = new RuntimeStore(_path, flushInterval: TimeSpan.FromMilliseconds(30)))
            {
                // No learning frame yet: a tick never asks (nothing can have changed).
                Thread.Sleep(150);
                Assert.False(store.SnapshotRequested);

                // A "data thread" that learns every frame and answers a request with its data.
                int answered = 0;
                using (var stop = new CancellationTokenSource())
                {
                    var frames = new Thread(() =>
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            store.MarkLearningFrame();
                            if (store.SnapshotRequested)
                            {
                                store.SaveLockLearners(Learners(1.25));
                                store.SnapshotTaken();
                                Interlocked.Increment(ref answered);
                            }
                            Thread.Sleep(16);
                        }
                    }) { IsBackground = true };
                    frames.Start();

                    Assert.True(WaitFor(() => File.Exists(_path) && File.ReadAllText(_path).Contains("1.25"), TimeSpan.FromSeconds(5)));
                    stop.Cancel();
                    frames.Join();
                }
                Assert.True(answered >= 1);
            }
        }

        [Fact]
        public void SaveSoon_writes_without_waiting_for_the_interval()
        {
            using (var store = new RuntimeStore(_path, flushInterval: TimeSpan.FromMinutes(10)))
            {
                store.SaveLockLearners(Learners(2.5));
                store.SaveSoon();
                Assert.True(WaitFor(() => File.Exists(_path) && File.ReadAllText(_path).Contains("2.5"), TimeSpan.FromSeconds(5)));
            }
        }

        [Fact]
        public void An_unchanged_snapshot_does_not_rewrite_the_file()
        {
            using (var store = new RuntimeStore(_path, flushInterval: TimeSpan.FromMinutes(10)))
            {
                store.SaveLockLearners(Learners(3.0));
                store.Flush();
                DateTime written = File.GetLastWriteTimeUtc(_path);
                Thread.Sleep(50);
                store.SaveLockLearners(Learners(3.0));   // handed over again, identical
                store.Flush();
                Assert.Equal(written, File.GetLastWriteTimeUtc(_path));
            }
        }

        // ---------------------------------------------------------------- reset

        private static CalibrationDataProvider Driven(string track, string car)
        {
            var provider = new CalibrationDataProvider();
            var rps = new double?[] { 30.0, 30.0, 30.0, 30.0 };
            var slip = new double?[] { 0.1, 0.2, 0.3, 0.4 };
            provider.Update(track, car, true, true, 120.0, 0.0, rps, slip);
            return provider;
        }

        [Fact]
        public void Reset_deletes_every_metric_of_the_key_and_nothing_else()
        {
            CalibrationDataProvider provider = Driven("spa", "gt3");
            var other = Driven("monza", "gt3").CalibrationData;
            foreach (KeyValuePair<string, CalibrationData> kv in other) provider.CalibrationData[kv.Key] = kv.Value;
            Assert.Equal(6, provider.CalibrationData.Count);   // Slip + RPS front + RPS rear, per track
            provider.ExportCalibrations(out _);

            Assert.True(provider.HasCalibration("spa", "gt3"));
            Assert.Equal(3, provider.ResetCalibration("spa", "gt3"));
            Assert.False(provider.HasCalibration("spa", "gt3"));
            Assert.True(provider.HasCalibration("monza", "gt3"));
            Assert.Equal(3, provider.CalibrationData.Count);

            Dictionary<string, CalibrationData> saved = provider.ExportCalibrations(out bool changed);
            Assert.True(changed);   // the deletion reaches the file
            Assert.Equal(3, saved.Count);
            Assert.Equal(0, provider.ResetCalibration("spa", "gt3"));
        }

        [Fact]
        public void The_engine_has_no_current_key_until_a_frame_names_one()
        {
            var engine = new Core.RawCalculator.RawCalculatorEngine();
            Assert.False(engine.HasCalibrationForCurrentContext());
            Assert.Equal(0, engine.ResetCalibrationForCurrentContext());
            engine.Calibration.Update("spa", "gt3", true, true, 120.0, 0.0,
                new double?[] { 30.0, 30.0, 30.0, 30.0 }, new double?[] { 0.1, 0.1, 0.1, 0.1 });
            engine.SetContext("spa", "gt3");
            Assert.True(engine.HasCalibrationForCurrentContext());
            Assert.Equal(3, engine.ResetCalibrationForCurrentContext());
            Assert.False(engine.HasCalibrationForCurrentContext());
        }

        private sealed class FakeResetHost : ICalibrationResetHost
        {
            public bool Has = true;
            public int Resets;
            public bool Persist = true;
            public readonly List<bool> PersistSets = new List<bool>();
            public bool PersistCalibration => Persist;
            public void SetPersistCalibration(bool persist)
            {
                PersistSets.Add(persist);
                Persist = persist;
                if (!persist) Has = false;   // turning it off deletes every calibration
            }
            public bool HasCalibrationForCurrentKey() => Has;
            public bool ResetCalibrationForCurrentKey()
            {
                Resets++;
                bool had = Has;
                Has = false;
                return had;
            }
        }

        [Fact]
        public void The_button_shows_only_while_the_key_has_data_and_hides_after_a_reset()
        {
            var host = new FakeResetHost();
            var vm = new CalibrationResetViewModel(() => host);
            var changed = new List<string>();
            vm.PropertyChanged += (s, e) => changed.Add(e.PropertyName);

            vm.Refresh();
            Assert.True(vm.IsVisible);
            vm.ResetCommand.Execute(null);
            Assert.Equal(1, host.Resets);
            Assert.False(vm.IsVisible);
            Assert.Contains(nameof(CalibrationResetViewModel.IsVisible), changed);

            host.Has = true;   // driving learns it again
            vm.Refresh();
            Assert.True(vm.IsVisible);
        }

        [Fact]
        public void A_host_without_the_capability_shows_no_button()
        {
            var vm = new CalibrationResetViewModel(() => new object());
            vm.Refresh();
            Assert.False(vm.IsVisible);
            vm.ResetCommand.Execute(null);   // harmless
            Assert.False(new CalibrationResetViewModel(() => null).IsVisible);
        }

        [Fact]
        public void Both_views_bind_the_reset_button_with_no_code_behind()
        {
            foreach (string view in new[] { "WheelChannelPageView", "WheelChannelEffectView" })
            {
                string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "QAdvanceFeedback", "Ui", "Views", view + ".xaml"));
                Assert.Contains("DataContext=\"{Binding CalibrationReset}\"", xaml);
                Assert.Contains("Visibility=\"{Binding IsVisible, Mode=OneWay, Converter={StaticResource VisibleWhen}}\"", xaml);
                Assert.Contains("Command=\"{Binding ResetCommand}\"", xaml);
                Assert.Contains("{v:Loc Calibration.Reset}", xaml);
            }
        }

        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "QAdvanceFeedback.sln"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("repo root not found");
        }
    }
}

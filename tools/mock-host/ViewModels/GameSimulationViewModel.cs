using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using MockHost.Sim;
using Newtonsoft.Json;
using QAdvanceFeedback;
using QAdvanceFeedback.Ui.Mvvm;

namespace MockHost.ViewModels
{
    /// <summary>One wheel's row on the Game Simulation tab.</summary>
    internal sealed class WheelSimViewModel : ObservableObject
    {
        private readonly Action _changed;
        private double _speedPercent = 100.0, _slip;
        private bool _onLooseSurface, _onKerb;

        public WheelSimViewModel(string label, Action changed) { Label = label; _changed = changed; }

        public string Label { get; }
        public double SpeedPercent { get => _speedPercent; set { if (SetProperty(ref _speedPercent, value)) _changed(); } }
        public double Slip { get => _slip; set { if (SetProperty(ref _slip, value)) _changed(); } }
        public bool OnLooseSurface { get => _onLooseSurface; set { if (SetProperty(ref _onLooseSurface, value)) _changed(); } }
        public bool OnKerb { get => _onKerb; set { if (SetProperty(ref _onKerb, value)) _changed(); } }

        public WheelState ToState() => new WheelState { SpeedPercent = SpeedPercent, Slip = Slip, OnLooseSurface = OnLooseSurface, OnKerb = OnKerb };

        public void Load(WheelState w)
        {
            if (w == null) return;
            _speedPercent = w.SpeedPercent; _slip = w.Slip; _onLooseSurface = w.OnLooseSurface; _onKerb = w.OnKerb;
        }
    }

    /// <summary>One extra SimHub property: a full name and a value (a number, true/false, or text).</summary>
    internal sealed class SimPropertyViewModel : ObservableObject
    {
        private readonly Action _changed;
        private string _name, _value;

        public SimPropertyViewModel(string name, string value, Action changed, Action<SimPropertyViewModel> remove)
        {
            _name = name ?? string.Empty;
            _value = value ?? string.Empty;
            _changed = changed;
            RemoveCommand = new RelayCommand(() => remove(this));
        }

        public string Name { get => _name; set { if (SetProperty(ref _name, (value ?? string.Empty).Trim())) _changed(); } }
        public string Value { get => _value; set { if (SetProperty(ref _value, value ?? string.Empty)) _changed(); } }
        public RelayCommand RemoveCommand { get; }

        public object Parsed()
        {
            if (double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            if (bool.TryParse(Value, out bool b)) return b;
            return Value;
        }
    }

    /// <summary>
    /// The Game Simulation tab: the fake game the plugin's DataUpdate sees. Every change builds a new
    /// <see cref="SimulationState"/> for <see cref="PluginRunner"/>; the tab's values are kept in
    /// PluginsData\Common\MockHost.Simulation.json between runs.
    /// </summary>
    internal sealed class GameSimulationViewModel : ObservableObject
    {
        public const string FileName = "MockHost.Simulation.json";

        private readonly PluginRunner _runner;
        private readonly string _path;
        private readonly DispatcherTimer _statusTimer;
        private bool _loading;

        private bool _gameRunning = true, _gamePaused, _gameInMenu;
        private string _gameName = "AssettoCorsaEVO", _carId = "mock_car", _carModel = "Mock Car", _trackId = "mock_track";
        private double _speedKmh = 100, _rpm = 5000, _maxRpm = 8000, _throttle = 50, _brake, _clutch;
        private string _gear = "3";
        private double _lateralG, _longitudinalG, _yawRate, _lateralVelocity;
        private bool _autoWave, _waveProperties;
        private double _wavePeriod = 4;
        private bool _reportsWheelSpeed = true, _reportsWheelRps = true, _reportsWheelSlip = true, _reportsGroundSpeed = true, _reportsYawRate = true;
        private string _status = string.Empty, _message = string.Empty, _health = string.Empty, _sourceHint = string.Empty;

        public GameSimulationViewModel(PluginRunner runner, string path)
        {
            _runner = runner;
            _path = path;
            Wheels = new[] { "Front Left", "Front Right", "Rear Left", "Rear Right" }
                .Select(l => new WheelSimViewModel(l, Publish)).ToArray();
            Properties = new ObservableCollection<SimPropertyViewModel>();
            GameNames = SuggestedGameNames();
            AddPropertyCommand = new RelayCommand(() => { Properties.Add(NewRow(string.Empty, "0")); Publish(); });
            AddSourcePropertiesCommand = new RelayCommand(AddSourceProperties);
            ResetWheelsCommand = new RelayCommand(ResetWheels);
            Load();
            Publish();
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _statusTimer.Tick += (s, e) => RefreshStatus();
            _statusTimer.Start();
        }

        public IReadOnlyList<string> GameNames { get; }
        public IReadOnlyList<WheelSimViewModel> Wheels { get; }
        public ObservableCollection<SimPropertyViewModel> Properties { get; }
        public RelayCommand AddPropertyCommand { get; }
        public RelayCommand AddSourcePropertiesCommand { get; }
        public RelayCommand ResetWheelsCommand { get; }

        public bool GameRunning { get => _gameRunning; set => Set(ref _gameRunning, value); }
        public bool GamePaused { get => _gamePaused; set => Set(ref _gamePaused, value); }
        public bool GameInMenu { get => _gameInMenu; set => Set(ref _gameInMenu, value); }
        public string GameName { get => _gameName; set => Set(ref _gameName, (value ?? string.Empty).Trim()); }
        public string CarId { get => _carId; set => Set(ref _carId, value ?? string.Empty); }
        public string CarModel { get => _carModel; set => Set(ref _carModel, value ?? string.Empty); }
        public string TrackId { get => _trackId; set => Set(ref _trackId, value ?? string.Empty); }

        public double SpeedKmh { get => _speedKmh; set => Set(ref _speedKmh, value); }
        public double Rpm { get => _rpm; set => Set(ref _rpm, value); }
        public double MaxRpm { get => _maxRpm; set => Set(ref _maxRpm, value); }
        public string Gear { get => _gear; set => Set(ref _gear, value ?? string.Empty); }
        public double Throttle { get => _throttle; set => Set(ref _throttle, value); }
        public double Brake { get => _brake; set => Set(ref _brake, value); }
        public double Clutch { get => _clutch; set => Set(ref _clutch, value); }

        public double LateralG { get => _lateralG; set => Set(ref _lateralG, value); }
        public double LongitudinalG { get => _longitudinalG; set => Set(ref _longitudinalG, value); }
        public double YawRate { get => _yawRate; set => Set(ref _yawRate, value); }
        public double LateralVelocity { get => _lateralVelocity; set => Set(ref _lateralVelocity, value); }
        public bool AutoWave { get => _autoWave; set => Set(ref _autoWave, value); }
        public bool WaveProperties { get => _waveProperties; set => Set(ref _waveProperties, value); }
        public double WavePeriod { get => _wavePeriod; set => Set(ref _wavePeriod, value); }

        public bool ReportsWheelSpeed { get => _reportsWheelSpeed; set => Set(ref _reportsWheelSpeed, value); }
        public bool ReportsWheelRps { get => _reportsWheelRps; set => Set(ref _reportsWheelRps, value); }
        public bool ReportsWheelSlip { get => _reportsWheelSlip; set => Set(ref _reportsWheelSlip, value); }
        public bool ReportsGroundSpeed { get => _reportsGroundSpeed; set => Set(ref _reportsGroundSpeed, value); }
        public bool ReportsYawRate { get => _reportsYawRate; set => Set(ref _reportsYawRate, value); }

        public string Status { get => _status; private set => SetProperty(ref _status, value); }
        public string Message { get => _message; private set => SetProperty(ref _message, value); }
        /// <summary>The current game has a per-wheel source in QAdvanceFeedback.SlipAngleSource.json whose properties are
        /// not all in the extra properties below - the plugin would find them missing (empty otherwise).</summary>
        public string SourceHint { get => _sourceHint; private set => SetProperty(ref _sourceHint, value); }

        /// <summary>The plugin's own health registry (what its settings page reports), one line per problem.</summary>
        public string Health { get => _health; private set => SetProperty(ref _health, value); }
        public string StoragePath => Path.GetFullPath(Path.Combine("PluginsData", "Common"));

        private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string name = null)
        {
            if (SetProperty(ref field, value, name)) Publish();
        }

        private SimPropertyViewModel NewRow(string name, string value) =>
            new SimPropertyViewModel(name, value, Publish, row => { Properties.Remove(row); Publish(); });

        private void Publish()
        {
            if (_loading) return;
            _runner.Use(ToState());
        }

        private SimulationState ToState()
        {
            var properties = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (SimPropertyViewModel p in Properties)
                if (!string.IsNullOrWhiteSpace(p.Name)) properties[p.Name] = p.Parsed();
            return new SimulationState
            {
                GameRunning = GameRunning, GamePaused = GamePaused, GameInMenu = GameInMenu,
                GameName = GameName, CarId = CarId, CarModel = CarModel, TrackId = TrackId,
                SpeedKmh = SpeedKmh, Rpm = Rpm, MaxRpm = MaxRpm, Gear = Gear,
                Throttle = Throttle, Brake = Brake, Clutch = Clutch,
                LateralG = LateralG, LongitudinalG = LongitudinalG, YawRateDegPerSec = YawRate, LateralVelocityMps = LateralVelocity,
                AutoWave = AutoWave, WaveProperties = WaveProperties, WavePeriodSeconds = WavePeriod,
                Wheels = Wheels.Select(w => w.ToState()).ToArray(),
                ReportsWheelSpeed = ReportsWheelSpeed, ReportsWheelRps = ReportsWheelRps, ReportsWheelSlip = ReportsWheelSlip,
                ReportsGroundSpeed = ReportsGroundSpeed, ReportsYawRate = ReportsYawRate,
                Properties = properties,
            };
        }

        // The current game's per-wheel slip-angle, longitudinal and surface properties from
        // QAdvanceFeedback.SlipAngleSource.json, so the plugin finds a native source for this game.
        private void AddSourceProperties()
        {
            var names = new List<string>();
            if (SlipAngleRawSourceTable.TryGet(GameName, out SlipAngleRawSourceEntry entry))
            {
                names.AddRange(entry.PropertyPaths ?? Array.Empty<string>());
                names.AddRange(entry.LongitudinalPaths ?? Array.Empty<string>());
                names.AddRange(entry.SurfacePaths ?? Array.Empty<string>());
            }
            else if (SlipAngleRawSourceTable.TryGetSurfacePaths(GameName, out IReadOnlyList<string> surface))
            {
                names.AddRange(surface);
            }
            names = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 0)
            {
                Message = "\"" + GameName + "\" has no per-wheel source in QAdvanceFeedback.SlipAngleSource.json.";
                return;
            }
            int added = 0;
            foreach (string n in names)
            {
                if (Properties.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))) continue;
                Properties.Add(NewRow(n, "0"));
                added++;
            }
            Message = added + " propert" + (added == 1 ? "y" : "ies") + " added for \"" + GameName + "\".";
            Publish();
        }

        // Re-read every status tick, so a source changed on the Plugin Settings tab (Apply) is followed.
        private string MissingSourceHint()
        {
            if (string.IsNullOrEmpty(GameName) || !SlipAngleRawSourceTable.TryGet(GameName, out SlipAngleRawSourceEntry entry)) return string.Empty;
            var wanted = (entry.PropertyPaths ?? Array.Empty<string>()).Concat(entry.LongitudinalPaths ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int missing = wanted.Count(n => !Properties.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)));
            return missing == 0 ? string.Empty : string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" has a per-wheel slip-angle source, but {1} of its {2} properties are not in the extra properties: the plugin "
                + "will report them as not found. Use \"Add this game's slip-angle source properties\".", GameName, missing, wanted.Count);
        }

        private void ResetWheels()
        {
            _loading = true;
            foreach (WheelSimViewModel w in Wheels) { w.SpeedPercent = 100; w.Slip = 0; w.OnLooseSurface = false; w.OnKerb = false; }
            _loading = false;
            Publish();
        }

        private void RefreshStatus()
        {
            string error = _runner.LastError;
            Status = string.Format(CultureInfo.InvariantCulture, "Frames: {0}   Last DataUpdate: {1:0.00} ms   SimHub properties: {2}{3}",
                _runner.Frames, _runner.LastFrameMilliseconds, _runner.PropertyCount,
                string.IsNullOrEmpty(error) ? string.Empty : "   Last error: " + error);
            var problems = QAdvanceFeedback.Core.Health.HealthRegistry.Snapshot()
                .Select(h => "Health: " + h.Subsystem + " (" + h.Severity + ") " + FirstLine(h.Detail)).ToList();
            if (!string.IsNullOrEmpty(_runner.InitNote)) problems.Insert(0, _runner.InitNote);
            SourceHint = MissingSourceHint();
            Health = string.Join(Environment.NewLine, problems);
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int end = text.IndexOfAny(new[] { '\r', '\n' });
            return end < 0 ? text : text.Substring(0, end);
        }

        private static IReadOnlyList<string> SuggestedGameNames()
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AssettoCorsa", "AssettoCorsaCompetizione", "AssettoCorsaEVO", "AssettoCorsaRally", "Automobilista2",
                "F12024", "F12025", "FH5", "FH6", "FM8", "IRacing", "LFS", "LMU", "RFactor2", "RRRE", "BeamNgDrive", "GranTurismo7",
            };
            foreach (string id in SlipAngleRawSourceTable.NativeGameIds) names.Add(id);
            return names.ToList();
        }

        // ---- persistence --------------------------------------------------------------------------------

        private sealed class SimulationFile
        {
            public SimulationState State { get; set; }
            public List<KeyValuePair<string, string>> Properties { get; set; }
        }

        private void Load()
        {
            if (!File.Exists(_path)) return;
            try
            {
                var file = JsonConvert.DeserializeObject<SimulationFile>(File.ReadAllText(_path));
                SimulationState s = file?.State;
                if (s == null) return;
                _loading = true;
                GameRunning = s.GameRunning; GamePaused = s.GamePaused; GameInMenu = s.GameInMenu;
                GameName = s.GameName; CarId = s.CarId; CarModel = s.CarModel; TrackId = s.TrackId;
                SpeedKmh = s.SpeedKmh; Rpm = s.Rpm; MaxRpm = s.MaxRpm; Gear = s.Gear;
                Throttle = s.Throttle; Brake = s.Brake; Clutch = s.Clutch;
                LateralG = s.LateralG; LongitudinalG = s.LongitudinalG; YawRate = s.YawRateDegPerSec; LateralVelocity = s.LateralVelocityMps;
                AutoWave = s.AutoWave; WaveProperties = s.WaveProperties; WavePeriod = s.WavePeriodSeconds;
                ReportsWheelSpeed = s.ReportsWheelSpeed; ReportsWheelRps = s.ReportsWheelRps; ReportsWheelSlip = s.ReportsWheelSlip;
                ReportsGroundSpeed = s.ReportsGroundSpeed; ReportsYawRate = s.ReportsYawRate;
                for (int i = 0; i < Wheels.Count && s.Wheels != null && i < s.Wheels.Length; i++) Wheels[i].Load(s.Wheels[i]);
                foreach (KeyValuePair<string, string> p in file.Properties ?? new List<KeyValuePair<string, string>>())
                    Properties.Add(NewRow(p.Key, p.Value));
            }
            catch (Exception e)
            {
                Message = "Could not read " + FileName + ": " + e.Message;
            }
            finally
            {
                _loading = false;
            }
        }

        public void Save()
        {
            _statusTimer.Stop();
            try
            {
                SimulationState state = ToState();
                state.Properties = new Dictionary<string, object>();
                var file = new SimulationFile
                {
                    State = state,
                    Properties = Properties.Select(p => new KeyValuePair<string, string>(p.Name, p.Value)).ToList(),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path)));
                File.WriteAllText(_path, JsonConvert.SerializeObject(file, Formatting.Indented));
            }
            catch (Exception) { /* the simulation's own values only; never block exit */ }
        }
    }
}

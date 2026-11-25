using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using GameReaderCommon;
using GameReaderCommon.Feedback;
using GameReaderCommon.Replays;
using GameReaderCommon.UDP;

#pragma warning disable CS0067 // the events are part of the interface; nothing here raises them

namespace MockHost.Sim
{
    /// <summary>
    /// The one thing the plugin asks SimHub's game manager for: which per-wheel readings the running game really
    /// reports (<see cref="GetFeedbackCapabilities"/>). Without it every gated per-wheel reading is "unknown"
    /// and the plugin's own wheel lock / slip calculation never runs. Everything else is an inert stub.
    /// </summary>
    internal sealed class MockGameManager : IGameManager
    {
        private volatile FeedbackCapabilities _capabilities = new FeedbackCapabilities();

        /// <summary>Set by the runner from the Game Simulation tab every time the simulation changes.</summary>
        public void UseCapabilities(FeedbackCapabilities capabilities) => _capabilities = capabilities ?? new FeedbackCapabilities();

        public FeedbackCapabilities GetFeedbackCapabilities(bool contextual = false) => _capabilities;

        public string CurrentGameName { get; set; } = string.Empty;

        public string GameName() => CurrentGameName;

        // ---- inert stubs ----------------------------------------------------------------------------------
        public GameReaderCommon.GameSettings.Car.CarManager CarManager => null;
        public int ReplayScreenCaptureFPS { get; set; }
        public ITrackStatisticsProvider TrackStatisticsProvider { get; set; }
        public IFuelStatisticsProvider FuelStatisticsProvider { get; set; }
        public string DetectedInstallPath { get; set; }
        public IGameInstallManager InstallManager { get; set; }
        public GameUnitSettings GameUnitSettings { get; set; }
        public LeaderBoardGapMode LeaderBoardGapMode { get; set; }
        public SimhubGapMode SimhubGapMode { get; set; }
        public ReplayModes ReplayMode { get; set; }
        public bool Enabled { get; set; } = true;
        public GameData GameData => null;
        public double UpdateInterval => 1000.0 / 60.0;
        public double MinimumTyreWear { get; set; }
        public string RequestedReplay { get; set; }
        public string ReplayPath { get; set; }
        public GameFeatures GameFeatures { get; set; }
        public bool GameProcessDetected { get; set; }
        public bool GameProcessIsAdmin { get; set; }
        public LeaderboardRelativeMode LeaderboardRelativeMode { get; set; }
        public string ReplayScreenCaptureDeviceName { get; set; }
        public string GameDisplayName { get; set; }
        public bool IsCustomGame => false;
        public IUDPStatus UDPStatus => null;

        public event CarChangedDelegate CarChanged;
        public event DataUpdatedDelegate DataUpdated;
        public event DataSampleChangedDelegate DataSampleChanged;
        public event GameRunningChangedDelegate GameStateChanged;
        public event NewLapDelegate NewLap;
        public event SessionRestartDelegate SessionRestart;
        public event TrackChangedDelegate TrackChanged;
        public event LowFuelLapsAlertDelegate LowFuelLapsAlert;

        public List<string> GetGameReportedPressedButtons() => new List<string>();
        public Dictionary<int, double> GetSectorDefinitions() => new Dictionary<int, double>();
        public void AddProcessNames(string[] names) { }
        public void ResetMaxSpeed() { }
        public ReplayStatus GetReplayStatus() => default(ReplayStatus);
        public double[] ToAbsoluteCoordinates(double[] source) => source;
        public ObservableCollection<ReplayEntry> GetReplays() => new ObservableCollection<ReplayEntry>();
        public void Start() { }
        public void Stop() { }
        public object GetRawDataSample() => null;
        public IEnumerable<string> GetProcesseNames() => Array.Empty<string>();
        public GameTrack GetTrack(string trackCode) => null;
        public GameCar GetCar(string carCode) => null;
        public void SetGameName(string code) => CurrentGameName = code ?? string.Empty;
        public void SetReaderSubCode(string code) { }
        public string GetReaderSubCode() => null;
        public double GetLastFrameDuration() => UpdateInterval;
        public void AutomaticConfigurationApplied() { }
        public double KmhToLocalSpeedUnit(double value) => value;
        public double? KmhToLocalSpeedUnit(double? value) => value;
        public double ConvertDegreesCToUserUnit(double celsiustemperature) => celsiustemperature;
        public void SetHighPrecision() { }
        public string GetGameAndReaderReplayPath() => null;
        public string GetRelativeGameAndReaderReplayPath() => null;
        public GameFamily GetGameFamily() => default(GameFamily);
        public void SetGameFamily(GameFamily gameFamily) { }
        public List<string> GetOpponentsGameSpecificDataKeys() => new List<string>();
    }
}

using System.Collections.Generic;
using Newtonsoft.Json;

namespace MockHost.Sim
{
    /// <summary>One wheel of the simulated car.</summary>
    internal sealed class WheelState
    {
        /// <summary>Wheel speed as a share of the car's speed: 100 rolls freely, 0 is locked, over 100 spins.</summary>
        public double SpeedPercent { get; set; } = 100.0;
        /// <summary>SimHub's own per-wheel slip value (FeedbackData.WheelSlip).</summary>
        public double Slip { get; set; }
        /// <summary>SimHub's WheelInGrassOrGravel flag.</summary>
        public bool OnLooseSurface { get; set; }
        public bool OnKerb { get; set; }
    }

    /// <summary>
    /// What the Game Simulation tab describes, as one immutable-by-convention snapshot: the tab builds a new one on
    /// every change and hands it to <see cref="PluginRunner"/>, whose background thread reads it each frame.
    /// </summary>
    internal sealed class SimulationState
    {
        public bool GameRunning { get; set; } = true;
        public bool GamePaused { get; set; }
        public bool GameInMenu { get; set; }

        public string GameName { get; set; } = "AssettoCorsaEVO";
        public string CarId { get; set; } = "mock_car";
        public string CarModel { get; set; } = "Mock Car";
        public string TrackId { get; set; } = "mock_track";

        public double SpeedKmh { get; set; } = 100.0;
        public double Rpm { get; set; } = 5000.0;
        public double MaxRpm { get; set; } = 8000.0;
        public string Gear { get; set; } = "3";
        public double Throttle { get; set; } = 50.0;
        public double Brake { get; set; }
        public double Clutch { get; set; }

        public double LateralG { get; set; }
        public double LongitudinalG { get; set; }
        public double YawRateDegPerSec { get; set; }
        public double LateralVelocityMps { get; set; }

        /// <summary>Swings lateral G, yaw rate and lateral velocity as a sine between -value and +value.</summary>
        public bool AutoWave { get; set; }
        public double WavePeriodSeconds { get; set; } = 4.0;
        /// <summary>With <see cref="AutoWave"/>, the numeric extra properties swing too (a per-wheel slip angle).</summary>
        public bool WaveProperties { get; set; }

        public WheelState[] Wheels { get; set; } = { new WheelState(), new WheelState(), new WheelState(), new WheelState() };

        // Which readings the simulated game reports (SimHub's FeedbackCapabilities and the optional fields).
        public bool ReportsWheelSpeed { get; set; } = true;
        public bool ReportsWheelRps { get; set; } = true;
        public bool ReportsWheelSlip { get; set; } = true;
        public bool ReportsGroundSpeed { get; set; } = true;
        public bool ReportsYawRate { get; set; } = true;

        /// <summary>Extra SimHub properties by full name (DataCorePlugin.GameRawData.X), already parsed to
        /// double / bool / string.</summary>
        [JsonIgnore]
        public IReadOnlyDictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    }
}

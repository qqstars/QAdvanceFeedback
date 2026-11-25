using System;
using GameReaderCommon;
using GameReaderCommon.Feedback;

namespace MockHost.Sim
{
    /// <summary>
    /// Builds the GameData SimHub hands a plugin each frame from a <see cref="SimulationState"/>. SimHub's data
    /// classes have internal setters, so values go in through <see cref="SimHubStandIn.SetProperty(object,string,object)"/>.
    /// </summary>
    internal static class GameFrameBuilder
    {
        private const double StandardGravity = 9.80665;
        private const double WheelRadiusMetres = 0.33;

        public static GameData Build(SimulationState s, double wave, StatusDataBase previous)
        {
            var data = new GameData { GameName = s.GameName ?? string.Empty };
            SimHubStandIn.SetProperty(data, "GameRunning", s.GameRunning);
            SimHubStandIn.SetProperty(data, "GamePaused", s.GamePaused);
            SimHubStandIn.SetProperty(data, "GameInMenu", s.GameInMenu);
            SimHubStandIn.SetProperty(data, "FrameTime", DateTime.Now);
            SimHubStandIn.SetProperty(data, "FrameTimeUTC", DateTime.UtcNow);
            data.RunningGameProcessDetected = s.GameRunning;

            StatusDataBase d = New(s, wave);
            data.NewData = d;
            data.OldData = previous ?? New(s, wave);
            return data;
        }

        public static FeedbackCapabilities Capabilities(SimulationState s) => new FeedbackCapabilities
        {
            Speed = true,
            RPM = true,
            Brake = true,
            Throttle = true,
            Gear = true,
            Surge = true,
            Sway = true,
            WheelsSpeed = s.ReportsWheelSpeed,
            WheelsRPS = s.ReportsWheelRps,
            WheelsSlip = s.ReportsWheelSlip,
            GroundSpeed = s.ReportsGroundSpeed,
            LocalVelocity = true,
            YawChangeVelocity = s.ReportsYawRate,
        };

        private static StatusDataBase New(SimulationState s, double wave)
        {
            var d = new MockStatusData();
            double lateralG = s.LateralG * wave, yaw = s.YawRateDegPerSec * wave, lateralVelocity = s.LateralVelocityMps * wave;
            double speedMps = s.SpeedKmh / 3.6;

            Set(d, "CarId", s.CarId ?? string.Empty);
            Set(d, "CarModel", s.CarModel ?? string.Empty);
            Set(d, "TrackId", s.TrackId ?? string.Empty);
            Set(d, "SpeedKmh", s.SpeedKmh);
            Set(d, "FilteredSpeedKmh", s.SpeedKmh);
            Set(d, "Rpms", s.Rpm);
            Set(d, "FilteredRpms", s.Rpm);
            Set(d, "MaxRpm", s.MaxRpm);
            Set(d, "Gear", s.Gear ?? "N");
            Set(d, "Throttle", s.Throttle);
            Set(d, "Brake", s.Brake);
            Set(d, "Clutch", s.Clutch);
            Set(d, "AccelerationSway", (double?)(lateralG * StandardGravity));
            Set(d, "AccelerationSurge", (double?)(s.LongitudinalG * StandardGravity));

            var fb = new FeedbackData
            {
                Speed = s.SpeedKmh,
                LocalVelocity = new LocalVelocity(speedMps, lateralVelocity, 0.0),
                OrientationYawChangePerSecond = yaw,
                LateralAcceleration = (float)(lateralG * StandardGravity),
                LongitudinalAcceleration = (float)(s.LongitudinalG * StandardGravity),
            };
            for (int i = 0; i < 4; i++)
            {
                WheelState w = s.Wheels[i];
                double wheelKmh = s.SpeedKmh * w.SpeedPercent / 100.0;
                fb.WheelSpeed[i] = wheelKmh;
                fb.WheelRPS[i] = wheelKmh / 3.6 / (2.0 * Math.PI * WheelRadiusMetres);
                fb.WheelSlip[i] = w.Slip;
                fb.WheelInGrassOrGravel[i] = w.OnLooseSurface;
                fb.WheelsOnKerbs[i] = w.OnKerb;
            }
            if (s.ReportsGroundSpeed) Set(fb, "GroundSpeed", (double?)s.SpeedKmh);
            if (s.ReportsYawRate) Set(fb, "YawChangeVelocity", (double?)yaw);
            Set(d, "FeedbackData", fb);
            return d;
        }

        /// <summary>SimHub's game readers use StatusData&lt;TRaw&gt;; the plugin only reads the base class.</summary>
        private sealed class MockStatusData : StatusDataBase
        {
            public override object GetRawDataObject() => null;
        }

        private static void Set(object target, string name, object value) => SimHubStandIn.SetProperty(target, name, value);
    }
}

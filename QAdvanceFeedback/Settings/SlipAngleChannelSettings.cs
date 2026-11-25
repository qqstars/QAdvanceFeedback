using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.SlipAngle;

namespace QAdvanceFeedback.Settings
{
    /// <summary>Plugin Internal's Slip Angle source (the "Angle source" dropdown). Saved as its number, so the values never change.</summary>
    public enum SlipAngleAngleSource
    {
        /// <summary>The game's per-wheel tyre slip angle when the game has a per-wheel telemetry source; otherwise the
        /// car angle.</summary>
        PerWheelWhenAvailable = 0,

        /// <summary>The car angle (body motion) for every game, even one with a per-wheel telemetry source.</summary>
        CarAngle = 1,
    }

    /// <summary>
    /// Everything the Slip Angle channel needs (docs\slip-angle-design.md §9): source, gate, weights,
    /// key data, curve, pulse, drift. Source modes are Plugin Internal
    /// (<see cref="Settings.SourceMode.Manual"/>) and <see cref="Settings.SourceMode.Custom"/> only.
    /// </summary>
    public sealed class SlipAngleChannelSettings
    {
        public const double DefaultWeightMax = 0.85, DefaultWeightMin = 0.15;

        private double _weightMax = DefaultWeightMax;
        private double _weightMin = DefaultWeightMin;

        /// <summary>Plugin Internal's angle source (owner, 2026-11-23): the game's per-wheel tyre angle when the game has
        /// a per-wheel telemetry source (QAdvanceFeedback.SlipAngleSource.json), else the car angle - or always the car
        /// angle. Applied with the page's Apply.</summary>
        public SlipAngleAngleSource AngleSource { get; set; } = SlipAngleAngleSource.PerWheelWhenAvailable;

        public SlipAngleGateSettings Gate { get; set; } = new SlipAngleGateSettings();

        /// <summary>Weight of the strongest wheel (§9.3). Finite and &gt;= 0.</summary>
        public double WeightMax
        {
            get => _weightMax;
            set => _weightMax = NonNegative(value, DefaultWeightMax);
        }

        /// <summary>Weight of the weaker wheel (§9.3). Finite and &gt;= 0.</summary>
        public double WeightMin
        {
            get => _weightMin;
            set => _weightMin = NonNegative(value, DefaultWeightMin);
        }

        public SlipAngleKeyDataSettings KeyData { get; set; } = new SlipAngleKeyDataSettings();

        public ProjectorSettings Projector { get; set; } = ProjectorSettings.CreateShippedDefault(ProjectionChannel.SlipAngle);

        public PulseSettings Pulse { get; set; } = new PulseSettings();

        public DriftSettings Drift { get; set; } = new DriftSettings();

        public bool CrossingGateEnabled { get; set; } = true;

        /// <summary>The per-game unit lists for native fields the table leaves unverified (owner, 2026-11-20):
        /// ByAngle, AngleUsingRatio1, AngleUsingRatio100, AngleUsingRadians; any other game reads radians while it is checked. The lists start null
        /// (so Json.NET never appends a saved list to a default one) and are seeded by
        /// <see cref="EnsureSourceUnits"/> when the file has none.</summary>
        public SlipAngleSourceUnits SourceUnits { get; set; } = new SlipAngleSourceUnits();

        /// <summary>Seeds the unit lists the config does not have yet from the hard-coded defaults. True when
        /// anything was seeded (the plugin then saves, so the file shows them).</summary>
        public bool EnsureSourceUnits()
        {
            if (SourceUnits == null) SourceUnits = new SlipAngleSourceUnits();
            return SourceUnits.Normalise();
        }

        private static double NonNegative(double v, double def) => ClampMath.IsFinite(v) && v >= 0.0 ? v : def;

        /// <summary>
        /// The Slip Angle curve shipped before 2026-11-22, never edited: anchors 30 / 62 / 78 -> 10 / 30 / 60, smooth
        /// ranges 3 / 2 / 2, Start 15 -> 0, End 100 -> 100, preset still Curve. Such a config moves to the new shipped
        /// curve on load (owner: Best 60 -> 20 range 6, Max 80 -> 60 range 3); any curve the driver changed, even by
        /// one value, is left exactly as it is.
        /// </summary>
        public static bool IsPreviousShippedCurve(ProjectorSettings p) =>
            p != null && p.Preset == ProjectorPreset.Curve
            && p.StartInput == 15.0 && p.EndInput == 100.0 && p.StartOutput == 0.0 && p.EndOutput == 100.0
            && p.SlightlyInput == 30.0 && p.SlightlyOutput == 10.0 && p.SlightlyFlattenRange == 3.0
            && p.ModerateInput == 62.0 && p.ModerateOutput == 30.0 && p.ModerateFlattenRange == 2.0
            && p.CriticalInput == 78.0 && p.CriticalOutput == 60.0 && p.CriticalFlattenRange == 2.0;

        /// <summary>Load-time repair: null sub-objects become defaults, Drift is clamped, unsupported modes become Manual.</summary>
        public void Normalise()
        {
            if (AngleSource != SlipAngleAngleSource.CarAngle) AngleSource = SlipAngleAngleSource.PerWheelWhenAvailable;
            if (Gate == null) Gate = new SlipAngleGateSettings();
            if (KeyData == null) KeyData = new SlipAngleKeyDataSettings();
            if (Projector == null) Projector = ProjectorSettings.CreateShippedDefault(ProjectionChannel.SlipAngle);
            else if (IsPreviousShippedCurve(Projector)) Projector.ApplyPreset(ProjectorPreset.Curve, ProjectionChannel.SlipAngle);
            if (Pulse == null) Pulse = new PulseSettings();
            if (Drift == null) Drift = new DriftSettings();
            Drift.Sanitise();
            if (SourceUnits == null) SourceUnits = new SlipAngleSourceUnits();
        }
    }
}

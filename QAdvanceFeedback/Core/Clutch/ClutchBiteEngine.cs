using System;

namespace QAdvanceFeedback.Core.Clutch
{
    public enum ClutchSourceKind { None, Game, Axis }

    /// <summary>The clutch value in use this frame and where it came from.</summary>
    public readonly struct ClutchReading
    {
        public ClutchReading(double? clutch0100, string identity, ClutchSourceKind kind)
        {
            Clutch0100 = clutch0100; Identity = identity; Kind = kind;
        }

        /// <summary>0 released .. 100 fully pressed; null when there is no clutch at all.</summary>
        public double? Clutch0100 { get; }
        /// <summary>The learning-key source identity: <c>GameClutch</c>, <c>Axis:&lt;name&gt;</c> or <c>None</c>.</summary>
        public string Identity { get; }
        public ClutchSourceKind Kind { get; }
    }

    /// <summary>
    /// Picks the clutch value (docs\clutch-bite-design.md, "Clutch input"):
    /// <list type="bullet">
    /// <item>the game's clutch once that game has reported a value above 5 (which tells a game without a
    /// clutch channel, always 0, apart from a released pedal); the latch resets on a game change;</item>
    /// <item>otherwise the fallback axis, if one is chosen and connected;</item>
    /// <item>otherwise the game's value as reported (or none).</item>
    /// </list>
    /// </summary>
    public sealed class ClutchSourceResolver
    {
        public const double GameClutchSeenAbove = 5.0;
        public const string GameIdentity = "GameClutch";
        public const string NoneIdentity = "None";

        private string _game;
        private bool _gameClutchSeen;

        public ClutchReading Resolve(string gameId, double? gameClutch, double? axis01, string axisName, bool invert)
        {
            if (!string.Equals(gameId, _game, StringComparison.Ordinal))
            {
                _game = gameId;
                _gameClutchSeen = false;
            }

            double? game = gameClutch.HasValue && ClampMath.IsFinite(gameClutch.Value) ? ClampMath.To0100(gameClutch.Value) : (double?)null;
            if (game.HasValue && game.Value > GameClutchSeenAbove) _gameClutchSeen = true;
            if (_gameClutchSeen && game.HasValue) return new ClutchReading(game, GameIdentity, ClutchSourceKind.Game);

            if (axis01.HasValue && ClampMath.IsFinite(axis01.Value) && !string.IsNullOrEmpty(axisName))
            {
                double v = ClampMath.To01(axis01.Value);
                return new ClutchReading((invert ? 1.0 - v : v) * 100.0, "Axis:" + axisName, ClutchSourceKind.Axis);
            }

            // A game that has not shown a clutch yet (always 0, or not pressed this game) is not a
            // source: its value is reported (it is a released pedal, output 0) but nothing is learned or
            // remembered under it (review fix M1).
            return new ClutchReading(game, NoneIdentity, ClutchSourceKind.None);
        }
    }

    /// <summary>The Clutch Bite settings as the engine needs them (Core stays free of the Settings layer).</summary>
    public readonly struct ClutchBiteParameters
    {
        public ClutchBiteParameters(ClutchApplyDirection apply, bool auto, double bitePoint, double range, double gamma,
            ClutchLearningParameters? learning = null)
        {
            Apply = apply; Auto = auto; BitePoint = bitePoint; Range = range; Gamma = gamma;
            Learning = learning ?? ClutchLearningParameters.Default;
        }

        public ClutchLearningParameters Learning { get; }

        public ClutchApplyDirection Apply { get; }
        public bool Auto { get; }
        public double BitePoint { get; }
        public double Range { get; }
        public double Gamma { get; }

        public ClutchBiteParameters With(ClutchApplyDirection? apply = null, bool? auto = null, double? bitePoint = null) =>
            new ClutchBiteParameters(apply ?? Apply, auto ?? Auto, bitePoint ?? BitePoint, Range, Gamma, Learning);
    }

    /// <summary>
    /// The per-frame Clutch Bite pipeline (docs\clutch-bite-design.md). For one frame it:
    /// <list type="number">
    /// <item>keys the learning by (game, car, clutch source), resetting the detector and the direction
    /// tracker when the key changes, so a sample can never land under another car or source;</item>
    /// <item>feeds the standing-start detector and adds accepted samples to the learner;</item>
    /// <item>computes the output from the final bite, the S-curve and the last direction.</item>
    /// </list>
    /// </summary>
    public sealed class ClutchBiteEngine
    {
        private readonly ClutchDirectionTracker _tracker = new ClutchDirectionTracker();
        private readonly ClutchBiteDetector _detector = new ClutchBiteDetector();

        public string CurrentKey { get; private set; }
        public ClutchBiteStatus CurrentStatus { get; private set; } = new ClutchBiteStatus { Final = 50 };
        public double? CurrentInput { get; private set; }

        /// <summary>Out of a session (pause, menu, no game): a standing start in progress is dropped.</summary>
        public void Interrupt() => _detector.Reset();

        /// <returns>The output, 0..100.</returns>
        public double Step(ClutchReading reading, ClutchFrame frame, string game, string car, ClutchBiteParameters p,
            ClutchBiteLearner learner, out bool sampleAccepted)
        {
            sampleAccepted = false;
            string key = ClutchBiteLearner.Key(game ?? string.Empty, car ?? string.Empty, reading.Identity ?? ClutchSourceResolver.NoneIdentity);
            if (!string.Equals(key, CurrentKey, StringComparison.Ordinal))
            {
                CurrentKey = key;
                _detector.Reset();
                _tracker.Reset();
            }

            CurrentInput = reading.Clutch0100;
            if (reading.Kind != ClutchSourceKind.None && learner != null)
            {
                var f = new ClutchFrame(frame.TimeSec, reading.Clutch0100, frame.Rpm, frame.SpeedKmh, frame.Throttle,
                    frame.InGear, frame.LooseSurface, frame.ValidForLearning);
                _detector.Parameters = p.Learning;
                if (_detector.Update(f, out double sample)) sampleAccepted = learner.AddSample(key, sample, p.Learning);
            }

            CurrentStatus = learner?.Status(key, p.BitePoint, p.Auto, p.Learning.MatureSamples) ?? new ClutchBiteStatus { Final = p.BitePoint };
            if (!reading.Clutch0100.HasValue) return 0.0;
            return Output(reading.Clutch0100.Value, p, CurrentStatus.Final, _tracker);
        }

        /// <summary>The output for a clutch position with a given final bite and direction tracker - the
        /// same rule the Test Effect uses.</summary>
        public static double Output(double clutch0100, ClutchBiteParameters p, double finalBite, ClutchDirectionTracker tracker)
        {
            tracker?.Update(clutch0100);
            if (tracker != null && !tracker.Allows(p.Apply)) return 0.0;
            return ClutchBiteCurve.Output(clutch0100, finalBite, p.Range, p.Gamma);
        }
    }
}

using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>One frame's inputs to <see cref="SlipAngleFrameProcessor.Process"/> (plain values only).</summary>
    public sealed class SlipAngleFrameRequest
    {
        public string GameId { get; set; } = string.Empty;
        public string CarId { get; set; } = string.Empty;
        public ITelemetryFrame Frame { get; set; }
        /// <summary>Seconds since the previous frame (0 / NaN when unknown).</summary>
        public double Dt { get; set; }

        /// <summary>This frame's Plugin Internal read (kerbs, surface, the game's per-wheel angle when it has one).</summary>
        public SlipAngleInput Input { get; set; } = SlipAngleInput.Empty;

        /// <summary>Native support state (unit detection / block diagnostics); optional.</summary>
        public SlipAngleNativeSupport NativeSupport { get; set; }

        public SlipAngleGateSettings Gate { get; set; } = new SlipAngleGateSettings();
        public double WeightMax { get; set; } = 0.85;
        public double WeightMin { get; set; } = 0.15;

        /// <summary>The persisted key data (null: always the learned window).</summary>
        public ISlipAngleKeyData KeyData { get; set; }
    }

    /// <summary>
    /// The Slip Angle per-frame pipeline (docs\slip-angle-design.md 9.1-9.8), pure and SimHub-free, so
    /// its order is unit-testable. The plugin's <c>DataUpdate</c> reads the inputs (Layer 2), calls
    /// <see cref="Process"/> once per running frame, and publishes the returned plain values.
    /// <para>Order: yaw sign observe → <see cref="SlipAngleGate.Step"/> → source (native / fallback /
    /// custom) → <see cref="SlipAngleRawCalculator.Compute"/> with the PREVIOUS frame's k (k is produced
    /// by the learner that this frame's Raw.All then feeds) → <see cref="ImpactGuard"/> (yaw term only
    /// when the per-game sign is known and |dVlat/dt| ≤ 0.15 g, ruling R12) → surface classifier on
    /// Raw.All → <see cref="KeyedSlipAngleLearner.Observe"/> → learned window / maturity / tier → manual
    /// key data gate + one-time seed → <see cref="SlipAngleNormalizer"/> → <see cref="SlipAngleProjectedEngine"/>
    /// → <see cref="KeyedSlipAngleLearner.SaveIfDue"/>.</para>
    /// <para>Source identity: Plugin Internal learns under <see cref="NativeIdentity"/> or
    /// <see cref="FallbackIdentity"/> (their scales differ, 9.1).</para>
    /// Not thread-safe: the data thread owns it (and the learner).
    /// </summary>
    public sealed class SlipAngleFrameProcessor
    {
        public const string NativeIdentity = "SlipAngle.PluginInternal.Native";
        public const string FallbackIdentity = "SlipAngle.PluginInternal.Fallback";

        /// <summary>A native GRIP-RATIO source learns under its own key (owner, 2026-11-20): its angle is 20% x
        /// the ratio, a different scale from a true angle, and a window learned before the game was known to be
        /// a ratio (FH6's, from the ratio read as radians) must not be used.</summary>
        public const string NativeRatioIdentity = "SlipAngle.PluginInternal.NativeRatio";

        /// <summary>The Plugin Internal identity for a native source in <paramref name="unit"/>.</summary>
        public static string NativeIdentityFor(SlipAngleUnit unit) =>
            SlipAngleSourceScale.IsRatio(unit) ? NativeRatioIdentity : NativeIdentity;

        /// <summary>Above this |dVlat/dt| the steady-state yaw check is skipped (R12).</summary>
        public const double YawCheckMaxDVlatG = 0.15;

        /// <summary>Learning needs a plausible frame time (matches <see cref="TelemetryLearningGate"/>).</summary>
        public const double MaxLearnDtSeconds = TelemetryLearningGate.MaxPlausibleDtSeconds;

        private readonly KeyedSlipAngleLearner _learner;
        private readonly Action<Dictionary<string, SlipAngleKeyState>> _save;
        private readonly YawSignDetector _yawSign = new YawSignDetector();
        private readonly ManualOverrideGate _manualGate = new ManualOverrideGate();
        private SurfaceBucketClassifier _surface = new SurfaceBucketClassifier();

        /// <summary>The games seen reporting loose by SimHub's grass/gravel flag this session (kept across a game / car
        /// switch: the evidence is about the game, not the car).</summary>
        private readonly HashSet<string> _simHubLooseGames = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// THE SURFACE (owner, 2026-11-22 / 2026-11-23): the game's own field translated by its source row; when that
        /// gives nothing, SimHub's grass/gravel flag; else Unknown (held, never guessed).
        /// </summary>
        private SurfaceBlend StepSurface(string game, ITelemetryFrame f, SlipAngleInput input, double dt)
        {
            SurfaceBucket? own = SurfaceBucketMap.Vote(input.WheelSurface);
            if (own.HasValue) return _surface.Step(own, dt, SurfaceBucketSource.Game);
            SurfaceBucket? simHub = f == null ? null : SurfaceBucketMap.FromSimHubLoose(
                f.WheelOnLooseSurfaceFrontLeft, f.WheelOnLooseSurfaceFrontRight, f.WheelOnLooseSurfaceRearLeft,
                f.WheelOnLooseSurfaceRearRight, _simHubLooseGames.Contains(game));
            if (simHub == SurfaceBucket.Loose) _simHubLooseGames.Add(game);
            return _surface.Step(simHub, dt, SurfaceBucketSource.SimHub);
        }
        private SlipAngleGate _gate = new SlipAngleGate();
        private ImpactGuard _impact = new ImpactGuard(ImpactGuardParameters.SlipAngle);
        private SlipAngleProjectedEngine _projected;
        private double? _previousVlat;
        private DriftSettings _saturatingDrift, _saturatingDriftSource;
        private double _saturatingDriftHoldSource;

        // ImpactGuard needs finite axes: a missing longitudinal / vertical G holds the last finite value
        // seen (0 until one is), so a dropout neither rejects the frame nor fakes a jump back to 0.
        private double _heldLongG;
        private double _heldVertG;

        public SlipAngleFrameProcessor(KeyedSlipAngleLearner learner, Action<Dictionary<string, SlipAngleKeyState>> save)
        {
            _learner = learner ?? throw new ArgumentNullException(nameof(learner));
            _save = save;
            Configure(new OutputProjector(ProjectorSettings.CreateShippedDefault(ProjectionChannel.SlipAngle)),
                new PulseSettings(), new DriftSettings());
        }

        public KeyedSlipAngleLearner Learner => _learner;

        public YawSignDetector YawSign => _yawSign;

        /// <summary>Rebuild the projection (curve, pulse, drift) from settings and apply the crossing gate
        /// switch (ruling R29) - Init and Apply. The projection is a plain reference swap, like the
        /// Lock/Slip projected engine.</summary>
        public void Configure(OutputProjector projector, PulseSettings pulse, DriftSettings drift, bool crossingGateEnabled = true)
        {
            _projected = new SlipAngleProjectedEngine(projector, pulse, drift);
            _learner.CrossingGateEnabled = crossingGateEnabled;
        }

        /// <summary>Game or car switch: drop frame-to-frame state (gate timers, impact history, held axes,
        /// dVlat, surface classifier). Learned state is untouched.</summary>
        public void Reset()
        {
            _gate = new SlipAngleGate();
            _impact = new ImpactGuard(ImpactGuardParameters.SlipAngle);
            _surface = new SurfaceBucketClassifier();
            NoteGap();
        }

        /// <summary>The (game, car) the previous processed frame was for - null before the first frame.</summary>
        private string _lastGame, _lastCar;

        /// <summary>A frame not processed (no game / paused / menu): the next dVlat/dt must not span it,
        /// held axis values are not carried across it, and the drift attenuator restarts from Normal
        /// (a drift held before a pause is not resumed half-attenuated - symmetric with Configure).</summary>
        /// <summary>SLIDE GUARD (owner, 2026-11-21): seconds left during which the car counts as sliding - set to 1 s
        /// whenever its body angle reaches <see cref="SlipAngleEarlyEstimator.SlideBodyAngleDeg"/>, so "sliding"
        /// means "body angle at least 10 degrees within the last second".</summary>
        private double _slideHold;

        private static double AxleMax(SlipAngleRawResult raw)
        {
            double front = raw.FrontDeg ?? double.NaN, rear = raw.RearDeg ?? double.NaN;
            if (!ClampMath.IsFinite(front)) return ClampMath.IsFinite(rear) ? Math.Abs(rear) : double.NaN;
            if (!ClampMath.IsFinite(rear)) return Math.Abs(front);
            return Math.Max(Math.Abs(front), Math.Abs(rear));
        }

        /// <summary>The car's body (drift) angle in degrees, atan(|lateral| / |forward| velocity); NaN without
        /// both velocities or below ~10 km/h.</summary>
        internal static double BodyAngleDeg(ITelemetryFrame f)
        {
            double? vlat = f?.LateralLocalVelocity;
            double? vfwd = f?.ForwardLocalVelocity ?? f?.GroundSpeedMps;
            if (!vlat.HasValue || !vfwd.HasValue || !ClampMath.IsFinite(vlat.Value) || !ClampMath.IsFinite(vfwd.Value)) return double.NaN;
            if (Math.Abs(vfwd.Value) < 3.0) return double.NaN;
            return Math.Atan2(Math.Abs(vlat.Value), Math.Abs(vfwd.Value)) * SlipAngleSourceScale.RadToDeg;
        }

        public void NoteGap()
        {
            _projected.ResetDrift();
            _learner.SpeedBands.ResetFrameState();
            _slideHold = 0.0;
            _previousVlat = null;
            _heldLongG = 0.0;
            _heldVertG = 0.0;
        }

        public SlipAngleFrameResult Process(SlipAngleFrameRequest r)
        {
            if (r == null) return SlipAngleFrameResult.Empty;
            string game = r.GameId ?? string.Empty;
            string car = r.CarId ?? string.Empty;
            // A CAR SWITCH STARTS THE FRAME STATE AGAIN (owner, 2026-11-22), like a game switch. The shared surface
            // classifier judges the surface against the CURRENT car's learned curves; a new car has none, so it could
            // never leave the previous car's last verdict (FH6: Low-grip) and the new car learned only into that
            // bucket - its Sealed window never moved and the tab sat on the Low-grip window until a restart.
            if (_lastGame != null && (!string.Equals(game, _lastGame, StringComparison.Ordinal)
                || !string.Equals(car, _lastCar, StringComparison.Ordinal)))
                Reset();
            _lastGame = game;
            _lastCar = car;
            ITelemetryFrame f = r.Frame;
            double dt = ClampMath.IsFinite(r.Dt) && r.Dt > 0.0 ? r.Dt : 0.0;
            SlipAngleInput input = r.Input ?? SlipAngleInput.Empty;
            var result = new SlipAngleFrameResult();

            // 1. Yaw sign (R12) and the gate.
            _yawSign.Observe(game, f?.LateralG, f?.ForwardLocalVelocity, f?.YawRateRadPerSec, f?.IsAirborne ?? false, dt);
            SlipAngleGateVerdict gate = _gate.Step(f, dt, r.Gate);

            // 2. Source + Raw (the previous frame's k: the learner has not seen this frame yet).
            // Plugin Internal's angle is computed EVERY frame, selected or not (Task G): it always learns,
            // and it alone drives the shared surface classifier (step 4).
            SurfaceBlend previousBlend = _surface.Current;
            bool nativeCandidate = input.IsNative && input.NativeWheelDeg.HasValue;
            string nativeIdentity = NativeIdentityFor(input.Unit);
            double internalK = nativeCandidate ? _learner.K(game, car, nativeIdentity, previousBlend) : 1.0;
            SlipAngleRawResult internalRaw = SlipAngleRawCalculator.Compute(input, f, input.IsNative, internalK, r.WeightMax, r.WeightMin);
            string internalIdentity = internalRaw.Tier == SourceNativeness.Native ? nativeIdentity : FallbackIdentity;
            if (internalRaw.Tier != SourceNativeness.Native) internalK = 1.0;   // unused by fallback

            // Plugin Internal is the only source (owner, 2026-11-24: Custom was removed - a game's source is edited in
            // QAdvanceFeedback.SlipAngleSource.json instead).
            SlipAngleRawResult raw = internalRaw;
            string identity = internalIdentity;
            double k = internalK;

            // 3. Impact guard (always stepped so its jump history stays continuous).
            double? latG = f?.LateralG;
            double latValue = latG.HasValue && ClampMath.IsFinite(latG.Value) ? latG.Value : double.NaN;
            double? dVlatDt = DVlatDt(f?.LateralLocalVelocity, dt);
            double? yaw = null;
            if (dVlatDt.HasValue && Math.Abs(AccelerationUnits.Ms2ToG(dVlatDt.Value)) <= YawCheckMaxDVlatG
                && f?.YawRateRadPerSec is double rate && ClampMath.IsFinite(rate)
                && _yawSign.TryGetSign(game, out int sign))
                yaw = sign * rate;
            double speedMps = f?.ForwardLocalVelocity ?? f?.GroundSpeedMps ?? double.NaN;
            ImpactVerdict impact = _impact.Observe(latValue, Hold(f?.LongitudinalG, ref _heldLongG), Hold(f?.VerticalG, ref _heldVertG),
                speedMps, yaw, yaw.HasValue ? dVlatDt : null, dt);

            // 4. Surface classifier + learner (only with data; pit / replay / restart / bad dt never teach).
            bool learnAllowed = gate.LearningAllowed
                && f?.IsInPit != true && f?.IsReplay != true && f?.IsSessionRestart != true
                && dt > 0.0 && dt <= MaxLearnDtSeconds;
            double absLat = Math.Abs(latValue);
            // SPEED-AWARE LATERAL G (owner, 2026-11-21): lateral G as a share of the grip limit at this speed,
            // scaled to the car's highest band - 1 until a band is learned (see SlipAngleSpeedBands). The bands are
            // per (game, car, source, surface) and never borrowed (owner, 2026-11-22); read on the surface the
            // classifier stood on last frame (this frame's is decided below).
            double speedKmh = f?.SpeedKmh ?? f?.GroundSpeedKmh ?? double.NaN;
            SurfaceBucket bandSurface = previousBlend.Active;
            double speedScale = _learner.SpeedBands.Scale(game, car, internalIdentity, bandSurface, speedKmh);
            double scaledLat = absLat * speedScale;
            // SLIDE GUARD + FAST LEARNING CONTEXT (owner, 2026-11-21): the body angle decides "sliding" for every
            // source; the speed band at this speed is the lateral G reference.
            double bodyDeg = BodyAngleDeg(f);
            if (ClampMath.IsFinite(bodyDeg) && bodyDeg >= SlipAngleEarlyEstimator.SlideBodyAngleDeg) _slideHold = 1.0;
            else _slideHold = Math.Max(0.0, _slideHold - dt);
            bool sliding = _slideHold > 0.0;
            _learner.SpeedBands.TryReference(game, car, internalIdentity, bandSurface, speedKmh,
                out double bandPeak, out double bandMean, out bool bandMatured, out bool bandYoung);
            result.Sliding = sliding;
            // THE SURFACE: ONLY WHAT THE GAME REPORTS, NEVER GUESSED (owner, 2026-11-22). Stepped every frame; a game
            // that reports no surface stays in the starting bucket (the source reads Unknown).
            SurfaceBlend blend = StepSurface(game, f, input, dt);
            if (internalRaw.AllDeg.HasValue && ClampMath.IsFinite(absLat))
            {
                double internalAll = internalRaw.AllDeg.Value;
                // The bands learn from Plugin Internal's angle and the RAW lateral G, on every surface into that
                // surface's own bands (owner, 2026-11-22; was sealed only) - and never while the car slides as long as
                // the band is not matured (owner, 2026-11-21).
                _learner.SpeedBands.Observe(game, car, internalIdentity, blend.Active, speedKmh, internalAll, absLat,
                    learnAllowed && impact.LearnAllowed && !(sliding && !bandMatured),
                    impact.RetroInvalidate, dt);
                // Plugin Internal learns this frame under its own key (Native or Fallback, whichever this frame's raw
                // angle is - docs\internal-source-learning-design.md 3.2).
                var internalEarly = new SlipAngleEarlyContext(absLat, bandPeak, bandMean, bandMatured, bandYoung, sliding,
                    AxleMax(internalRaw), internalRaw.Tier == SourceNativeness.Native && SlipAngleSourceScale.IsRatio(input.Unit), bodyDeg);
                _learner.Observe(game, car, internalIdentity, blend, internalRaw.FrontDeg ?? double.NaN, internalRaw.RearDeg ?? double.NaN,
                    internalAll, scaledLat, impact, learnAllowed, dt, internalEarly);
            }

            // 5. Learned window, maturity, tier (data thread only - these getters mutate).
            SlipAngleWindow learned = _learner.Window(game, car, identity, blend);
            double maturity = _learner.Maturity(game, car, identity, blend);
            ColdStartTier tier = _learner.Tier(game, car, identity, blend);

            // 6. Key data: manual gate, one-time seed, resolution.
            string gateKey = ColdStartTierResolver.Key(game, car, identity);
            _manualGate.Observe(gateKey, dt, advancing: gate.LearnSpeedOk && !gate.Airborne);
            bool gateReady = _manualGate.IsReady(gateKey, maturity);
            SlipAngleWindow applied = learned;
            if (r.KeyData != null)
            {
                if (gateReady && !r.KeyData.GetAutoGenerate(identity) && r.KeyData.TrySeed(game, identity, learned))
                    result.KeyDataSeeded = true;
                applied = r.KeyData.Resolve(game, identity, learned, _manualGate, gateKey, maturity);
                // Learning above never depends on this: Auto or Manual, the learned window keeps learning and is what is
                // saved; Manual only replaces the window the output uses.
                result.ManualApplied = gateReady && !r.KeyData.GetAutoGenerate(identity);
            }

            // 7. Normalize + project (the projection always steps so drift / pulse state stay continuous).
            double normalized = raw.AllDeg.HasValue ? SlipAngleNormalizer.Normalize(raw.AllDeg.Value, applied) : 0.0;
            double unclamped = raw.AllDeg.HasValue ? SlipAngleNormalizer.NormalizeUnclamped(raw.AllDeg.Value, applied) : 0.0;
            // Drift start: a native grip-ratio source has its own rule (a reachable start under its ceiling; one
            // that stops at full slip starts at full slip with the longer hold - owner, 2026-11-20).
            double driftStartNu;
            DriftSettings driftOverride = null;
            if (raw.Tier == SourceNativeness.Native && SlipAngleSourceScale.IsRatio(input.Unit))
            {
                driftStartNu = SlipAngleDriftStart.RatioNu(applied, input.RatioExceedsFullScale, out bool saturating);
                if (saturating) driftOverride = SaturatingDrift();
            }
            else
            {
                driftStartNu = SlipAngleDriftStart.Nu(applied);
            }
            SlipAngleProjectedResult projected = _projected.Compute(normalized, unclamped, maturity, tier, gate, dt,
                driftStartNu, f?.ThrottlePercent, driftOverride);

            // 8. Persistence on this (data) thread.
            result.LearnerSaved = _learner.SaveIfDue(dt, _save);

            // ---- capture plain values.
            result.RawAll = raw.AllDeg;
            result.NormalizedAll = normalized;
            result.NormalizedUnclamped = unclamped;
            result.ProjectedAll = projected.FinalAll;
            result.ProjectedPulseFree = projected.PulseFreeAll;
            if (raw.WheelsDeg.HasValue)
            {
                Corners w = raw.WheelsDeg.Value;
                result.WheelFrontLeft = w.FrontLeft;
                result.WheelFrontRight = w.FrontRight;
                result.WheelRearLeft = w.RearLeft;
                result.WheelRearRight = w.RearRight;
            }
            result.Front = raw.FrontDeg;
            result.Rear = raw.RearDeg;
            result.K = KeyedSlipAngleLearner.ClampK(k);
            result.SpeedScale = speedScale;
            result.FrontLearnedBest = _learner.Window(game, car, identity, SlipAngleTarget.Front, blend).Best;
            result.RearLearnedBest = _learner.Window(game, car, identity, SlipAngleTarget.Rear, blend).Best;
            result.Tier = raw.Tier;
            result.DriftState = projected.DriftState;
            result.DriftFactor = projected.DriftFactor;
            result.LearnedWindow = learned;
            result.AppliedWindow = applied;
            result.Maturity = maturity;
            result.WindowQuality = _learner.WindowQuality(game, car, identity, SlipAngleTarget.All, blend.Active);
            result.BorrowingTier = tier;
            result.PeakConfirmed = _learner.PeakConfirmed(game, car, identity, SlipAngleTarget.All, blend.Active);
            result.Crossings = _learner.CrossingCount(game, car, identity, SlipAngleTarget.All, blend.Active);
            if (_learner.TryEarly(game, car, identity, SlipAngleTarget.All, blend.Active, out double earlyWeight, out long earlyGuarded))
            {
                result.EarlyWeight = earlyWeight;
                result.EarlyGuarded = earlyGuarded;
            }
            result.CurvePeakG = _learner.CurvePeakG(game, car, identity, SlipAngleTarget.All, blend.Active);
            result.WobbleRemaining = _gate.WobbleRemainingSeconds;
            result.PedalGateOpen = gate.PedalOpen;
            result.ImpactFlag = impact.ImpactThisFrame;
            result.ExclusionRemaining = impact.ExclusionRemaining;
            result.YawResidual = impact.YawResidualG;
            result.SurfaceBucket = blend.Active;
            result.SurfaceWeightSealed = blend.WSealed;
            result.SurfaceWeightLoose = blend.WLoose;
            result.SurfaceWeightLowGrip = blend.WLowGrip;
            result.SurfaceSource = blend.Source;
            result.Unit = input.Unit;
            result.SourceState = input.SourceState;
            SlipAngleNativeSupport native = r.NativeSupport;
            if (native != null && game.Length > 0)
            {
                result.UnitUndetermined = native.IsUnitUndetermined(game);
                if (native.TryGetUnitVotes(game, out int votesDeg, out int votesRad))
                {
                    result.UnitVotesDegrees = votesDeg;
                    result.UnitVotesRadians = votesRad;
                }
                result.NativeBlocked = native.IsBlocked(game);
            }
            result.SourceIdentity = identity;
            result.SourceIsPluginInternal = true;   // the only source (Custom was removed, owner 2026-11-24)
            result.ManualGateReady = gateReady;
            return result;
        }

        /// <summary>The configured drift settings with the saturating-ratio hold, rebuilt when they change.</summary>
        private DriftSettings SaturatingDrift()
        {
            DriftSettings configured = _projected.Drift;
            if (!ReferenceEquals(configured, _saturatingDriftSource) || _saturatingDrift == null
                || _saturatingDriftHoldSource != configured.HoldSeconds)
            {
                _saturatingDriftSource = configured;
                _saturatingDriftHoldSource = configured.HoldSeconds;
                _saturatingDrift = configured.WithHoldSeconds(SlipAngleDriftStart.SaturatingHoldSeconds(configured.HoldSeconds));
            }
            return _saturatingDrift;
        }

        private double? DVlatDt(double? vlat, double dt)
        {
            double? previous = _previousVlat;
            _previousVlat = vlat.HasValue && ClampMath.IsFinite(vlat.Value) ? vlat : null;
            if (!previous.HasValue || !_previousVlat.HasValue || !(dt > 0.0)) return null;
            return (_previousVlat.Value - previous.Value) / dt;
        }

        private static double Hold(double? v, ref double held)
        {
            if (v.HasValue && ClampMath.IsFinite(v.Value)) held = v.Value;
            return held;
        }
    }
}

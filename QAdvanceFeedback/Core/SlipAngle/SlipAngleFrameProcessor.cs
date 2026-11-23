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

        /// <summary>This frame's Plugin Internal read (always read, also in Custom mode: kerbs, surface,
        /// native promotion keep running).</summary>
        public SlipAngleInput Input { get; set; } = SlipAngleInput.Empty;

        /// <summary>Native support state (unit detection / block diagnostics); optional.</summary>
        public SlipAngleNativeSupport NativeSupport { get; set; }

        /// <summary>False = Plugin Internal (native or fallback); true = Custom per-wheel sources.</summary>
        public bool UseCustomSource { get; set; }

        /// <summary>Custom mode: per-wheel degrees FL, FR, RL, RR; a null entry = unreadable wheel.</summary>
        public double?[] CustomWheelsDeg { get; set; }

        /// <summary>Custom mode: <see cref="Normalized.SourceIdentity"/> of the four custom fields.</summary>
        public string CustomSourceIdentity { get; set; }

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
    /// <see cref="FallbackIdentity"/> (their scales differ, 9.1); Custom under its own composite identity.</para>
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

        public const double MaxCustomDeg = 45.0;

        private readonly KeyedSlipAngleLearner _learner;
        private readonly Action<Dictionary<string, SlipAngleKeyState>> _save;
        private readonly YawSignDetector _yawSign = new YawSignDetector();
        private readonly ManualOverrideGate _manualGate = new ManualOverrideGate();
        private SurfaceBucketClassifier _surface = new SurfaceBucketClassifier();
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

            SlipAngleRawResult raw;
            string identity;
            double k;
            if (r.UseCustomSource)
            {
                identity = string.IsNullOrEmpty(r.CustomSourceIdentity) ? "SlipAngle.Custom" : r.CustomSourceIdentity;
                k = _learner.K(game, car, identity, previousBlend);
                raw = ComputeCustom(r.CustomWheelsDeg, input.KerbWheels ?? f?.WheelsOnKerbs, k, r.WeightMax, r.WeightMin);
            }
            else
            {
                raw = internalRaw;
                identity = internalIdentity;
                k = internalK;
            }

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
            double customScale = r.UseCustomSource ? _learner.SpeedBands.Scale(game, car, identity, bandSurface, speedKmh) : speedScale;
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
            // that reports no surface stays in the starting bucket (the source reads Unknown). Every source - Plugin
            // Internal and Custom - learns into this one bucket.
            SurfaceBlend blend = _surface.Step(SurfaceBucketMap.FromGame(game, input.RawWheelSurface), dt);
            if (internalRaw.AllDeg.HasValue && ClampMath.IsFinite(absLat))
            {
                double internalAll = internalRaw.AllDeg.Value;
                // The bands learn from Plugin Internal's angle and the RAW lateral G, on every surface into that
                // surface's own bands (owner, 2026-11-22; was sealed only) - and never while the car slides as long as
                // the band is not matured (owner, 2026-11-21).
                _learner.SpeedBands.Observe(game, car, internalIdentity, blend.Active, speedKmh, internalAll, absLat,
                    learnAllowed && impact.LearnAllowed && !(sliding && !bandMatured),
                    impact.RetroInvalidate, dt);
                // INTERNAL SOURCE ALWAYS LEARNS (docs\internal-source-learning-design.md 3.2): Plugin Internal
                // learns this frame under its own key (Native or Fallback, whichever this frame's raw angle
                // is) with the same gates, selected or not.
                var internalEarly = new SlipAngleEarlyContext(absLat, bandPeak, bandMean, bandMatured, bandYoung, sliding,
                    AxleMax(internalRaw), internalRaw.Tier == SourceNativeness.Native && SlipAngleSourceScale.IsRatio(input.Unit), bodyDeg);
                _learner.Observe(game, car, internalIdentity, blend, internalRaw.FrontDeg ?? double.NaN, internalRaw.RearDeg ?? double.NaN,
                    internalAll, scaledLat, impact, learnAllowed, dt, internalEarly);
            }

            // Custom learns only while selected, under its own key - its own speed bands included - into the shared
            // classification.
            if (r.UseCustomSource && raw.AllDeg.HasValue && ClampMath.IsFinite(absLat))
            {
                _learner.SpeedBands.TryReference(game, car, identity, bandSurface, speedKmh,
                    out double cPeak, out double cMean, out bool cMatured, out bool cYoung);
                _learner.SpeedBands.Observe(game, car, identity, blend.Active, speedKmh, raw.AllDeg.Value, absLat,
                    learnAllowed && impact.LearnAllowed && !(sliding && !cMatured), impact.RetroInvalidate, dt);
                _learner.Observe(game, car, identity, blend, raw.FrontDeg ?? double.NaN, raw.RearDeg ?? double.NaN, raw.AllDeg.Value,
                    absLat * customScale, impact, learnAllowed, dt,
                    new SlipAngleEarlyContext(absLat, cPeak, cMean, cMatured, cYoung, sliding, AxleMax(raw), false, bodyDeg));
            }

            // 4b. While Custom is selected, the reads the selected path makes for Plugin Internal's key (the
            // getters mutate), so its state follows the same history. Only the active (Custom) pass publishes.
            if (r.UseCustomSource)
                ReadPluginInternal(game, car, internalIdentity, blend, gate, dt);

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
            if (!r.UseCustomSource && raw.Tier == SourceNativeness.Native && SlipAngleSourceScale.IsRatio(input.Unit))
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
            result.SpeedScale = r.UseCustomSource ? customScale : speedScale;
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
            result.SourceIsPluginInternal = !r.UseCustomSource;
            result.ManualGateReady = gateReady;
            return result;
        }

        /// <summary>
        /// While Custom is the active source: the window / maturity / tier reads (these getters mutate -
        /// re-prior, cold latch) and the manual-gate time the selected path gives Plugin Internal's key, in
        /// the same order, and nothing that publishes. Its learner update already ran in step 4.
        /// </summary>
        private void ReadPluginInternal(string game, string car, string identity, SurfaceBlend blend,
            SlipAngleGateVerdict gate, double dt)
        {
            _learner.Window(game, car, identity, blend);
            _learner.Maturity(game, car, identity, blend);
            _learner.Tier(game, car, identity, blend);
            _manualGate.Observe(ColdStartTierResolver.Key(game, car, identity), dt, advancing: gate.LearnSpeedOk && !gate.Airborne);
            _learner.Window(game, car, identity, SlipAngleTarget.Front, blend);
            _learner.Window(game, car, identity, SlipAngleTarget.Rear, blend);
            _learner.WindowQuality(game, car, identity, SlipAngleTarget.All, blend.Active);
            _learner.PeakConfirmed(game, car, identity, SlipAngleTarget.All, blend.Active);
        }

        /// <summary>Custom mode: |α| per wheel clamped to 0-45°; an unreadable wheel is excluded from its
        /// axle exactly like a kerb wheel (both missing → that axle unavailable; all → no data).</summary>
        private static SlipAngleRawResult ComputeCustom(double?[] wheels, bool[] kerbs, double k, double wMax, double wMin)
        {
            var values = new double[4];
            var excluded = new bool[4];
            bool any = false;
            for (int i = 0; i < 4; i++)
            {
                double? v = wheels != null && i < wheels.Length ? wheels[i] : null;
                bool kerb = kerbs != null && i < kerbs.Length && kerbs[i];
                if (v.HasValue && ClampMath.IsFinite(v.Value))
                {
                    values[i] = ClampMath.Clamp(Math.Abs(v.Value), 0.0, MaxCustomDeg);
                    any = true;
                }
                else
                {
                    excluded[i] = true;
                }
                excluded[i] |= kerb;
            }
            if (!any) return SlipAngleRawResult.None;

            var input = new SlipAngleInput(string.Empty, hasNativeSource: true,
                nativeWheelDeg: new Corners(values[0], values[1], values[2], values[3]), isNative: true,
                unit: SlipAngleUnit.Degrees, kerbWheels: excluded);
            SlipAngleRawResult r = SlipAngleRawCalculator.Compute(input, null, true, k, wMax, wMin);
            // A driver-defined source is never classified native/fallback (Lock/Slip parity): tier Unknown.
            return new SlipAngleRawResult(r.FrontDeg, r.RearDeg, r.AllDeg, SourceNativeness.Unknown, r.WheelsDeg);
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

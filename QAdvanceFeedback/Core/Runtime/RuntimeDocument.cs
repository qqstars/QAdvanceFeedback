using System.Collections.Generic;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Core.Runtime
{
    /// <summary>
    /// The full contents of the runtime/learned-parameters file, <c>QAdvanceFeedback.Parameters.json</c>
    /// (renamed from <c>plugin.QAdvanceFeedback.runtime.json</c> - see <c>RuntimeStore</c>'s remarks for
    /// the one-time import of a file still under the old name):
    /// <list type="bullet">
    /// <item>Layer 4's two <see cref="Normalized.GripLearner"/> families (Lock, Slip), each now keyed per
    /// (gameId, carId) via <see cref="KeyedGripLearner"/> - fixing the gap docs\layer45-report.md
    /// flagged explicitly ("a single global value per channel, not per-car/per-game").</item>
    /// <item>The G-force channels' two <see cref="GForce.GForceMaxLearner"/> AUTO-learned-maximum
    /// dictionaries (already keyed per game+car since the wiring task - unaffected by this change).</item>
    /// </list>
    /// Plain POCO with public fields and a parameterless constructor, same convention as every other
    /// settings/runtime type in this plugin family, so Newtonsoft round-trips it with no attributes -
    /// the actual (de)serialisation happens outside Core, in <c>RuntimeStore</c>, since Core itself
    /// never references Newtonsoft.Json.
    /// </summary>
    public sealed class RuntimeDocument
    {
        /// <summary>Bumped from 1: the shape of <c>LockLearnedPeakG</c>/<c>LockSamples</c>/
        /// <c>SlipLearnedPeakG</c>/<c>SlipSamples</c> (one global value per channel) changed to the
        /// keyed dictionaries below (one value per game+car). A file written under the OLD name at
        /// the OLD shape is handled separately, as a one-time import - see <c>RuntimeStore.LoadFromDisk</c>
        /// and <c>LegacyRuntimeDocument</c> - not by trying to deserialise it into this shape directly.</summary>
        /// <remarks>
        /// BUMPED AGAIN, 2 -&gt; 3 (telemetry-integrity pass, items 2/3): <see cref="LockScaleLearners"/>/
        /// <see cref="SlipScaleLearners"/> (the Raw-side per-source calibration learner,
        /// <c>KeyedScaleLearner</c> - previously session-scoped only, now cold/warm persisted) and
        /// <see cref="SurfaceSupportByGame"/> (the per-game telemetry-support detector,
        /// <c>KeyedTelemetrySupport</c>) are NEW additions, not a shape change to anything that already
        /// existed - UNLIKE the 1-&gt;2 bump, this needs NO explicit one-time-import code: a Version-2 file
        /// simply does not have these three keys in its JSON at all, and Newtonsoft's own
        /// deserialisation convention (construct the object first - running every field initialiser,
        /// including these three new dictionaries' own empty defaults - THEN overwrite only the fields
        /// actually present in the JSON) already leaves them at their correct "nothing persisted yet"
        /// empty state with zero extra code. The Version number is still bumped, and this reasoning
        /// stated explicitly, so a future reader is not left wondering why no matching
        /// <c>LegacyRuntimeDocument</c>-style conversion exists for this bump the way one does for 1-&gt;2.
        /// </remarks>
        /// <remarks>
        /// BUMPED AGAIN, 3 -&gt; 4 (docs\cold-start-and-timing-fix-report.md - the F1 25 car-switch/restart
        /// regression): <see cref="LockPhysicalReference"/>/<see cref="SlipPhysicalReference"/> (the
        /// shared, (game,car)-only physical-limit detector that now solely gates
        /// <c>KeyedScaleLearner</c>'s PRIMARY tier - previously session-scoped only, flagged as a
        /// candidate follow-up in the f1-normalization-fix-report and fixed here) and
        /// <see cref="LockScaleCrossCarSeed"/>/<see cref="SlipScaleCrossCarSeed"/> (the new per-(game,
        /// source) cold-start seed a brand-new car can start from instead of bare identity - see
        /// <c>KeyedScaleLearner</c>'s own remarks) are NEW additions, exactly like the 2-&gt;3 bump before
        /// it - no explicit one-time-import code needed, for the identical reason that bump's own remarks
        /// give (a Version-3 file simply lacks these keys; Newtonsoft's field-initialiser-then-overwrite
        /// convention already leaves them at their correct empty "nothing persisted yet" state).
        /// </remarks>
        /// <remarks>
        /// BUMPED AGAIN, 4 -&gt; 5 (speed-aware-grip-report.md): <see cref="GripLearnerState.GMech"/>/
        /// <see cref="GripLearnerState.K"/> (the speed-dependent grip model's two learned parameters -
        /// see <c>Normalized.GripLearner</c>) are NEW fields on the EXISTING <see cref="GripLearnerState"/>
        /// shape, not a new dictionary - exactly like the 2-&gt;3 bump's own reasoning: a Version-4 (or
        /// earlier) file simply lacks these two keys inside each entry's own JSON object, and Newtonsoft's
        /// construct-then-overwrite convention already leaves them at their correct "not learned yet"
        /// sentinel (0.0), which <c>GripLearner.Load</c> already treats as "fall back to the flat PeakG,
        /// k=0" - the exact bit-identical behaviour this feature guarantees for any document written
        /// before it existed. No explicit one-time-import code needed.
        /// </remarks>
        /// <remarks>
        /// BUMPED AGAIN, 5 -&gt; 6 (docs\adaptive-peak-learner-report.md): <see cref="GripLearnerState.RaiseCandidateG"/>/
        /// <see cref="GripLearnerState.RaiseCandidateHits"/>/<see cref="GripLearnerState.LowerCandidateG"/>/
        /// <see cref="GripLearnerState.LowerCandidateHits"/> (the evidence-weighted adaptive peak
        /// estimator's own in-progress corroboration state, replacing the old fixed-gain decaying
        /// maximum) are NEW fields on the EXISTING <see cref="GripLearnerState"/> shape, not a new
        /// dictionary - exactly like the 4-&gt;5 bump's own reasoning: a Version-5 (or earlier) file
        /// simply lacks these four keys inside each entry's own JSON object, and Newtonsoft's own
        /// construct-then-overwrite convention already leaves them at their correct "no corroboration in
        /// progress yet" defaults (0.0/0), which <c>GripLearner.Load</c> already treats as a no-op seed -
        /// bit-identical to a freshly-constructed learner's own starting confidence state. No explicit
        /// one-time-import code needed.
        /// </remarks>
        /// <remarks>
        /// BUMPED AGAIN, 6 -&gt; 7 (docs\stability-confidence-fix-report.md - the cold-vs-converged
        /// over-reporting safety fix): <see cref="GripLearnerState.QuietStreak"/> (the flat scalar's own
        /// in-progress "reference has settled" streak, gating <c>GripLearner.MaturityConfidence</c>
        /// alongside sample count) is a NEW field on the EXISTING <see cref="GripLearnerState"/> shape,
        /// not a new dictionary - exactly like the 5-&gt;6 bump's own reasoning: a Version-6 (or
        /// earlier) file simply lacks this one key inside each entry's own JSON object, and Newtonsoft's
        /// construct-then-overwrite convention already leaves it at its correct "no settling evidence
        /// yet this session" default (0.0), which <c>GripLearner.Load</c> already treats as a no-op seed
        /// - bit-identical to a freshly-constructed learner's own starting stability state. No explicit
        /// one-time-import code needed.
        /// </remarks>
        /// <remarks>
        /// BUMPED AGAIN, 7 -&gt; 8 (docs\v1068-four-range-report.md, Feature C) - <see cref="LockAnchors"/>
        /// (WheelLock's own learned S75/S90 anchors - <c>Normalized.LockAnchorLearner</c>) is a NEW
        /// dictionary, not a shape change to anything that already existed - exactly like the 2-&gt;3 bump's
        /// own reasoning: a Version-7 (or earlier) file simply lacks this key in its JSON at all, and
        /// Newtonsoft's construct-then-overwrite convention already leaves it at its own field
        /// initialiser's empty default - no explicit one-time-import code needed. No Slip equivalent - the
        /// owner was explicit that the 30/60 anchors, and this entire four-range mapping, apply to
        /// WheelLock only.
        /// </remarks>
        /// <remarks>
        /// BUMPED AGAIN, 8 -&gt; 9 (v1.0.7, docs\v107-tiered-coldstart-report.md - the tiered cold-start
        /// reference system): <see cref="LockScaleCrossCarSeed"/>/<see cref="SlipScaleCrossCarSeed"/> are
        /// now VESTIGIAL - the old (game,source)-only cross-car seed mechanism they backed is superseded
        /// by <c>KeyedScaleLearner.ResolveReference</c>, which resolves ALL FOUR tiers directly from
        /// <see cref="LockScaleLearners"/>/<see cref="SlipScaleLearners"/>' own per-key entries (already
        /// persisted) rather than a second, separately-maintained seed dictionary - see
        /// <c>KeyedScaleLearner</c>'s own remarks for why this reconciliation replaces rather than layers
        /// on top of the old mechanism. <c>KeyedScaleLearner.ObserveAtPhysicalLimit</c> no longer writes to
        /// the seed, so these two dictionaries are always empty going forward; a Version-8-or-earlier
        /// file's own seed section is simply READ (for backward-compatible deserialisation - the fields
        /// still exist so an old file does not fail to parse) and then never acted upon again - a
        /// one-time, silent, safe drop, not a throw. No NEW field/dictionary was needed for the tiered
        /// resolver itself, since it deliberately reuses state this class already persists.
        /// </remarks>
        /// <remarks>Version 11 (1.0.7.1) adds <see cref="ShakeItCalibration"/> and
        /// <see cref="ShakeItPrecalibration"/> - additive, so no migration is needed (the same reasoning
        /// as every earlier additive bump): a Version-9-or-earlier file simply has neither section, both
        /// deserialise to empty dictionaries, and Layer 3 then behaves as a first run - which is exactly
        /// the condition that triggers the automatic ShakeIt precalibration import.</remarks>
        public int Version = 11;

        /// <summary>Per (gameId, carId) Lock-channel learner state - key format matches
        /// <see cref="KeyedGripLearner.MakeKey"/>.</summary>
        public Dictionary<string, GripLearnerState> LockLearners = new Dictionary<string, GripLearnerState>();

        /// <summary>The Slip channel's equivalent of <see cref="LockLearners"/>.</summary>
        public Dictionary<string, GripLearnerState> SlipLearners = new Dictionary<string, GripLearnerState>();

        public Dictionary<string, double> GForceAccelLearnedMaxima = new Dictionary<string, double>();
        public Dictionary<string, double> GForceDecelLearnedMaxima = new Dictionary<string, double>();

        /// <summary>Version 3: the Lock channel's per (gameId, carId, sourceIdentity) COLD calibration
        /// ceiling - see <c>KeyedScaleLearner.ExportAll</c>/<c>ImportAll</c>.</summary>
        public Dictionary<string, ScaleLearnerState> LockScaleLearners = new Dictionary<string, ScaleLearnerState>();

        /// <summary>The Slip channel's equivalent of <see cref="LockScaleLearners"/>.</summary>
        public Dictionary<string, ScaleLearnerState> SlipScaleLearners = new Dictionary<string, ScaleLearnerState>();

        /// <summary>Version 3: per-GAME (only) telemetry support detection - see
        /// <c>KeyedTelemetrySupport.ExportAll</c>/<c>ImportAll</c>. Currently tracks whether a title
        /// genuinely supports loose-surface (grass/gravel) reporting - the one field this plugin's own
        /// audit found no <c>FeedbackCapabilities</c> flag covers at all.</summary>
        public Dictionary<string, bool> SurfaceSupportByGame = new Dictionary<string, bool>();

        /// <summary>Version 4: the Lock channel's shared, (game,car)-only physical-limit detector - see
        /// <c>NormalizedWheelLockSlipEngine.LockPhysicalReference</c>/<c>KeyedGripLearner.ExportAll</c>/
        /// <c>ImportAll</c>.</summary>
        public Dictionary<string, GripLearnerState> LockPhysicalReference = new Dictionary<string, GripLearnerState>();

        /// <summary>The Slip channel's equivalent of <see cref="LockPhysicalReference"/>.</summary>
        public Dictionary<string, GripLearnerState> SlipPhysicalReference = new Dictionary<string, GripLearnerState>();

        /// <summary>Version 4: the Lock channel's per-(gameId,sourceIdentity) cross-car cold-start seed -
        /// see <c>KeyedScaleLearner.ExportCrossCarSeeds</c>/<c>ImportCrossCarSeeds</c>.</summary>
        public Dictionary<string, ScaleLearnerState> LockScaleCrossCarSeed = new Dictionary<string, ScaleLearnerState>();

        /// <summary>The Slip channel's equivalent of <see cref="LockScaleCrossCarSeed"/>.</summary>
        public Dictionary<string, ScaleLearnerState> SlipScaleCrossCarSeed = new Dictionary<string, ScaleLearnerState>();

        /// <summary>Version 8 (docs\v1068-four-range-report.md, Feature C): WheelLock's own learned
        /// S75/S90 anchors - see <c>LockAnchorLearner.ExportAll</c>/<c>ImportAll</c>. WHEELLOCK ONLY -
        /// there is deliberately no Slip equivalent.</summary>
        public Dictionary<string, LockAnchorState> LockAnchors = new Dictionary<string, LockAnchorState>();

        /// <summary>
        /// Version 11 (1.0.7.1): LAYER 3's own ShakeIt calibration, keyed exactly as SimHub keys its
        /// own - <c>track;car;metric</c>, see
        /// <see cref="RawCalculator.Calibration.CalibrationDataProvider.BuildKey"/>. This is the direct
        /// equivalent of the <c>CalibrationDataV5</c> dictionary SimHub persists, and it is persisted
        /// here for the same reason: so a calibration simply RESUMES accumulating next run rather than
        /// rebuilding from zero. There is deliberately no reference/blend/handover machinery around it -
        /// resuming a histogram cannot produce a transition artefact, which an earlier
        /// reference-and-blend design demonstrably could.
        /// </summary>
        public Dictionary<string, RawCalculator.Calibration.CalibrationData> ShakeItCalibration =
            new Dictionary<string, RawCalculator.Calibration.CalibrationData>();

        /// <summary>
        /// Version 11 (1.0.7.1): SHIPPED ShakeIt precalibration, converted from SimHub's own
        /// <c>ShakeIt\ShakeItPrecalibration\GameData.json</c> - outer key is SimHub's game code, inner key
        /// is the metric name (<c>Slip</c>, <c>RPSToSpeedFront</c>, ...). Where a preset exists for the
        /// running game, it supplies three quarters of the published band permanently and this key's own
        /// live calibration the remaining quarter - SimHub's own fixed 0.25 ratio, not a ramp. See
        /// <see cref="RawCalculator.Calibration.PreloadedCalibrationData"/>.
        /// <para/>
        /// This is the ONE thing SimHub ships that we cannot: their file is theirs to distribute. Ours is
        /// empty until the driver runs the converter, which is why the converter runs automatically the
        /// first time no Raw calibration exists at all.
        /// </summary>
        public Dictionary<string, Dictionary<string, RawCalculator.Calibration.PreloadedCalibrationData>> ShakeItPrecalibration =
            new Dictionary<string, Dictionary<string, RawCalculator.Calibration.PreloadedCalibrationData>>();

        /// <summary>Version 11: the per-game wheel-speed-delta bounds that sit alongside
        /// <see cref="ShakeItPrecalibration"/> on SimHub's own GameCalibration object, keyed by the same
        /// game-code pattern - see <see cref="RawCalculator.Calibration.GameCalibrationBounds"/>.</summary>
        public Dictionary<string, RawCalculator.Calibration.GameCalibrationBounds> ShakeItGameBounds =
            new Dictionary<string, RawCalculator.Calibration.GameCalibrationBounds>();

        /// <summary>Version 11: source file name to its last-write time in UTC ticks. The ShakeIt import
        /// runs on EVERY launch so a SimHub update that ships new games is picked up automatically; this
        /// is what keeps that cheap, since an unchanged file is never opened.</summary>
        public Dictionary<string, long> ShakeItSourceTimestamps = new Dictionary<string, long>();
    }

    /// <summary>
    /// The OLD (Version 1, pre-per-car) runtime file shape - one global <c>LockLearnedPeakG</c>/
    /// <c>LockSamples</c>/<c>SlipLearnedPeakG</c>/<c>SlipSamples</c> per channel, under the OLD file
    /// name <c>plugin.QAdvanceFeedback.runtime.json</c>. Kept as its own small type (rather than trying
    /// to keep the old fields alongside the new dictionaries on <see cref="RuntimeDocument"/> itself)
    /// so the two shapes can never be confused mid-deserialisation - <c>RuntimeStore</c> only ever
    /// parses a file under the NEW name as <see cref="RuntimeDocument"/> and a file under the OLD name
    /// (and ONLY when the new one does not exist yet) as this type, then converts it once.
    /// </summary>
    public sealed class LegacyRuntimeDocument
    {
        public int Version = 1;

        public double LockLearnedPeakG = 1.0;
        public int LockSamples = 0;

        public double SlipLearnedPeakG = 1.0;
        public int SlipSamples = 0;

        public Dictionary<string, double> GForceAccelLearnedMaxima = new Dictionary<string, double>();
        public Dictionary<string, double> GForceDecelLearnedMaxima = new Dictionary<string, double>();
    }
}

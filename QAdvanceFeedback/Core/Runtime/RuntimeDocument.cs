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
        public int Version = 8;

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

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
        public int Version = 3;

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

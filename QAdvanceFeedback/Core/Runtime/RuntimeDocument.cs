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
        public int Version = 2;

        /// <summary>Per (gameId, carId) Lock-channel learner state - key format matches
        /// <see cref="KeyedGripLearner.MakeKey"/>.</summary>
        public Dictionary<string, GripLearnerState> LockLearners = new Dictionary<string, GripLearnerState>();

        /// <summary>The Slip channel's equivalent of <see cref="LockLearners"/>.</summary>
        public Dictionary<string, GripLearnerState> SlipLearners = new Dictionary<string, GripLearnerState>();

        public Dictionary<string, double> GForceAccelLearnedMaxima = new Dictionary<string, double>();
        public Dictionary<string, double> GForceDecelLearnedMaxima = new Dictionary<string, double>();
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

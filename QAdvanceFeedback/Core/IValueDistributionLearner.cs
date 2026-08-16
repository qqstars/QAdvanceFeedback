namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// A small, game-agnostic contract for learning the shape of a stream of numbers observed over
    /// time: fold in a new reading, then ask what value sits at a given percentile of everything seen
    /// so far, what the plain mean is, and whether enough evidence has accumulated to trust either
    /// answer yet. Used wherever Layer 3 needs to compare a live reading against "what does this signal
    /// typically look like" without any external, pre-supplied reference for what normal/extreme values
    /// are - see <c>QAdvanceFeedback.Core.RawCalculator.StreamingPercentileLearner</c> (the concrete
    /// implementation) and <c>DispatchBranchFormulas</c>/<c>RawCalculatorEngine</c> for where this is
    /// used.
    /// </summary>
    public interface IValueDistributionLearner
    {
        /// <summary>Folds one new reading into the learner. Implementations should ignore a
        /// non-finite or otherwise unusable reading rather than let it corrupt the learned shape.</summary>
        void Observe(double value);

        /// <summary>The value below which roughly <paramref name="percentileRank"/> percent (0-100) of
        /// every reading observed so far falls, or null while <see cref="IsMature"/> is false.</summary>
        double? Percentile(double percentileRank);

        /// <summary>The plain mean of every reading observed so far, or null if nothing has been
        /// observed yet.</summary>
        double? Average();

        /// <summary>How many readings have been folded in so far.</summary>
        int Count { get; }

        /// <summary>Whether enough readings have been observed for <see cref="Percentile"/> to be
        /// trusted.</summary>
        bool IsMature { get; }
    }
}

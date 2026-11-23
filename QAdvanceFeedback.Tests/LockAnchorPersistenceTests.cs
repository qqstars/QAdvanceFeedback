using System;
using System.Collections.Generic;
using System.IO;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Lock's learned S90/S75 anchor ratios must reach QAdvanceFeedback.Parameters.json and come back on
    /// the next start - otherwise a restart shows (and the engine uses) the 0.90/0.70 seeds instead of the
    /// persisted S90/S75 (found 2026-11-19 while verifying the restart display: the runtime cache's copy
    /// kept only the legacy fields).
    /// </summary>
    public class LockAnchorPersistenceTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "qaf-anchors-" + Guid.NewGuid() + ".json");

        public void Dispose()
        {
            if (File.Exists(_path)) File.Delete(_path);
        }

        [Fact]
        public void Every_anchor_field_survives_the_parameters_file()
        {
            var state = new LockAnchorState
            {
                S75 = 40.0, Hits75 = 2, Candidate75 = 41.0,
                S90 = 50.0, Hits90 = 3, Candidate90 = 51.0,
                RatioLevel75 = 0.66, RatioHits75 = 4, RatioCandidate75 = 0.65,
                RatioLevel90 = 0.86, RatioHits90 = 5, RatioCandidate90 = 0.85,
                PhysicalRatioLevel75 = 0.32, PhysicalRatioHits75 = 1, PhysicalRatioCandidate75 = 0.31,
                PhysicalRatioLevel90 = 0.68, PhysicalRatioHits90 = 3, PhysicalRatioCandidate90 = 0.67,
            };

            using (var store = new RuntimeStore(_path, flushInterval: TimeSpan.FromHours(1)))
            {
                store.SaveLockAnchors(new Dictionary<string, LockAnchorState> { ["k"] = state });
                store.Flush();
            }

            Dictionary<string, LockAnchorState> back;
            using (var store = new RuntimeStore(_path, flushInterval: TimeSpan.FromHours(1)))
                store.LoadLockAnchors(out back);

            LockAnchorState b = back["k"];
            Assert.Equal((40.0, 2, 41.0, 50.0, 3, 51.0), (b.S75, b.Hits75, b.Candidate75, b.S90, b.Hits90, b.Candidate90));
            Assert.Equal((0.66, 4, 0.65, 0.86, 5, 0.85), (b.RatioLevel75, b.RatioHits75, b.RatioCandidate75, b.RatioLevel90, b.RatioHits90, b.RatioCandidate90));
            Assert.Equal((0.32, 1, 0.31, 0.68, 3, 0.67),
                (b.PhysicalRatioLevel75, b.PhysicalRatioHits75, b.PhysicalRatioCandidate75, b.PhysicalRatioLevel90, b.PhysicalRatioHits90, b.PhysicalRatioCandidate90));
        }
    }
}

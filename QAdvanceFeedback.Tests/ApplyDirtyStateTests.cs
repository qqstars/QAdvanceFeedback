using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Covers the Apply button's dirty-tracking flag (docs\apply-dirty-and-screenshots-report.md) -
    /// the one piece of that feature that is pure C# with no WPF/SimHub dependency, so it is the only
    /// part actually exercised by xunit; the wiring of every settings control's change handler in
    /// SettingsControl.xaml.cs (which calls into this class) is verified by enumeration and by a
    /// manual WPF-hosted check instead, since the Tests project is deliberately SimHub/WPF-free (see
    /// this project's own csproj remarks) and cannot instantiate a net48 UserControl.
    /// </summary>
    public class ApplyDirtyStateTests
    {
        [Fact]
        public void A_fresh_state_is_not_dirty()
        {
            var state = new ApplyDirtyState();
            Assert.False(state.IsDirty);
        }

        [Fact]
        public void MarkDirty_sets_IsDirty()
        {
            var state = new ApplyDirtyState();
            state.MarkDirty();
            Assert.True(state.IsDirty);
        }

        [Fact]
        public void MarkClean_clears_IsDirty()
        {
            var state = new ApplyDirtyState();
            state.MarkDirty();
            state.MarkClean();
            Assert.False(state.IsDirty);
        }

        [Fact]
        public void MarkDirty_while_loading_is_suppressed()
        {
            var state = new ApplyDirtyState();
            using (state.BeginLoading())
            {
                state.MarkDirty();
                Assert.False(state.IsDirty);
            }
            Assert.False(state.IsDirty);
        }

        [Fact]
        public void MarkDirty_after_loading_ends_still_works()
        {
            var state = new ApplyDirtyState();
            using (state.BeginLoading()) { /* simulate a load with no edits */ }

            state.MarkDirty();

            Assert.True(state.IsDirty);
        }

        // ------------------------------------------------------------------------------------
        // Reentrancy: this is the exact bug the depth counter replaces a bare bool to avoid.
        // SettingsControl.LoadFromSettings wraps its whole body in ONE BeginLoading scope, but calls
        // into helpers (LoadAnchorControls, SetSourceModeCombo) that open and close their OWN nested
        // BeginLoading scope. With a plain bool, the inner scope's Dispose would reset the flag to
        // "not loading" while the OUTER load still has more control assignments left to make below it -
        // letting those remaining assignments incorrectly mark dirty.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Nested_BeginLoading_does_not_end_the_outer_scope_early()
        {
            var state = new ApplyDirtyState();

            using (state.BeginLoading()) // outer scope - e.g. LoadFromSettings
            {
                using (state.BeginLoading()) // inner scope - e.g. LoadAnchorControls, nested inside
                {
                    state.MarkDirty();
                }

                // The inner scope has closed, but the OUTER one is still open - a further
                // programmatic assignment here (as LoadFromSettings makes many of, after its own
                // nested calls return) must still be suppressed.
                Assert.True(state.IsLoading);
                state.MarkDirty();
                Assert.False(state.IsDirty);
            }

            Assert.False(state.IsLoading);

            // Only once every scope - inner AND outer - has closed does a real edit register again.
            state.MarkDirty();
            Assert.True(state.IsDirty);
        }

        [Fact]
        public void IsLoading_reflects_open_scopes()
        {
            var state = new ApplyDirtyState();
            Assert.False(state.IsLoading);

            System.IDisposable scope = state.BeginLoading();
            Assert.True(state.IsLoading);

            scope.Dispose();
            Assert.False(state.IsLoading);
        }

        [Fact]
        public void MarkClean_while_loading_still_clears_IsDirty()
        {
            // Not a scenario SettingsControl relies on today, but MarkClean has no reason to be
            // suppressed by IsLoading the way MarkDirty is - it should always win.
            var state = new ApplyDirtyState();
            state.MarkDirty();
            using (state.BeginLoading())
            {
                state.MarkClean();
                Assert.False(state.IsDirty);
            }
        }
    }
}

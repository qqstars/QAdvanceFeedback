using System;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// Tracks whether <see cref="SettingsControl"/> has any unsaved change, so the Apply button's
    /// enabled state is driven by ONE small, unit-testable flag instead of scattered per-control
    /// bookkeeping in the code-behind. Every input control's change handler routes through the same
    /// <c>MarkDirty</c> call (see <see cref="SettingsControl"/>'s own private MarkDirty wrapper), so a
    /// newly added control has one obvious thing to call - and this class has no WPF/SimHub
    /// dependency at all, so it is exercised directly by xunit (see ApplyDirtyStateTests) even though
    /// the WPF control that owns it cannot be instantiated from the test project.
    /// <para/>
    /// Loading settings into controls programmatically (construction, after a successful Apply, after
    /// Restore, after a curve preset flip) must NOT be treated as a user edit. Callers wrap that code
    /// in <see cref="BeginLoading"/>, which suppresses <see cref="MarkDirty"/> for its scope.
    /// <para/>
    /// The guard is a re-entrant DEPTH COUNTER, not a bool: loading code nests (SettingsControl's own
    /// LoadFromSettings calls into LoadAnchorControls/SetSourceModeCombo, each of which used to flip an
    /// independent bool). With a plain bool, the FIRST inner call to finish would clear the guard while
    /// the outer load was still in progress, letting the REMAINING assignments in the outer method mark
    /// dirty. A counter has no such reentrancy hazard - it only reads as "not loading" once every
    /// <see cref="BeginLoading"/> scope, however deeply nested, has been disposed.
    /// </summary>
    public sealed class ApplyDirtyState
    {
        private int _loadingDepth;

        /// <summary>True if there is at least one change that has not yet been Applied.</summary>
        public bool IsDirty { get; private set; }

        /// <summary>True while any <see cref="BeginLoading"/> scope (possibly nested) is still open.</summary>
        public bool IsLoading => _loadingDepth > 0;

        /// <summary>Call from every control's change handler. A no-op while <see cref="IsLoading"/>.</summary>
        public void MarkDirty()
        {
            if (IsLoading) return;
            IsDirty = true;
        }

        /// <summary>Call once a successful Apply - or an already-persisted Restore, see
        /// SettingsControl.RestoreAllDefaults's own remarks - has left nothing pending.</summary>
        public void MarkClean() => IsDirty = false;

        /// <summary>
        /// Wrap a block of programmatic control assignments so any change events they raise do not
        /// mark dirty. Reentrant - safe to call from a loading helper that itself calls another
        /// loading helper.
        /// </summary>
        public IDisposable BeginLoading()
        {
            _loadingDepth++;
            return new LoadingScope(this);
        }

        private sealed class LoadingScope : IDisposable
        {
            private ApplyDirtyState _owner;

            public LoadingScope(ApplyDirtyState owner) => _owner = owner;

            public void Dispose()
            {
                if (_owner == null) return;
                _owner._loadingDepth--;
                _owner = null;
            }
        }
    }
}

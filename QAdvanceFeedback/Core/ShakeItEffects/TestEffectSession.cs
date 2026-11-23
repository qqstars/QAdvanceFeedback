using System;

namespace QAdvanceFeedback.Core.ShakeItEffects
{
    public enum TestKind { None, GForce, Lock, Slip, Clutch, SlipAngle }

    /// <summary>A test that was stopped because another one began.</summary>
    public sealed class TestStop
    {
        public TestStop(object owner, TestKind kind, TestKind by) { Owner = owner; Kind = kind; By = by; }

        /// <summary>The view model whose test was stopped.</summary>
        public object Owner { get; }
        public TestKind Kind { get; }
        /// <summary>The kind of the test that took over.</summary>
        public TestKind By { get; }
    }

    /// <summary>
    /// Which Test Effect runs (docs\shakeit-test-effect-mvvm-design.md). At most one runs at a time,
    /// keyed by OWNER: there can be several test view models of the same kind (the page's G-Force test
    /// plus one per G-Force effect; one per WheelLock/WheelSlip effect in each profile).
    /// <list type="bullet">
    /// <item>Beginning one stops any other owner, of any kind. The stopped owner hears
    /// <see cref="Stopped"/> and turns its toggle off.</item>
    /// <item><see cref="End"/> clears only the caller's own claim.</item>
    /// </list>
    /// Thread-safe; <see cref="Stopped"/> is raised outside the lock.
    /// </summary>
    public sealed class TestEffectSession
    {
        private readonly object _lock = new object();
        private object _owner;
        private TestKind _active;

        public TestKind Active { get { lock (_lock) return _active; } }
        public object ActiveOwner { get { lock (_lock) return _owner; } }

        /// <summary>Raised for the test that was stopped because another one began.</summary>
        public event Action<TestStop> Stopped;

        /// <summary>How many view models listen now (only running tests do).</summary>
        public int ListenerCount => Stopped?.GetInvocationList().Length ?? 0;

        /// <summary>True when two kinds publish to the same outputs (G-Force, Clutch, Slip Angle, or the
        /// Projected.* pair Lock and Slip share): a test taking over from one of these must not have the stopped one
        /// release the outputs it now drives.</summary>
        public static bool SharesOutputs(TestKind a, TestKind b)
        {
            bool aLockSlip = a == TestKind.Lock || a == TestKind.Slip;
            bool bLockSlip = b == TestKind.Lock || b == TestKind.Slip;
            return (a == TestKind.GForce && b == TestKind.GForce) || (a == TestKind.Clutch && b == TestKind.Clutch)
                || (a == TestKind.SlipAngle && b == TestKind.SlipAngle) || (aLockSlip && bLockSlip);
        }

        /// <summary>Starts <paramref name="owner"/>'s test; false if it was already the active one.</summary>
        public bool Begin(object owner, TestKind kind)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            object previous;
            TestKind previousKind;
            lock (_lock)
            {
                if (ReferenceEquals(_owner, owner)) { _active = kind; return false; }
                previous = _owner;
                previousKind = _active;
                _owner = owner;
                _active = kind;
            }
            if (previous != null) Stopped?.Invoke(new TestStop(previous, previousKind, kind));
            return true;
        }

        /// <summary>Ends <paramref name="owner"/>'s test if it is the active one; otherwise does nothing.</summary>
        public void End(object owner)
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_owner, owner)) return;
                _owner = null;
                _active = TestKind.None;
            }
        }
    }
}

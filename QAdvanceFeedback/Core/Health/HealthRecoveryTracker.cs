namespace QAdvanceFeedback.Core.Health
{
    /// <summary>
    /// Clears one subsystem's <see cref="HealthRegistry"/> entry once it has run cleanly for a while, so
    /// a one-off fault does not leave a permanent message on the settings screen. Call
    /// <see cref="NoteFault"/> next to the <see cref="HealthRegistry.Report"/> and <see cref="NoteGood"/>
    /// after every successful frame: after <see cref="GoodFramesToClear"/> consecutive good frames the
    /// entry is removed (<see cref="HealthRegistry.Clear"/>). Any fault restarts the count. Not
    /// thread-safe - one owning thread (the data thread).
    /// </summary>
    public sealed class HealthRecoveryTracker
    {
        /// <summary>Consecutive good frames after which the entry is cleared (about 5 s at 60 Hz).</summary>
        public const int DefaultGoodFramesToClear = 300;

        private readonly string _subsystem;
        private int _good;
        private bool _faulted;

        public HealthRecoveryTracker(string subsystem, int goodFramesToClear = DefaultGoodFramesToClear)
        {
            _subsystem = subsystem;
            GoodFramesToClear = goodFramesToClear < 1 ? 1 : goodFramesToClear;
        }

        public int GoodFramesToClear { get; }

        /// <summary>True between a fault and the clear.</summary>
        public bool IsFaulted => _faulted;

        /// <summary>A fault was reported this frame: (re)start the recovery count.</summary>
        public void NoteFault()
        {
            _faulted = true;
            _good = 0;
        }

        /// <summary>A good frame. Returns true on the frame that clears the entry.</summary>
        public bool NoteGood()
        {
            if (!_faulted) return false;
            if (++_good < GoodFramesToClear) return false;
            _faulted = false;
            _good = 0;
            HealthRegistry.Clear(_subsystem);
            return true;
        }
    }
}

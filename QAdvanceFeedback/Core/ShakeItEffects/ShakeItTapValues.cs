using System;

namespace QAdvanceFeedback.Core.ShakeItEffects
{
    /// <summary>
    /// One ShakeIt frame of SimHub's own built-in wheel lock and slip values, 0..100 per wheel, as
    /// computed by hidden WheelsLock/WheelsSlip containers inside a native QAdvanceFeedback effect.
    /// </summary>
    public sealed class ShakeItTapValues
    {
        public ShakeItTapValues(Corners lockWheels, Corners slipWheels)
            : this(lockWheels, slipWheels, null, null, null)
        {
        }

        /// <param name="lockSupported">ShakeIt's own Wheels Lock effect reports the game supported
        /// (<c>GetIsSupportedOnCurrentGame(caps)</c>); null when the capabilities were not known.</param>
        /// <param name="slipSupported">Same, for Wheels Slip (the two differ: direct-mode lock vs slip).</param>
        /// <param name="gameId">The game the values (and support flags) were computed for.</param>
        public ShakeItTapValues(Corners lockWheels, Corners slipWheels, bool? lockSupported, bool? slipSupported, string gameId)
            : this(lockWheels, slipWheels, lockSupported, slipSupported, gameId, null)
        {
        }

        /// <param name="algorithm">The applied <see cref="ShakeItAlgorithmSnapshot"/> the hidden containers
        /// computed these values under (null when unknown) - see
        /// <see cref="ShakeItSourceResolution.TapComputedUnder"/>.</param>
        public ShakeItTapValues(Corners lockWheels, Corners slipWheels, bool? lockSupported, bool? slipSupported, string gameId,
            ShakeItAlgorithmSnapshot algorithm)
        {
            Lock = Clamp(lockWheels);
            Slip = Clamp(slipWheels);
            LockSupported = lockSupported;
            SlipSupported = slipSupported;
            GameId = gameId;
            Algorithm = algorithm;
        }

        /// <summary>The algorithm snapshot these values were computed under (null when unknown).</summary>
        public ShakeItAlgorithmSnapshot Algorithm { get; }

        public Corners Lock { get; }
        public Corners Slip { get; }
        public bool? LockSupported { get; }
        public bool? SlipSupported { get; }
        public string GameId { get; }

        /// <summary>The channel's support flag when <paramref name="snapshot"/> was computed for
        /// <paramref name="gameId"/> (any age: support does not go stale within a game); else null.</summary>
        public static bool? SupportFor(FrameSnapshot<ShakeItTapValues> snapshot, string gameId, bool isLock)
        {
            ShakeItTapValues v = snapshot?.Value;
            if (v == null || !string.Equals(v.GameId ?? string.Empty, gameId ?? string.Empty, StringComparison.Ordinal)) return null;
            return isLock ? v.LockSupported : v.SlipSupported;
        }

        /// <summary>The wheel's value when <paramref name="snapshot"/> is fresh; false when there is no
        /// tap or it is older than <see cref="FrameSnapshot.StaleTicks"/> (the caller falls back).</summary>
        public static bool TryGet(FrameSnapshot<ShakeItTapValues> snapshot, long nowTicks, bool isLock, int wheelIndex, out double value)
        {
            value = 0;
            if (snapshot?.Value == null || !snapshot.IsFresh(nowTicks, FrameSnapshot.StaleTicks)) return false;
            value = (isLock ? snapshot.Value.Lock : snapshot.Value.Slip)[wheelIndex];
            return true;
        }

        private static Corners Clamp(Corners c) =>
            new Corners(Clamp(c.FrontLeft), Clamp(c.FrontRight), Clamp(c.RearLeft), Clamp(c.RearRight));

        private static double Clamp(double v)
        {
            if (double.IsNaN(v) || v < 0) return 0;
            return v > 100 ? 100 : v;
        }
    }

    /// <summary>
    /// Lets the hidden engines run once per ShakeIt frame however many QAdvanceFeedback effects (or
    /// ShakeIt profiles - Motors and Bass Shakers) evaluate in it. Not thread-safe by itself; the tap
    /// locks around it.
    /// </summary>
    public sealed class TapDeduper
    {
        private DateTime _last = DateTime.MinValue;
        private bool _hasLast;

        public bool ShouldRun(DateTime frameTime)
        {
            if (_hasLast && frameTime == _last) return false;
            _last = frameTime;
            _hasLast = true;
            return true;
        }
    }
}

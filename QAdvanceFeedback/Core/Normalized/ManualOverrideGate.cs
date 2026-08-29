using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// One channel's manually-configured key data points, as plain numbers.
    /// <para/>
    /// Deliberately a Core-level value type carrying nothing but doubles: the Settings layer owns the
    /// persisted model, the per-game selection and the validation, and hands the RESOLVED triple down.
    /// Core keeps no upward dependency on Settings - the same rule <see cref="SourceIdentity"/> follows.
    /// </summary>
    public struct ManualAnchors
    {
        /// <summary>False means "Auto" - the learned values are published and this struct is ignored.</summary>
        public bool Active;

        public double SMax;
        public double S90;
        public double S75;

        public static ManualAnchors None => default(ManualAnchors);

        public static ManualAnchors Of(double sMax, double s90, double s75)
            => new ManualAnchors { Active = true, SMax = sMax, S90 = s90, S75 = s75 };
    }

    /// <summary>
    /// Decides WHEN a manually-configured key data point may take over from the learned one.
    /// <para/>
    /// The rule, from the owner: a manual value is not applied until the channel has both finished its
    /// cold start AND accumulated <see cref="MinimumInGameSeconds"/> of real driving - "whichever is
    /// longer". Until then the learned value is published, because a manual number configured against
    /// one car/source is not necessarily meaningful the instant a different session starts, and the
    /// driver has had no chance to see what this session is actually reading.
    /// <para/>
    /// IN-GAME TIME ONLY. Wall-clock time is not the measure - a paused game, a menu, or a car sitting
    /// in the pits accumulates nothing. The caller passes <c>advancing</c> for frames where the car is
    /// genuinely moving, and only those frames add to the total. Without this, alt-tabbing away for a
    /// minute would silently satisfy the gate.
    /// <para/>
    /// Keyed per (channel, game, car, source) exactly like the learners themselves, so switching any of
    /// those starts the gate again rather than inheriting another context's readiness.
    /// </summary>
    public sealed class ManualOverrideGate
    {
        /// <summary>Driving time a context must accumulate before a manual value is applied. Paired
        /// with, not instead of, cold-start completion - both must be satisfied.</summary>
        public const double MinimumInGameSeconds = 30.0;

        /// <summary>Hand-over confidence at which cold start counts as finished. Matches the threshold
        /// the cold-start replay harness reports against.</summary>
        public const double ColdStartDoneConfidence = 0.95;

        /// <summary>A single frame longer than this is a stall, a breakpoint or a resumed pause, not
        /// driving - it is clamped rather than credited, so one long frame cannot satisfy the gate.</summary>
        private const double MaxCreditedFrameSeconds = 0.5;

        private readonly Dictionary<string, double> _elapsed = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>Accumulate driving time for this context. <paramref name="advancing"/> must be false
        /// whenever the game is paused or the car is not moving.</summary>
        public void Observe(string key, double dtSeconds, bool advancing)
        {
            if (string.IsNullOrEmpty(key) || !advancing) return;
            if (!ClampMath.IsFinite(dtSeconds) || dtSeconds <= 0.0) return;
            if (dtSeconds > MaxCreditedFrameSeconds) dtSeconds = MaxCreditedFrameSeconds;

            _elapsed.TryGetValue(key, out double total);
            // Stop accumulating once the bar is cleared - this counter is a gate, not a statistic, and
            // letting it grow forever would be an unbounded double over a multi-year session.
            if (total >= MinimumInGameSeconds) return;
            _elapsed[key] = total + dtSeconds;
        }

        /// <summary>Driving time accumulated for this context, in seconds.</summary>
        public double ElapsedSeconds(string key)
            => !string.IsNullOrEmpty(key) && _elapsed.TryGetValue(key, out double v) ? v : 0.0;

        /// <summary>Whether a manual value may now be applied for this context: BOTH the driving-time
        /// bar and cold-start completion, per the owner's "whichever is longer".</summary>
        public bool IsReady(string key, double handoverConfidence)
            => ElapsedSeconds(key) >= MinimumInGameSeconds
            && handoverConfidence >= ColdStartDoneConfidence;

        /// <summary>Forget everything - used when a fresh cold start is deliberately requested.</summary>
        public void Reset() => _elapsed.Clear();
    }
}

using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.SlipAngle;

namespace QAdvanceFeedback.Settings
{
    /// <summary>One stored Slip Angle window (degrees) for one slot, plus its one-time seed latch.</summary>
    public sealed class SlipAngleWindowEntry
    {
        public double Min { get; set; }
        public double Best { get; set; }
        public double Max { get; set; }
        public bool Seeded { get; set; }
    }

    /// <summary>
    /// Slip Angle key data (docs\slip-angle-design.md 9.4): ONE Min/Best/Max triple on Raw.All, stored in
    /// slots keyed (mode, game, source) exactly like <see cref="KeyDataPointSettings"/> (same
    /// <see cref="KeyDataPointSettings.MakeSlotKey"/>). Learning never stops: <see cref="AutoGenerate"/>
    /// only decides whether the learned or the manual window is applied, and a manual window is applied
    /// only once <see cref="ManualOverrideGate"/> is ready (see <see cref="Resolve"/>). Auto is read-only:
    /// <see cref="TryCommitManual"/> refuses. Shipped default is <see cref="SlipAngleWindow.ShippedDefault"/>.
    /// </summary>
    public sealed class SlipAngleKeyDataSettings : ISlipAngleKeyData
    {
        public const double MinValueDeg = 0.5;
        public const double MaxValueDeg = 45.0;
        public const double StepDeg = 0.1;

        public bool AutoGenerate { get; set; } = true;
        public bool PerGame { get; set; }

        /// <summary>Auto/Manual and Global/Per-Game PER SOURCE - same model and fallback/migration
        /// semantics as <see cref="KeyDataPointSettings.SourceFlags"/>. A source absent here inherits the
        /// global <see cref="AutoGenerate"/>/<see cref="PerGame"/> pair.</summary>
        public Dictionary<string, KeyDataSourceFlags> SourceFlags { get; set; }
            = new Dictionary<string, KeyDataSourceFlags>(StringComparer.OrdinalIgnoreCase);

        public bool GetAutoGenerate(string sourceIdentity)
        {
            KeyDataSourceFlags f = FindFlags(sourceIdentity);
            return f != null ? f.AutoGenerate : AutoGenerate;
        }

        public bool GetPerGame(string sourceIdentity)
        {
            KeyDataSourceFlags f = FindFlags(sourceIdentity);
            return f != null ? f.PerGame : PerGame;
        }

        public void SetAutoGenerate(string sourceIdentity, bool value) => EnsureFlags(sourceIdentity).AutoGenerate = value;

        public void SetPerGame(string sourceIdentity, bool value) => EnsureFlags(sourceIdentity).PerGame = value;

        private KeyDataSourceFlags FindFlags(string sourceIdentity)
        {
            if (SourceFlags == null || string.IsNullOrEmpty(sourceIdentity)) return null;
            return SourceFlags.TryGetValue(sourceIdentity, out KeyDataSourceFlags f) ? f : null;
        }

        private KeyDataSourceFlags EnsureFlags(string sourceIdentity)
        {
            if (SourceFlags == null)
                SourceFlags = new Dictionary<string, KeyDataSourceFlags>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(sourceIdentity))
                return new KeyDataSourceFlags { AutoGenerate = AutoGenerate, PerGame = PerGame };
            if (!SourceFlags.TryGetValue(sourceIdentity, out KeyDataSourceFlags f) || f == null)
                SourceFlags[sourceIdentity] = f = new KeyDataSourceFlags { AutoGenerate = AutoGenerate, PerGame = PerGame };
            return f;
        }

        public Dictionary<string, SlipAngleWindowEntry> Values { get; set; }
            = new Dictionary<string, SlipAngleWindowEntry>(StringComparer.OrdinalIgnoreCase);

        private string Slot(string gameId, string sourceIdentity)
            => KeyDataPointSettings.MakeSlotKey(GetPerGame(sourceIdentity), gameId, sourceIdentity);

        /// <summary>Whether a window is committable: finite, 0.5-45 each, 0 &lt; Min &lt; Best &lt; Max, gaps &gt;= 0.2.</summary>
        public static bool IsValid(double min, double best, double max)
            => ClampMath.IsFinite(min) && ClampMath.IsFinite(best) && ClampMath.IsFinite(max)
            && min >= MinValueDeg && max <= MaxValueDeg
            && min < best && best < max
            && new SlipAngleWindow(min, best, max).IsValid;

        private static double Round(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);

        public bool TryGetSlot(string gameId, string sourceIdentity, out SlipAngleWindow window)
        {
            window = default(SlipAngleWindow);
            if (Values == null) return false;
            if (!Values.TryGetValue(Slot(gameId, sourceIdentity), out SlipAngleWindowEntry e) || e == null) return false;
            if (!IsValid(e.Min, e.Best, e.Max)) return false;
            // A slot saved before the learned-Max cap reads back with its whole window scaled under it.
            window = new SlipAngleWindow(e.Min, e.Best, e.Max).CappedAt(SlipAngleWindowLearner.MaxCapDegFor(sourceIdentity));
            return true;
        }

        /// <summary>Commit a driver edit (rounded to 0.1). Max is clamped to the source's learned-Max cap
        /// (<see cref="SlipAngleWindowLearner.MaxCapDegFor"/>: 20 degrees native, 30 car-angle - the same
        /// hard limit the learner obeys). Refused in Auto, for per-game with no game, or when invalid
        /// (including a Best that no longer sits a full gap below the clamped Max).</summary>
        public bool TryCommitManual(string gameId, string sourceIdentity, double min, double best, double max)
        {
            if (GetAutoGenerate(sourceIdentity)) return false;
            if (GetPerGame(sourceIdentity) && string.IsNullOrEmpty(gameId)) return false;
            double cap = SlipAngleWindowLearner.MaxCapDegFor(sourceIdentity);
            // Only a Max still within the 45-degree bound is clamped; beyond it is rejected as before.
            double a = Round(min), b = Round(best), c = Round(max > cap && max <= MaxValueDeg ? cap : max);
            if (!IsValid(a, b, c)) return false;
            Write(gameId, sourceIdentity, a, b, c);
            return true;
        }

        /// <summary>One-time seed of a slot from the learned window; false if already seeded, invalid, or
        /// per-game with no game.</summary>
        public bool TrySeed(string gameId, string sourceIdentity, SlipAngleWindow learned)
        {
            if (GetPerGame(sourceIdentity) && string.IsNullOrEmpty(gameId)) return false;
            if (IsSeeded(gameId, sourceIdentity)) return false;
            double a = Round(learned.Min), b = Round(learned.Best), c = Round(learned.Max);
            if (!IsValid(a, b, c)) return false;
            Write(gameId, sourceIdentity, a, b, c);
            return true;
        }

        private void Write(string gameId, string sourceIdentity, double a, double b, double c)
        {
            if (Values == null)
                Values = new Dictionary<string, SlipAngleWindowEntry>(StringComparer.OrdinalIgnoreCase);
            string slot = Slot(gameId, sourceIdentity);
            if (!Values.TryGetValue(slot, out SlipAngleWindowEntry e) || e == null)
                Values[slot] = e = new SlipAngleWindowEntry();
            e.Min = a; e.Best = b; e.Max = c; e.Seeded = true;
        }

        /// <summary>Forget the slot, values and seeded latch (re-arms the seed).</summary>
        public void ClearSlot(string gameId, string sourceIdentity)
        {
            if (Values == null) return;
            Values.Remove(Slot(gameId, sourceIdentity));
        }

        /// <summary>The shipped Auto/Manual and Global/Per-Game choices (same as <see cref="KeyDataPointSettings"/>).</summary>
        public const bool DefaultAutoGenerate = KeyDataPointSettings.DefaultAutoGenerate;
        public const bool DefaultPerGame = KeyDataPointSettings.DefaultPerGame;

        /// <summary>
        /// Whether this source's whole key data configuration - Auto/Manual, Global/Per-Game and every
        /// stored window - is still what ships (mirrors <see cref="KeyDataPointSettings.MatchesDefaults"/>):
        /// drives whether the "Reset to default" button shows. A stored slot holding exactly the shipped
        /// window (<see cref="SlipAngleWindow.ShippedDefault"/>) counts as default.
        /// </summary>
        public bool IsDefault(string sourceIdentity)
        {
            if (GetAutoGenerate(sourceIdentity) != DefaultAutoGenerate) return false;
            if (GetPerGame(sourceIdentity) != DefaultPerGame) return false;
            if (Values == null) return true;
            SlipAngleWindow d = SlipAngleWindow.ShippedDefault;
            string suffix = SlotSuffixFor(sourceIdentity);
            foreach (KeyValuePair<string, SlipAngleWindowEntry> p in Values)
            {
                if (p.Value == null || !p.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                if (Math.Abs(p.Value.Min - d.Min) > 1e-6 || Math.Abs(p.Value.Best - d.Best) > 1e-6
                    || Math.Abs(p.Value.Max - d.Max) > 1e-6) return false;
            }
            return true;
        }

        /// <summary>Flags AND windows back to shipped, for this source only (every game's slot of it),
        /// mirroring <see cref="KeyDataPointSettings.RestoreSourceDefaults"/>. The seed re-arms.</summary>
        public void ResetSource(string sourceIdentity)
        {
            if (string.IsNullOrEmpty(sourceIdentity)) return;
            SetAutoGenerate(sourceIdentity, DefaultAutoGenerate);
            SetPerGame(sourceIdentity, DefaultPerGame);
            if (Values == null) return;
            string suffix = SlotSuffixFor(sourceIdentity);
            var remove = new List<string>();
            foreach (string key in Values.Keys)
                if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) remove.Add(key);
            foreach (string key in remove) Values.Remove(key);
        }

        /// <summary>Both slot forms of <see cref="KeyDataPointSettings.MakeSlotKey"/> end in "|src:&lt;identity&gt;".</summary>
        private static string SlotSuffixFor(string sourceIdentity) => KeyDataPointSettings.SlotSuffixFor(sourceIdentity);   // the same stored form as MakeSlotKey (v21)

        public bool IsSeeded(string gameId, string sourceIdentity)
            => Values != null
            && Values.TryGetValue(Slot(gameId, sourceIdentity), out SlipAngleWindowEntry e)
            && e != null && e.Seeded;

        /// <summary>
        /// The window to apply: Auto gives the learned one; Manual gives the slot only when
        /// <paramref name="gate"/> is ready for <paramref name="gateKey"/> at <paramref name="maturity"/>
        /// (the learner's Maturity), else the learned one.
        /// </summary>
        public SlipAngleWindow Resolve(string gameId, string sourceIdentity, SlipAngleWindow learned,
            ManualOverrideGate gate, string gateKey, double maturity)
        {
            if (GetAutoGenerate(sourceIdentity) || gate == null || !gate.IsReady(gateKey, maturity)) return learned;
            return TryGetSlot(gameId, sourceIdentity, out SlipAngleWindow manual) ? manual : learned;
        }

        public SlipAngleKeyDataSettings Clone()
        {
            var copy = new SlipAngleKeyDataSettings { AutoGenerate = AutoGenerate, PerGame = PerGame };
            if (SourceFlags != null)
                foreach (KeyValuePair<string, KeyDataSourceFlags> p in SourceFlags)
                    if (p.Value != null)
                        copy.SourceFlags[p.Key] = new KeyDataSourceFlags { AutoGenerate = p.Value.AutoGenerate, PerGame = p.Value.PerGame };
            if (Values != null)
                foreach (KeyValuePair<string, SlipAngleWindowEntry> p in Values)
                    if (p.Value != null)
                        copy.Values[p.Key] = new SlipAngleWindowEntry
                        { Min = p.Value.Min, Best = p.Value.Best, Max = p.Value.Max, Seeded = p.Value.Seeded };
            return copy;
        }
    }
}

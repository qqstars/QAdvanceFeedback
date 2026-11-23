using System;
using System.Collections.Generic;
using System.Linq;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>One game in a ratio list: the game id, and whether its values were seen above full slip
    /// (100%): true = they go over, false = they stop at 100%, null = not known yet (treated as "go over").</summary>
    public sealed class SlipAngleRatioGame
    {
        public string Game { get; set; }
        public bool? ExceedsFullScale { get; set; }
    }

    /// <summary>
    /// The per-game unit lists for native slip-angle fields whose unit the table does not state (owner,
    /// 2026-11-20), persisted in the plugin config (SlipAngle.SourceUnits) so they can be read and edited:
    /// <list type="bullet">
    /// <item><see cref="ByAngle"/>: the value is an angle in degrees.</item>
    /// <item><see cref="AngleUsingRatio1"/>: a 0..1 grip ratio (x 100, then 20%).</item>
    /// <item><see cref="AngleUsingRatio100"/>: a 0..100 grip ratio (20%).</item>
    /// <item><see cref="AngleUsingRadians"/>: radians, checked (owner, 2026-11-21) - a game the body-angle check
    /// found to be radians, or one the user put here. Detection then leaves it alone.</item>
    /// <item><see cref="UseCarAngle"/>: ignore the game's tyre field and use the car-angle estimate (a driver's
    /// choice from the unit dropdown, or added by hand).</item>
    /// <item>Any other game: read as radians (x 57.3) while the body-angle check runs on it.</item>
    /// </list>
    /// Every list lives in the plugin config file, so a user can add a game to any of them without a new
    /// plugin version.
    /// A list missing from the file is seeded from the hard-coded defaults (<see cref="Normalise"/>), and the
    /// plugin then writes it. <see cref="SlipAngleUnitClassifier"/> adds games it detects. A listed game is read as
    /// its list says even when the table states a unit (the lists are authoritative); the unit dropdown never
    /// offers a game whose unit is hard-coded, so that only happens by a hand edit. Thread-safe.
    /// </summary>
    public sealed class SlipAngleSourceUnits
    {
        /// <summary>Known degrees fields: KartKraft (official schema: "slipAngle ... Degrees").</summary>
        public static readonly IReadOnlyList<string> DefaultByAngle = new[] { "KartKraft" };

        /// <summary>Known 0..1 ratio fields: Forza's normalized "TireSlipAngle" (official Data Out docs:
        /// "0 means 100% grip and |angle| &gt; 1.0 means loss of grip") - it goes over 1.</summary>
        public static readonly IReadOnlyList<string> DefaultRatio1 = new[] { "FM7", "FM8", "FH4", "FH5", "FH6" };

        private readonly object _gate = new object();

        public List<string> ByAngle { get; set; }
        public List<SlipAngleRatioGame> AngleUsingRatio1 { get; set; }
        public List<SlipAngleRatioGame> AngleUsingRatio100 { get; set; }
        public List<string> AngleUsingRadians { get; set; }
        public List<string> UseCarAngle { get; set; }

        /// <summary>The games whose list entry the DRIVER chose from the unit dropdown (owner, 2026-11-22) - not found
        /// by the check. Only these show their unit as picked (with Restore default); a game the check placed shows
        /// Auto-detect. A game in <see cref="UseCarAngle"/> always counts as chosen (the check never picks it).</summary>
        public List<string> ChosenByUser { get; set; }

        /// <summary>True for a game whose unit ships hard-coded in these lists (KartKraft, the five Forza games) -
        /// known for certain, so the unit dropdown never offers to change it.</summary>
        public static bool IsShippedDefault(string gameId) =>
            !string.IsNullOrEmpty(gameId)
            && (DefaultByAngle.Any(g => Same(g, gameId)) || DefaultRatio1.Any(g => Same(g, gameId)));

        public static SlipAngleSourceUnits CreateDefault()
        {
            var u = new SlipAngleSourceUnits();
            u.Normalise();
            return u;
        }

        /// <summary>Seeds every missing list from the defaults and drops blank entries. True when anything was
        /// seeded (the caller saves the config so the file shows the lists).</summary>
        public bool Normalise()
        {
            lock (_gate)
            {
                bool seeded = false;
                if (ByAngle == null) { ByAngle = DefaultByAngle.ToList(); seeded = true; }
                if (AngleUsingRatio1 == null)
                {
                    AngleUsingRatio1 = DefaultRatio1.Select(g => new SlipAngleRatioGame { Game = g, ExceedsFullScale = true }).ToList();
                    seeded = true;
                }
                if (AngleUsingRatio100 == null) { AngleUsingRatio100 = new List<SlipAngleRatioGame>(); seeded = true; }
                if (AngleUsingRadians == null) { AngleUsingRadians = new List<string>(); seeded = true; }
                if (UseCarAngle == null) { UseCarAngle = new List<string>(); seeded = true; }
                if (ChosenByUser == null) { ChosenByUser = new List<string>(); seeded = true; }
                ChosenByUser.RemoveAll(string.IsNullOrWhiteSpace);
                ByAngle.RemoveAll(string.IsNullOrWhiteSpace);
                AngleUsingRadians.RemoveAll(string.IsNullOrWhiteSpace);
                UseCarAngle.RemoveAll(string.IsNullOrWhiteSpace);
                AngleUsingRatio1.RemoveAll(e => e == null || string.IsNullOrWhiteSpace(e.Game));
                AngleUsingRatio100.RemoveAll(e => e == null || string.IsNullOrWhiteSpace(e.Game));
                return seeded;
            }
        }

        /// <summary>The unit for <paramref name="gameId"/>. THE LISTS ARE AUTHORITATIVE (owner, 2026-11-21): a game
        /// in a list is read as that list says, whatever the table states; else the table's own unit when it
        /// states one; else <see cref="SlipAngleUnit.Radians"/> (read as radians while it is checked). A missing
        /// list reads as its defaults. (A game in <see cref="UseCarAngle"/> has no tyre field read at all.)</summary>
        public SlipAngleUnit Resolve(string gameId, SlipAngleUnit tableUnit)
        {
            if (!string.IsNullOrEmpty(gameId))
            {
                lock (_gate)
                {
                    if (Contains(ByAngle, DefaultByAngle, gameId)) return SlipAngleUnit.Degrees;
                    if (Find(AngleUsingRatio1, DefaultRatio1, gameId, out _)) return SlipAngleUnit.Ratio1;
                    if (Find(AngleUsingRatio100, null, gameId, out _)) return SlipAngleUnit.Ratio100;
                    if (Contains(AngleUsingRadians, null, gameId)) return SlipAngleUnit.Radians;
                }
            }
            return tableUnit != SlipAngleUnit.Unverified ? tableUnit : SlipAngleUnit.Radians;
        }

        /// <summary>True when the game is in any list (its unit is settled; detection leaves it alone).</summary>
        public bool IsListed(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return false;
            lock (_gate)
                return Contains(ByAngle, DefaultByAngle, gameId) || Find(AngleUsingRatio1, DefaultRatio1, gameId, out _)
                    || Find(AngleUsingRatio100, null, gameId, out _) || Contains(AngleUsingRadians, null, gameId)
                    || Contains(UseCarAngle, null, gameId);
        }

        /// <summary>True when the game is in <see cref="UseCarAngle"/>: its tyre field is ignored and the car-angle
        /// estimate is used.</summary>
        public bool ForcesCarAngle(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return false;
            lock (_gate) return Contains(UseCarAngle, null, gameId);
        }

        /// <summary>The dropdown's current choice for a listed game; null when the game is in no list.</summary>
        public SlipAngleUnitChoice? Choice(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return null;
            lock (_gate)
            {
                if (Contains(UseCarAngle, null, gameId)) return SlipAngleUnitChoice.CarAngle;
                if (Contains(ByAngle, DefaultByAngle, gameId)) return SlipAngleUnitChoice.Degrees;
                if (Find(AngleUsingRatio1, DefaultRatio1, gameId, out _)) return SlipAngleUnitChoice.Ratio1;
                if (Find(AngleUsingRatio100, null, gameId, out _)) return SlipAngleUnitChoice.Ratio100;
                if (Contains(AngleUsingRadians, null, gameId)) return SlipAngleUnitChoice.Radians;
                return null;
            }
        }

        /// <summary>Puts the game in the list for <paramref name="choice"/> (out of every other); null takes it out
        /// of every list, so the body-angle check runs on it again. Does not touch <see cref="ChosenByUser"/> - the
        /// dropdown goes through <see cref="Choose"/> / <see cref="RestoreDefault"/>. True when a list changed.</summary>
        public bool SetChoice(string gameId, SlipAngleUnitChoice? choice)
        {
            if (string.IsNullOrEmpty(gameId)) return false;
            lock (_gate)
            {
                Normalise();
                if (choice == SlipAngleUnitChoice.CarAngle)
                {
                    bool changed = Assign(gameId, SlipAngleUnit.Unverified);
                    if (!UseCarAngle.Any(g => Same(g, gameId))) { UseCarAngle.Add(gameId); changed = true; }
                    return changed;
                }
                SlipAngleUnit unit = choice == SlipAngleUnitChoice.Degrees ? SlipAngleUnit.Degrees
                    : choice == SlipAngleUnitChoice.Radians ? SlipAngleUnit.Radians
                    : choice == SlipAngleUnitChoice.Ratio1 ? SlipAngleUnit.Ratio1
                    : choice == SlipAngleUnitChoice.Ratio100 ? SlipAngleUnit.Ratio100
                    : SlipAngleUnit.Unverified;   // null or AutoDetect: out of every list
                return Assign(gameId, unit);
            }
        }

        /// <summary>True when the driver chose this game's unit (<see cref="ChosenByUser"/>, or the car angle).</summary>
        public bool IsChosenByUser(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return false;
            lock (_gate) return Contains(ChosenByUser, null, gameId) || Contains(UseCarAngle, null, gameId);
        }

        /// <summary>A DRIVER's pick from the dropdown (owner, 2026-11-22): the game moves to that list and is marked as
        /// chosen. <see cref="SlipAngleUnitChoice.AutoDetect"/> is <see cref="RestoreDefault"/>. True when anything changed.</summary>
        public bool Choose(string gameId, SlipAngleUnitChoice choice)
        {
            if (string.IsNullOrEmpty(gameId)) return false;
            if (choice == SlipAngleUnitChoice.AutoDetect) return RestoreDefault(gameId);
            lock (_gate)
            {
                bool changed = SetChoice(gameId, choice);
                if (!ChosenByUser.Any(g => Same(g, gameId))) { ChosenByUser.Add(gameId); changed = true; }
                return changed;
            }
        }

        /// <summary>
        /// RESTORE DEFAULT (owner, 2026-11-22): the game leaves every list and the chosen mark; a game whose unit ships
        /// in these lists (<see cref="IsShippedDefault"/>: KartKraft's degrees, Forza's 0-1 ratio that goes over 1) goes
        /// back into its list. Any other game is then checked again while driving (Auto-detect); a game whose unit the
        /// table states is read in that unit. True when anything changed.
        /// </summary>
        public bool RestoreDefault(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return false;
            lock (_gate)
            {
                Normalise();
                bool changed = ChosenByUser.RemoveAll(g => Same(g, gameId)) > 0;
                if (DefaultByAngle.Any(g => Same(g, gameId)))
                    changed |= Assign(gameId, SlipAngleUnit.Degrees);
                else if (DefaultRatio1.Any(g => Same(g, gameId)))
                {
                    changed |= Assign(gameId, SlipAngleUnit.Ratio1);
                    SlipAngleRatioGame e = AngleUsingRatio1.First(x => Same(x.Game, gameId));
                    if (e.ExceedsFullScale == null) { e.ExceedsFullScale = true; changed = true; }
                }
                else changed |= Assign(gameId, SlipAngleUnit.Unverified);
                return changed;
            }
        }

        /// <summary>A ratio game's "over 100%" knowledge; null when unknown or not a ratio game.</summary>
        public bool? ExceedsFullScale(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return null;
            lock (_gate)
            {
                if (Find(AngleUsingRatio1, DefaultRatio1, gameId, out SlipAngleRatioGame e1)) return e1?.ExceedsFullScale ?? true;
                if (Find(AngleUsingRatio100, null, gameId, out SlipAngleRatioGame e100)) return e100?.ExceedsFullScale;
                return null;
            }
        }

        /// <summary>Puts <paramref name="gameId"/> in the list for <paramref name="unit"/> (Degrees, Ratio1,
        /// Ratio100 or Radians), out of the others; Unverified takes it out of every list (checked again). True
        /// when a list changed.</summary>
        public bool Assign(string gameId, SlipAngleUnit unit)
        {
            if (string.IsNullOrEmpty(gameId)) return false;
            lock (_gate)
            {
                Normalise();
                bool changed = false;
                if (unit != SlipAngleUnit.Degrees) changed |= ByAngle.RemoveAll(g => Same(g, gameId)) > 0;
                if (unit != SlipAngleUnit.Ratio1) changed |= AngleUsingRatio1.RemoveAll(e => Same(e.Game, gameId)) > 0;
                if (unit != SlipAngleUnit.Ratio100) changed |= AngleUsingRatio100.RemoveAll(e => Same(e.Game, gameId)) > 0;
                if (unit != SlipAngleUnit.Radians) changed |= AngleUsingRadians.RemoveAll(g => Same(g, gameId)) > 0;
                changed |= UseCarAngle.RemoveAll(g => Same(g, gameId)) > 0;
                switch (unit)
                {
                    case SlipAngleUnit.Degrees:
                        if (!ByAngle.Any(g => Same(g, gameId))) { ByAngle.Add(gameId); changed = true; }
                        break;
                    case SlipAngleUnit.Ratio1:
                        if (!AngleUsingRatio1.Any(e => Same(e.Game, gameId))) { AngleUsingRatio1.Add(new SlipAngleRatioGame { Game = gameId }); changed = true; }
                        break;
                    case SlipAngleUnit.Ratio100:
                        if (!AngleUsingRatio100.Any(e => Same(e.Game, gameId))) { AngleUsingRatio100.Add(new SlipAngleRatioGame { Game = gameId }); changed = true; }
                        break;
                    case SlipAngleUnit.Radians:
                        if (!AngleUsingRadians.Any(g => Same(g, gameId))) { AngleUsingRadians.Add(gameId); changed = true; }
                        break;
                }
                return changed;
            }
        }

        /// <summary>Records whether a ratio game's values go over 100%. True when the entry changed.</summary>
        public bool SetExceedsFullScale(string gameId, bool exceeds)
        {
            if (string.IsNullOrEmpty(gameId)) return false;
            lock (_gate)
            {
                Normalise();
                SlipAngleRatioGame e = AngleUsingRatio1.FirstOrDefault(x => Same(x.Game, gameId))
                    ?? AngleUsingRatio100.FirstOrDefault(x => Same(x.Game, gameId));
                if (e == null || e.ExceedsFullScale == exceeds) return false;
                e.ExceedsFullScale = exceeds;
                return true;
            }
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static bool Contains(List<string> list, IReadOnlyList<string> defaults, string gameId) =>
            (list ?? (IEnumerable<string>)defaults ?? Array.Empty<string>()).Any(g => Same(g, gameId));

        private static bool Find(List<SlipAngleRatioGame> list, IReadOnlyList<string> defaults, string gameId, out SlipAngleRatioGame entry)
        {
            entry = null;
            if (list != null)
            {
                entry = list.FirstOrDefault(e => e != null && Same(e.Game, gameId));
                return entry != null;
            }
            return defaults != null && defaults.Any(g => Same(g, gameId));
        }
    }
}

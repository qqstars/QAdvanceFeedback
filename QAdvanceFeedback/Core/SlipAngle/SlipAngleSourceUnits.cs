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
    /// A list missing from the file starts empty (<see cref="Normalise"/>), and the plugin then writes it - no game
    /// is hard-coded here (owner, 2026-11-24): a known unit is the game's JSON row's <c>unit</c>.
    /// <see cref="SlipAngleUnitClassifier"/> adds games it detects. A unit the game's JSON row states wins over the
    /// lists (owner, 2026-11-24); the lists hold the driver's pick under Auto Detect and what the check found. These
    /// lists are the plugin config's, never the source JSON's. Thread-safe.
    /// </summary>
    public sealed class SlipAngleSourceUnits
    {
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
                if (ByAngle == null) { ByAngle = new List<string>(); seeded = true; }
                if (AngleUsingRatio1 == null) { AngleUsingRatio1 = new List<SlipAngleRatioGame>(); seeded = true; }
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

        /// <summary>The unit for <paramref name="gameId"/>. A UNIT THE SOURCE FILE STATES WINS (owner, 2026-11-24: the
        /// Source Unit is set in QAdvanceFeedback.SlipAngleSource.json; only the car angle - <see cref="UseCarAngle"/>,
        /// checked by the caller - overrides it); else the game's list (the driver's pick under Auto Detect, or what
        /// the check found); else <see cref="SlipAngleUnit.Radians"/> (read as radians while it is checked).</summary>
        public SlipAngleUnit Resolve(string gameId, SlipAngleUnit tableUnit)
        {
            if (tableUnit != SlipAngleUnit.Unverified) return tableUnit;
            if (!string.IsNullOrEmpty(gameId))
            {
                lock (_gate)
                {
                    if (Contains(ByAngle, gameId)) return SlipAngleUnit.Degrees;
                    if (Find(AngleUsingRatio1, gameId, out _)) return SlipAngleUnit.Ratio1;
                    if (Find(AngleUsingRatio100, gameId, out _)) return SlipAngleUnit.Ratio100;
                    if (Contains(AngleUsingRadians, gameId)) return SlipAngleUnit.Radians;
                }
            }
            return tableUnit != SlipAngleUnit.Unverified ? tableUnit : SlipAngleUnit.Radians;
        }

        /// <summary>True when the game is in any list (its unit is settled; detection leaves it alone).</summary>
        public bool IsListed(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return false;
            lock (_gate)
                return Contains(ByAngle, gameId) || Find(AngleUsingRatio1, gameId, out _)
                    || Find(AngleUsingRatio100, gameId, out _) || Contains(AngleUsingRadians, gameId)
                    || Contains(UseCarAngle, gameId);
        }

        /// <summary>True when the game is in <see cref="UseCarAngle"/>: its tyre field is ignored and the car-angle
        /// estimate is used.</summary>
        public bool ForcesCarAngle(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return false;
            lock (_gate) return Contains(UseCarAngle, gameId);
        }

        /// <summary>The dropdown's current choice for a listed game; null when the game is in no list.</summary>
        public SlipAngleUnitChoice? Choice(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return null;
            lock (_gate)
            {
                if (Contains(UseCarAngle, gameId)) return SlipAngleUnitChoice.CarAngle;
                if (Contains(ByAngle, gameId)) return SlipAngleUnitChoice.Degrees;
                if (Find(AngleUsingRatio1, gameId, out _)) return SlipAngleUnitChoice.Ratio1;
                if (Find(AngleUsingRatio100, gameId, out _)) return SlipAngleUnitChoice.Ratio100;
                if (Contains(AngleUsingRadians, gameId)) return SlipAngleUnitChoice.Radians;
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
            lock (_gate) return Contains(ChosenByUser, gameId) || Contains(UseCarAngle, gameId);
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
        /// RESTORE DEFAULT (owner, 2026-11-22): the game leaves every list and the chosen mark. A game whose unit its row
        /// in QAdvanceFeedback.SlipAngleSource.json states is then read in that unit; any other game is checked again while
        /// driving (Auto-detect). No unit is hard-coded here (owner, 2026-11-24: the JSON is the only source definition).
        /// True when anything changed.
        /// </summary>
        public bool RestoreDefault(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return false;
            lock (_gate)
            {
                Normalise();
                bool changed = ChosenByUser.RemoveAll(g => Same(g, gameId)) > 0;
                changed |= Assign(gameId, SlipAngleUnit.Unverified);
                return changed;
            }
        }

        /// <summary>A ratio game's "over 100%" knowledge; null when unknown or not a ratio game.</summary>
        public bool? ExceedsFullScale(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return null;
            lock (_gate)
            {
                if (Find(AngleUsingRatio1, gameId, out SlipAngleRatioGame e1)) return e1?.ExceedsFullScale ?? true;
                if (Find(AngleUsingRatio100, gameId, out SlipAngleRatioGame e100)) return e100?.ExceedsFullScale;
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

        private static bool Contains(List<string> list, string gameId) => list != null && list.Any(g => Same(g, gameId));

        private static bool Find(List<SlipAngleRatioGame> list, string gameId, out SlipAngleRatioGame entry)
        {
            entry = list?.FirstOrDefault(e => e != null && Same(e.Game, gameId));
            return entry != null;
        }
    }
}

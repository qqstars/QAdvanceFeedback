using System;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>How far a Slip Angle parameter may look for another key's learned value (<see cref="SlipAngleBorrowRules"/>).</summary>
    public enum SlipAngleBorrowScope
    {
        /// <summary>Never borrowed: a key with nothing of its own starts from the shipped starting value.</summary>
        Never,
        /// <summary>Same game, same car, same source - only the surface differs (the WRC case: one car, tarmac and gravel).</summary>
        SameGameCarSourceOtherSurface,
        /// <summary>Same game and same source - any car, any surface.</summary>
        SameGameSameSource,
    }

    /// <summary>One key's parts as the borrowing rules compare them. <see cref="Source"/> is the key's source segment
    /// (for the window keys the source identity and the target, so a rear window is never borrowed for a front key).</summary>
    public readonly struct SlipAngleBorrowKey
    {
        public SlipAngleBorrowKey(string game, string car, string source, string surface)
        {
            Game = game ?? string.Empty;
            Car = car ?? string.Empty;
            Source = source ?? string.Empty;
            Surface = surface ?? string.Empty;
        }

        public string Game { get; }
        public string Car { get; }
        public string Source { get; }
        public string Surface { get; }

        /// <summary>Splits a <c>game|#|car|#|source|#|surface</c> key (<see cref="ColdStartTierResolver.Key"/>).</summary>
        public static bool TryParse(string key, out SlipAngleBorrowKey parts)
        {
            parts = default;
            if (!ColdStartTierResolver.TryParseKey(key, out string g, out string c, out string s, out string surf)) return false;
            parts = new SlipAngleBorrowKey(g, c, s, surf);
            return true;
        }
    }

    /// <summary>
    /// THE SLIP ANGLE BORROWING RULES - ONE PLACE (owner, 2026-11-22). Every Slip Angle parameter that a brand-new key
    /// (game, car, source, surface with nothing learned of its own) could take from another key asks here, so a rule is
    /// changed by changing its scope below and nothing else.
    /// <list type="bullet">
    /// <item><see cref="WindowScope"/> - the starting Min / Best / Max window. A slip-RATIO source (0-1 or 0-100: the
    /// ratio means the same on every car of a game) borrows within the same game from any car and surface; an ANGLE
    /// source (degrees, radians, the car angle, a Custom source) only from the same game, car and source on another
    /// surface - in one game different cars, tyres and surfaces differ, so only a surface change is close enough.</item>
    /// <item><see cref="KScope"/> - the front / rear balance k: same game, car and source on another surface only.</item>
    /// <item><see cref="SpeedBandScope"/> - lateral G per speed group: never. Measured on the owner's F1 and FH6 logs,
    /// starting with fully learned groups did not shorten the learning at all (the crossings arrive at the same
    /// moments), and another surface's G levels would block this surface's own groups from learning.</item>
    /// </list>
    /// <para><b>Tyre.</b> No game reader reads the tyre compound, so it is not part of any rule - "same source" is the
    /// key's source identity (Plugin Internal native angle / native ratio / car angle, or a Custom source).</para>
    /// <para>Never borrowed across games. A key's OWN saved learning (a restart) is not borrowing and is always used.</para>
    /// </summary>
    public static class SlipAngleBorrowRules
    {
        /// <summary>The starting window: <see cref="SlipAngleBorrowScope.SameGameSameSource"/> for a slip-ratio source,
        /// else <see cref="SlipAngleBorrowScope.SameGameCarSourceOtherSurface"/>.</summary>
        public static SlipAngleBorrowScope WindowScope(string sourceIdentity) =>
            IsRatioSource(sourceIdentity) ? SlipAngleBorrowScope.SameGameSameSource : SlipAngleBorrowScope.SameGameCarSourceOtherSurface;

        /// <summary>The front / rear balance k.</summary>
        public const SlipAngleBorrowScope KScope = SlipAngleBorrowScope.SameGameCarSourceOtherSurface;

        /// <summary>Lateral G per speed group.</summary>
        public const SlipAngleBorrowScope SpeedBandScope = SlipAngleBorrowScope.Never;

        /// <summary>True for the Plugin Internal native grip-ratio source (0-1 and 0-100 share it: both become the same
        /// angle scale).</summary>
        public static bool IsRatioSource(string sourceIdentity) =>
            string.Equals(sourceIdentity, SlipAngleFrameProcessor.NativeRatioIdentity, StringComparison.Ordinal);

        public static bool SameGame(SlipAngleBorrowKey a, SlipAngleBorrowKey b) => string.Equals(a.Game, b.Game, StringComparison.Ordinal);

        public static bool SameCar(SlipAngleBorrowKey a, SlipAngleBorrowKey b) => string.Equals(a.Car, b.Car, StringComparison.Ordinal);

        public static bool SameSource(SlipAngleBorrowKey a, SlipAngleBorrowKey b) => string.Equals(a.Source, b.Source, StringComparison.Ordinal);

        public static bool SameSurface(SlipAngleBorrowKey a, SlipAngleBorrowKey b) => string.Equals(a.Surface, b.Surface, StringComparison.Ordinal);

        /// <summary>Same game, car and source; a different surface.</summary>
        public static bool OnlySurfaceDiffers(SlipAngleBorrowKey candidate, SlipAngleBorrowKey wanted) =>
            SameGame(candidate, wanted) && SameCar(candidate, wanted) && SameSource(candidate, wanted) && !SameSurface(candidate, wanted);

        /// <summary>Whether <paramref name="candidate"/> may lend to <paramref name="wanted"/> under <paramref name="scope"/>
        /// (never the key itself).</summary>
        public static bool Allows(SlipAngleBorrowScope scope, SlipAngleBorrowKey candidate, SlipAngleBorrowKey wanted)
        {
            switch (scope)
            {
                case SlipAngleBorrowScope.SameGameCarSourceOtherSurface:
                    return OnlySurfaceDiffers(candidate, wanted);
                case SlipAngleBorrowScope.SameGameSameSource:
                    return SameGame(candidate, wanted) && SameSource(candidate, wanted)
                        && !(SameCar(candidate, wanted) && SameSurface(candidate, wanted));
                default:
                    return false;
            }
        }

        /// <summary>As <see cref="Allows(SlipAngleBorrowScope, SlipAngleBorrowKey, SlipAngleBorrowKey)"/> for a stored key;
        /// false when it does not parse.</summary>
        public static bool Allows(SlipAngleBorrowScope scope, string candidateKey, SlipAngleBorrowKey wanted) =>
            SlipAngleBorrowKey.TryParse(candidateKey, out SlipAngleBorrowKey c) && Allows(scope, c, wanted);
    }
}

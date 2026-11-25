using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>Keyed Slip Angle learner: keys, borrowing tiers, surface blend, k, persistence
    /// (docs\slip-angle-design.md 9.3, 9.5 intro, 9.5.6-9).</summary>
    public class KeyedSlipAngleLearnerTests : IDisposable
    {
        private const double Dt = 1.0 / 60.0;
        private const double PeakG = 1.5;
        private const string Game = "RFactor2";   // a game that reports its surface: multi-surface behaviour exists only there (owner, 2026-11-22)
        private const string Car = "Car1";
        private const string Src = "Native";
        private static readonly ImpactVerdict Allowed = new ImpactVerdict(true, false, false, 0.0, 0.0);

        private readonly string _path = Path.Combine(Path.GetTempPath(), "qaf-slipangle-" + Guid.NewGuid() + ".json");

        public void Dispose()
        {
            try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best effort */ }
        }

        // ---- building confirmed learner states --------------------------------------------------------

        private static Func<double, double> Curve(double peakDeg) =>
            a => a <= peakDeg ? PeakG * Math.Sin(Math.PI / 2.0 * a / peakDeg) : Math.Max(0.3, PeakG - 0.15 * (a - peakDeg));

        private static GripCurveBinsState CurveState(Func<double, double> curve, double toDeg = 15.0, int perBin = 40)
        {
            var bins = new GripCurveBins();
            for (int k = 0; k < perBin; k++)
                for (int i = 0; GripCurveBins.BinCenterDeg(i) < toDeg; i++)
                {
                    double a = (i + (k + 0.5) / perBin) * GripCurveBins.BinWidthDeg;
                    bins.Observe(a, curve(a));
                }
            return bins.Export();
        }

        private static readonly Dictionary<double, SlipAngleLearnerState> ConfirmedCache = new Dictionary<double, SlipAngleLearnerState>();

        /// <summary>A confirmed window learner's state whose curve peaks at <paramref name="peakDeg"/>.</summary>
        private static SlipAngleLearnerState Confirmed(double peakDeg)
        {
            lock (ConfirmedCache)
            {
                if (ConfirmedCache.TryGetValue(peakDeg, out SlipAngleLearnerState cached)) return cached;
                Func<double, double> curve = Curve(peakDeg);
                var l = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
                l.Import(new SlipAngleLearnerState { Bins = CurveState(curve) });
                for (int c = 0; c < 3; c++)
                {
                    double start = 0.75 * peakDeg;
                    for (int i = 0; i < 120; i++) l.Observe(start, curve(start), Allowed, true, Dt);
                    for (int i = 0; i <= 33; i++) { double a = start + 0.12 * i; l.Observe(a, curve(a), Allowed, true, Dt); }
                    for (int i = 0; i < 72; i++) l.Observe(1.0, 0.2, Allowed, true, Dt);
                }
                Assert.True(l.PeakConfirmed, $"helper failed to confirm a peak at {peakDeg}");
                SlipAngleLearnerState state = l.Export();
                ConfirmedCache[peakDeg] = state;
                return state;
            }
        }

        /// <summary>The window a confirmed state publishes on its own.</summary>
        private static SlipAngleWindow PublishedOf(SlipAngleLearnerState state)
        {
            var l = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            l.Import(state);
            return l.Published;
        }

        private static string Key(SlipAngleTarget t, SurfaceBucket b, string car = Car, string game = Game) =>
            KeyedSlipAngleLearner.MakeKey(game, car, Src, t, b);

        private static KeyedSlipAngleLearner With(params (string key, SlipAngleLearnerState state)[] entries)
        {
            var d = new Dictionary<string, SlipAngleKeyState>(StringComparer.Ordinal);
            foreach (var (key, state) in entries) d[key] = new SlipAngleKeyState { Learner = state };
            var k = new KeyedSlipAngleLearner();
            k.Import(d);
            return k;
        }

        private static void AssertWindow(SlipAngleWindow expected, SlipAngleWindow actual, int precision = 12)
        {
            Assert.Equal(expected.Min, actual.Min, precision);
            Assert.Equal(expected.Best, actual.Best, precision);
            Assert.Equal(expected.Max, actual.Max, precision);
        }

        /// <summary>A non-learning frame: still advances k and the persistence clock.</summary>
        private static void Idle(KeyedSlipAngleLearner k, SurfaceBlend blend, int frames = 1)
        {
            for (int i = 0; i < frames; i++)
                k.Observe(Game, Car, Src, blend, 1.0, 1.0, 1.0, 0.1, Allowed, false, Dt);
        }

        // ---- keys -------------------------------------------------------------------------------------

        [Fact]
        public void Key_parses_back_to_its_four_resolver_parts()
        {
            string key = KeyedSlipAngleLearner.MakeKey(Game, Car, Src, SlipAngleTarget.Rear, SurfaceBucket.Loose);
            Assert.Equal(ColdStartTierResolver.Key(Game, Car, Src + "/rear", "loose"), key);
            Assert.True(ColdStartTierResolver.TryParseKey(key, out string g, out string c, out string s, out string surf));
            Assert.Equal(Game, g);
            Assert.Equal(Car, c);
            Assert.Equal(Src + "/rear", s);
            Assert.Equal("loose", surf);

            Assert.True(KeyedSlipAngleLearner.TryParseKey(key, out g, out c, out s, out SlipAngleTarget t, out SurfaceBucket b));
            Assert.Equal((Game, Car, Src, SlipAngleTarget.Rear, SurfaceBucket.Loose), (g, c, s, t, b));
        }

        [Fact]
        public void Every_target_and_bucket_key_round_trips()
        {
            foreach (SlipAngleTarget t in Enum.GetValues(typeof(SlipAngleTarget)))
                foreach (SurfaceBucket b in Enum.GetValues(typeof(SurfaceBucket)))
                {
                    string key = KeyedSlipAngleLearner.MakeKey("g", "c", "Raw:x/y", t, b);
                    Assert.True(KeyedSlipAngleLearner.TryParseKey(key, out _, out _, out string s, out SlipAngleTarget t2, out SurfaceBucket b2));
                    Assert.Equal(("Raw:x/y", t, b), (s, t2, b2));
                }
        }

        // ---- borrowing ----------------------------------------------------------------------------------

        [Fact]
        public void Nothing_to_borrow_is_tier1_shipped_default()
        {
            var k = new KeyedSlipAngleLearner();
            SurfaceBlend blend = SurfaceBlend.Only(SurfaceBucket.Sealed);
            Assert.Equal(ColdStartTier.Tier1, k.Tier(Game, Car, Src, blend));
            AssertWindow(SlipAngleWindow.ShippedDefault, k.Window(Game, Car, Src, blend));
            Assert.Equal(0.0, k.Maturity(Game, Car, Src, blend));
        }

        [Fact]
        public void Unseen_bucket_borrows_tier4()
        {
            SlipAngleLearnerState sealedAll = Confirmed(8.0);
            var k = With((Key(SlipAngleTarget.All, SurfaceBucket.Sealed), sealedAll));
            SurfaceBlend loose = SurfaceBlend.Only(SurfaceBucket.Loose);

            Assert.Equal(ColdStartTier.Tier4, k.Tier(Game, Car, Src, loose));
            AssertWindow(PublishedOf(sealedAll), k.Window(Game, Car, Src, loose));
            // ...and its maturity is counted separately (nothing driven on it yet).
            Assert.Equal(0.0, k.Maturity(Game, Car, Src, loose));
            Assert.True(k.Maturity(Game, Car, Src, SurfaceBlend.Only(SurfaceBucket.Sealed)) > 0.0);

            // Driving on it (unconfirmed so far) keeps the borrowed window, and still Tier 4.
            for (int i = 0; i < 300; i++)
                k.Observe(Game, Car, Src, loose, 4.0, 4.0, 4.0, 0.8, Allowed, true, Dt);
            Assert.Equal(ColdStartTier.Tier4, k.Tier(Game, Car, Src, loose));
            AssertWindow(PublishedOf(sealedAll), k.Window(Game, Car, Src, loose));
        }

        [Fact]
        public void Borrowing_returns_the_published_window_from_the_right_key()
        {
            SlipAngleLearnerState front6 = Confirmed(6.0), all8 = Confirmed(8.0), all10 = Confirmed(10.0);
            var k = With(
                (Key(SlipAngleTarget.Front, SurfaceBucket.Sealed), front6),
                (Key(SlipAngleTarget.All, SurfaceBucket.Sealed), all8),
                (Key(SlipAngleTarget.All, SurfaceBucket.Loose, car: "Car2"), all10));

            // Same car, other surface (Tier 4) beats other car (Tier 3); the target must match.
            Assert.Equal(ColdStartTier.Tier4, k.BucketTier(Game, Car, Src, SlipAngleTarget.All, SurfaceBucket.Loose, out string borrowed));
            Assert.Equal(Key(SlipAngleTarget.All, SurfaceBucket.Sealed), borrowed);
            AssertWindow(PublishedOf(all8), k.BucketWindow(Game, Car, Src, SlipAngleTarget.All, SurfaceBucket.Loose));

            Assert.Equal(ColdStartTier.Tier4, k.BucketTier(Game, Car, Src, SlipAngleTarget.Front, SurfaceBucket.Loose, out borrowed));
            Assert.Equal(Key(SlipAngleTarget.Front, SurfaceBucket.Sealed), borrowed);
            AssertWindow(PublishedOf(front6), k.BucketWindow(Game, Car, Src, SlipAngleTarget.Front, SurfaceBucket.Loose));

            // Car2 sealed: its own loose bucket (Tier 4) over Car1's sealed (Tier 3).
            Assert.Equal(ColdStartTier.Tier4, k.BucketTier(Game, "Car2", Src, SlipAngleTarget.All, SurfaceBucket.Sealed, out borrowed));
            Assert.Equal(Key(SlipAngleTarget.All, SurfaceBucket.Loose, car: "Car2"), borrowed);
            AssertWindow(PublishedOf(all10), k.BucketWindow(Game, "Car2", Src, SlipAngleTarget.All, SurfaceBucket.Sealed));

            // A new car with an ANGLE source (owner, 2026-11-22; SlipAngleBorrowRules): never another car's window.
            Assert.Equal(ColdStartTier.Tier1, k.BucketTier(Game, "Car3", Src, SlipAngleTarget.All, SurfaceBucket.Sealed, out borrowed));
            Assert.Null(borrowed);

            // Rear: no rear key anywhere - nothing to borrow (other targets are never borrowed).
            Assert.Equal(ColdStartTier.Tier1, k.BucketTier(Game, Car, Src, SlipAngleTarget.Rear, SurfaceBucket.Sealed, out borrowed));
            Assert.Null(borrowed);
            AssertWindow(SlipAngleWindow.ShippedDefault, k.BucketWindow(Game, Car, Src, SlipAngleTarget.Rear, SurfaceBucket.Sealed));

            // Another source never borrows from this one.
            Assert.Equal(ColdStartTier.Tier1, k.BucketTier(Game, Car, "Raw:other", SlipAngleTarget.All, SurfaceBucket.Loose, out _));
        }

        [Fact]
        public void An_unconfirmed_key_is_not_borrowable()
        {
            var unconfirmed = new SlipAngleLearnerState { Bins = CurveState(Curve(8.0)) };
            var k = With((Key(SlipAngleTarget.All, SurfaceBucket.Sealed), unconfirmed));
            Assert.Equal(ColdStartTier.Tier1, k.Tier(Game, Car, Src, SurfaceBlend.Only(SurfaceBucket.Loose)));
        }

        // ---- the borrowing rules (owner, 2026-11-22; SlipAngleBorrowRules) ---------------------------------

        private const string Ratio = SlipAngleFrameProcessor.NativeRatioIdentity;

        [Fact]
        public void The_rules_slip_ratio_borrows_within_the_game_an_angle_source_only_from_another_surface()
        {
            var wanted = new SlipAngleBorrowKey("G", "C", "src", "sealed");
            Assert.Equal(SlipAngleBorrowScope.SameGameSameSource, SlipAngleBorrowRules.WindowScope(Ratio));
            foreach (string angle in new[] { SlipAngleFrameProcessor.NativeIdentity, SlipAngleFrameProcessor.FallbackIdentity, "custom-id" })
                Assert.Equal(SlipAngleBorrowScope.SameGameCarSourceOtherSurface, SlipAngleBorrowRules.WindowScope(angle));
            Assert.Equal(SlipAngleBorrowScope.SameGameCarSourceOtherSurface, SlipAngleBorrowRules.KScope);
            Assert.Equal(SlipAngleBorrowScope.Never, SlipAngleBorrowRules.SpeedBandScope);

            SlipAngleBorrowScope surface = SlipAngleBorrowScope.SameGameCarSourceOtherSurface, game = SlipAngleBorrowScope.SameGameSameSource;
            Assert.True(SlipAngleBorrowRules.Allows(surface, new SlipAngleBorrowKey("G", "C", "src", "loose"), wanted));
            Assert.False(SlipAngleBorrowRules.Allows(surface, new SlipAngleBorrowKey("G", "C2", "src", "loose"), wanted));   // another car
            Assert.False(SlipAngleBorrowRules.Allows(surface, new SlipAngleBorrowKey("G", "C", "other", "loose"), wanted));  // another source
            Assert.False(SlipAngleBorrowRules.Allows(surface, new SlipAngleBorrowKey("G", "C", "src", "sealed"), wanted));   // itself
            Assert.True(SlipAngleBorrowRules.Allows(game, new SlipAngleBorrowKey("G", "C2", "src", "sealed"), wanted));
            Assert.True(SlipAngleBorrowRules.Allows(game, new SlipAngleBorrowKey("G", "C2", "src", "loose"), wanted));
            Assert.False(SlipAngleBorrowRules.Allows(game, new SlipAngleBorrowKey("G2", "C", "src", "loose"), wanted));      // another game: never
            Assert.False(SlipAngleBorrowRules.Allows(SlipAngleBorrowScope.Never, new SlipAngleBorrowKey("G", "C", "src", "loose"), wanted));
        }

        /// <summary>A lender that learned only through the fast estimate (FH6 never confirms a peak): 30% confident, its
        /// Max 8.5 from a 5/8/10 start (owner, 2026-11-22).</summary>
        private static SlipAngleLearnerState PartlyLearned() => new SlipAngleLearnerState
        {
            PriorMin = 5.0, PriorBest = 8.0, PriorMax = 10.0, Observations = 10,
            EarlyWeight = 3.0, EarlyWeightedAngle = 24.0, EarlyStartMax = 10.0, EarlyCrossings = 3,
            EarlyRecentCount = 1, EarlyRecentLow = 8.5, EarlyRecentHigh = 8.5, EarlyRecentMean = 8.5,
        };

        /// <summary>BORROWED BY CONFIDENCE (owner, 2026-11-22): a lender needs no confirmed peak; the start is the shipped
        /// window moved toward the lender's by the lender's confidence, and the LED shows the borrow.</summary>
        [Fact]
        public void A_partly_learned_lender_lends_in_proportion_to_its_confidence()
        {
            var k = With((KeyedSlipAngleLearner.MakeKey(Game, Car, Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed), PartlyLearned()));
            SlipAngleWindow lent = k.BucketWindow(Game, Car, Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed);
            Assert.Equal(8.5, lent.Max, 6);

            Assert.True(k.TryWindowBorrow(Game, "Car2", Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed, out ColdStartTier tier, out double c));
            Assert.Equal(ColdStartTier.Tier3, tier);
            Assert.Equal(0.3, c, 6);
            SlipAngleWindow start = k.BucketWindow(Game, "Car2", Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed);
            Assert.Equal(10.0 + 0.3 * (lent.Max - 10.0), start.Max, 6);    // 9.55
            Assert.Equal(8.0 + 0.3 * (lent.Best - 8.0), start.Best, 6);
            Assert.Equal(5.0 + 0.3 * (lent.Min - 5.0), start.Min, 6);

            var context = new SlipAngleKeyDataContext();
            context.Observe(Game, "Car2", SourceNativeness.Native, SurfaceBlend.Only(SurfaceBucket.Sealed));
            SlipAngleWindowMaturity? m = SlipAngleKeyDataModel.LearnedWindowMaturity(k, context.Current, Ratio);
            Assert.True(m.Value.WindowBorrowed);                     // the half-and-half LED
            Assert.Equal(0.3, m.Value.Max, 6);                       // right half: the lender's confidence
            Assert.Equal(0.0, m.Value.OwnMax);                       // left half: its own (red)
            Assert.Equal(9.55, SlipAngleKeyDataModel.LearnedWindow(k, context.Current, Ratio).Value.Max, 6);

            // A lender with nothing of its own lends nothing; an angle source still never borrows another car's window.
            Assert.False(k.TryWindowBorrow(Game, "Car2", Src, SlipAngleTarget.All, SurfaceBucket.Sealed, out _, out _));
        }

        [Fact]
        public void The_borrower_follows_its_lender_until_it_learns_something_of_its_own()
        {
            var k = With((KeyedSlipAngleLearner.MakeKey(Game, Car, Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed), PartlyLearned()));
            SurfaceBlend sealedOnly = SurfaceBlend.Only(SurfaceBucket.Sealed);
            // Car2 exists (a non-learning frame created it) but has nothing of its own: it follows the lender.
            k.Observe(Game, "Car2", Ratio, sealedOnly, 1.0, 1.0, 1.0, 0.1, Allowed, false, Dt);
            Assert.Equal(9.55, k.BucketWindow(Game, "Car2", Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed).Max, 6);

            // The lender becomes fully confident: the borrower's start follows it all the way.
            var d = new Dictionary<string, SlipAngleKeyState>(StringComparer.Ordinal)
            {
                [KeyedSlipAngleLearner.MakeKey(Game, Car, Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed)] = new SlipAngleKeyState { Learner = Confirmed(8.0) },
            };
            var confident = new KeyedSlipAngleLearner();
            confident.Import(d);
            confident.Observe(Game, "Car2", Ratio, sealedOnly, 1.0, 1.0, 1.0, 0.1, Allowed, false, Dt);
            AssertWindow(PublishedOf(Confirmed(8.0)), confident.BucketWindow(Game, "Car2", Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed), 6);

            // Once Car2 learns anything of its own it is no longer borrowing.
            for (int i = 0; i < 120; i++) confident.Observe(Game, "Car2", Ratio, sealedOnly, 6.0, 6.0, 6.0, 1.5, Allowed, true, Dt);
            Assert.False(confident.TryWindowBorrow(Game, "Car2", Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed, out _, out _));
        }

        [Fact]
        public void A_slip_ratio_window_is_borrowed_from_another_car_of_the_same_game_never_another_game()
        {
            var k = With((KeyedSlipAngleLearner.MakeKey(Game, "Car2", Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed), Confirmed(8.0)),
                         (KeyedSlipAngleLearner.MakeKey("OtherGame", Car, Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed), Confirmed(10.0)));
            Assert.Equal(ColdStartTier.Tier3, k.BucketTier(Game, Car, Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed, out string borrowed));
            Assert.Equal(KeyedSlipAngleLearner.MakeKey(Game, "Car2", Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed), borrowed);
            Assert.True(k.TryWindowBorrow(Game, Car, Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed, out ColdStartTier tier, out double lender));
            Assert.Equal(ColdStartTier.Tier3, tier);
            Assert.Equal(1.0, lender);

            // Another game with nothing of this game's: the shipped start.
            Assert.Equal(ColdStartTier.Tier1, k.BucketTier("ThirdGame", Car, Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed, out borrowed));
            AssertWindow(SlipAngleWindow.ShippedDefault, k.BucketWindow("ThirdGame", Car, Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed));
        }

        [Fact]
        public void An_angle_window_is_borrowed_only_from_the_same_car_and_source_on_another_surface()
        {
            var k = With((Key(SlipAngleTarget.All, SurfaceBucket.Sealed), Confirmed(8.0)),
                         (Key(SlipAngleTarget.All, SurfaceBucket.Sealed, car: "Car2"), Confirmed(10.0)),
                         (Key(SlipAngleTarget.All, SurfaceBucket.Sealed, game: "OtherGame"), Confirmed(6.0)));
            Assert.Equal(ColdStartTier.Tier4, k.BucketTier(Game, Car, Src, SlipAngleTarget.All, SurfaceBucket.Loose, out string borrowed));
            Assert.Equal(Key(SlipAngleTarget.All, SurfaceBucket.Sealed), borrowed);
            Assert.Equal(ColdStartTier.Tier1, k.BucketTier(Game, "Car3", Src, SlipAngleTarget.All, SurfaceBucket.Sealed, out _));   // another car
            Assert.Equal(ColdStartTier.Tier1, k.BucketTier("ThirdGame", Car, Src, SlipAngleTarget.All, SurfaceBucket.Loose, out _)); // another game
            Assert.False(k.TryWindowBorrow(Game, "Car3", Src, SlipAngleTarget.All, SurfaceBucket.Sealed, out _, out _));
        }

        [Fact]
        public void A_borrowed_window_stops_counting_as_borrowed_once_the_key_learns_anything_of_its_own()
        {
            var k = With((Key(SlipAngleTarget.All, SurfaceBucket.Sealed), Confirmed(8.0)));
            SurfaceBlend loose = SurfaceBlend.Only(SurfaceBucket.Loose);
            Assert.True(k.TryWindowBorrow(Game, Car, Src, SlipAngleTarget.All, SurfaceBucket.Loose, out _, out _));
            Assert.True(k.HasWindowOrBorrow(Game, Car, Src, SlipAngleTarget.All, SurfaceBucket.Loose));
            for (int i = 0; i < 120; i++) k.Observe(Game, Car, Src, loose, 6.0, 6.0, 6.0, 1.5, Allowed, true, Dt);
            Assert.False(k.TryWindowBorrow(Game, Car, Src, SlipAngleTarget.All, SurfaceBucket.Loose, out _, out _));
        }

        [Fact]
        public void An_older_file_with_another_cars_window_as_its_prior_starts_again_under_the_rules()
        {
            // An unconfirmed key saved before the rules, its prior borrowed from Car2 (Tier 3): re-resolved to the shipped start.
            var state = new SlipAngleLearnerState { PriorMin = 6.0, PriorBest = 9.0, PriorMax = 12.0 };
            var d = new Dictionary<string, SlipAngleKeyState>(StringComparer.Ordinal)
            {
                [Key(SlipAngleTarget.All, SurfaceBucket.Sealed, car: "Car2")] = new SlipAngleKeyState { Learner = Confirmed(10.0) },
                [Key(SlipAngleTarget.All, SurfaceBucket.Sealed)] = new SlipAngleKeyState { Learner = state, ColdTier = 3 },
            };
            var k = new KeyedSlipAngleLearner();
            k.Import(d);
            AssertWindow(SlipAngleWindow.ShippedDefault, k.BucketWindow(Game, Car, Src, SlipAngleTarget.All, SurfaceBucket.Sealed));
        }

        /// <summary>THE DISPLAY FOLLOWS THE KEY AT ONCE (owner, 2026-11-22): at the menu a car switch moves the Slip Angle
        /// context straight away; a key with nothing of its own shows the borrowed window with the half LED, else the
        /// shipped start - before any driven frame.</summary>
        [Fact]
        public void At_the_menu_a_car_switch_shows_the_new_keys_borrowed_or_shipped_start_at_once()
        {
            var k = With((KeyedSlipAngleLearner.MakeKey(Game, Car, Ratio, SlipAngleTarget.All, SurfaceBucket.Sealed), Confirmed(8.0)),
                         (Key(SlipAngleTarget.All, SurfaceBucket.Sealed), Confirmed(8.0)));
            var context = new SlipAngleKeyDataContext();
            Assert.True(context.Observe(Game, Car, SourceNativeness.Native, SurfaceBlend.Only(SurfaceBucket.Sealed)));

            Assert.True(context.Follow(Game, "Car2"));   // the menu: no driven frame
            Assert.Equal("Car2", context.Current.CarId);
            Assert.False(context.Follow(Game, null));     // an unknown car in the same game keeps it
            Assert.Equal("Car2", context.Current.CarId);

            // Slip ratio: another car's window, half LED.
            AssertWindow(PublishedOf(Confirmed(8.0)), SlipAngleKeyDataModel.LearnedWindow(k, context.Current, Ratio).Value);
            SlipAngleWindowMaturity? m = SlipAngleKeyDataModel.LearnedWindowMaturity(k, context.Current, Ratio);
            Assert.True(m.Value.WindowBorrowed);
            Assert.Equal(ColdStartTier.Tier3, m.Value.WindowBorrowedFrom);
            Assert.Equal(0.0, m.Value.OwnMax);

            // An angle source: never another car - the shipped default, not borrowed.
            Assert.Null(SlipAngleKeyDataModel.LearnedWindow(k, context.Current, Src));
            Assert.False(SlipAngleKeyDataModel.LearnedWindowMaturity(k, context.Current, Src).Value.WindowBorrowed);

            Assert.True(context.Follow("OtherGame", null));   // a new game at its menu: no car yet, tier unknown
            Assert.Equal(string.Empty, context.Current.CarId);
            Assert.Equal(SourceNativeness.Unknown, context.Current.Tier);
        }

        /// <summary>NEVER GUESS (owner, 2026-11-22): a saved bucket the game could never have reported can only come from
        /// the old guessing and is dropped on load and from the next save. Since SimHub's grass/gravel flag (2026-11-23)
        /// any game can report Loose, so that is only a Low-grip bucket of a game whose source row cannot report it
        /// (FH6). Its Sealed and Loose data stay; GT7 (snow) keeps Low-grip.</summary>
        [Fact]
        public void Buckets_reached_only_by_the_old_surface_guessing_are_dropped_on_load()
        {
            SurfaceBucketMap.ReportsLowGrip = g => SlipAngleSourceCompiled.Catalog.ReportsLowGrip(g);
            string fh6Sealed = KeyedSlipAngleLearner.MakeKey("FH6", Car, Src, SlipAngleTarget.All, SurfaceBucket.Sealed);
            string fh6Loose = KeyedSlipAngleLearner.MakeKey("FH6", Car, Src, SlipAngleTarget.All, SurfaceBucket.Loose);
            string fh6LowGrip = KeyedSlipAngleLearner.MakeKey("FH6", Car, Src, SlipAngleTarget.Front, SurfaceBucket.LowGrip);
            string rf2Loose = KeyedSlipAngleLearner.MakeKey("RFactor2", Car, Src, SlipAngleTarget.All, SurfaceBucket.Loose);
            var d = new Dictionary<string, SlipAngleKeyState>(StringComparer.Ordinal)
            {
                [fh6Sealed] = new SlipAngleKeyState { Learner = Confirmed(8.0) },
                [fh6Loose] = new SlipAngleKeyState { Learner = Confirmed(8.0), Balance = new SlipAngleBalanceEstimator().Export() },
                [fh6LowGrip] = new SlipAngleKeyState { Learner = Confirmed(8.0) },
                [rf2Loose] = new SlipAngleKeyState { Learner = Confirmed(8.0) },
            };
            var k = new KeyedSlipAngleLearner();
            k.Import(d);
            Dictionary<string, SlipAngleKeyState> saved = null;
            Assert.True(k.SaveIfDue(KeyedSlipAngleLearner.PersistIntervalSeconds, s => saved = s));   // rewritten without them
            Assert.Contains(fh6Sealed, saved.Keys);
            Assert.Contains(rf2Loose, saved.Keys);
            Assert.Contains(fh6Loose, saved.Keys);
            Assert.DoesNotContain(fh6LowGrip, saved.Keys);

            var bands = new SlipAngleSpeedBands();
            var set = new SlipAngleSpeedBandSetState();
            set.Bands.Add(new SlipAngleSpeedBandState { LowKmh = 40, MeanG = 2.0, SampleCount = 4, CountedCount = 3, PeakG = 2.0 });
            bands.Import(new Dictionary<string, SlipAngleSpeedBandSetState>
            {
                [SlipAngleSpeedBands.MakeKey("FH6", Car, Src, SurfaceBucket.Sealed)] = set,
                [SlipAngleSpeedBands.MakeKey("FH6", Car, Src, SurfaceBucket.Loose)] = set,
                [SlipAngleSpeedBands.MakeKey("RFactor2", Car, Src, SurfaceBucket.Loose)] = set,
                [SlipAngleSpeedBands.MakeKey("FH6", Car, Src, SurfaceBucket.LowGrip)] = set,
            });
            Assert.True(bands.Changed);
            Dictionary<string, SlipAngleSpeedBandSetState> keptBands = bands.Export();
            Assert.Equal(3, keptBands.Count);
            Assert.Contains(SlipAngleSpeedBands.MakeKey("FH6", Car, Src, SurfaceBucket.Loose), keptBands.Keys);
            Assert.DoesNotContain(SlipAngleSpeedBands.MakeKey("FH6", Car, Src, SurfaceBucket.LowGrip), keptBands.Keys);
        }

        [Fact]
        public void K_on_a_new_surface_is_the_same_cars_other_surface_k_at_once_and_another_car_starts_at_exactly_one()
        {
            var k = new KeyedSlipAngleLearner();
            SurfaceBlend sealedOnly = SurfaceBlend.Only(SurfaceBucket.Sealed), loose = SurfaceBlend.Only(SurfaceBucket.Loose);
            NearLimit(k, sealedOnly, front: 8.0, rear: 4.0, frames: 900);
            Assert.Equal(0.5, k.K(Game, Car, Src, sealedOnly), 2);

            Idle(k, loose);   // the first frame on loose: no easing, the borrowed k at once
            Assert.Equal(0.5, k.K(Game, Car, Src, loose), 2);
            Assert.True(k.KBorrowed(Game, Car, Src, SurfaceBucket.Loose, out SurfaceBucket? lender));
            Assert.Equal(SurfaceBucket.Sealed, lender);

            Assert.Equal(1.0, k.K(Game, "Car2", Src, sealedOnly));   // another car: never borrowed
            Assert.False(k.KBorrowed(Game, "Car2", Src, SurfaceBucket.Sealed, out _));

            // Loose learns its own balance (1.0) and leaves the borrowed start behind.
            NearLimit(k, loose, front: 4.0, rear: 4.0, frames: 900);
            Assert.False(k.KBorrowed(Game, Car, Src, SurfaceBucket.Loose, out _));
            Idle(k, loose, 600);
            Assert.Equal(1.0, k.K(Game, Car, Src, loose), 2);
        }

        // ---- learning and blending ------------------------------------------------------------------------

        [Fact]
        public void Learning_goes_to_the_dominant_bucket_only()
        {
            var k = new KeyedSlipAngleLearner();
            var blend = new SurfaceBlend(SurfaceBucket.Loose, 0.4, 0.6, 0.0, SurfaceBucketSource.Unknown);
            for (int i = 0; i < 10; i++) k.Observe(Game, Car, Src, blend, 4.0, 4.0, 4.0, 0.8, Allowed, true, Dt);

            Dictionary<string, SlipAngleKeyState> exported = k.Export();
            Assert.Equal(3, exported.Count);
            foreach (SlipAngleTarget t in Enum.GetValues(typeof(SlipAngleTarget)))
                Assert.True(exported.ContainsKey(Key(t, SurfaceBucket.Loose)));
        }

        [Fact]
        public void Window_blends_the_bucket_windows_by_the_classifier_weights()
        {
            SlipAngleLearnerState s8 = Confirmed(8.0), l10 = Confirmed(10.0);
            var k = With((Key(SlipAngleTarget.All, SurfaceBucket.Sealed), s8), (Key(SlipAngleTarget.All, SurfaceBucket.Loose), l10));
            SlipAngleWindow a = PublishedOf(s8), b = PublishedOf(l10);
            var blend = new SurfaceBlend(SurfaceBucket.Loose, 0.25, 0.75, 0.0, SurfaceBucketSource.Unknown);

            SlipAngleWindow w = k.Window(Game, Car, Src, blend);
            AssertWindow(new SlipAngleWindow(0.25 * a.Min + 0.75 * b.Min, 0.25 * a.Best + 0.75 * b.Best, 0.25 * a.Max + 0.75 * b.Max), w);
            Assert.True(w.IsValid);
        }

        [Fact]
        public void Maturity_is_blended_by_weight_and_tier_is_the_dominant_buckets()
        {
            SlipAngleLearnerState s8 = Confirmed(8.0);
            var k = With((Key(SlipAngleTarget.All, SurfaceBucket.Sealed), s8));
            double sealedMaturity = k.Maturity(Game, Car, Src, SurfaceBlend.Only(SurfaceBucket.Sealed));
            Assert.True(sealedMaturity > 0.0);

            var blend = new SurfaceBlend(SurfaceBucket.Loose, 0.5, 0.5, 0.0, SurfaceBucketSource.Unknown);
            Assert.Equal(0.5 * sealedMaturity, k.Maturity(Game, Car, Src, blend), 12);
            Assert.Equal(ColdStartTier.Tier4, k.Tier(Game, Car, Src, blend));
        }

        [Fact]
        public void Restart_with_no_new_driving_reproduces_the_persisted_window_exactly()
        {
            SlipAngleLearnerState s8 = Confirmed(8.0);
            var k = With((Key(SlipAngleTarget.All, SurfaceBucket.Sealed), s8));
            SurfaceBlend sealedOnly = SurfaceBlend.Only(SurfaceBucket.Sealed);
            Idle(k, sealedOnly, 120);
            SlipAngleWindow w = k.Window(Game, Car, Src, sealedOnly);
            SlipAngleWindow p = PublishedOf(s8);
            Assert.Equal(p.Min, w.Min);
            Assert.Equal(p.Best, w.Best);
            Assert.Equal(p.Max, w.Max);
        }

        [Fact]
        public void Hot_weight_is_zero_without_session_crossings_and_grows_with_them()
        {
            Assert.Equal(0.0, KeyedSlipAngleLearner.HotWeightTarget(1.0, 0));
            Assert.Equal(0.0, KeyedSlipAngleLearner.HotWeightTarget(0.0, 10));
            double w1 = KeyedSlipAngleLearner.HotWeightTarget(0.8, 1), w5 = KeyedSlipAngleLearner.HotWeightTarget(0.8, 5);
            Assert.True(w1 > 0.0 && w5 > w1 && w5 < 0.8);
            // Full trust is reachable: 10 session crossings at a window quality of 0.5 or better.
            Assert.Equal(1.0, KeyedSlipAngleLearner.HotWeightTarget(KeyedSlipAngleLearner.QualityForFullTrust, 10));
            Assert.Equal(1.0, KeyedSlipAngleLearner.HotWeightTarget(0.9, 40));
        }

        // ---- fix round 1: latched cold reference, persisted published window, borrowed prior -----------

        /// <summary>One loose-bucket corner through the curve's peak (all three targets, same alpha);
        /// <paramref name="each"/> runs after every frame.</summary>
        private static void LooseCorner(KeyedSlipAngleLearner k, Func<double, double> curve, double peakDeg, Action each = null)
        {
            SurfaceBlend loose = SurfaceBlend.Only(SurfaceBucket.Loose);
            void F(double a, double g) { k.Observe(Game, Car, Src, loose, a, a, a, g, Allowed, true, Dt); each?.Invoke(); }
            double start = 0.75 * peakDeg;
            for (int i = 0; i < 120; i++) F(start, curve(start));
            for (int i = 0; i <= 33; i++) { double a = start + 0.12 * i; F(a, curve(a)); }
            for (int i = 0; i < 72; i++) F(1.0, 0.2);
        }

        /// <summary>Sealed All confirmed at 8 deg; the loose All key has a learned (unconfirmed) curve
        /// peaking at 10 deg, so it starts on the borrowed sealed window (Tier 4).</summary>
        private static KeyedSlipAngleLearner LooseBorrowingFromSealed() =>
            With((Key(SlipAngleTarget.All, SurfaceBucket.Sealed), Confirmed(8.0)),
                 (Key(SlipAngleTarget.All, SurfaceBucket.Loose), new SlipAngleLearnerState { Bins = CurveState(Curve(10.0)) }));

        private static double MaxDelta(SlipAngleWindow a, SlipAngleWindow b) =>
            Math.Max(Math.Abs(a.Min - b.Min), Math.Max(Math.Abs(a.Best - b.Best), Math.Abs(a.Max - b.Max)));

        [Fact]
        public void Published_window_is_continuous_across_the_confirmation_frame()
        {
            KeyedSlipAngleLearner k = LooseBorrowingFromSealed();
            SurfaceBlend loose = SurfaceBlend.Only(SurfaceBucket.Loose);
            Assert.Equal(ColdStartTier.Tier4, k.Tier(Game, Car, Src, loose));
            AssertWindow(PublishedOf(Confirmed(8.0)), k.Window(Game, Car, Src, loose));

            SlipAngleWindow previous = k.Window(Game, Car, Src, loose);
            double worst = 0.0;
            int framesSinceConfirmed = -1;
            for (int c = 0; c < 8; c++)
                LooseCorner(k, Curve(10.0), 10.0, () =>
                {
                    if (framesSinceConfirmed >= 0) framesSinceConfirmed++;
                    else if (k.PeakConfirmed(Game, Car, Src, SlipAngleTarget.All, SurfaceBucket.Loose)) framesSinceConfirmed = 0;
                    SlipAngleWindow now = k.Window(Game, Car, Src, loose);
                    // Across the confirmation frame and the following 2 s of hand-over.
                    if (framesSinceConfirmed < 120) worst = Math.Max(worst, MaxDelta(previous, now));
                    previous = now;
                });

            Assert.True(framesSinceConfirmed >= 0, "fixture: the loose key must confirm");
            Assert.True(worst <= 0.05, $"published window stepped by {worst:0.000} deg in one frame");
            // Its own confirmation does not swap its tier / cold reference away from the borrowed one.
            Assert.Equal(ColdStartTier.Tier4, k.Tier(Game, Car, Src, loose));
        }

        [Fact]
        public void Steady_driving_converges_to_the_learned_window()
        {
            KeyedSlipAngleLearner k = LooseBorrowingFromSealed();
            SurfaceBlend loose = SurfaceBlend.Only(SurfaceBucket.Loose);
            for (int c = 0; c < 40; c++) LooseCorner(k, Curve(10.0), 10.0);

            SlipAngleWindow learned = k.LearnedWindow(Game, Car, Src, SlipAngleTarget.All, SurfaceBucket.Loose).Value;
            Assert.True(MaxDelta(PublishedOf(Confirmed(8.0)), learned) > 0.5, "fixture: learned must differ from the borrowed window");
            Assert.True(MaxDelta(learned, k.Window(Game, Car, Src, loose)) <= 0.1,
                $"published {k.Window(Game, Car, Src, loose)} vs learned {learned}");
        }

        [Fact]
        public void A_restart_with_no_driving_publishes_exactly_what_was_last_published()
        {
            KeyedSlipAngleLearner session1 = LooseBorrowingFromSealed();
            for (int c = 0; c < 6; c++) LooseCorner(session1, Curve(10.0), 10.0);   // mid hand-over
            SurfaceBlend looseOnly = SurfaceBlend.Only(SurfaceBucket.Loose);
            SlipAngleWindow learned1 = session1.LearnedWindow(Game, Car, Src, SlipAngleTarget.All, SurfaceBucket.Loose).Value;
            SlipAngleWindow published1 = session1.Window(Game, Car, Src, looseOnly);
            Assert.True(session1.PeakConfirmed(Game, Car, Src, SlipAngleTarget.All, SurfaceBucket.Loose), "fixture: confirmed");
            Assert.True(MaxDelta(learned1, published1) > 0.05, "fixture: must be mid hand-over (published != learned)");
            Assert.True(MaxDelta(PublishedOf(Confirmed(8.0)), published1) > 0.05, "fixture: must be mid hand-over (published != cold)");
            Dictionary<string, SlipAngleKeyState> saved = session1.Export();

            var session2 = new KeyedSlipAngleLearner();
            session2.Import(saved);
            Idle(session2, SurfaceBlend.Only(SurfaceBucket.Loose), 120);

            foreach (SurfaceBucket b in Enum.GetValues(typeof(SurfaceBucket)))
                foreach (SlipAngleTarget t in Enum.GetValues(typeof(SlipAngleTarget)))
                {
                    SlipAngleWindow w1 = session1.BucketWindow(Game, Car, Src, t, b), w2 = session2.BucketWindow(Game, Car, Src, t, b);
                    Assert.Equal((w1.Min, w1.Best, w1.Max), (w2.Min, w2.Best, w2.Max));
                }
            SurfaceBlend loose = SurfaceBlend.Only(SurfaceBucket.Loose);
            Assert.Equal(session1.Tier(Game, Car, Src, loose), session2.Tier(Game, Car, Src, loose));
        }

        [Fact]
        public void The_borrowed_window_is_the_priors_and_a_curve_still_rising_above_its_Best_raises_it()
        {
            SlipAngleLearnerState sealed6 = Confirmed(6.0);
            double borrowedBest = PublishedOf(sealed6).Best;
            // The raise target sits BELOW the shipped Best: a shipped-prior learner would never see it.
            Assert.True(borrowedBest + 1.0 < SlipAngleWindow.ShippedDefault.Best, "fixture: below the shipped Best");

            // Loose curve still clearly rising, populated up to borrowed Best + 1.5 deg.
            Func<double, double> rising = a => PeakG * Math.Sin(Math.PI / 2.0 * Math.Min(a, 14.0) / 14.0);
            var k = With((Key(SlipAngleTarget.All, SurfaceBucket.Sealed), sealed6),
                         (Key(SlipAngleTarget.All, SurfaceBucket.Loose), new SlipAngleLearnerState { Bins = CurveState(rising, borrowedBest + 1.5) }));
            SurfaceBlend loose = SurfaceBlend.Only(SurfaceBucket.Loose);
            for (int i = 0; i < 60; i++) k.Observe(Game, Car, Src, loose, 2.0, 2.0, 2.0, rising(2.0), Allowed, true, Dt);

            SlipAngleWindow w = k.Window(Game, Car, Src, loose);
            Assert.True(w.Best >= borrowedBest + 1.0, $"Best {w.Best:0.00} should follow the rising curve above the borrowed {borrowedBest:0.00}");
        }

        [Fact]
        public void A_better_tier_appearing_before_confirmation_re_priors_the_key()
        {
            var k = new KeyedSlipAngleLearner();
            SurfaceBlend loose = SurfaceBlend.Only(SurfaceBucket.Loose);
            Idle(k, loose, 10);   // creates the loose keys at Tier 1 (shipped)
            Assert.Equal(ColdStartTier.Tier1, k.Tier(Game, Car, Src, loose));

            // Import is a fresh load, so drive the same situation within one session instead: a second
            // learner where sealed is confirmed and loose was created before it.
            var d = new Dictionary<string, SlipAngleKeyState>(StringComparer.Ordinal)
            {
                [Key(SlipAngleTarget.All, SurfaceBucket.Loose)] = new SlipAngleKeyState { Learner = new SlipAngleLearnerState() },
                [Key(SlipAngleTarget.All, SurfaceBucket.Sealed)] = new SlipAngleKeyState { Learner = new SlipAngleLearnerState { Bins = CurveState(Curve(8.0)) } },
            };
            k.Import(d);
            Assert.Equal(ColdStartTier.Tier1, k.Tier(Game, Car, Src, loose));
            SurfaceBlend sealedOnly = SurfaceBlend.Only(SurfaceBucket.Sealed);
            for (int c = 0; c < 6 && !k.PeakConfirmed(Game, Car, Src, SlipAngleTarget.All, SurfaceBucket.Sealed); c++)
            {
                double start = 6.0;
                for (int i = 0; i < 120; i++) k.Observe(Game, Car, Src, sealedOnly, start, start, start, Curve(8.0)(start), Allowed, true, Dt);
                for (int i = 0; i <= 33; i++) { double a = start + 0.12 * i; k.Observe(Game, Car, Src, sealedOnly, a, a, a, Curve(8.0)(a), Allowed, true, Dt); }
                for (int i = 0; i < 72; i++) k.Observe(Game, Car, Src, sealedOnly, 1.0, 1.0, 1.0, 0.2, Allowed, true, Dt);
            }
            Assert.True(k.PeakConfirmed(Game, Car, Src, SlipAngleTarget.All, SurfaceBucket.Sealed), "fixture: sealed must confirm");

            Assert.Equal(ColdStartTier.Tier4, k.Tier(Game, Car, Src, loose));
            SlipAngleWindow sealedLearned = k.LearnedWindow(Game, Car, Src, SlipAngleTarget.All, SurfaceBucket.Sealed).Value;
            AssertWindow(sealedLearned, k.Window(Game, Car, Src, loose));
        }

        [Fact]
        public void ExpectedLatG_passes_through_the_buckets_all_curve()
        {
            SlipAngleLearnerState s8 = Confirmed(8.0);
            var k = With((Key(SlipAngleTarget.All, SurfaceBucket.Sealed), s8));
            var l = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            l.Import(s8);
            Assert.Equal(l.ExpectedLatG(5.0), k.ExpectedLatG(Game, Car, Src, SurfaceBucket.Sealed, 5.0));
            Assert.True(double.IsNaN(k.ExpectedLatG(Game, Car, Src, SurfaceBucket.Loose, 5.0)));
            Assert.Equal(l.PeakG, k.SealedPeakG(Game, Car, Src));
            Assert.Equal(0.0, k.SealedPeakG(Game, "Other", Src));
        }

        // ---- k (9.3) ------------------------------------------------------------------------------------

        /// <summary>Owner, 2026-11-22: k is the near-limit balance (SlipAngleBalanceEstimator), not the confirmed
        /// windows' Best ratio - confirmed front / rear windows alone no longer move it.</summary>
        [Fact]
        public void K_comes_from_the_near_limit_balance_not_from_confirmed_windows()
        {
            SurfaceBlend sealedOnly = SurfaceBlend.Only(SurfaceBucket.Sealed);
            var both = With((Key(SlipAngleTarget.Front, SurfaceBucket.Sealed), Confirmed(6.0)),
                            (Key(SlipAngleTarget.Rear, SurfaceBucket.Sealed), Confirmed(8.0)));
            Idle(both, sealedOnly, 120);
            Assert.Equal(1.0, both.K(Game, Car, Src, sealedOnly));

            var k = new KeyedSlipAngleLearner();
            NearLimit(k, sealedOnly, front: 8.0, rear: 4.0, frames: 600);
            Assert.Equal(0.5, k.K(Game, Car, Src, sealedOnly), 2);
        }

        /// <summary>Cornering frames at 3 g (they mature the G reference and are all near the limit), both axles given.</summary>
        private static void NearLimit(KeyedSlipAngleLearner k, SurfaceBlend blend, double front, double rear, int frames)
        {
            for (int i = 0; i < frames; i++)
                k.Observe(Game, Car, Src, blend, front, rear, Math.Max(front, rear), 3.0, Allowed, true, Dt);
        }

        [Fact]
        public void K_slew_limited()
        {
            // Both surfaces have k evidence of their own (sealed 2.0, loose 1.0): a surface change slews between them.
            var k = new KeyedSlipAngleLearner();
            SurfaceBlend sealedOnly = SurfaceBlend.Only(SurfaceBucket.Sealed), loose = SurfaceBlend.Only(SurfaceBucket.Loose);
            NearLimit(k, sealedOnly, front: 3.0, rear: 6.0, frames: 900);
            NearLimit(k, loose, front: 4.0, rear: 4.0, frames: 900);
            Idle(k, sealedOnly, 600);
            double kSealed = k.K(Game, Car, Src, sealedOnly);
            double kLoose = 1.0;
            Assert.Equal(2.0, kSealed, 2);

            double previous = kSealed;
            int frames = 0;
            while (Math.Abs(previous - kLoose) > 1e-12 && frames < 1000)
            {
                Idle(k, loose);
                double now = k.K(Game, Car, Src, loose);
                Assert.True(Math.Abs(now - previous) <= KeyedSlipAngleLearner.KSlewPerSecond * Dt + 1e-12,
                    $"k stepped by {Math.Abs(now - previous)} in one frame");
                previous = now;
                frames++;
            }
            Assert.Equal(kLoose, previous, 12);
            double seconds = frames * Dt;
            Assert.InRange(seconds, (kSealed - kLoose) / KeyedSlipAngleLearner.KSlewPerSecond - 2 * Dt,
                (kSealed - kLoose) / KeyedSlipAngleLearner.KSlewPerSecond + 2 * Dt);
        }

        [Fact]
        public void K_is_clamped()
        {
            Assert.Equal(KeyedSlipAngleLearner.KMax, KeyedSlipAngleLearner.ClampK(3.0));
            Assert.Equal(KeyedSlipAngleLearner.KMin, KeyedSlipAngleLearner.ClampK(0.2));
            Assert.Equal(1.0, KeyedSlipAngleLearner.ClampK(double.NaN));
        }

        // ---- persistence (9.5.9) -----------------------------------------------------------------------

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            IncludeFields = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        private static string Serialize(Dictionary<string, SlipAngleKeyState> d)
        {
            var sorted = new SortedDictionary<string, SlipAngleKeyState>(d, StringComparer.Ordinal);
            return JsonSerializer.Serialize(sorted, Json);
        }

        [Fact]
        public void Persist_round_trip_bit_exact()
        {
            var k = With((Key(SlipAngleTarget.Front, SurfaceBucket.Sealed), Confirmed(6.0)),
                         (Key(SlipAngleTarget.Rear, SurfaceBucket.Sealed), Confirmed(8.0)),
                         (Key(SlipAngleTarget.All, SurfaceBucket.Sealed), Confirmed(8.0)));
            SurfaceBlend loose = SurfaceBlend.Only(SurfaceBucket.Loose);
            // Some live learning on another bucket, with awkward (non-terminating) doubles.
            for (int i = 0; i < 400; i++)
            {
                double a = 2.0 + (i % 97) * 0.0731;
                k.Observe(Game, Car, Src, loose, a, a * 1.1, a, 0.3 + (i % 13) * 0.0617, Allowed, true, Dt);
            }
            Dictionary<string, SlipAngleKeyState> before = k.Export();

            using (var store = new RuntimeStore(_path, flushInterval: TimeSpan.FromMinutes(10)))
            {
                store.SaveSlipAngleLearners(before);
                store.Flush();
            }
            Dictionary<string, SlipAngleKeyState> loaded;
            using (var reopened = new RuntimeStore(_path, flushInterval: TimeSpan.FromMinutes(10)))
                reopened.LoadSlipAngleLearners(out loaded);

            var restored = new KeyedSlipAngleLearner();
            restored.Import(loaded);
            Assert.Equal(Serialize(before), Serialize(restored.Export()));

            foreach (SurfaceBucket b in Enum.GetValues(typeof(SurfaceBucket)))
            {
                SurfaceBlend blend = SurfaceBlend.Only(b);
                SlipAngleWindow w1 = k.Window(Game, Car, Src, blend), w2 = restored.Window(Game, Car, Src, blend);
                Assert.Equal((w1.Min, w1.Best, w1.Max), (w2.Min, w2.Best, w2.Max));
                Assert.Equal(k.Maturity(Game, Car, Src, blend), restored.Maturity(Game, Car, Src, blend));
            }
        }

        [Fact]
        public void Import_ignores_malformed_keys_and_null_states()
        {
            var d = new Dictionary<string, SlipAngleKeyState>(StringComparer.Ordinal)
            {
                ["not-a-key"] = new SlipAngleKeyState { Learner = Confirmed(8.0) },
                [ColdStartTierResolver.Key(Game, Car, Src + "/middle", "sealed")] = new SlipAngleKeyState { Learner = Confirmed(8.0) },
                [ColdStartTierResolver.Key(Game, Car, Src + "/all", "mud")] = new SlipAngleKeyState { Learner = Confirmed(8.0) },
                [Key(SlipAngleTarget.All, SurfaceBucket.Sealed)] = null,
            };
            var k = new KeyedSlipAngleLearner();
            k.Import(d);
            Assert.Empty(k.Export());
            k.Import(null);
            Assert.Empty(k.Export());
        }

        [Fact]
        public void SaveIfDue_writes_exports_into_the_store_cache_on_the_calling_thread()
        {
            int testThread = Thread.CurrentThread.ManagedThreadId;
            var saveThreads = new List<int>();
            using var store = new RuntimeStore(_path, flushInterval: TimeSpan.FromMinutes(10));
            Action<Dictionary<string, SlipAngleKeyState>> save = d =>
            {
                saveThreads.Add(Thread.CurrentThread.ManagedThreadId);
                store.SaveSlipAngleLearners(d);
            };

            var k = new KeyedSlipAngleLearner();
            SurfaceBlend sealedOnly = SurfaceBlend.Only(SurfaceBucket.Sealed);
            int saves = 0;
            int frames = (int)Math.Round((KeyedSlipAngleLearner.PersistIntervalSeconds + 1.0) / Dt);
            for (int i = 0; i < frames; i++)
            {
                k.Observe(Game, Car, Src, sealedOnly, 4.0, 4.0, 4.0, 0.8, Allowed, true, Dt);
                if (k.SaveIfDue(Dt, save)) saves++;
            }

            Assert.Equal(1, saves);
            Assert.All(saveThreads, id => Assert.Equal(testThread, id));
            store.LoadSlipAngleLearners(out Dictionary<string, SlipAngleKeyState> cached);
            Assert.True(cached.ContainsKey(Key(SlipAngleTarget.All, SurfaceBucket.Sealed)));

            // The second after that save was observed too: it goes out after another full interval.
            int more = 0;
            for (int i = 0; i < frames; i++) if (k.SaveIfDue(Dt, save)) more++;
            Assert.Equal(1, more);
            // Nothing observed since the last save: never re-exported, however long it waits.
            for (int i = 0; i < frames; i++) Assert.False(k.SaveIfDue(Dt, save));
            Assert.Equal(2, saveThreads.Count);
            store.LoadSlipAngleLearners(out cached);

            // Flushing (the timer path) only writes what the data thread already put in the cache.
            store.Flush();
            using var reopened = new RuntimeStore(_path, flushInterval: TimeSpan.FromMinutes(10));
            reopened.LoadSlipAngleLearners(out Dictionary<string, SlipAngleKeyState> onDisk);
            Assert.Equal(Serialize(cached), Serialize(onDisk));
        }
    }
}

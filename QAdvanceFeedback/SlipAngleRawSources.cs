using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.SlipAngle;

namespace QAdvanceFeedback
{
    /// <summary>How a table row turns its raw field(s) into a slip angle.</summary>
    public enum SlipAngleTransform
    {
        /// <summary>The field is the slip angle itself.</summary>
        Direct = 0,

        /// <summary>The field is tan(slip angle) (LFS <c>TanSlipAngle</c>); the angle is atan(v), radians.</summary>
        AtanOfTangent = 1,

        /// <summary>No angle field: α = atan2(lateral patch velocity, |longitudinal ground velocity|),
        /// radians (rFactor 2 / LMU; the NASCAR Heat reader family with its terrain speeds, provisional).</summary>
        Atan2PatchVelocity = 2,
    }

    /// <summary>One game's native slip-angle source, from <c>QAdvanceFeedback.SlipAngleSource.json</c>
    /// (<see cref="SlipAngleSourceCatalog"/>).</summary>
    public sealed class SlipAngleRawSourceEntry
    {
        /// <summary>The SimHub game id (the row's "id").</summary>
        public string GameId { get; }

        /// <summary>Four full SimHub property names, FL, FR, RL, RR. For
        /// <see cref="SlipAngleTransform.Atan2PatchVelocity"/>, the lateral patch velocity.</summary>
        public IReadOnlyList<string> PropertyPaths { get; }

        /// <summary>Atan2PatchVelocity only: the longitudinal ground velocity properties, FL, FR, RL, RR;
        /// null for every other transform.</summary>
        public IReadOnlyList<string> LongitudinalPaths { get; }

        /// <summary>Unit of the value AFTER <see cref="Transform"/>; <see cref="SlipAngleUnit.Unverified"/>
        /// (no "unit" in the file) means it is detected at runtime (ruling R6).</summary>
        public SlipAngleUnit Unit { get; }

        public SlipAngleTransform Transform { get; }

        /// <summary>Optional per-wheel raw surface properties, FL, FR, RL, RR; null when the row has none.</summary>
        public IReadOnlyList<string> SurfacePaths { get; }

        /// <summary>The row is marked provisional (NASCAR Heat family: field semantics unproven).</summary>
        public bool Provisional { get; }

        /// <summary>The row's free-text note (diagnostics; never read by the plugin).</summary>
        public string Note { get; }

        // Plain arrays for the per-frame read.
        internal string[] FullPropertyPaths { get; }
        internal string[] FullLongitudinalPaths { get; }
        internal string[] FullSurfacePaths { get; }

        public SlipAngleRawSourceEntry(
            string gameId, string[] propertyPaths, string[] longitudinalPaths, SlipAngleUnit unit,
            SlipAngleTransform transform, string[] surfacePaths, bool provisional = false, string note = null)
        {
            GameId = gameId;
            FullPropertyPaths = propertyPaths;
            FullLongitudinalPaths = longitudinalPaths;
            FullSurfacePaths = surfacePaths;
            PropertyPaths = Array.AsReadOnly(propertyPaths);
            LongitudinalPaths = longitudinalPaths == null ? null : Array.AsReadOnly(longitudinalPaths);
            Unit = unit;
            Transform = transform;
            SurfacePaths = surfacePaths == null ? null : Array.AsReadOnly(surfacePaths);
            Provisional = provisional;
            Note = note;
        }
    }

    /// <summary>
    /// The per-game native slip-angle table the reader uses. THE JSON FILE IS THE ONLY DEFINITION (owner,
    /// 2026-11-23): there are no built-in rows - the table is whatever catalog was last put in use
    /// (<see cref="Use"/>, by <see cref="SlipAngleSourceStore"/> from PluginsData\Common), starting as the copy
    /// compiled into the DLL (<see cref="SlipAngleSourceCompiled"/>). A game with no row has no native per-wheel
    /// slip angle and Slip Angle uses the car-angle estimate.
    /// <para/>
    /// PATH FORM: full SimHub property names. SimHub publishes array element i as <c>name</c> + (i + 1)
    /// formatted "00" (DataCorePlugin.DeclareObject, decompiled), so <c>Physics.slipAngle[0]</c> is
    /// <c>DataCorePlugin.GameRawData.Physics.slipAngle01</c>; it lower-cases names on lookup, so case is irrelevant.
    /// Every row lists its wheels already in FL, FR, RL, RR order.
    /// </summary>
    public static class SlipAngleRawSourceTable
    {
        /// <summary>The prefix every game's raw data is published under.</summary>
        public const string RawDataPrefix = "DataCorePlugin.GameRawData.";

        private static SlipAngleSourceCatalog _catalog;
        private static int _revision;

        /// <summary>The catalog in use (the compiled copy until one is put in use).</summary>
        public static SlipAngleSourceCatalog Catalog => Volatile.Read(ref _catalog) ?? SlipAngleSourceCompiled.Catalog;

        /// <summary>Bumped by every <see cref="Use"/>, so a reader drops what it learned about the old paths.</summary>
        public static int Revision => Volatile.Read(ref _revision);

        /// <summary>Puts <paramref name="catalog"/> in use (null: the compiled copy).</summary>
        public static void Use(SlipAngleSourceCatalog catalog)
        {
            Volatile.Write(ref _catalog, catalog);
            Interlocked.Increment(ref _revision);
            Core.KeyRules.SourceKeys.SlipAngleDefinitionChanged();   // Version 21: a game's definition is part of its key
            // Which games can report Low-grip now decides which saved Low-grip buckets are kept (SurfaceBucketMap).
            SurfaceBucketMap.ReportsLowGrip = g => Catalog.ReportsLowGrip(g);
        }

        /// <summary>The game's per-wheel surface translation (raw value to bucket); null when the row has none.</summary>
        public static IReadOnlyDictionary<string, SurfaceBucket> SurfaceValues(string gameId) => Catalog.SurfaceValues(gameId);

        /// <summary>The ids (as written - Game1|Game2, patterns) of the rows with a native slip angle.</summary>
        public static IReadOnlyCollection<string> NativeGameIds => Catalog.NativeGameIds;

        /// <summary>The native slip-angle row for <paramref name="gameId"/> (id or alias, case-insensitive).</summary>
        public static bool TryGet(string gameId, out SlipAngleRawSourceEntry entry) => Catalog.TryGetNative(gameId, out entry);

        /// <summary>The per-wheel raw surface properties (FL, FR, RL, RR) for <paramref name="gameId"/>; false when
        /// the row has none.</summary>
        public static bool TryGetSurfacePaths(string gameId, out IReadOnlyList<string> paths)
        {
            string[] p = Catalog.SurfacePaths(gameId);
            paths = p == null ? null : Array.AsReadOnly(p);
            return p != null;
        }

        /// <summary>Whether SimHub's <c>FeedbackData.VerticalAcceleration</c> is actually written for this game: the
        /// game's row in the source file says <c>verticalAcceleration: true</c> (it stays at a default 0 elsewhere).</summary>
        public static bool ReportsVerticalAcceleration(string gameId) => Catalog.ReportsVerticalAcceleration(gameId);

        /// <summary>The full SimHub property name for a raw-data path.</summary>
        public static string FullPropertyName(string relativePath) => RawDataPrefix + relativePath;

        /// <summary>The surface properties for the per-frame reader.</summary>
        internal static string[] FullSurfacePaths(string gameId) => Catalog.SurfacePaths(gameId);
    }

    /// <summary>
    /// Builds the per-frame <see cref="SlipAngleInput"/> from a game's raw data through the table
    /// (Layer 2). The SimHub property read is injected (<c>PluginManager.GetPropertyValue</c> in the
    /// plugin, a dictionary in tests), so this class holds no SimHub types.
    /// <para/>
    /// RESILIENCE (docs\architecture.md "Resilience model"): never throws. A row whose four primary
    /// paths all read null for <see cref="MissingVerdictFrames"/> MOVING frames in a row (speed above
    /// <see cref="SlipAngleNativeSupport.MinMovingSpeedKmh"/>; any frame at or below it, or with no
    /// speed, resets the count, so menus, the garage and loading screens never count) gets a per-game,
    /// session-long "path missing" verdict and its slip paths are not read again (the LMU exposure
    /// question; a reader that publishes nothing). An exception reading a game degrades that game for
    /// the session and is reported once to <see cref="HealthRegistry"/>.
    /// </summary>
    public sealed class SlipAngleRawSourceReader
    {
        /// <summary>Consecutive all-null frames before a row is ruled missing (about 10 s at 60 Hz;
        /// long enough to outlast SimHub declaring a reader's raw properties after the game starts).</summary>
        public const int MissingVerdictFrames = 600;

        private readonly SlipAngleNativeSupport _native;
        private readonly SlipAngleUnitClassifier _classifier = new SlipAngleUnitClassifier();
        private readonly SlipAngleRatioRangeTracker _ratioRange = new SlipAngleRatioRangeTracker();
        private readonly SlipAngleSourceUnits _defaultUnits = SlipAngleSourceUnits.CreateDefault();
        private readonly Dictionary<string, int> _missCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public SlipAngleRawSourceReader(SlipAngleNativeSupport nativeSupport)
        {
            _native = nativeSupport ?? new SlipAngleNativeSupport();
        }

        /// <summary>The promotion/unit-detection state this reader feeds.</summary>
        public SlipAngleNativeSupport NativeSupport => _native;

        /// <summary>The per-game unit lists (the plugin config's SlipAngle.SourceUnits); the hard-coded
        /// defaults when unset. Read on every frame, so an edit to the config applies at once.</summary>
        public Func<SlipAngleSourceUnits> Units { get; set; }

        /// <summary>True while the driver chose "Always car angle" (SlipAngleChannelSettings.AngleSource): no game's
        /// tyre field is read, even one with a row. Read on every frame.</summary>
        public Func<bool> AlwaysCarAngle { get; set; }

        /// <summary>Raised (on the data thread) after this reader changed a unit list - a game detected as
        /// degrees or a ratio, or a ratio game's "over 100%" verdict - so the plugin saves the config.</summary>
        public event Action UnitsChanged;

        /// <summary>The body-angle unit check (diagnostics).</summary>
        public SlipAngleUnitClassifier UnitClassifier => _classifier;

        /// <summary>True once the game's slip paths were ruled missing, or reading it failed.</summary>
        public bool IsSourceMissing(string gameId)
            => !string.IsNullOrEmpty(gameId) && (_missing.Contains(gameId) || _failed.Contains(gameId));

        /// <summary>
        /// Reads one frame for <paramref name="gameId"/>. <paramref name="frame"/> supplies speed
        /// (ground speed, else base speed), lateral G for unit detection, and kerbs; <paramref name="dt"/>
        /// is seconds since the previous frame.
        /// </summary>
        public SlipAngleInput Read(string gameId, Func<string, object> getProperty, ITelemetryFrame frame, double dt)
        {
            string id = gameId ?? string.Empty;
            ForgetVerdictsIfTableChanged();
            bool[] kerbs = frame?.WheelsOnKerbs == null ? null : (bool[])frame.WheelsOnKerbs.Clone();
            if (id.Length == 0 || getProperty == null || _failed.Contains(id))
                return new SlipAngleInput(id, kerbWheels: kerbs);

            try
            {
                string[] surface = ReadSurface(id, getProperty);
                SurfaceBucket?[] wheelSurface = TranslateSurface(id, surface);
                bool hasEntry = SlipAngleRawSourceTable.TryGet(id, out SlipAngleRawSourceEntry entry);
                SlipAngleSourceState? carAngle =
                    AlwaysCarAngle?.Invoke() == true ? SlipAngleSourceState.CarAngleAlways          // "Always car angle", every game
                    : hasEntry && (Units?.Invoke() ?? _defaultUnits).ForcesCarAngle(id)
                        ? SlipAngleSourceState.CarAngleForGame                                    // "Source Type for this game" = Car Angle
                    : !hasEntry ? SlipAngleSourceState.NoSource
                    : _missing.Contains(id) ? SlipAngleSourceState.NotReported
                    : (SlipAngleSourceState?)null;
                if (carAngle.HasValue)
                    return new SlipAngleInput(id, kerbWheels: kerbs, rawWheelSurface: surface, wheelSurface: wheelSurface, sourceState: carAngle);

                Corners? raw = ReadCorners(entry, getProperty, out bool allMissing);
                double speedKmh = frame?.GroundSpeedKmh ?? frame?.SpeedKmh ?? double.NaN;
                if (TrackMissing(id, allMissing, speedKmh > SlipAngleNativeSupport.MinMovingSpeedKmh))
                    return new SlipAngleInput(id, kerbWheels: kerbs, rawWheelSurface: surface, wheelSurface: wheelSurface,
                        sourceState: SlipAngleSourceState.NotReported);

                // THE UNIT (owner, 2026-11-20): the table's when it states one; else the game's config list
                // (ByAngle / AngleUsingRatio1 / AngleUsingRatio100 / AngleUsingRadians); else radians. An unlisted game keeps being
                // checked against the car's body angle and moves to its list when it is not radians.
                SlipAngleSourceUnits units = Units?.Invoke() ?? _defaultUnits;
                bool changed = false;
                if (entry.Unit == SlipAngleUnit.Unverified && !units.IsListed(id))
                {
                    _classifier.Observe(id, raw, frame?.LateralG, frame?.LateralLocalVelocity, frame?.ForwardLocalVelocity, speedKmh, dt);
                    // Every verdict is recorded - radians too (AngleUsingRadians, owner 2026-11-21) - so the check
                    // finishes and the heading can say what the unit is.
                    if (_classifier.TryGetVerdict(id, out SlipAngleUnit verdict) && verdict != SlipAngleUnit.Unverified)
                        changed |= units.Assign(id, verdict);
                }
                SlipAngleUnit unit = units.Resolve(id, entry.Unit);

                bool? exceeds = null;
                if (SlipAngleSourceScale.IsRatio(unit))
                {
                    _ratioRange.Observe(id, raw, unit, speedKmh);
                    bool? seen = _ratioRange.Exceeds(id);
                    if (seen.HasValue) changed |= units.SetExceedsFullScale(id, seen.Value);
                    exceeds = units.ExceedsFullScale(id);
                }
                if (changed) UnitsChanged?.Invoke();

                _native.Observe(id, raw, unit, speedKmh, dt, frame?.LateralG, frame?.ForwardLocalVelocity);

                Corners? deg = raw.HasValue ? ToDegrees(raw.Value, unit) : (Corners?)null;
                return new SlipAngleInput(id, hasNativeSource: true, nativeWheelDeg: deg, isNative: _native.IsNative(id),
                    unit: unit, isProvisionalSource: entry.Provisional, kerbWheels: kerbs, rawWheelSurface: surface, wheelSurface: wheelSurface,
                    ratioExceedsFullScale: exceeds);
            }
            catch (Exception e)
            {
                _failed.Add(id);
                HealthRegistry.Report(HealthSubsystems.SlipAngleSource, HealthSeverity.Degraded,
                    "Health.Impact.SlipAngleSource", e.ToString(), isSimHubCompatibilityIssue: true);
                return new SlipAngleInput(id, kerbWheels: kerbs);
            }
        }

        /// <summary>A new source file (an edit saved from the page, or by hand) may have new paths: what this reader
        /// ruled about the old ones (missing, failed) no longer holds.</summary>
        private void ForgetVerdictsIfTableChanged()
        {
            int revision = SlipAngleRawSourceTable.Revision;
            if (revision == _tableRevision) return;
            _tableRevision = revision;
            _missCounts.Clear();
            _missing.Clear();
            _failed.Clear();
        }

        private int _tableRevision = SlipAngleRawSourceTable.Revision;

        /// <summary>The row's four values after its transform, in the row's unit, FL/FR/RL/RR; null
        /// when any wheel is unreadable, non-numeric or non-finite.</summary>
        public static Corners? ReadCorners(SlipAngleRawSourceEntry entry, Func<string, object> getProperty)
            => ReadCorners(entry, getProperty, out _);

        private static Corners? ReadCorners(SlipAngleRawSourceEntry entry, Func<string, object> getProperty, out bool allMissing)
        {
            allMissing = true;
            if (entry == null || getProperty == null) return null;

            var values = new double[4];
            bool complete = true;
            for (int i = 0; i < 4; i++)
            {
                object primaryRaw = getProperty(entry.FullPropertyPaths[i]);
                if (primaryRaw != null) allMissing = false;
                double? primary = ToFiniteDouble(primaryRaw);

                double? value;
                switch (entry.Transform)
                {
                    case SlipAngleTransform.AtanOfTangent:
                        value = primary.HasValue ? Math.Atan(primary.Value) : (double?)null;
                        break;
                    case SlipAngleTransform.Atan2PatchVelocity:
                        double? lon = ToFiniteDouble(getProperty(entry.FullLongitudinalPaths[i]));
                        value = primary.HasValue && lon.HasValue ? Math.Atan2(primary.Value, Math.Abs(lon.Value)) : (double?)null;
                        break;
                    default:
                        value = primary;
                        break;
                }

                if (value.HasValue) values[i] = value.Value;
                else complete = false;
            }
            return complete ? new Corners(values[0], values[1], values[2], values[3]) : (Corners?)null;
        }

        /// <summary>The raw surface values translated by the row's <c>surfaceValues</c>; null when it has none.</summary>
        private static SurfaceBucket?[] TranslateSurface(string id, string[] surface)
        {
            IReadOnlyDictionary<string, SurfaceBucket> values = surface == null ? null : SlipAngleRawSourceTable.SurfaceValues(id);
            if (values == null) return null;
            var wheels = new SurfaceBucket?[surface.Length];
            for (int i = 0; i < surface.Length; i++)
                wheels[i] = surface[i] != null && values.TryGetValue(surface[i].Trim(), out SurfaceBucket b) ? b : (SurfaceBucket?)null;
            return wheels;
        }

        private static string[] ReadSurface(string id, Func<string, object> getProperty)
        {
            string[] paths = SlipAngleRawSourceTable.FullSurfacePaths(id);
            if (paths == null) return null;
            var surface = new string[4];
            for (int i = 0; i < 4; i++)
            {
                object raw = getProperty(paths[i]);
                surface[i] = raw == null ? null : Convert.ToString(raw, CultureInfo.InvariantCulture);
            }
            return surface;
        }

        /// <summary>Counts consecutive all-null MOVING frames; a hit or a non-moving frame resets the
        /// count. True once (and from then on) the row is missing.</summary>
        private bool TrackMissing(string id, bool allMissing, bool moving)
        {
            if (!allMissing || !moving)
            {
                _missCounts.Remove(id);
                return false;
            }
            _missCounts.TryGetValue(id, out int count);
            count++;
            if (count >= MissingVerdictFrames)
            {
                _missCounts.Remove(id);
                _missing.Add(id);
                return true;
            }
            _missCounts[id] = count;
            return false;
        }

        private static Corners ToDegrees(Corners c, SlipAngleUnit unit)
            => new Corners(
                SlipAngleNativeSupport.ToDegrees(c.FrontLeft, unit),
                SlipAngleNativeSupport.ToDegrees(c.FrontRight, unit),
                SlipAngleNativeSupport.ToDegrees(c.RearLeft, unit),
                SlipAngleNativeSupport.ToDegrees(c.RearRight, unit));

        /// <summary>A numeric property value as a finite double; null for null, strings, booleans,
        /// non-numeric or non-finite values.</summary>
        private static double? ToFiniteDouble(object raw)
        {
            if (raw == null || raw is string || raw is bool || !(raw is IConvertible)) return null;
            try
            {
                double v = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return double.IsNaN(v) || double.IsInfinity(v) ? (double?)null : v;
            }
            catch (Exception e) when (e is InvalidCastException || e is FormatException || e is OverflowException)
            {
                return null;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    /// <summary>Why a row's FL/FR/RL/RR order is what it is (docs\slip-angle-spike-report.md "Method").</summary>
    public enum SlipAngleWheelOrderBasis
    {
        /// <summary>The field, type or enum names the wheel (FrontLeft, LF, fl, ...).</summary>
        Named = 0,

        /// <summary>The game reader itself remaps raw indices into SimHub's feedback order (F1, LFS:
        /// raw RL, RR, FL, FR).</summary>
        ReaderRemap = 1,

        /// <summary>The reader copies raw[i] into FeedbackData[i], so raw index i is SimHub's own
        /// wheel i (FL, FR, RL, RR); not named in the assembly beyond that equality.</summary>
        FeedbackDataIndex = 2,

        /// <summary>The spike could not determine the order; FL, FR, RL, RR is ASSUMED.</summary>
        Unverified = 3,
    }

    /// <summary>One game's native slip-angle source, transcribed from the spike report.</summary>
    public sealed class SlipAngleRawSourceEntry
    {
        /// <summary>The SimHub game id, as the spike lists it.</summary>
        public string GameId { get; }

        /// <summary>Four property paths under <see cref="SlipAngleRawSourceTable.RawDataPrefix"/>, in
        /// FL, FR, RL, RR order (any raw-index remap already applied). For
        /// <see cref="SlipAngleTransform.Atan2PatchVelocity"/>, the lateral patch velocity.</summary>
        public IReadOnlyList<string> PropertyPaths { get; }

        /// <summary>Atan2PatchVelocity only: the longitudinal ground velocity paths, FL, FR, RL, RR;
        /// null for every other transform.</summary>
        public IReadOnlyList<string> LongitudinalPaths { get; }

        /// <summary>Unit of the value AFTER <see cref="Transform"/>; <see cref="SlipAngleUnit.Unverified"/>
        /// means it is detected at runtime (ruling R6).</summary>
        public SlipAngleUnit Unit { get; }

        public SlipAngleTransform Transform { get; }

        /// <summary>The spike's own wording for the wheel order (diagnostics).</summary>
        public string WheelOrder { get; }

        public SlipAngleWheelOrderBasis WheelOrderBasis { get; }

        /// <summary>False when FL, FR, RL, RR is only assumed (spike: order unverified).</summary>
        public bool WheelOrderVerified => WheelOrderBasis != SlipAngleWheelOrderBasis.Unverified;

        /// <summary>Optional per-wheel raw surface paths, FL, FR, RL, RR; null when the spike found none.</summary>
        public IReadOnlyList<string> SurfacePaths { get; }

        /// <summary>The spike marks the source provisional (NASCAR Heat family: field semantics unproven).</summary>
        public bool Provisional { get; }

        /// <summary>The game id is not in SimHub 9.13.2's GameRegistry (Rennsport: reader exists, id
        /// literal not found), so the row may never match.</summary>
        public bool GameIdUnverified { get; }

        /// <summary>The spike could not confirm the member is published under GameRawData (LMU:
        /// <c>JsonIgnore</c>, not <c>DoNotExpose</c>); the reader's path-missing verdict covers it.</summary>
        public bool ExposureUnconfirmed { get; }

        // Full SimHub property names, built once so the per-frame read allocates no strings.
        internal string[] FullPropertyPaths { get; }
        internal string[] FullLongitudinalPaths { get; }
        internal string[] FullSurfacePaths { get; }

        internal SlipAngleRawSourceEntry(
            string gameId, string[] propertyPaths, string[] longitudinalPaths, SlipAngleUnit unit,
            SlipAngleTransform transform, string wheelOrder, SlipAngleWheelOrderBasis wheelOrderBasis,
            string[] surfacePaths, bool provisional = false, bool gameIdUnverified = false, bool exposureUnconfirmed = false)
        {
            GameId = gameId;
            PropertyPaths = Array.AsReadOnly(propertyPaths);
            LongitudinalPaths = longitudinalPaths == null ? null : Array.AsReadOnly(longitudinalPaths);
            Unit = unit;
            Transform = transform;
            WheelOrder = wheelOrder;
            WheelOrderBasis = wheelOrderBasis;
            SurfacePaths = surfacePaths == null ? null : Array.AsReadOnly(surfacePaths);
            Provisional = provisional;
            GameIdUnverified = gameIdUnverified;
            ExposureUnconfirmed = exposureUnconfirmed;
            FullPropertyPaths = SlipAngleRawSourceTable.FullPropertyNames(propertyPaths);
            FullLongitudinalPaths = SlipAngleRawSourceTable.FullPropertyNames(longitudinalPaths);
            FullSurfacePaths = SlipAngleRawSourceTable.FullPropertyNames(surfacePaths);
        }
    }

    /// <summary>
    /// The per-game native slip-angle table (docs\slip-angle-design.md §9.1), rows EXACTLY as
    /// docs\slip-angle-spike-report.md (SimHub 9.13.2) lists them, plus the per-wheel raw surface
    /// fields the spike documents for fallback-only games (ruling R13). Pure strings; no SimHub types.
    /// <para/>
    /// PATH FORM: the spike writes array members as <c>name[i]</c>; SimHub publishes array element i
    /// as <c>name</c> + (i + 1) formatted "00" (DataCorePlugin.DeclareObject, decompiled), so
    /// <c>Physics.slipAngle[0]</c> is the property <c>DataCorePlugin.GameRawData.Physics.slipAngle01</c>.
    /// SimHub lower-cases property names on lookup, so case is irrelevant at runtime.
    /// <para/>
    /// WHEEL ORDER: every row's paths are stored already in FL, FR, RL, RR order. Raw RL, RR, FL, FR
    /// rows (F1 23+, LFS) are remapped here. Rows whose order the spike could not determine
    /// (AC Rally, AC EVO, KartKraft, Bus Bound, and the surface-only PCars, rF1-family and F1 2019-22
    /// rows) ASSUME FL, FR, RL, RR and say so via <see cref="SlipAngleWheelOrderBasis.Unverified"/>.
    /// <para/>
    /// Not listed (spike: Fallback only / Unverified / N/A, or no documented four-wheel field): every
    /// other game, including AC, AMS2, RRRE, iRacing, EA WRC, Dirt Rally 2, BeamNG's
    /// <c>wheel_slip_*</c> (meaning unknown), bikes, trucks with variable wheel counts (ETS2/ATS) and
    /// Kart Racing Pro (array length not stated).
    /// </summary>
    public static class SlipAngleRawSourceTable
    {
        /// <summary>Every path in this table is relative to this SimHub property prefix.</summary>
        public const string RawDataPrefix = "DataCorePlugin.GameRawData.";

        private const string RawRlRrFlFr = "raw RL,RR,FL,FR (reader remaps (j+2)%4)";
        private const string FeedbackIndex = "FeedbackData index";
        private const string NotDetermined = "unverified (FL,FR,RL,RR assumed)";

        private static readonly int[] Identity = { 0, 1, 2, 3 };

        /// <summary>FL, FR, RL, RR taken from raw indices 2, 3, 0, 1 (raw order RL, RR, FL, FR).</summary>
        private static readonly int[] RawRearFirst = { 2, 3, 0, 1 };

        private static readonly Dictionary<string, SlipAngleRawSourceEntry> Native = BuildNative();

        /// <summary>
        /// Extra ids for the same row. The spike lists ACC as "ACC", but this repo's own transcription
        /// of a decompiled SimHub plugin (<c>ViperSupportedGames</c>) shows SimHub reports the game as
        /// "AssettoCorsaCompetizione"; both reach the one spike row so the row cannot silently miss.
        /// </summary>
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AssettoCorsaCompetizione"] = "ACC",
        };

        private static readonly Dictionary<string, SurfaceOnly> SurfaceOnlyRows = BuildSurfaceOnly();

        private static readonly HashSet<string> VerticalAccelerationGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IRacing",
            "Rennsport",
        };

        /// <summary>The spike's native (direct and computed) game ids, aliases excluded.</summary>
        public static IReadOnlyCollection<string> NativeGameIds { get; } = Native.Keys.ToArray();

        /// <summary>The native slip-angle row for <paramref name="gameId"/> (case-insensitive).</summary>
        public static bool TryGet(string gameId, out SlipAngleRawSourceEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(gameId)) return false;
            if (Aliases.TryGetValue(gameId, out string canonical)) gameId = canonical;
            return Native.TryGetValue(gameId, out entry);
        }

        /// <summary>The per-wheel raw surface paths (FL, FR, RL, RR) for <paramref name="gameId"/>:
        /// a native row's own surface paths, else a surface-only row's. False when the spike documents none.</summary>
        public static bool TryGetSurfacePaths(string gameId, out IReadOnlyList<string> paths, out SlipAngleWheelOrderBasis basis)
        {
            paths = null;
            basis = SlipAngleWheelOrderBasis.Unverified;
            if (TryGet(gameId, out SlipAngleRawSourceEntry e))
            {
                if (e.SurfacePaths == null) return false;
                paths = e.SurfacePaths;
                basis = e.WheelOrderBasis;
                return true;
            }
            if (string.IsNullOrEmpty(gameId) || !SurfaceOnlyRows.TryGetValue(gameId, out SurfaceOnly s)) return false;
            paths = s.Paths;
            basis = s.Basis;
            return true;
        }

        /// <summary>Whether SimHub's <c>FeedbackData.VerticalAcceleration</c> is actually written for this
        /// game (spike: only iRacing and Rennsport; it stays at a default 0 everywhere else).</summary>
        public static bool ReportsVerticalAcceleration(string gameId)
            => !string.IsNullOrEmpty(gameId) && VerticalAccelerationGames.Contains(gameId);

        /// <summary>The full SimHub property name for a table path.</summary>
        public static string FullPropertyName(string relativePath) => RawDataPrefix + relativePath;

        internal static string[] FullPropertyNames(IEnumerable<string> relativePaths)
            => relativePaths?.Select(FullPropertyName).ToArray();

        /// <summary>Full-name form of <see cref="TryGetSurfacePaths"/> for the per-frame reader.</summary>
        internal static string[] FullSurfacePaths(string gameId)
        {
            if (TryGet(gameId, out SlipAngleRawSourceEntry e)) return e.FullSurfacePaths;
            return !string.IsNullOrEmpty(gameId) && SurfaceOnlyRows.TryGetValue(gameId, out SurfaceOnly s) ? s.FullPaths : null;
        }

        // ---- rows ----

        private sealed class SurfaceOnly
        {
            public IReadOnlyList<string> Paths;
            public string[] FullPaths;
            public SlipAngleWheelOrderBasis Basis;
        }

        /// <summary>SimHub's published name for array element <paramref name="rawIndex"/> of
        /// <paramref name="arrayPath"/>, followed by <paramref name="member"/> (may be empty).</summary>
        private static string Element(string arrayPath, int rawIndex, string member)
            => arrayPath + (rawIndex + 1).ToString("00", CultureInfo.InvariantCulture) + member;

        private static string[] Array4(string arrayPath, string member, int[] cornerToRaw)
            => cornerToRaw.Select(raw => Element(arrayPath, raw, member)).ToArray();

        private static string[] Named(string format, params string[] wheels)
            => wheels.Select(w => string.Format(CultureInfo.InvariantCulture, format, w)).ToArray();

        private static Dictionary<string, SlipAngleRawSourceEntry> BuildNative()
        {
            var rows = new List<SlipAngleRawSourceEntry>();

            // ---- Native (direct) ----
            rows.Add(new SlipAngleRawSourceEntry("ACC", Array4("Physics.slipAngle", "", Identity), null,
                SlipAngleUnit.Unverified, SlipAngleTransform.Direct, FeedbackIndex, SlipAngleWheelOrderBasis.FeedbackDataIndex, null));
            rows.Add(new SlipAngleRawSourceEntry("AssettoCorsaRally", Array4("Physics.slipAngle", "", Identity), null,
                SlipAngleUnit.Unverified, SlipAngleTransform.Direct, NotDetermined, SlipAngleWheelOrderBasis.Unverified, null));
            rows.Add(new SlipAngleRawSourceEntry("AssettoCorsaEVO", Array4("Physics.slipAngle", "", Identity), null,
                SlipAngleUnit.Unverified, SlipAngleTransform.Direct, NotDetermined, SlipAngleWheelOrderBasis.Unverified, null));

            foreach (string f1 in new[] { "F12023", "F12024", "F12025" })
            {
                rows.Add(new SlipAngleRawSourceEntry(f1, Array4("PacketMotionExData.m_wheelSlipAngle", "", RawRearFirst), null,
                    SlipAngleUnit.Unverified, SlipAngleTransform.Direct, RawRlRrFlFr, SlipAngleWheelOrderBasis.ReaderRemap,
                    Array4("PlayerCarTelemetryData.m_surfaceType", "", RawRearFirst)));
            }

            foreach (string forza in new[] { "FM7", "FM8", "FH4", "FH5", "FH6" })
            {
                rows.Add(new SlipAngleRawSourceEntry(forza,
                    Named("TireSlipAngle{0}", "FrontLeft", "FrontRight", "RearLeft", "RearRight"), null,
                    SlipAngleUnit.Unverified, SlipAngleTransform.Direct, "named FL,FR,RL,RR", SlipAngleWheelOrderBasis.Named, null));
            }

            rows.Add(new SlipAngleRawSourceEntry("LFS", Array4("OutSim2.OSWheels", ".TanSlipAngle", RawRearFirst), null,
                SlipAngleUnit.Radians, SlipAngleTransform.AtanOfTangent, "raw RL,RR,FL,FR (remapped)", SlipAngleWheelOrderBasis.ReaderRemap, null));
            rows.Add(new SlipAngleRawSourceEntry("Wreckfest2", Array4("Main.carPlayer.tires", ".slipAngle", Identity), null,
                SlipAngleUnit.Unverified, SlipAngleTransform.Direct, "FL,FR,RL,RR (enum)", SlipAngleWheelOrderBasis.Named,
                Array4("Main.carPlayer.tires", ".surfaceType", Identity)));
            rows.Add(new SlipAngleRawSourceEntry("ProjectMotorRacing", Array4("PlayerTelemetry.m_wheels", ".m_slipAngle", Identity), null,
                SlipAngleUnit.Unverified, SlipAngleTransform.Direct, FeedbackIndex, SlipAngleWheelOrderBasis.FeedbackDataIndex,
                Array4("PlayerTelemetry.m_wheels", ".m_contactMaterialHash", Identity)));
            rows.Add(new SlipAngleRawSourceEntry("Drift21",
                Named("RaceTelemetry.TireSlipAngleRadians_{0}", "FrontLeft", "FrontRight", "BackLeft", "BackRight"), null,
                SlipAngleUnit.Radians, SlipAngleTransform.Direct, "named FL,FR,BL,BR", SlipAngleWheelOrderBasis.Named, null));
            rows.Add(new SlipAngleRawSourceEntry("KartKraft", Array4("Motion.WheelsArray", ".SlipAngle", Identity), null,
                SlipAngleUnit.Unverified, SlipAngleTransform.Direct, NotDetermined, SlipAngleWheelOrderBasis.Unverified,
                Array4("Motion.WheelsArray", ".Surface", Identity)));
            rows.Add(new SlipAngleRawSourceEntry("BusBound", Array4("Telemetry.Bus.Wheels", ".SlipAngle", Identity), null,
                SlipAngleUnit.Unverified, SlipAngleTransform.Direct, NotDetermined, SlipAngleWheelOrderBasis.Unverified,
                Array4("Telemetry.Bus.Wheels", ".Ground", Identity)));
            rows.Add(new SlipAngleRawSourceEntry("Rennsport",
                Named("TelemetryPoint.{0}.SlipAngle", "LF", "RF", "LR", "RR"), null,
                SlipAngleUnit.Unverified, SlipAngleTransform.Direct, "named LF,RF,LR,RR", SlipAngleWheelOrderBasis.Named,
                Named("TelemetryPoint.{0}.ContactSurface", "LF", "RF", "LR", "RR"), gameIdUnverified: true));

            // ---- Native (computed) ----
            rows.Add(new SlipAngleRawSourceEntry("RFactor2",
                Array4("CurrentPlayerTelemetry.mWheels", ".mLateralPatchVel", Identity),
                Array4("CurrentPlayerTelemetry.mWheels", ".mLongitudinalGroundVel", Identity),
                SlipAngleUnit.Radians, SlipAngleTransform.Atan2PatchVelocity, FeedbackIndex, SlipAngleWheelOrderBasis.FeedbackDataIndex,
                Array4("CurrentPlayerTelemetry.mWheels", ".mSurfaceType", Identity)));
            rows.Add(new SlipAngleRawSourceEntry("LMU",
                Array4("PlayerNativeTelemetry.mWheel", ".mLateralPatchVel", Identity),
                Array4("PlayerNativeTelemetry.mWheel", ".mLongitudinalGroundVel", Identity),
                SlipAngleUnit.Radians, SlipAngleTransform.Atan2PatchVelocity, FeedbackIndex, SlipAngleWheelOrderBasis.FeedbackDataIndex,
                Array4("PlayerNativeTelemetry.mWheel", ".mSurfaceType", Identity), exposureUnconfirmed: true));

            // NASCAR Heat reader family - PROVISIONAL (terrain speeds not proven to be patch velocities).
            foreach (string nascar in new[] { "NASCARHeat4", "NASCARHeat5", "TonyStewartsAllAmericanRacing", "TonyStewartsSprintCarRacing", "SRXTheGame" })
            {
                rows.Add(new SlipAngleRawSourceEntry(nascar,
                    Named("LCarPacket.wheel_{0}.lat_terrain_speed", "lf", "rf", "lr", "rr"),
                    Named("LCarPacket.wheel_{0}.lon_terrain_speed", "lf", "rf", "lr", "rr"),
                    SlipAngleUnit.Radians, SlipAngleTransform.Atan2PatchVelocity, "named LF,RF,LR,RR", SlipAngleWheelOrderBasis.Named,
                    Named("LCarPacket.wheel_{0}.surface", "lf", "rf", "lr", "rr"), provisional: true));
            }

            var map = new Dictionary<string, SlipAngleRawSourceEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (SlipAngleRawSourceEntry row in rows) map.Add(row.GameId, row);
            return map;
        }

        private static Dictionary<string, SurfaceOnly> BuildSurfaceOnly()
        {
            var map = new Dictionary<string, SurfaceOnly>(StringComparer.OrdinalIgnoreCase);
            void Add(string id, string[] paths, SlipAngleWheelOrderBasis basis)
                => map.Add(id, new SurfaceOnly { Paths = Array.AsReadOnly(paths), FullPaths = FullPropertyNames(paths), Basis = basis });

            Add("Automobilista2", Array4("mTerrain", "", Identity), SlipAngleWheelOrderBasis.FeedbackDataIndex);
            foreach (string pc in new[] { "PCars", "PCars2", "PCars3" })
                Add(pc, Array4("mTerrain", "", Identity), SlipAngleWheelOrderBasis.Unverified);
            foreach (string rf1 in new[] { "RFactor1", "StockCarExtreme", "CopaPetrobrasDeMarcas", "FormulaTruck2013", "Automobilista", "SIMBINGTR2" })
                Add(rf1, Array4("Data.wheel", ".surfaceType", Identity), SlipAngleWheelOrderBasis.Unverified);
            Add("GranTurismo7", Array4("surfaceType", "", Identity), SlipAngleWheelOrderBasis.FeedbackDataIndex);
            Add("BeamNgDrive", Named("wheel_contactMaterial_{0}", "fl", "fr", "rl", "rr"), SlipAngleWheelOrderBasis.Named);
            // The spike gives no order for the F1 2019-22 surface array (only for F1 23+), so it is assumed.
            foreach (string f1 in new[] { "F12019", "F12020", "F12021", "F12022" })
                Add(f1, Array4("PlayerCarTelemetryData.m_surfaceType", "", Identity), SlipAngleWheelOrderBasis.Unverified);
            return map;
        }
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
            bool[] kerbs = frame?.WheelsOnKerbs == null ? null : (bool[])frame.WheelsOnKerbs.Clone();
            if (id.Length == 0 || getProperty == null || _failed.Contains(id))
                return new SlipAngleInput(id, kerbWheels: kerbs);

            try
            {
                string[] surface = ReadSurface(id, getProperty);
                if (!SlipAngleRawSourceTable.TryGet(id, out SlipAngleRawSourceEntry entry) || _missing.Contains(id)
                    || (Units?.Invoke() ?? _defaultUnits).ForcesCarAngle(id))   // the driver chose the car angle
                    return new SlipAngleInput(id, kerbWheels: kerbs, rawWheelSurface: surface);

                Corners? raw = ReadCorners(entry, getProperty, out bool allMissing);
                double speedKmh = frame?.GroundSpeedKmh ?? frame?.SpeedKmh ?? double.NaN;
                if (TrackMissing(id, allMissing, speedKmh > SlipAngleNativeSupport.MinMovingSpeedKmh))
                    return new SlipAngleInput(id, kerbWheels: kerbs, rawWheelSurface: surface);

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
                    unit: unit, isProvisionalSource: entry.Provisional, kerbWheels: kerbs, rawWheelSurface: surface,
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

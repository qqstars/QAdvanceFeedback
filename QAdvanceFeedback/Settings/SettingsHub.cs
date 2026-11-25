using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Clutch;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.SlipAngle;

namespace QAdvanceFeedback.Settings
{
    /// <summary>What the hub reads for <see cref="SettingField.SlipAngleSourceUnit"/>: the dropdown's selected entry
    /// and whether the game's unit is its default (Restore default hidden). Compared by value.</summary>
    public sealed class SlipAngleUnitState
    {
        public SlipAngleUnitState(SlipAngleUnitChoice? selected, bool isDefault)
        {
            Selected = selected;
            IsDefault = isDefault;
        }

        public SlipAngleUnitChoice? Selected { get; }
        public bool IsDefault { get; }

        public override bool Equals(object obj) =>
            obj is SlipAngleUnitState o && o.Selected == Selected && o.IsDefault == IsDefault;

        public override int GetHashCode() => ((int?)Selected ?? -1) * 2 + (IsDefault ? 1 : 0);
    }

    /// <summary>
    /// The settings both the plugin settings page and the native ShakeIt effect panels show
    /// (docs\shakeit-native-effects-design.md, "Two-way settings sync"). The last three are
    /// broadcast-only: the ShakeIt side shows them but never edits them.
    /// </summary>
    public enum SettingField
    {
        LockSourceMode,
        SlipSourceMode,
        AccelMaxMode,
        DecelMaxMode,
        LatMaxMode,
        FixedAccelMaxG,
        FixedDecelMaxG,
        FixedLatMaxG,
        LockProjector,
        SlipProjector,
        SourceAvailability,
        // Key data points, for each channel's APPLIED source (and, for the values, the slot its
        // Global/Per-Game choice selects in the current game) - see KeyDataPanelModel.
        LockKeyDataAuto,
        SlipKeyDataAuto,
        LockNormalizePattern,
        SlipNormalizePattern,
        LockKeyDataValues,
        SlipKeyDataValues,
        // G-Force options shown on the G-Force ShakeIt effects.
        LateralDirection,
        IntegrateWheelLockAndSlip,
        // Clutch Bite (docs\clutch-bite-design.md).
        ClutchApply,
        ClutchBiteMode,
        ClutchBitePoint,
        ClutchFadedRange,
        ClutchGamma,
        ClutchAxis,
        ClutchAxisInvert,
        // The bite point learning parameters as one value (plugin page only).
        ClutchLearning,
        // Slip Angle (docs\slip-angle-design.md 9.10). The projector is broadcast-only.
        SlipAngleTriggerBy,
        SlipAngleBrakeThreshold,
        SlipAngleThrottleThreshold,
        SlipAngleKeyDataAuto,
        SlipAngleKeyDataValues,
        SlipAngleProjector,
        // G-Force option (Task 25 adds the engine behaviour).
        IntegrateSlipAngle,
        // ShakeIt algorithm settings (docs\shakeit-source-design.md §6): what each channel's hidden
        // ShakeIt container reads while its source is ShakeIt Built-in.
        LockShakeItLegacyAlgorithm,
        LockShakeItSensibility,
        LockShakeItLockOnly,
        SlipShakeItLegacyAlgorithm,
        SlipShakeItMuteWhileLockActive,
        // The current game's slip-angle unit dropdown (owner, 2026-11-21 / 2026-11-22): reads the selected
        // SlipAngleUnitChoice (SlipAngleSelectedUnit); writing a choice picks it, null restores the game's default.
        SlipAngleSourceUnit,
        // Plugin Internal's angle source (owner, 2026-11-23): per-wheel tyre angle when available, or always the car
        // angle (SlipAngleChannelSettings.AngleSource). Set by the plugin page's Apply; broadcast so the panel follows.
        SlipAngleAngleSource
    }

    public enum SettingsOrigin
    {
        PluginPage,
        ShakeItPanel,
        File,
        Runtime
    }

    public sealed class SettingsChangedEventArgs : EventArgs
    {
        public SettingsChangedEventArgs(IReadOnlyCollection<SettingField> fields, SettingsOrigin origin)
        {
            Fields = fields;
            Origin = origin;
        }

        public IReadOnlyCollection<SettingField> Fields { get; }
        public SettingsOrigin Origin { get; }
    }

    /// <summary>
    /// Single writer of the shared settings fields. A write applies to the live settings object,
    /// runs the plugin's apply-and-save hook once, then tells every listener exactly which fields
    /// changed, so a UI refreshes only those (unapplied edits elsewhere on the plugin page survive).
    /// Last write per field wins. SimHub-free; listeners marshal to their own dispatcher.
    /// </summary>
    public sealed class SettingsHub
    {
        private static readonly SettingField[] EditableFields =
        {
            SettingField.LockSourceMode, SettingField.SlipSourceMode,
            SettingField.AccelMaxMode, SettingField.DecelMaxMode, SettingField.LatMaxMode,
            SettingField.FixedAccelMaxG, SettingField.FixedDecelMaxG, SettingField.FixedLatMaxG,
            SettingField.LockKeyDataAuto, SettingField.SlipKeyDataAuto,
            SettingField.LockNormalizePattern, SettingField.SlipNormalizePattern,
            SettingField.LockKeyDataValues, SettingField.SlipKeyDataValues,
            SettingField.LateralDirection, SettingField.IntegrateWheelLockAndSlip,
            SettingField.ClutchApply, SettingField.ClutchBiteMode, SettingField.ClutchBitePoint,
            SettingField.ClutchFadedRange, SettingField.ClutchGamma, SettingField.ClutchAxis, SettingField.ClutchAxisInvert,
            SettingField.ClutchLearning,
            SettingField.SlipAngleTriggerBy, SettingField.SlipAngleBrakeThreshold, SettingField.SlipAngleThrottleThreshold,
            SettingField.SlipAngleKeyDataAuto, SettingField.SlipAngleKeyDataValues, SettingField.IntegrateSlipAngle,
            SettingField.SlipAngleSourceUnit, SettingField.SlipAngleAngleSource,
            SettingField.LockShakeItLegacyAlgorithm, SettingField.LockShakeItSensibility, SettingField.LockShakeItLockOnly,
            SettingField.SlipShakeItLegacyAlgorithm, SettingField.SlipShakeItMuteWhileLockActive
        };

        private readonly Func<QAdvanceFeedbackSettings> _settings;
        private readonly Action<QAdvanceFeedbackSettings> _applyAndSave;
        private readonly Func<string> _currentGame;
        private readonly Func<SourceNativeness> _slipAngleNativeness;

        /// <param name="currentGame">The running game - selects the Per-Game key data slot.</param>
        public SettingsHub(Func<QAdvanceFeedbackSettings> settings, Action<QAdvanceFeedbackSettings> applyAndSave,
            Func<string> currentGame = null, Func<SourceNativeness> slipAngleNativeness = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _applyAndSave = applyAndSave;
            _currentGame = currentGame ?? (() => null);
            _slipAngleNativeness = slipAngleNativeness ?? (() => SourceNativeness.Unknown);
        }

        /// <summary>The running game the key data fields are read and written for.</summary>
        public string CurrentGame => _currentGame();

        public event EventHandler<SettingsChangedEventArgs> Changed;

        public static bool IsEditable(SettingField field) => Array.IndexOf(EditableFields, field) >= 0;

        /// <summary>Write one shared field. Returns false (and does nothing) when the value is unchanged.</summary>
        public bool Set(SettingField field, object value, SettingsOrigin origin)
        {
            if (!IsEditable(field))
                throw new InvalidOperationException(field + " is broadcast-only and cannot be set.");

            QAdvanceFeedbackSettings s = _settings();
            if (s == null) return false;

            string game = _currentGame();
            SourceNativeness tier = _slipAngleNativeness();
            object before = Read(s, field, game, tier);
            if (Equals(before, value)) return false;

            Write(s, field, value, game, tier);
            if (Equals(before, Read(s, field, game, tier))) return false;   // clamped back, or refused (invalid key data)

            ApplyAndSave(s);
            Raise(new[] { field }, origin);
            return true;
        }

        /// <summary>A failed apply/save (an I/O error, or a dictionary the data thread is writing while it
        /// is serialized) must never throw into SimHub's ShakeIt UI. The value is already live; it is
        /// reported to the health panel and saved by the next successful save.</summary>
        private void ApplyAndSave(QAdvanceFeedbackSettings s)
        {
            try { _applyAndSave?.Invoke(s); }
            catch (Exception e)
            {
                Core.Health.HealthRegistry.Report(Core.Health.HealthSubsystems.ShakeItEffects,
                    Core.Health.HealthSeverity.Degraded, "Health.Impact.ShakeItEffects", e.ToString());
            }
        }

        /// <summary>Broadcast fields someone else already wrote and saved (the plugin page's Apply,
        /// source-availability changes). Never saves.</summary>
        public void NotifyApplied(IEnumerable<SettingField> fields, SettingsOrigin origin)
        {
            var list = new List<SettingField>(fields ?? new SettingField[0]);
            if (list.Count == 0) return;
            Raise(list, origin);
        }

        /// <summary>The config file changed on disk outside this process's own saves: adopt every
        /// differing shared field, then save once and broadcast with <see cref="SettingsOrigin.File"/>.</summary>
        public void NotifyFileChanged(QAdvanceFeedbackSettings fromDisk)
        {
            QAdvanceFeedbackSettings s = _settings();
            if (s == null || fromDisk == null) return;

            string game = _currentGame();
            SourceNativeness tier = _slipAngleNativeness();
            var changed = new List<SettingField>();
            foreach (SettingField field in EditableFields)
            {
                object diskValue = Read(fromDisk, field, game, tier);
                if (diskValue == null || Equals(Read(s, field, game, tier), diskValue)) continue;
                Write(s, field, diskValue, game, tier);
                if (Equals(Read(s, field, game, tier), diskValue)) changed.Add(field);
            }
            if (changed.Count == 0) return;

            ApplyAndSave(s);
            Raise(changed, SettingsOrigin.File);
        }

        /// <summary>Which shared fields (editable and broadcast-only projector fields) differ.</summary>
        public static IReadOnlyList<SettingField> Diff(QAdvanceFeedbackSettings before, QAdvanceFeedbackSettings after, string gameId = null, SourceNativeness slipAngleTier = SourceNativeness.Unknown)
        {
            var result = new List<SettingField>();
            if (before == null || after == null) return result;

            foreach (SettingField field in EditableFields)
                if (!Equals(Read(before, field, gameId, slipAngleTier), Read(after, field, gameId, slipAngleTier))) result.Add(field);

            if (!SameJson(before.Lock?.Projector, after.Lock?.Projector)) result.Add(SettingField.LockProjector);
            if (!SameJson(before.Slip?.Projector, after.Slip?.Projector)) result.Add(SettingField.SlipProjector);
            if (!SameJson(before.SlipAngle?.Projector, after.SlipAngle?.Projector)) result.Add(SettingField.SlipAngleProjector);
            return result;
        }

        /// <summary>The source identity the Slip Angle key data is keyed by (Plugin Internal, the only source): the
        /// identity of the current identity tier (the
        /// last in-session confirmed tier for the current game, kept across pause / menu -
        /// <see cref="SlipAngleIdentityTier"/>) -
        /// Fallback gives <see cref="SlipAngleFrameProcessor.FallbackIdentity"/>, Native and Unknown give
        /// <see cref="SlipAngleFrameProcessor.NativeIdentity"/> (the processor's default) - or, for a game whose
        /// native field is a grip ratio (its unit list, owner 2026-11-20), <see cref="SlipAngleFrameProcessor.NativeRatioIdentity"/>,
        /// exactly as the processor keys it. <paramref name="units"/> defaults to <paramref name="ch"/>'s.</summary>
        public static string SlipAngleIdentity(SlipAngleChannelSettings ch, SourceNativeness tier = SourceNativeness.Unknown,
            string gameId = null, SlipAngleSourceUnits units = null)
        {
            // "Always car angle" (General Source Type, owner 2026-11-23) wins over the game's own tyre angle.
            if (ch?.AngleSource == SlipAngleAngleSource.CarAngle) return SlipAngleFrameProcessor.FallbackIdentity;
            if (tier == SourceNativeness.Fallback) return SlipAngleFrameProcessor.FallbackIdentity;
            if ((units ?? ch?.SourceUnits)?.ForcesCarAngle(gameId) == true) return SlipAngleFrameProcessor.FallbackIdentity;
            return SlipAngleFrameProcessor.NativeIdentityFor(NativeUnit(gameId, units ?? ch?.SourceUnits));
        }

        /// <summary>
        /// THE UNIT-AWARE KEY DATA HEADING (owner, 2026-11-21): the string key naming what the Min/Best/Max numbers
        /// are for the source on screen - every source is turned into degrees, but from different things: the
        /// car-angle fallback (the body angle), or the game's native
        /// unit by the same rule the reader uses (a unit the table states is respected; else the game's list; else
        /// read as radians while the body-angle check runs). No game: the plain heading.
        /// </summary>
        public static string SlipAngleKeyDataHeaderKey(SourceNativeness tier, string gameId, SlipAngleSourceUnits units,
            SlipAngleAngleSource angleSource = SlipAngleAngleSource.PerWheelWhenAvailable)
        {
            const string Head = "SlipAngle.KeyData.Header";
            if (angleSource == SlipAngleAngleSource.CarAngle) return Head + ".CarAngle";
            if (tier == SourceNativeness.Fallback || (units != null && units.ForcesCarAngle(gameId))) return Head + ".CarAngle";
            if (string.IsNullOrEmpty(gameId) || !SlipAngleRawSourceTable.TryGet(gameId, out SlipAngleRawSourceEntry entry)) return Head;
            SlipAngleSourceUnits u = units ?? SlipAngleSourceUnits.CreateDefault();
            switch (u.Resolve(gameId, entry.Unit))
            {
                case SlipAngleUnit.Radians:
                    return !u.IsListed(gameId) && entry.Unit == SlipAngleUnit.Unverified ? Head + ".AssumedRadians" : Head + ".Radians";
                case SlipAngleUnit.Ratio1: return Head + ".Ratio1";
                case SlipAngleUnit.Ratio100: return Head + ".Ratio100";
                default: return Head;
            }
        }

        /// <summary>True when a game's slip-angle unit is known for certain (owner, 2026-11-21): its row in
        /// QAdvanceFeedback.SlipAngleSource.json states it (owner, 2026-11-24: the JSON is the only definition - nothing is
        /// hard-coded in the plugin). Its dropdown offers only that unit and the car angle (<see cref="SlipAngleUnitOptions"/>).
        /// A game with no native field is treated the same (its only source is the car angle).</summary>
        public static bool SlipAngleUnitIsHardCoded(string gameId)
        {
            if (string.IsNullOrEmpty(gameId) || !SlipAngleRawSourceTable.TryGet(gameId, out SlipAngleRawSourceEntry entry)) return true;
            return entry.Unit != SlipAngleUnit.Unverified;
        }

        /// <summary>The stated unit of a game as a dropdown entry (its JSON row's <c>unit</c>); null for a game with no
        /// native field or whose unit is detected.</summary>
        public static SlipAngleUnitChoice? SlipAngleHardCodedUnit(string gameId)
        {
            if (string.IsNullOrEmpty(gameId) || !SlipAngleRawSourceTable.TryGet(gameId, out SlipAngleRawSourceEntry entry)) return null;
            return entry.Unit != SlipAngleUnit.Unverified ? ToChoice(entry.Unit) : null;
        }

        private static SlipAngleUnitChoice? ToChoice(SlipAngleUnit unit)
        {
            switch (unit)
            {
                case SlipAngleUnit.Degrees: return SlipAngleUnitChoice.Degrees;
                case SlipAngleUnit.Radians: return SlipAngleUnitChoice.Radians;
                case SlipAngleUnit.Ratio1: return SlipAngleUnitChoice.Ratio1;
                case SlipAngleUnit.Ratio100: return SlipAngleUnitChoice.Ratio100;
                default: return null;
            }
        }

        /// <summary>
        /// "SOURCE TYPE FOR THIS GAME" - THE ENTRIES (owner, 2026-11-24). The game's own pick, saved to the plugin config
        /// (never to the source JSON); the default is what the JSON says:
        /// <list type="bullet">
        /// <item>a source whose unit is set (Per-Wheel Angle with a Source Unit, or Lateral + Longitudinal Velocity -
        /// always radians): Per-Wheel Angle in that unit, then Car Angle;</item>
        /// <item>Per-Wheel Angle on Auto Detect: Auto (top), Angle, Radian, Ratio 0-1, Ratio 0-100, Car Angle;</item>
        /// <item>no per-wheel source (Telemetry Type None), or no game: none (the dropdown is hidden).</item>
        /// </list>
        /// </summary>
        public static IReadOnlyList<SlipAngleUnitChoice> SlipAngleUnitOptions(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return Array.Empty<SlipAngleUnitChoice>();
            if (!SlipAngleRawSourceTable.TryGet(gameId, out _)) return Array.Empty<SlipAngleUnitChoice>();
            SlipAngleUnitChoice? hard = SlipAngleHardCodedUnit(gameId);
            if (hard.HasValue) return new[] { hard.Value, SlipAngleUnitChoice.CarAngle };
            return new[]
            {
                SlipAngleUnitChoice.AutoDetect, SlipAngleUnitChoice.Degrees, SlipAngleUnitChoice.Radians,
                SlipAngleUnitChoice.Ratio1, SlipAngleUnitChoice.Ratio100, SlipAngleUnitChoice.CarAngle,
            };
        }

        /// <summary>The dropdown's selected entry: the car angle when chosen (or the game has no native field); for a
        /// hard-coded game its unit; otherwise the driver's pick, else Auto-detect (a unit the check found still shows
        /// as Auto-detect - <see cref="SlipAngleDetectedUnit"/> names it). Null with no game.</summary>
        public static SlipAngleUnitChoice? SlipAngleSelectedUnit(string gameId, SlipAngleSourceUnits units)
        {
            if (string.IsNullOrEmpty(gameId)) return null;
            if (!SlipAngleRawSourceTable.TryGet(gameId, out _)) return null;   // no source: no dropdown
            if (units != null && units.ForcesCarAngle(gameId)) return SlipAngleUnitChoice.CarAngle;
            SlipAngleUnitChoice? hard = SlipAngleHardCodedUnit(gameId);
            if (hard.HasValue) return hard.Value;
            SlipAngleUnitChoice? listed = units?.Choice(gameId);
            return listed.HasValue && units.IsChosenByUser(gameId) ? listed.Value : SlipAngleUnitChoice.AutoDetect;
        }

        /// <summary>The unit the check found for a game on Auto-detect (shown beside "Auto-detect"); null while it is
        /// still being checked, or when the game is not on Auto-detect.</summary>
        public static SlipAngleUnitChoice? SlipAngleDetectedUnit(string gameId, SlipAngleSourceUnits units)
        {
            if (SlipAngleSelectedUnit(gameId, units) != SlipAngleUnitChoice.AutoDetect) return null;
            return units?.Choice(gameId);
        }

        /// <summary>True when the game's unit is its default - Restore default hides: a hard-coded game read in its own
        /// unit (not the car angle, no hand-made list entry against it); any other game on Auto-detect. A game with no
        /// native field, or no game, is always default.</summary>
        public static bool SlipAngleUnitIsDefault(string gameId, SlipAngleSourceUnits units)
        {
            if (string.IsNullOrEmpty(gameId) || units == null || !SlipAngleRawSourceTable.TryGet(gameId, out SlipAngleRawSourceEntry entry)) return true;
            if (units.ForcesCarAngle(gameId) || units.IsChosenByUser(gameId)) return false;
            SlipAngleUnitChoice? hard = SlipAngleHardCodedUnit(gameId);
            return !hard.HasValue || ToChoice(units.Resolve(gameId, entry.Unit)) == hard;
        }

        /// <summary>Whether the "Source Type for this game" dropdown shows (owner, 2026-11-24): for a game with a per-wheel
        /// source (Telemetry Type not None) - its entries depend on the source, <see cref="SlipAngleUnitOptions"/>. Never while the angle source is "Always car
        /// angle" (owner, 2026-11-23: no game's tyre value is read then).</summary>
        public static bool ShowSlipAngleUnitChoice(string gameId, SlipAngleSourceUnits units,
            SlipAngleAngleSource angleSource = SlipAngleAngleSource.PerWheelWhenAvailable) =>
            angleSource != SlipAngleAngleSource.CarAngle && units != null && !string.IsNullOrEmpty(gameId)
            && SlipAngleRawSourceTable.TryGet(gameId, out _);   // Telemetry Type None: hidden (owner, 2026-11-24)

        /// <summary>Applies a dropdown write for <paramref name="gameId"/>: null, Auto-detect or the game's hard-coded
        /// unit restore the default (<see cref="SlipAngleSourceUnits.RestoreDefault"/>); any other offered entry is the
        /// driver's pick (<see cref="SlipAngleSourceUnits.Choose"/>); an entry not offered for this game is refused.</summary>
        public static bool ApplySlipAngleUnitChoice(SlipAngleSourceUnits units, string gameId, SlipAngleUnitChoice? choice)
        {
            if (units == null || string.IsNullOrEmpty(gameId)) return false;
            if (!choice.HasValue || choice == SlipAngleUnitChoice.AutoDetect || choice == SlipAngleHardCodedUnit(gameId))
                return units.RestoreDefault(gameId);
            if (!SlipAngleUnitOptions(gameId).Contains(choice.Value)) return false;
            return units.Choose(gameId, choice.Value);
        }

        /// <summary>The unit a game's native slip-angle field is read in (the table's, else its list, else
        /// radians); <see cref="SlipAngleUnit.Unverified"/> for a game with no native field.</summary>
        public static SlipAngleUnit NativeUnit(string gameId, SlipAngleSourceUnits units)
        {
            if (string.IsNullOrEmpty(gameId) || !SlipAngleRawSourceTable.TryGet(gameId, out SlipAngleRawSourceEntry entry))
                return SlipAngleUnit.Unverified;
            return (units ?? SlipAngleSourceUnits.CreateDefault()).Resolve(gameId, entry.Unit);
        }

        private static object ReadSlipAngleValues(SlipAngleChannelSettings ch, string gameId, SourceNativeness tier)
        {
            if (ch?.KeyData == null) return null;
            return ch.KeyData.TryGetSlot(gameId, SlipAngleIdentity(ch, tier, gameId), out SlipAngleWindow w) ? (object)w : null;
        }

        /// <summary>A refused commit (Auto, per-game with no game, invalid window) changes nothing, so
        /// <see cref="Set"/> sees an unchanged value and returns false without saving or broadcasting.</summary>
        private static void WriteSlipAngleValues(SlipAngleChannelSettings ch, object value, string gameId, SourceNativeness tier)
        {
            if (ch?.KeyData == null || !(value is SlipAngleWindow w)) return;
            ch.KeyData.TryCommitManual(gameId, SlipAngleIdentity(ch, tier, gameId), w.Min, w.Best, w.Max);
        }

        private static double ClampPercent(object value)
        {
            double v = Convert.ToDouble(value);
            if (double.IsNaN(v)) return 0.0;
            return Math.Max(0.0, Math.Min(100.0, v));
        }

        public static object Read(QAdvanceFeedbackSettings s, SettingField field, string gameId = null, SourceNativeness slipAngleTier = SourceNativeness.Unknown)
        {
            switch (field)
            {
                case SettingField.LockKeyDataAuto: return s.Lock?.KeyDataPoints?.GetAutoGenerate(KeyDataPanelModel.Identity(s.Lock, true));
                case SettingField.SlipKeyDataAuto: return s.Slip?.KeyDataPoints?.GetAutoGenerate(KeyDataPanelModel.Identity(s.Slip, false));
                case SettingField.LockNormalizePattern: return s.Lock?.NormalizePattern;
                case SettingField.SlipNormalizePattern: return s.Slip?.NormalizePattern;
                case SettingField.LockKeyDataValues: return ReadValues(s.Lock, true, gameId);
                case SettingField.SlipKeyDataValues: return ReadValues(s.Slip, false, gameId);
                case SettingField.LockSourceMode: return s.Lock?.SourceMode;
                case SettingField.SlipSourceMode: return s.Slip?.SourceMode;
                case SettingField.AccelMaxMode: return s.GForce?.AccelMaxMode;
                case SettingField.DecelMaxMode: return s.GForce?.DecelMaxMode;
                case SettingField.LatMaxMode: return s.GForce?.LatMaxMode;
                case SettingField.FixedAccelMaxG: return s.GForce?.FixedAccelMaxG;
                case SettingField.FixedDecelMaxG: return s.GForce?.FixedDecelMaxG;
                case SettingField.FixedLatMaxG: return s.GForce?.FixedLatMaxG;
                case SettingField.LateralDirection: return s.GForce?.LateralDirection;
                case SettingField.IntegrateWheelLockAndSlip: return s.GForce?.IntegrateWheelLockAndSlip;
                case SettingField.ClutchApply: return s.Clutch?.Apply;
                case SettingField.ClutchBiteMode: return s.Clutch?.Mode;
                case SettingField.ClutchBitePoint: return s.Clutch?.BitePoint;
                case SettingField.ClutchFadedRange: return s.Clutch?.FadedRange;
                case SettingField.ClutchGamma: return s.Clutch?.Gamma;
                case SettingField.ClutchAxis: return s.Clutch?.FallbackAxis;
                case SettingField.ClutchAxisInvert: return s.Clutch?.InvertAxis;
                case SettingField.ClutchLearning: return s.Clutch?.Learning?.ToParameters();
                case SettingField.SlipAngleTriggerBy: return s.SlipAngle?.Gate?.TriggerBy;
                case SettingField.SlipAngleBrakeThreshold: return s.SlipAngle?.Gate?.BrakeThreshold;
                case SettingField.SlipAngleThrottleThreshold: return s.SlipAngle?.Gate?.ThrottleThreshold;
                case SettingField.SlipAngleKeyDataAuto: return s.SlipAngle?.KeyData?.GetAutoGenerate(SlipAngleIdentity(s.SlipAngle, slipAngleTier, gameId));
                case SettingField.SlipAngleKeyDataValues: return ReadSlipAngleValues(s.SlipAngle, gameId, slipAngleTier);
                case SettingField.SlipAngleSourceUnit:
                    // The selection plus whether it is the default: a restore that changes the lists but not the
                    // selection (a hand-listed hard-coded game) still counts as a change.
                    return s.SlipAngle?.SourceUnits == null || string.IsNullOrEmpty(gameId) ? null
                        : (object)new SlipAngleUnitState(SlipAngleSelectedUnit(gameId, s.SlipAngle.SourceUnits),
                            SlipAngleUnitIsDefault(gameId, s.SlipAngle.SourceUnits));
                case SettingField.SlipAngleAngleSource: return s.SlipAngle?.AngleSource;
                case SettingField.IntegrateSlipAngle: return s.GForce?.IntegrateSlipAngle;
                case SettingField.LockShakeItLegacyAlgorithm: return s.Lock?.ShakeItAlgorithm?.UseLegacyIracingAlgorithm;
                case SettingField.LockShakeItSensibility: return s.Lock?.ShakeItAlgorithm?.LockSensibility;
                case SettingField.LockShakeItLockOnly: return s.Lock?.ShakeItAlgorithm?.LockOnly;
                case SettingField.SlipShakeItLegacyAlgorithm: return s.Slip?.ShakeItAlgorithm?.UseLegacyIracingAlgorithm;
                case SettingField.SlipShakeItMuteWhileLockActive: return s.Slip?.ShakeItAlgorithm?.MuteWhileLockActive;
                default: return null;
            }
        }

        private static KeyDataTriple ReadValues(WheelChannelSettings ch, bool isLock, string gameId)
        {
            if (ch?.KeyDataPoints == null) return null;
            return ch.KeyDataPoints.TryGetManual(gameId, KeyDataPanelModel.Identity(ch, isLock), out double a, out double b, out double c)
                ? new KeyDataTriple(a, b, c) : null;
        }

        private static void WriteValues(WheelChannelSettings ch, bool isLock, object value, string gameId)
        {
            if (ch?.KeyDataPoints == null || !(value is KeyDataTriple t)) return;
            KeyDataPanelModel.TryCommitManual(ch.KeyDataPoints, gameId, KeyDataPanelModel.Identity(ch, isLock),
                t.SMax, t.S90, t.S75, ch.NormalizePattern == NormalizePattern.MaxGripOnly);
        }

        private static void Write(QAdvanceFeedbackSettings s, SettingField field, object value, string gameId, SourceNativeness slipAngleTier = SourceNativeness.Unknown)
        {
            switch (field)
            {
                case SettingField.LockKeyDataAuto: s.Lock?.KeyDataPoints?.SetAutoGenerate(KeyDataPanelModel.Identity(s.Lock, true), (bool)value); break;
                case SettingField.SlipKeyDataAuto: s.Slip?.KeyDataPoints?.SetAutoGenerate(KeyDataPanelModel.Identity(s.Slip, false), (bool)value); break;
                case SettingField.LockNormalizePattern: if (s.Lock != null) s.Lock.NormalizePattern = (NormalizePattern)value; break;
                case SettingField.SlipNormalizePattern: if (s.Slip != null) s.Slip.NormalizePattern = (NormalizePattern)value; break;
                case SettingField.LockKeyDataValues: WriteValues(s.Lock, true, value, gameId); break;
                case SettingField.SlipKeyDataValues: WriteValues(s.Slip, false, value, gameId); break;
                case SettingField.LockSourceMode: SwitchSource(s, s.Lock, (SourceMode)value, isLock: true); break;
                case SettingField.SlipSourceMode: SwitchSource(s, s.Slip, (SourceMode)value, isLock: false); break;
                case SettingField.AccelMaxMode: s.GForce.AccelMaxMode = (GMaxMode)value; break;
                case SettingField.DecelMaxMode: s.GForce.DecelMaxMode = (GMaxMode)value; break;
                case SettingField.LatMaxMode: s.GForce.LatMaxMode = (GMaxMode)value; break;
                case SettingField.FixedAccelMaxG: s.GForce.FixedAccelMaxG = Convert.ToDouble(value); break;
                case SettingField.FixedDecelMaxG: s.GForce.FixedDecelMaxG = Convert.ToDouble(value); break;
                case SettingField.FixedLatMaxG: s.GForce.FixedLatMaxG = Convert.ToDouble(value); break;
                case SettingField.LateralDirection: s.GForce.LateralDirection = (LateralDirectionMode)value; break;
                case SettingField.IntegrateWheelLockAndSlip: s.GForce.IntegrateWheelLockAndSlip = (bool)value; break;
                case SettingField.SlipAngleTriggerBy: if (s.SlipAngle?.Gate != null) s.SlipAngle.Gate.TriggerBy = (TriggerBy)value; break;
                case SettingField.SlipAngleBrakeThreshold: if (s.SlipAngle?.Gate != null) s.SlipAngle.Gate.BrakeThreshold = ClampPercent(value); break;
                case SettingField.SlipAngleThrottleThreshold: if (s.SlipAngle?.Gate != null) s.SlipAngle.Gate.ThrottleThreshold = ClampPercent(value); break;
                case SettingField.SlipAngleKeyDataAuto:
                    s.SlipAngle?.KeyData?.SetAutoGenerate(SlipAngleIdentity(s.SlipAngle, slipAngleTier, gameId), (bool)value);
                    break;
                case SettingField.SlipAngleKeyDataValues: WriteSlipAngleValues(s.SlipAngle, value, gameId, slipAngleTier); break;
                case SettingField.SlipAngleSourceUnit:
                    if (s.SlipAngle != null)
                    {
                        s.SlipAngle.EnsureSourceUnits();
                        ApplySlipAngleUnitChoice(s.SlipAngle.SourceUnits, gameId, value as SlipAngleUnitChoice?);
                    }
                    break;
                case SettingField.SlipAngleAngleSource: if (s.SlipAngle != null) s.SlipAngle.AngleSource = (SlipAngleAngleSource)value; break;
                case SettingField.IntegrateSlipAngle: if (s.GForce != null) s.GForce.IntegrateSlipAngle = (bool)value; break;
                case SettingField.ClutchApply: if (s.Clutch != null) s.Clutch.Apply = (ClutchApplyDirection)value; break;
                case SettingField.ClutchBiteMode: if (s.Clutch != null) s.Clutch.Mode = (ClutchBiteMode)value; break;
                case SettingField.ClutchBitePoint: if (s.Clutch != null) s.Clutch.BitePoint = Convert.ToDouble(value); break;
                case SettingField.ClutchFadedRange: if (s.Clutch != null) s.Clutch.FadedRange = Convert.ToDouble(value); break;
                case SettingField.ClutchGamma: if (s.Clutch != null) s.Clutch.Gamma = Convert.ToDouble(value); break;
                case SettingField.ClutchAxis: if (s.Clutch != null) s.Clutch.FallbackAxis = string.IsNullOrWhiteSpace(value as string) ? null : (string)value; break;
                case SettingField.ClutchAxisInvert: if (s.Clutch != null) s.Clutch.InvertAxis = (bool)value; break;
                case SettingField.ClutchLearning:
                    if (s.Clutch != null) (s.Clutch.Learning ?? (s.Clutch.Learning = new ClutchLearningSettings())).Apply((ClutchLearningParameters)value);
                    break;
                case SettingField.LockShakeItLegacyAlgorithm: if (s.Lock != null) AlgorithmOf(s.Lock).UseLegacyIracingAlgorithm = (bool)value; break;
                case SettingField.LockShakeItSensibility: if (s.Lock != null) AlgorithmOf(s.Lock).LockSensibility = Convert.ToDouble(value); break;
                case SettingField.LockShakeItLockOnly: if (s.Lock != null) AlgorithmOf(s.Lock).LockOnly = (bool)value; break;
                case SettingField.SlipShakeItLegacyAlgorithm: if (s.Slip != null) AlgorithmOf(s.Slip).UseLegacyIracingAlgorithm = (bool)value; break;
                case SettingField.SlipShakeItMuteWhileLockActive: if (s.Slip != null) AlgorithmOf(s.Slip).MuteWhileLockActive = (bool)value; break;
            }
        }

        private static ShakeItAlgorithmSettings AlgorithmOf(WheelChannelSettings channel) =>
            channel.ShakeItAlgorithm ?? (channel.ShakeItAlgorithm = new ShakeItAlgorithmSettings());

        /// <summary>THE ONE SOURCE SWITCH, used by the hub (a ShakeIt panel) and the settings page's
        /// Source combo alike, so both produce identical settings: entering Custom first seeds its
        /// cold-start reference from the source being left, then the mode switch archives the current
        /// sources and restores (or presets) the new mode's. A no-op for the mode already selected.</summary>
        public static void SwitchSource(QAdvanceFeedbackSettings s, WheelChannelSettings channel, SourceMode mode, bool isLock)
        {
            if (channel == null || channel.SourceMode == mode) return;
            if (mode == SourceMode.Custom && s.KeyDataPointDefaults != null)
            {
                string identity = SourceIdentity.Compute(
                    channel.SourceFrontLeft, channel.ScriptTypeFrontLeft.ToString(),
                    channel.SourceFrontRight, channel.ScriptTypeFrontRight.ToString(),
                    channel.SourceRearLeft, channel.ScriptTypeRearLeft.ToString(),
                    channel.SourceRearRight, channel.ScriptTypeRearRight.ToString());
                s.KeyDataPointDefaults.SeedCustomFrom(KeyDataPointSettings.ClassifyExact(identity, isLock), isLock);
            }
            channel.SwitchSourceMode(mode, isLock);
        }

        private static bool SameJson(object a, object b) =>
            string.Equals(JsonConvert.SerializeObject(a), JsonConvert.SerializeObject(b), StringComparison.Ordinal);

        private void Raise(IReadOnlyCollection<SettingField> fields, SettingsOrigin origin)
        {
            EventHandler<SettingsChangedEventArgs> handlers = Changed;
            if (handlers == null) return;
            var args = new SettingsChangedEventArgs(fields, origin);
            foreach (EventHandler<SettingsChangedEventArgs> h in handlers.GetInvocationList())
            {
                try { h(this, args); }
                catch (Exception) { /* one broken listener (e.g. a closed panel) must not block the rest */ }
            }
        }
    }
}

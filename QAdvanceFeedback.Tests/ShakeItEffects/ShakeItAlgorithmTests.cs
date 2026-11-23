using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests.ShakeItEffects
{
    /// <summary>
    /// The ShakeIt algorithm settings (docs\shakeit-source-design.md §6): filter mapping from our pedal
    /// thresholds, the learning-key fingerprint, the mute emulation, settings load/normalise and the
    /// SettingsHub fields.
    /// </summary>
    public class ShakeItAlgorithmTests
    {
        // ---- Filter mapping -------------------------------------------------------------------------

        [Theory]
        [InlineData(20.0, 20)]      // shipped default
        [InlineData(0.0, 0)]
        [InlineData(100.0, 100)]
        [InlineData(20.5, 21)]      // midpoint rounds away from zero (banker's would give 20)
        [InlineData(21.5, 22)]
        [InlineData(20.4, 20)]
        [InlineData(20.6, 21)]
        [InlineData(0.5, 1)]
        [InlineData(99.5, 100)]
        [InlineData(150.0, 100)]    // clamped
        [InlineData(-5.0, 0)]
        [InlineData(double.NaN, 0)]
        [InlineData(double.PositiveInfinity, 0)]
        public void Lock_brake_filter_is_the_rounded_clamped_threshold(double threshold, int expected)
        {
            Assert.Equal(expected, ShakeItFilterMapping.LockBrakeFilter(threshold));
            Assert.Equal(expected, ShakeItFilterMapping.ToFilterPercent(threshold));
        }

        [Theory]
        //           brake  throttle useBrake brakeF useThrottle throttleF
        [InlineData(100.0, 40.0, false, 100, true, 40)]   // shipped defaults: brake path off, throttle 40
        [InlineData(20.0, 40.0, true, 20, true, 40)]
        [InlineData(99.0, 10.0, true, 99, true, 10)]      // the old export profile's values
        [InlineData(99.5, 40.0, true, 100, true, 40)]     // < 100 is on; rounds to 100 (brake > 100: never)
        [InlineData(0.0, 40.0, true, 0, true, 40)]
        [InlineData(100.0, 0.0, false, 100, false, 0)]    // throttle 0 -> off; both off = always allowed
        [InlineData(20.0, 0.0, false, 20, false, 0)]      // throttle 0 is "always" for us, so brake must not gate
        [InlineData(100.0, 100.0, false, 100, true, 100)]
        [InlineData(20.5, 10.5, true, 21, true, 11)]      // fractional .5 rounding
        [InlineData(20.0, 0.4, true, 20, true, 0)]        // > 0 is on even when it rounds to 0
        public void Slip_filters_follow_our_thresholds(double brake, double throttle,
            bool useBrake, int brakeFilter, bool useThrottle, int throttleFilter)
        {
            ShakeItAlgorithmSnapshot snap = Snapshot(slipBrake: brake, slipThrottle: throttle);

            Assert.Equal(useBrake, snap.SlipUseBrakeFilter);
            Assert.Equal(brakeFilter, snap.SlipBrakeFilter);
            Assert.Equal(useThrottle, snap.SlipUseThrottleFilter);
            Assert.Equal(throttleFilter, snap.SlipThrottleFilter);
        }

        [Fact]
        public void Defaults_snapshot_matches_the_shipped_settings_and_thresholds()
        {
            ShakeItAlgorithmSnapshot d = ShakeItAlgorithmSnapshot.Defaults;

            Assert.True(d.LockUseLegacyIracingAlgorithm);
            Assert.Equal(50.0, d.LockSensibility);
            Assert.True(d.LockOnly);
            Assert.Equal(20, d.LockBrakeFilter);

            Assert.True(d.SlipUseLegacyIracingAlgorithm);
            Assert.False(d.SlipMuteWhileLockActive);
            Assert.False(d.SlipUseBrakeFilter);
            Assert.Equal(100, d.SlipBrakeFilter);
            Assert.True(d.SlipUseThrottleFilter);
            Assert.Equal(40, d.SlipThrottleFilter);

            Assert.Equal(string.Empty, d.LockFingerprint);
            Assert.Equal(string.Empty, d.SlipFingerprint);
        }

        [Fact]
        public void Defaults_snapshot_equals_one_built_from_fresh_settings()
        {
            var s = new QAdvanceFeedbackSettings();
            ShakeItAlgorithmSnapshot snap = ShakeItAlgorithmSettings.Snapshot(s.Lock, s.Slip, null);

            Assert.Equal(ShakeItAlgorithmSnapshot.Defaults.Inputs, snap.Inputs);
            Assert.Same(ShakeItAlgorithmSnapshot.Defaults, ShakeItAlgorithmSettings.Snapshot(s.Lock, s.Slip, ShakeItAlgorithmSnapshot.Defaults));
        }

        [Fact]
        public void Snapshot_reads_each_channels_own_part_and_thresholds()
        {
            var s = new QAdvanceFeedbackSettings();
            s.Lock.ShakeItAlgorithm.UseLegacyIracingAlgorithm = false;
            s.Lock.ShakeItAlgorithm.LockSensibility = 65;
            s.Lock.ShakeItAlgorithm.LockOnly = false;
            s.Lock.ShakeItAlgorithm.MuteWhileLockActive = true;      // not Lock's: ignored
            s.Slip.ShakeItAlgorithm.LockSensibility = 10;            // not Slip's: ignored
            s.Slip.ShakeItAlgorithm.MuteWhileLockActive = true;
            s.Lock.BrakeThresholdPercent = 30;
            s.Slip.BrakeThresholdPercent = 25;
            s.Slip.ThrottleThresholdPercent = 15;

            ShakeItAlgorithmSnapshot snap = ShakeItAlgorithmSettings.Snapshot(s.Lock, s.Slip, null);

            Assert.False(snap.LockUseLegacyIracingAlgorithm);
            Assert.Equal(65.0, snap.LockSensibility);
            Assert.False(snap.LockOnly);
            Assert.Equal(30, snap.LockBrakeFilter);
            Assert.True(snap.SlipUseLegacyIracingAlgorithm);
            Assert.True(snap.SlipMuteWhileLockActive);
            Assert.True(snap.SlipUseBrakeFilter);
            Assert.Equal(25, snap.SlipBrakeFilter);
            Assert.Equal(15, snap.SlipThrottleFilter);
        }

        [Fact]
        public void Snapshot_with_null_channels_is_the_defaults()
        {
            Assert.Equal(ShakeItAlgorithmSnapshot.Defaults.Inputs, ShakeItAlgorithmSettings.Snapshot(null, null, null).Inputs);
            var ch = new WheelChannelSettings { ShakeItAlgorithm = null };
            Assert.True(ShakeItAlgorithmSettings.Snapshot(ch, ch, null).LockOnly);
        }

        [Fact]
        public void Reuse_keeps_the_instance_until_an_input_changes()
        {
            ShakeItAlgorithmSnapshot a = Snapshot();
            Assert.Same(a, ShakeItAlgorithmSnapshot.Reuse(a, a.Inputs));

            ShakeItAlgorithmSnapshot b = ShakeItAlgorithmSnapshot.Reuse(a, Snapshot(lockBrake: 21).Inputs);
            Assert.NotSame(a, b);
            Assert.Equal(21, b.LockBrakeFilter);
        }

        // ---- Fingerprint ----------------------------------------------------------------------------

        [Fact]
        public void Fingerprint_is_empty_at_the_defaults()
        {
            Assert.Equal(string.Empty, new ShakeItAlgorithmSettings().Fingerprint(true));
            Assert.Equal(string.Empty, new ShakeItAlgorithmSettings().Fingerprint(false));
            Assert.Equal(string.Empty, ShakeItAlgorithmFingerprint.ForLock(true, 50.0, true));
            Assert.Equal(string.Empty, ShakeItAlgorithmFingerprint.ForSlip(true, false));
        }

        [Fact]
        public void Every_changed_lock_setting_gives_a_distinct_fingerprint()
        {
            var prints = new List<string>
            {
                ShakeItAlgorithmFingerprint.ForLock(true, 50, true),
                ShakeItAlgorithmFingerprint.ForLock(false, 50, true),
                ShakeItAlgorithmFingerprint.ForLock(true, 55, true),
                ShakeItAlgorithmFingerprint.ForLock(true, 60, true),
                ShakeItAlgorithmFingerprint.ForLock(true, 0, true),
                ShakeItAlgorithmFingerprint.ForLock(true, 50, false),
                ShakeItAlgorithmFingerprint.ForLock(false, 55, false)
            };
            Assert.Equal(prints.Count, new HashSet<string>(prints).Count);
            for (int i = 1; i < prints.Count; i++) Assert.NotEqual(string.Empty, prints[i]);
        }

        [Fact]
        public void Every_changed_slip_setting_gives_a_distinct_fingerprint()
        {
            var prints = new List<string>
            {
                ShakeItAlgorithmFingerprint.ForSlip(true, false),
                ShakeItAlgorithmFingerprint.ForSlip(false, false),
                ShakeItAlgorithmFingerprint.ForSlip(true, true),
                ShakeItAlgorithmFingerprint.ForSlip(false, true)
            };
            Assert.Equal(prints.Count, new HashSet<string>(prints).Count);
        }

        [Fact]
        public void Fingerprint_is_stable()
        {
            Assert.Equal(ShakeItAlgorithmFingerprint.ForLock(false, 55, false), ShakeItAlgorithmFingerprint.ForLock(false, 55, false));
            Assert.Equal("#alg=legacy:0,sens:55,lockOnly:0", ShakeItAlgorithmFingerprint.ForLock(false, 55, false));
            Assert.Equal("#alg=legacy:1,mute:1", ShakeItAlgorithmFingerprint.ForSlip(true, true));
        }

        [Fact]
        public void Each_channel_fingerprint_uses_only_its_own_settings()
        {
            var a = new ShakeItAlgorithmSettings { MuteWhileLockActive = true };
            Assert.Equal(string.Empty, a.Fingerprint(true));      // mute is Slip's only
            Assert.NotEqual(string.Empty, a.Fingerprint(false));

            var b = new ShakeItAlgorithmSettings { LockSensibility = 70, LockOnly = false };
            Assert.NotEqual(string.Empty, b.Fingerprint(true));
            Assert.Equal(string.Empty, b.Fingerprint(false));     // sensibility / lock-only are Lock's only
        }

        [Fact]
        public void Thresholds_do_not_change_the_fingerprint()
        {
            ShakeItAlgorithmSnapshot a = Snapshot(lockLegacy: false, slipMute: true);
            ShakeItAlgorithmSnapshot b = Snapshot(lockLegacy: false, slipMute: true, lockBrake: 5, slipBrake: 30, slipThrottle: 0);

            Assert.NotEqual(a.LockBrakeFilter, b.LockBrakeFilter);
            Assert.Equal(a.LockFingerprint, b.LockFingerprint);
            Assert.Equal(a.SlipFingerprint, b.SlipFingerprint);

            Assert.Equal(string.Empty, Snapshot(lockBrake: 80, slipBrake: 10, slipThrottle: 90).LockFingerprint);
            Assert.Equal(string.Empty, Snapshot(lockBrake: 80, slipBrake: 10, slipThrottle: 90).SlipFingerprint);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ShakeIt_identity_is_todays_string_at_the_defaults(bool isLock)
        {
            string today = ShakeItSourceResolution.ShakeItIdentity(isLock);
            Assert.Equal(today, ShakeItSourceResolution.ShakeItIdentity(isLock, ShakeItAlgorithmSnapshot.Defaults.Fingerprint(isLock)));
            Assert.Equal(today, ShakeItSourceResolution.ShakeItIdentity(isLock, new ShakeItAlgorithmSettings().Fingerprint(isLock)));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_fingerprinted_identity_is_a_new_key_that_still_reads_as_the_ShakeIt_source(bool isLock)
        {
            string fp = isLock ? ShakeItAlgorithmFingerprint.ForLock(false, 70, false) : ShakeItAlgorithmFingerprint.ForSlip(false, true);
            string identity = ShakeItSourceResolution.ShakeItIdentity(isLock, fp);

            Assert.NotEqual(ShakeItSourceResolution.ShakeItIdentity(isLock), identity);
            Assert.DoesNotContain("|#|", identity);
            Assert.Equal(4, identity.Split('~').Length);
            Assert.Equal(KnownFeedbackSource.ShakeItMotorsExport, KnownSourceColdStartReference.Classify(identity, isLock));
        }

        [Fact]
        public void Panel_identity_appends_the_fingerprint_for_a_ShakeIt_channel_only()
        {
            var ch = new WheelChannelSettings();
            ch.ApplyMotorsExportDefaults(true);
            Assert.Equal(KeyDataPanelModel.Identity(ch), KeyDataPanelModel.Identity(ch, true));
            Assert.Equal(ShakeItSourceResolution.ShakeItIdentity(true), KeyDataPanelModel.Identity(ch, true));

            ch.ShakeItAlgorithm.LockSensibility = 60;
            Assert.Equal(ShakeItSourceResolution.ShakeItIdentity(true, ch.ShakeItAlgorithm.Fingerprint(true)),
                KeyDataPanelModel.Identity(ch, true));

            ch.ResetSourcesToDefault(true);   // Raw: the algorithm settings do not key it
            Assert.Equal(KeyDataPanelModel.Identity(ch), KeyDataPanelModel.Identity(ch, true));
        }

        // ---- Shipped defaults ignore the fingerprint (fix round 1) -----------------------------------

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Fingerprinted_ShakeIt_identity_classifies_as_ShakeIt_for_shipped_defaults(bool isLock)
        {
            string fp = isLock ? ShakeItAlgorithmFingerprint.ForLock(true, 55, true) : ShakeItAlgorithmFingerprint.ForSlip(true, true);
            string engineKey = ShakeItSourceResolution.ShakeItIdentity(isLock, fp);
            string baseKey = ShakeItSourceResolution.ShakeItIdentity(isLock);

            Assert.Equal(baseKey, ShakeItAlgorithmFingerprint.BaseIdentity(engineKey));
            Assert.Same(baseKey, ShakeItAlgorithmFingerprint.BaseIdentity(baseKey));
            Assert.Equal(KnownFeedbackSource.ShakeItMotorsExport, KeyDataPointSettings.ClassifyExact(engineKey, isLock));

            var defaults = KeyDataPointDefaults.CreateShipped();
            Assert.True(KeyDataPointSettings.TryResolveShippedDefaults(baseKey, isLock, defaults, out double a1, out double b1, out double c1));
            Assert.True(KeyDataPointSettings.TryResolveShippedDefaults(engineKey, isLock, defaults, out double a2, out double b2, out double c2));
            Assert.Equal((a1, b1, c1), (a2, b2, c2));
        }

        [Fact]
        public void Only_a_ShakeIt_base_is_accepted_after_stripping()
        {
            string rawWithMarker = RawSourceFallback.RawIdentity(true) + ShakeItAlgorithmFingerprint.ForLock(false, 50, true);
            Assert.Equal(KnownFeedbackSource.Unknown, KeyDataPointSettings.ClassifyExact(rawWithMarker, true));
            Assert.Equal(KnownFeedbackSource.QAdvanceFeedbackRaw, KeyDataPointSettings.ClassifyExact(RawSourceFallback.RawIdentity(true), true));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Panel_and_engine_agree_on_shipped_key_data_at_a_non_default_setting(bool isLock)
        {
            var ch = isLock ? WheelChannelSettings.CreateLockDefaults() : WheelChannelSettings.CreateSlipDefaults();
            ch.ApplyMotorsExportDefaults(isLock);
            if (isLock) ch.ShakeItAlgorithm.LockSensibility = 55; else ch.ShakeItAlgorithm.MuteWhileLockActive = true;

            string engineKey = ShakeItSourceResolution.ShakeItIdentity(isLock, ch.ShakeItAlgorithm.Fingerprint(isLock));
            string panelKey = KeyDataPanelModel.Identity(ch, isLock);
            Assert.Equal(engineKey, panelKey);
            Assert.NotEqual(ShakeItSourceResolution.ShakeItIdentity(isLock), engineKey);

            ch.KeyDataPoints.SetAutoGenerate(engineKey, false);   // Manual, nothing typed yet
            var defaults = KeyDataPointDefaults.CreateShipped();

            // What the engine's ResolveManualAnchors falls back to (same call, same key)...
            Assert.True(KeyDataPointSettings.TryResolveShippedDefaults(engineKey, isLock, defaults, out double e1, out double e2, out double e3));
            // ...is what the panel shows.
            KeyDataView view = KeyDataPanelModel.Resolve(ch, isLock, "game", null, defaults);
            Assert.False(view.Auto);
            Assert.Equal(e1, view.SMax);
            Assert.Equal(e2, view.S90);
            Assert.Equal(e3, view.S75);
        }

        [Fact]
        public void MatchesDefaults_sees_a_changed_slot_under_the_fingerprinted_key()
        {
            var ch = WheelChannelSettings.CreateLockDefaults();
            ch.ApplyMotorsExportDefaults(true);
            ch.ShakeItAlgorithm.LockSensibility = 55;
            string key = KeyDataPanelModel.Identity(ch, true);
            var defaults = KeyDataPointDefaults.CreateShipped();

            Assert.True(ch.KeyDataPoints.MatchesDefaults(key, true, defaults));
            ch.KeyDataPoints.SetAutoGenerate(key, false);
            Assert.False(ch.KeyDataPoints.MatchesDefaults(key, true, defaults));   // restore is offered
        }

        [Theory]
        [InlineData(50.0001, 50.0)]
        [InlineData(52.4, 50.0)]
        [InlineData(52.5, 55.0)]
        [InlineData(2.5, 5.0)]
        [InlineData(98.0, 100.0)]
        public void Sensibility_snaps_to_ShakeIts_step_of_5(double value, double expected)
        {
            Assert.Equal(expected, new ShakeItAlgorithmSettings { LockSensibility = value }.LockSensibility);
            Assert.Equal(expected, Snapshot(lockSensibility: value).LockSensibility);
        }

        [Fact]
        public void A_hair_off_the_default_tick_is_still_the_default_key()
        {
            Assert.Equal(string.Empty, ShakeItAlgorithmFingerprint.ForLock(true, 50.0001, true));
            Assert.Equal(string.Empty, new ShakeItAlgorithmSettings { LockSensibility = 49.9 }.Fingerprint(true));
            Assert.Same(ShakeItAlgorithmSnapshot.Defaults,
                ShakeItAlgorithmSnapshot.Reuse(ShakeItAlgorithmSnapshot.Defaults, Snapshot(lockSensibility: 50.0001).Inputs));
        }

        // ---- Mute emulation -------------------------------------------------------------------------

        [Fact]
        public void Mute_off_never_mutes()
        {
            var slip = new Corners(10, 20, 30, 40);
            Assert.False(ShakeItMuteEmulation.IsMuted(false, Corners.Uniform(80)));
            Assert.Equal(slip, ShakeItMuteEmulation.Apply(false, Corners.Uniform(80), slip));
        }

        [Fact]
        public void Mute_on_passes_slip_while_lock_is_inactive()
        {
            var slip = new Corners(10, 20, 30, 40);
            Assert.Equal(slip, ShakeItMuteEmulation.Apply(true, Corners.Zero, slip));
            Assert.Equal(slip, ShakeItMuteEmulation.Apply(true, Corners.Uniform(double.NaN), slip));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void Mute_on_zeroes_all_four_slip_wheels_when_any_lock_wheel_is_active(int activeWheel)
        {
            var values = new double[4];
            values[activeWheel] = 0.01;
            var lockValues = new Corners(values[0], values[1], values[2], values[3]);

            Assert.True(ShakeItMuteEmulation.IsMuted(true, lockValues));
            Assert.Equal(Corners.Zero, ShakeItMuteEmulation.Apply(true, lockValues, new Corners(10, 20, 30, 40)));
        }

        // ---- Settings load / normalise --------------------------------------------------------------

        [Fact]
        public void Old_config_without_the_block_loads_the_defaults()
        {
            string json = JsonConvert.SerializeObject(new QAdvanceFeedbackSettings());
            JObject o = JObject.Parse(json);
            ((JObject)o["Lock"]).Remove("ShakeItAlgorithm");
            ((JObject)o["Slip"]).Remove("ShakeItAlgorithm");

            QAdvanceFeedbackSettings loaded = LoadThroughConfigStore(o.ToString());

            AssertDefaults(loaded.Lock.ShakeItAlgorithm);
            AssertDefaults(loaded.Slip.ShakeItAlgorithm);
        }

        [Fact]
        public void A_null_block_is_repaired_to_the_defaults()
        {
            JObject o = JObject.Parse(JsonConvert.SerializeObject(new QAdvanceFeedbackSettings()));
            o["Lock"]["ShakeItAlgorithm"] = JValue.CreateNull();
            o["Slip"]["ShakeItAlgorithm"] = JValue.CreateNull();

            QAdvanceFeedbackSettings loaded = LoadThroughConfigStore(o.ToString());

            AssertDefaults(loaded.Lock.ShakeItAlgorithm);
            AssertDefaults(loaded.Slip.ShakeItAlgorithm);
        }

        [Theory]
        [InlineData(150.0, 100.0)]
        [InlineData(-3.0, 0.0)]
        [InlineData(37.5, 40.0)]
        [InlineData(36.0, 35.0)]
        public void Out_of_range_sensibility_is_clamped_on_load(double stored, double expected)
        {
            JObject o = JObject.Parse(JsonConvert.SerializeObject(new QAdvanceFeedbackSettings()));
            o["Lock"]["ShakeItAlgorithm"]["LockSensibility"] = stored;

            Assert.Equal(expected, LoadThroughConfigStore(o.ToString()).Lock.ShakeItAlgorithm.LockSensibility);
        }

        [Fact]
        public void Non_finite_sensibility_is_the_default()
        {
            var a = new ShakeItAlgorithmSettings { LockSensibility = double.NaN };
            Assert.Equal(50.0, a.LockSensibility);
            a.LockSensibility = double.PositiveInfinity;
            Assert.Equal(50.0, a.LockSensibility);
        }

        [Fact]
        public void Settings_round_trip_through_json()
        {
            var s = new QAdvanceFeedbackSettings();
            s.Lock.ShakeItAlgorithm.UseLegacyIracingAlgorithm = false;
            s.Lock.ShakeItAlgorithm.LockSensibility = 35;
            s.Lock.ShakeItAlgorithm.LockOnly = false;
            s.Slip.ShakeItAlgorithm.MuteWhileLockActive = true;

            QAdvanceFeedbackSettings back = LoadThroughConfigStore(JsonConvert.SerializeObject(s));

            Assert.False(back.Lock.ShakeItAlgorithm.UseLegacyIracingAlgorithm);
            Assert.Equal(35.0, back.Lock.ShakeItAlgorithm.LockSensibility);
            Assert.False(back.Lock.ShakeItAlgorithm.LockOnly);
            Assert.True(back.Slip.ShakeItAlgorithm.MuteWhileLockActive);
            Assert.True(back.Slip.ShakeItAlgorithm.UseLegacyIracingAlgorithm);
        }

        [Fact]
        public void Restore_defaults_resets_the_block()
        {
            var s = new QAdvanceFeedbackSettings();
            s.Lock.ShakeItAlgorithm.LockSensibility = 80;
            s.Slip.ShakeItAlgorithm.MuteWhileLockActive = true;
            s.RestoreDefaults();
            AssertDefaults(s.Lock.ShakeItAlgorithm);
            AssertDefaults(s.Slip.ShakeItAlgorithm);
        }

        // ---- SettingsHub ----------------------------------------------------------------------------

        public static IEnumerable<object[]> HubFields() => new[]
        {
            new object[] { SettingField.LockShakeItLegacyAlgorithm, false },
            new object[] { SettingField.LockShakeItSensibility, 65.0 },
            new object[] { SettingField.LockShakeItLockOnly, false },
            new object[] { SettingField.SlipShakeItLegacyAlgorithm, false },
            new object[] { SettingField.SlipShakeItMuteWhileLockActive, true }
        };

        [Theory]
        [MemberData(nameof(HubFields))]
        public void Hub_field_round_trips_saves_once_and_raises_only_itself(SettingField field, object value)
        {
            var s = new QAdvanceFeedbackSettings();
            int saves = 0;
            var hub = new SettingsHub(() => s, _ => saves++);
            SettingsChangedEventArgs got = null;
            hub.Changed += (_, e) => got = e;

            Assert.True(SettingsHub.IsEditable(field));
            Assert.True(hub.Set(field, value, SettingsOrigin.ShakeItPanel));

            Assert.Equal(value, SettingsHub.Read(s, field));
            Assert.Equal(1, saves);
            Assert.Equal(new[] { field }, got.Fields);
            Assert.False(hub.Set(field, value, SettingsOrigin.ShakeItPanel));   // unchanged: no-op
            Assert.Equal(1, saves);
        }

        [Fact]
        public void Hub_fields_write_the_right_channel()
        {
            var s = new QAdvanceFeedbackSettings();
            var hub = new SettingsHub(() => s, _ => { });

            hub.Set(SettingField.LockShakeItLegacyAlgorithm, false, SettingsOrigin.PluginPage);
            Assert.False(s.Lock.ShakeItAlgorithm.UseLegacyIracingAlgorithm);
            Assert.True(s.Slip.ShakeItAlgorithm.UseLegacyIracingAlgorithm);

            hub.Set(SettingField.SlipShakeItLegacyAlgorithm, false, SettingsOrigin.PluginPage);
            Assert.False(s.Slip.ShakeItAlgorithm.UseLegacyIracingAlgorithm);

            hub.Set(SettingField.SlipShakeItMuteWhileLockActive, true, SettingsOrigin.PluginPage);
            Assert.True(s.Slip.ShakeItAlgorithm.MuteWhileLockActive);
            Assert.False(s.Lock.ShakeItAlgorithm.MuteWhileLockActive);
        }

        [Fact]
        public void Hub_sensibility_is_clamped_and_a_clamped_back_write_is_a_noop()
        {
            var s = new QAdvanceFeedbackSettings();
            int saves = 0;
            var hub = new SettingsHub(() => s, _ => saves++);

            Assert.True(hub.Set(SettingField.LockShakeItSensibility, 150.0, SettingsOrigin.ShakeItPanel));
            Assert.Equal(100.0, s.Lock.ShakeItAlgorithm.LockSensibility);
            Assert.False(hub.Set(SettingField.LockShakeItSensibility, 120.0, SettingsOrigin.ShakeItPanel));
            Assert.Equal(1, saves);
        }

        [Fact]
        public void Hub_recreates_a_missing_block()
        {
            var s = new QAdvanceFeedbackSettings();
            s.Lock.ShakeItAlgorithm = null;
            var hub = new SettingsHub(() => s, _ => { });

            Assert.True(hub.Set(SettingField.LockShakeItLockOnly, false, SettingsOrigin.ShakeItPanel));
            Assert.False(s.Lock.ShakeItAlgorithm.LockOnly);
        }

        [Theory]
        [MemberData(nameof(HubFields))]
        public void Diff_reports_each_algorithm_field(SettingField field, object value)
        {
            var before = new QAdvanceFeedbackSettings();
            QAdvanceFeedbackSettings after = Clone(before);
            new SettingsHub(() => after, _ => { }).Set(field, value, SettingsOrigin.PluginPage);

            Assert.Equal(new[] { field }, SettingsHub.Diff(before, after));
            Assert.Empty(SettingsHub.Diff(before, Clone(before)));
        }

        [Fact]
        public void File_change_adopts_the_algorithm_fields()
        {
            var s = new QAdvanceFeedbackSettings();
            var hub = new SettingsHub(() => s, _ => { });
            SettingsChangedEventArgs got = null;
            hub.Changed += (_, e) => got = e;

            QAdvanceFeedbackSettings disk = Clone(s);
            disk.Lock.ShakeItAlgorithm.LockSensibility = 25;
            disk.Slip.ShakeItAlgorithm.MuteWhileLockActive = true;
            hub.NotifyFileChanged(disk);

            Assert.Equal(25.0, s.Lock.ShakeItAlgorithm.LockSensibility);
            Assert.True(s.Slip.ShakeItAlgorithm.MuteWhileLockActive);
            Assert.Equal(SettingsOrigin.File, got.Origin);
            Assert.Contains(SettingField.LockShakeItSensibility, got.Fields);
            Assert.Contains(SettingField.SlipShakeItMuteWhileLockActive, got.Fields);
        }

        [Fact]
        public void Hub_key_data_follows_the_fingerprinted_key()
        {
            var s = new QAdvanceFeedbackSettings();
            s.Lock.ApplyMotorsExportDefaults(true);
            string today = KeyDataPanelModel.Identity(s.Lock);
            s.Lock.KeyDataPoints.SetAutoGenerate(today, false);
            Assert.Equal(false, SettingsHub.Read(s, SettingField.LockKeyDataAuto));

            s.Lock.ShakeItAlgorithm.LockOnly = false;   // new key: its own (default Auto) slot
            Assert.Equal(true, SettingsHub.Read(s, SettingField.LockKeyDataAuto));
        }

        // ---- Helpers --------------------------------------------------------------------------------

        private static ShakeItAlgorithmSnapshot Snapshot(
            bool lockLegacy = true, double lockSensibility = 50, bool lockOnly = true, double lockBrake = 20,
            bool slipLegacy = true, bool slipMute = false, double slipBrake = 100, double slipThrottle = 40) =>
            new ShakeItAlgorithmSnapshot(new ShakeItAlgorithmInputs(
                lockLegacy, lockSensibility, lockOnly, lockBrake, slipLegacy, slipMute, slipBrake, slipThrottle));

        private static void AssertDefaults(ShakeItAlgorithmSettings a)
        {
            Assert.NotNull(a);
            Assert.True(a.UseLegacyIracingAlgorithm);
            Assert.Equal(50.0, a.LockSensibility);
            Assert.True(a.LockOnly);
            Assert.False(a.MuteWhileLockActive);
        }

        private static QAdvanceFeedbackSettings Clone(QAdvanceFeedbackSettings s) =>
            JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(JsonConvert.SerializeObject(s));

        private static QAdvanceFeedbackSettings LoadThroughConfigStore(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), "qaf-shakeit-alg-" + Guid.NewGuid() + ".json");
            try
            {
                File.WriteAllText(path, json);
                return ConfigStore.Load(path);
            }
            finally
            {
                try { File.Delete(path); } catch { /* best effort */ }
            }
        }
    }
}

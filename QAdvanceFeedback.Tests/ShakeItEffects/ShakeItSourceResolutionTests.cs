using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests.ShakeItEffects
{
    /// <summary>
    /// Per-channel source resolution for the ShakeIt Built-in source (docs\shakeit-source-design.md §4-5):
    /// the built-in values only when Supported and fresh, otherwise Raw values AND the Raw identity, decided
    /// per channel; the ShakeIt identity string itself unchanged.
    /// </summary>
    public class ShakeItSourceResolutionTests
    {
        private static readonly ShakeItTapValues Tap =
            new ShakeItTapValues(new Corners(11, 12, 13, 14), new Corners(21, 22, 23, 24));

        private static double Raw(object state, int i) => 100 + i;

        private static double Configured(object state, int i) => 200 + i;

        private static double ConfiguredMustNotBeRead(object state, int i) =>
            throw new InvalidOperationException("ShakeIt mode must never read its configured (export) fields");

        /// <summary>Stands in for whatever the channel's stored rows produce: ShakeIt must never use it.</summary>
        private const string RowsIdentity = "rows-identity-must-not-be-used-by-ShakeIt";

        private static string ShakeItIdentity(bool isLock)
        {
            var channel = new WheelChannelSettings();
            channel.ApplyMotorsExportDefaults(isLock);
            return SourceIdentity.Compute(
                channel.SourceFrontLeft, channel.ScriptTypeFrontLeft.ToString(),
                channel.SourceFrontRight, channel.ScriptTypeFrontRight.ToString(),
                channel.SourceRearLeft, channel.ScriptTypeRearLeft.ToString(),
                channel.SourceRearRight, channel.ScriptTypeRearRight.ToString());
        }

        private static ChannelSourceResolution ResolveShakeIt(bool isLock, ShakeItSourceState state, bool tapFresh)
        {
            ChannelSourceRoute route = ShakeItSourceResolution.Route(isShakeItMode: true, viperBlocked: false, state, tapFresh);
            return ShakeItSourceResolution.Resolve<object>(route, isLock, tapFresh ? Tap : null, null,
                Raw, ConfiguredMustNotBeRead, RowsIdentity, RawSourceFallback.RawIdentity(isLock));
        }

        [Fact]
        public void ShakeIt_identity_string_is_byte_identical_to_the_legacy_export_names()
        {
            Assert.Equal(
                "Plain:ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft~" +
                "Plain:ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontRight~" +
                "Plain:ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearLeft~" +
                "Plain:ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearRight",
                ShakeItIdentity(true));
            Assert.Equal(
                "Plain:ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.FrontLeft~" +
                "Plain:ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.FrontRight~" +
                "Plain:ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.RearLeft~" +
                "Plain:ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.RearRight",
                ShakeItIdentity(false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Canonical_ShakeIt_identity_equals_the_preset_rows_identity_and_is_unchanged_without_a_fingerprint(bool isLock)
        {
            Assert.Equal(ShakeItIdentity(isLock), ShakeItSourceResolution.ShakeItIdentity(isLock));
            Assert.Equal(ShakeItIdentity(isLock), ShakeItSourceResolution.ShakeItIdentity(isLock, null));
            Assert.Equal(ShakeItIdentity(isLock), ShakeItSourceResolution.ShakeItIdentity(isLock, string.Empty));
            Assert.NotEqual(ShakeItIdentity(isLock), ShakeItSourceResolution.ShakeItIdentity(isLock, "|fp"));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Onboarding_Raw_rows_then_ShakeIt_picked_while_not_set_up_then_Supported_learns_under_todays_ShakeIt_key(bool isLock)
        {
            // Plugin Internal saved: Raw rows.
            var channel = new WheelChannelSettings();
            channel.ResetSourcesToDefault(isLock);
            string rawRowsIdentity = SourceIdentity.Compute(
                channel.SourceFrontLeft, "Plain", channel.SourceFrontRight, "Plain",
                channel.SourceRearLeft, "Plain", channel.SourceRearRight, "Plain");
            Assert.Equal(RawSourceFallback.RawIdentity(isLock), rawRowsIdentity);

            // ShakeIt picked while NotSetUp: the page shadows the rows to Raw and, on Apply, the rows are
            // not saved - SaveChannel calls KeepPresetSourcesWhileShadowed, then assigns the mode.
            channel.KeepPresetSourcesWhileShadowed(SourceMode.ShakeIt, isLock);
            channel.SourceMode = SourceMode.ShakeIt;
            Assert.Equal(ShakeItIdentity(isLock), SourceIdentity.Compute(
                channel.SourceFrontLeft, channel.ScriptTypeFrontLeft.ToString(),
                channel.SourceFrontRight, channel.ScriptTypeFrontRight.ToString(),
                channel.SourceRearLeft, channel.ScriptTypeRearLeft.ToString(),
                channel.SourceRearRight, channel.ScriptTypeRearRight.ToString()));

            // While still not set up: Raw values, Raw key.
            ChannelSourceResolution before = ResolveShakeIt(isLock, ShakeItSourceState.NotSetUp, tapFresh: false);
            Assert.Equal(RawSourceFallback.RawIdentity(isLock), before.Identity);
            Assert.Equal(new Corners(100, 101, 102, 103), before.Values);

            // Effect added, driving: tap values under today's ShakeIt key - even if the engine were handed
            // the stale Raw rows' identity as the configured identity (belt and braces).
            ChannelSourceRoute route = ShakeItSourceResolution.Route(channel.SourceMode == SourceMode.ShakeIt, false,
                ShakeItSourceState.Supported, tapFresh: true);
            ChannelSourceResolution after = ShakeItSourceResolution.Resolve<object>(route, isLock, Tap, null,
                Raw, ConfiguredMustNotBeRead, rawRowsIdentity, RawSourceFallback.RawIdentity(isLock));
            Assert.Equal(ChannelSourceRoute.ShakeItBuiltIn, after.Route);
            Assert.Equal(isLock ? Tap.Lock : Tap.Slip, after.Values);
            Assert.Equal(ShakeItIdentity(isLock), after.Identity);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Viper_onboarding_Raw_rows_then_Viper_picked_while_blocked_then_supported_reads_Viper_under_the_Viper_key(bool isLock)
        {
            // Plugin Internal saved: Raw rows.
            var channel = new WheelChannelSettings();
            channel.ResetSourcesToDefault(isLock);

            // Viper picked on an unsupported title: shadowed, rows not saved; SaveToSettings assigns the
            // mode, then KeepPresetSourcesWhileShadowed.
            channel.SourceMode = SourceMode.Viper;
            channel.KeepPresetSourcesWhileShadowed(channel.SourceMode, isLock);

            var preset = new WheelChannelSettings { SourceMode = SourceMode.Viper };
            preset.ResetSourcesForCurrentMode(isLock);
            Assert.Equal(SourceMode.Viper, channel.SourceMode);
            Assert.Equal(preset.SourceFrontLeft, channel.SourceFrontLeft);
            Assert.Equal(preset.SourceFrontRight, channel.SourceFrontRight);
            Assert.Equal(preset.SourceRearLeft, channel.SourceRearLeft);
            Assert.Equal(preset.SourceRearRight, channel.SourceRearRight);
            Assert.Equal(ScriptType.NCalc, channel.ScriptTypeFrontLeft);
            Assert.False(channel.RepairPresetSourcesIfStale(isLock));   // nothing left for the load-time repair

            // Title becomes supported: the Configured route, keyed by the rows - now the Viper identity.
            string rowsIdentity = SourceIdentity.Compute(
                channel.SourceFrontLeft, channel.ScriptTypeFrontLeft.ToString(),
                channel.SourceFrontRight, channel.ScriptTypeFrontRight.ToString(),
                channel.SourceRearLeft, channel.ScriptTypeRearLeft.ToString(),
                channel.SourceRearRight, channel.ScriptTypeRearRight.ToString());
            ChannelSourceRoute route = ShakeItSourceResolution.Route(isShakeItMode: false, viperBlocked: false,
                ShakeItSourceState.NotSetUp, tapFresh: false);
            ChannelSourceResolution r = ShakeItSourceResolution.Resolve<object>(route, isLock, null, null,
                Raw, Configured, rowsIdentity, RawSourceFallback.RawIdentity(isLock));
            Assert.Equal(ChannelSourceRoute.Configured, r.Route);
            Assert.Equal(KnownSourceColdStartReference.ViperSourceIdentity(isLock), r.Identity);
            Assert.NotEqual(RawSourceFallback.RawIdentity(isLock), r.Identity);
        }

        [Theory]
        [InlineData(SourceMode.Manual)]
        [InlineData(SourceMode.Custom)]
        public void A_shadowed_save_keeps_the_stored_sources_for_every_mode_but_the_presets(SourceMode mode)
        {
            var channel = new WheelChannelSettings();
            channel.ResetSourcesToDefault(true);
            string before = channel.SourceFrontLeft + channel.SourceFrontRight + channel.SourceRearLeft + channel.SourceRearRight;

            channel.KeepPresetSourcesWhileShadowed(mode, true);

            Assert.Equal(before, channel.SourceFrontLeft + channel.SourceFrontRight + channel.SourceRearLeft + channel.SourceRearRight);
        }

        [Theory]
        [InlineData(true, ShakeItSourceState.NotSetUp, true)]
        [InlineData(true, ShakeItSourceState.SetUpIdle, true)]
        [InlineData(true, ShakeItSourceState.NotSupported, true)]
        [InlineData(true, ShakeItSourceState.Supported, false)]   // supported but stale
        [InlineData(true, ShakeItSourceState.NotSetUp, false)]
        [InlineData(false, ShakeItSourceState.NotSetUp, true)]
        [InlineData(false, ShakeItSourceState.SetUpIdle, true)]
        [InlineData(false, ShakeItSourceState.NotSupported, true)]
        [InlineData(false, ShakeItSourceState.Supported, false)]
        public void Not_supported_or_stale_reads_Raw_for_all_four_wheels_under_the_Raw_identity(
            bool isLock, ShakeItSourceState state, bool tapFresh)
        {
            ChannelSourceResolution r = ResolveShakeIt(isLock, state, tapFresh);

            Assert.Equal(ChannelSourceRoute.RawFallback, r.Route);
            Assert.Equal(new Corners(100, 101, 102, 103), r.Values);
            Assert.Equal(RawSourceFallback.RawIdentity(isLock), r.Identity);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Supported_and_fresh_reads_the_built_in_values_under_the_ShakeIt_identity(bool isLock)
        {
            ChannelSourceResolution r = ResolveShakeIt(isLock, ShakeItSourceState.Supported, tapFresh: true);

            Assert.Equal(ChannelSourceRoute.ShakeItBuiltIn, r.Route);
            Assert.Equal(isLock ? Tap.Lock : Tap.Slip, r.Values);
            Assert.Equal(ShakeItIdentity(isLock), r.Identity);
        }

        [Fact]
        public void Lock_and_slip_are_decided_independently()
        {
            ChannelSourceResolution lockR = ResolveShakeIt(true, ShakeItSourceState.Supported, tapFresh: true);
            ChannelSourceResolution slipR = ResolveShakeIt(false, ShakeItSourceState.NotSupported, tapFresh: true);

            Assert.Equal(ChannelSourceRoute.ShakeItBuiltIn, lockR.Route);
            Assert.Equal(Tap.Lock, lockR.Values);
            Assert.Equal(ShakeItIdentity(true), lockR.Identity);

            Assert.Equal(ChannelSourceRoute.RawFallback, slipR.Route);
            Assert.Equal(new Corners(100, 101, 102, 103), slipR.Values);
            Assert.Equal(RawSourceFallback.RawIdentity(false), slipR.Identity);
        }

        [Fact]
        public void ShakeIt_learning_never_receives_Raw_values_in_a_mixed_sequence()
        {
            // A recording "learner": every frame's values filed under the identity it was resolved with.
            var learned = new Dictionary<string, List<Corners>>(StringComparer.Ordinal);
            var sequence = new (ShakeItSourceState state, bool fresh)[]
            {
                (ShakeItSourceState.NotSetUp, false), (ShakeItSourceState.SetUpIdle, false),
                (ShakeItSourceState.Supported, true), (ShakeItSourceState.Supported, false),
                (ShakeItSourceState.Supported, true), (ShakeItSourceState.NotSupported, true),
                (ShakeItSourceState.NotSupported, false), (ShakeItSourceState.Supported, true),
                (ShakeItSourceState.NotSetUp, true)
            };

            foreach (var (state, fresh) in sequence)
            {
                ChannelSourceResolution r = ResolveShakeIt(true, state, fresh);
                if (!learned.TryGetValue(r.Identity, out List<Corners> list)) learned[r.Identity] = list = new List<Corners>();
                list.Add(r.Values);
            }

            List<Corners> shakeIt = learned[ShakeItIdentity(true)];
            Assert.Equal(3, shakeIt.Count);
            Assert.All(shakeIt, v => Assert.Equal(Tap.Lock, v));
            Assert.All(learned[RawSourceFallback.RawIdentity(true)], v => Assert.Equal(new Corners(100, 101, 102, 103), v));
            Assert.Equal(2, learned.Count);
        }

        [Fact]
        public void ShakeIt_route_without_a_tap_snapshot_still_falls_back_to_Raw_not_the_ShakeIt_key()
        {
            ChannelSourceResolution r = ShakeItSourceResolution.Resolve<object>(ChannelSourceRoute.ShakeItBuiltIn, true, null, null,
                Raw, ConfiguredMustNotBeRead, RowsIdentity, RawSourceFallback.RawIdentity(true));

            Assert.Equal(ChannelSourceRoute.RawFallback, r.Route);
            Assert.Equal(RawSourceFallback.RawIdentity(true), r.Identity);
        }

        [Theory]
        [InlineData(ShakeItSourceState.NotSetUp)]
        [InlineData(ShakeItSourceState.SetUpIdle)]
        [InlineData(ShakeItSourceState.Supported)]
        [InlineData(ShakeItSourceState.NotSupported)]
        public void Other_modes_read_their_configured_sources_whatever_the_ShakeIt_state(ShakeItSourceState state)
        {
            ChannelSourceRoute route = ShakeItSourceResolution.Route(isShakeItMode: false, viperBlocked: false, state, tapFresh: true);
            ChannelSourceResolution r = ShakeItSourceResolution.Resolve<object>(route, true, Tap, null, Raw, Configured, "configured", "raw");

            Assert.Equal(ChannelSourceRoute.Configured, r.Route);
            Assert.Equal(new Corners(200, 201, 202, 203), r.Values);
            Assert.Equal("configured", r.Identity);
        }

        [Fact]
        public void A_blocked_Viper_channel_still_reads_Raw_under_the_Raw_identity()
        {
            ChannelSourceRoute route = ShakeItSourceResolution.Route(isShakeItMode: false, viperBlocked: true,
                ShakeItSourceState.Supported, tapFresh: true);
            ChannelSourceResolution r = ShakeItSourceResolution.Resolve<object>(route, false, Tap, null, Raw, Configured, "viper",
                RawSourceFallback.RawIdentity(false));

            Assert.Equal(ChannelSourceRoute.RawFallback, r.Route);
            Assert.Equal(new Corners(100, 101, 102, 103), r.Values);
            Assert.Equal(RawSourceFallback.RawIdentity(false), r.Identity);
        }

        [Theory]
        [InlineData(ShakeItSourceState.NotSetUp, true)]
        [InlineData(ShakeItSourceState.NotSupported, true)]
        [InlineData(ShakeItSourceState.SetUpIdle, false)]
        [InlineData(ShakeItSourceState.Supported, false)]
        public void Page_status_falls_back_only_on_a_verdict_not_while_idle(ShakeItSourceState state, bool expected)
        {
            Assert.Equal(expected, ShakeItSourceResolution.FallsBackToRaw(state));
        }
    }
}

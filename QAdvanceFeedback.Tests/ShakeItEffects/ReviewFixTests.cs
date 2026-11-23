using System;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests.ShakeItEffects
{
    /// <summary>Fixes from the whole-change review of the native ShakeIt effects
    /// (docs\shakeit-native-effects-report.md, "Review fixes").</summary>
    public class ReviewFixTests
    {
        private static QAdvanceFeedbackSettings Clone(QAdvanceFeedbackSettings s) =>
            JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(JsonConvert.SerializeObject(s));

        // C1 - a plugin page that was off screen catches up on what changed meanwhile.

        // I1 - a failing apply/save from a ShakeIt panel must not throw into SimHub's UI.

        [Fact]
        public void Set_survives_a_failing_apply_and_reports_it()
        {
            var live = new QAdvanceFeedbackSettings();
            var hub = new SettingsHub(() => live, _ => throw new InvalidOperationException("Collection was modified"));
            SettingsChangedEventArgs got = null;
            hub.Changed += (_, e) => got = e;

            bool changed = hub.Set(SettingField.AccelMaxMode, GMaxMode.Fixed, SettingsOrigin.ShakeItPanel);

            Assert.True(changed);
            Assert.Equal(GMaxMode.Fixed, live.GForce.AccelMaxMode);
            Assert.NotNull(got);   // listeners still learn the value changed
            Assert.Contains(HealthRegistry.Snapshot(), e => e.Subsystem == HealthSubsystems.ShakeItEffects);
        }

        // I2 - G-Force native effects follow ShakeIt's own in-game gate, except for the Test Effect.

        [Theory]
        [InlineData(false, true, false, false, false, true)]    // driving
        [InlineData(false, true, true, false, false, false)]    // paused
        [InlineData(false, true, false, true, false, false)]    // in a menu
        [InlineData(false, true, false, false, true, false)]    // spectating
        [InlineData(false, false, false, false, false, false)]  // no game
        [InlineData(true, false, false, false, false, true)]    // Test Effect, out of game
        [InlineData(true, true, true, false, false, true)]      // Test Effect while paused
        public void GForce_output_gate(bool testEffect, bool running, bool paused, bool menu, bool spectating, bool expected) =>
            Assert.Equal(expected, TestAwareOutputGate.Allows(testEffect, running, paused, menu, spectating));
    }
}

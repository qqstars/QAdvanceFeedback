using System;
using System.Globalization;
using System.IO;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.ShakeItEffects;
using Xunit;

namespace QAdvanceFeedback.Tests.ShakeItEffects
{
    /// <summary>The native effects' localized names and default gains (owner, 2026-11-18).</summary>
    [Collection("Strings culture")]
    public class NativeEffectDefaultsTests
    {
        [Theory]
        [InlineData(NativeEffect.WheelLock, 75)]
        [InlineData(NativeEffect.WheelSlip, 75)]
        [InlineData(NativeEffect.GForceBottomFront, 100)]
        [InlineData(NativeEffect.GForceBottomRear, 100)]
        [InlineData(NativeEffect.GForceBackLow, 100)]
        [InlineData(NativeEffect.GForceBackTop, 100)]
        [InlineData(NativeEffect.ClutchBite, 100)]
        [InlineData(NativeEffect.SlipAngle, 75)]
        public void Default_gain(NativeEffect effect, double gain) => Assert.Equal(gain, NativeEffects.DefaultGainPercent(effect));

        [Fact]
        public void Every_effect_has_a_short_english_name_and_a_chinese_name()
        {
            var original = Strings.Culture;
            try
            {
                foreach (NativeEffect e in Enum.GetValues(typeof(NativeEffect)))
                {
                    Strings.Culture = CultureInfo.GetCultureInfo("en-US");
                    string en = Strings.Get(NativeEffects.NameKey(e)), enDesc = Strings.Get(NativeEffects.DescriptionKey(e));
                    Strings.Culture = CultureInfo.GetCultureInfo("zh-Hans");
                    string zh = Strings.Get(NativeEffects.NameKey(e)), zhDesc = Strings.Get(NativeEffects.DescriptionKey(e));

                    Assert.False(string.IsNullOrWhiteSpace(en));
                    Assert.DoesNotContain("QAdvanceFeedback", en);   // the category already says it
                    Assert.NotEqual(en, zh);
                    Assert.NotEqual(enDesc, zhDesc);
                    Assert.DoesNotContain("QAdvanceFeedback", enDesc);   // owner, 2026-11-20
                    Assert.DoesNotContain("QAdvanceFeedback", zhDesc);
                    Assert.NotEqual(NativeEffects.NameKey(e), en);   // a real string, not the key
                }
                Strings.Culture = CultureInfo.GetCultureInfo("en-US");
                Assert.Equal("Wheel Lock", Strings.Get(NativeEffects.NameKey(NativeEffect.WheelLock)));
                Assert.Equal("Clutch Bite", Strings.Get(NativeEffects.NameKey(NativeEffect.ClutchBite)));
            }
            finally { Strings.Culture = original; }
        }

        [Theory]
        [InlineData("QAdvanceFeedbackWheelChannelContainers.cs", "NativeEffect.WheelLock")]
        [InlineData("QAdvanceFeedbackWheelChannelContainers.cs", "NativeEffect.WheelSlip")]
        [InlineData("QAdvanceFeedbackGForceContainers.cs", "NativeEffects.For(Pad)")]
        [InlineData("QAdvanceFeedbackClutchBiteContainer.cs", "NativeEffect.ClutchBite")]
        [InlineData("QAdvanceFeedbackSlipAngleContainer.cs", "NativeEffect.SlipAngle")]
        public void Containers_apply_the_localized_name_and_default_gain(string file, string effect)
        {
            string code = File.ReadAllText(PluginFile("Integration", "ShakeIt", file));
            Assert.Contains(effect, code);
            Assert.Contains("ShakeItEffectSupport.ApplyIdentity(this,", code);
        }

        /// <summary>The "Add effect" list (the attribute's English text) matches the English table.</summary>
        [Theory]
        [InlineData("QAdvanceFeedbackWheelChannelContainers.cs", NativeEffect.WheelLock)]
        [InlineData("QAdvanceFeedbackWheelChannelContainers.cs", NativeEffect.WheelSlip)]
        [InlineData("QAdvanceFeedbackGForceContainers.cs", NativeEffect.GForceBottomFront)]
        [InlineData("QAdvanceFeedbackGForceContainers.cs", NativeEffect.GForceBottomRear)]
        [InlineData("QAdvanceFeedbackGForceContainers.cs", NativeEffect.GForceBackLow)]
        [InlineData("QAdvanceFeedbackGForceContainers.cs", NativeEffect.GForceBackTop)]
        [InlineData("QAdvanceFeedbackClutchBiteContainer.cs", NativeEffect.ClutchBite)]
        [InlineData("QAdvanceFeedbackSlipAngleContainer.cs", NativeEffect.SlipAngle)]
        public void Add_effect_description_matches_the_english_table(string file, NativeEffect effect)
        {
            string code = File.ReadAllText(PluginFile("Integration", "ShakeIt", file));
            string en = StringTableEn.Create()[NativeEffects.DescriptionKey(effect)];
            Assert.Contains("\"" + en + "\"", code);
        }

        [Theory]
        [InlineData(NativeEffect.GForceBottomFront, "G-Force, seat-bottom front leg pads (GForce.Bottom.FrontLeft / FrontRight).")]
        [InlineData(NativeEffect.GForceBottomRear, "G-Force, seat-bottom rear butt pads (GForce.Bottom.RearLeft / RearRight).")]
        [InlineData(NativeEffect.GForceBackLow, "G-Force, seat-back low waist pads (GForce.Back.LowLeft / LowRight).")]
        [InlineData(NativeEffect.GForceBackTop, "G-Force, seat-back top back pads (GForce.Back.TopLeft / TopRight).")]
        public void G_force_descriptions_name_the_body_part(NativeEffect effect, string expected) =>
            Assert.Equal(expected, StringTableEn.Create()[NativeEffects.DescriptionKey(effect)]);

        [Fact]
        public void Built_in_description_matches_current_and_previous_wordings_in_both_languages()
        {
            foreach (NativeEffect e in Enum.GetValues(typeof(NativeEffect)))
            {
                string key = NativeEffects.DescriptionKey(e);
                Assert.True(NativeEffects.IsBuiltInDescription(e, StringTableEn.Create()[key]));
                Assert.True(NativeEffects.IsBuiltInDescription(e, StringTableZhHans.Create()[key]));
                Assert.True(NativeEffects.IsBuiltInDescription(e, " " + StringTableEn.Create()[key] + " "));
            }
            Assert.True(NativeEffects.IsBuiltInDescription(NativeEffect.SlipAngle,
                "QAdvanceFeedback's projected tyre slip angle (the same value as SlipAngle.Projected.All): builds through the grip window and peaks past Max, eased down in a held drift."));
            Assert.True(NativeEffects.IsBuiltInDescription(NativeEffect.SlipAngle,
                "QAdvanceFeedback 映射后的轮胎侧偏角（数值与 SlipAngle.Projected.All 相同）：在抓地窗口内逐步增强，超过最大值后达到峰值，持续漂移时逐渐减弱。"));
            Assert.True(NativeEffects.IsBuiltInDescription(NativeEffect.GForceBackTop,
                "QAdvanceFeedback G-Force, seat-back top pads (GForce.Back.TopLeft / TopRight)."));
        }

        [Fact]
        public void A_renamed_effect_or_another_effects_description_is_kept()
        {
            Assert.False(NativeEffects.IsBuiltInDescription(NativeEffect.SlipAngle, "Rear tyres"));
            Assert.False(NativeEffects.IsBuiltInDescription(NativeEffect.SlipAngle, null));
            Assert.False(NativeEffects.IsBuiltInDescription(NativeEffect.SlipAngle, "  "));
            Assert.False(NativeEffects.IsBuiltInDescription(NativeEffect.SlipAngle,
                StringTableEn.Create()[NativeEffects.DescriptionKey(NativeEffect.ClutchBite)]));
        }

        /// <summary>Each container clears its saved description after "Add effect" (LoadDefaultSettings) and
        /// after a profile loads (OnDeserialized).</summary>
        [Theory]
        [InlineData("QAdvanceFeedbackWheelChannelContainers.cs")]
        [InlineData("QAdvanceFeedbackGForceContainers.cs")]
        [InlineData("QAdvanceFeedbackClutchBiteContainer.cs")]
        [InlineData("QAdvanceFeedbackSlipAngleContainer.cs")]
        public void Containers_clear_the_built_in_description_on_add_and_on_load(string file)
        {
            string code = File.ReadAllText(PluginFile("Integration", "ShakeIt", file));
            Assert.Contains("public override void LoadDefaultSettings()", code);
            Assert.Contains("[System.Runtime.Serialization.OnDeserialized]", code);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(code, @"ShakeItEffectSupport\.ClearBuiltInDescription\(this,").Count);
        }

        [Fact]
        public void Identity_sets_gain_and_swaps_the_container_name_for_the_localized_one()
        {
            string code = File.ReadAllText(PluginFile("Integration", "ShakeIt", "ShakeItEffectSupport.cs"));
            Assert.Contains("Gain = NativeEffects.DefaultGainPercent(", code);
            Assert.Contains("<ContainerName>k__BackingField", code);
            Assert.Contains("FromConstant", code);
        }

        /// <summary>Slip Angle (docs\slip-angle-design.md 9.10): the frozen container type name, one placement,
        /// and the owner's tone defaults - low 40 Hz, high on at 25 Hz, noise off.</summary>
        [Fact]
        public void Slip_angle_container_name_and_tone_defaults_are_pinned()
        {
            ToneDefault d = ToneDefaults.SlipAngle;
            Assert.Equal(40, d.LowHz);
            Assert.True(d.UseHigh);
            Assert.Equal(25, d.HighHz);
            Assert.False(d.UseNoise);

            string code = File.ReadAllText(PluginFile("Integration", "ShakeIt", "QAdvanceFeedbackSlipAngleContainer.cs"));
            Assert.Contains("public sealed class QAdvanceFeedbackSlipAngleContainer : EffectsContainerBase<ToneOutput>", code);
            Assert.Contains("\"QAdvanceFeedback\", OutputMode.Any", code);
            Assert.Contains("ShakeItEffectSupport.Apply(output, ToneDefaults.SlipAngle)", code);
            Assert.Contains("SlipAngleEffectOutput.Value0100(", code);
            // One placement: a single effect, no routing.
            Assert.DoesNotContain("IRoutingTarget", code);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, @"new QAdvanceFeedbackEffect\("));

            // Apply always writes the two shared switches off (FrequencyBasedOnPreFilter / UsePrehemptiveMode).
            string support = File.ReadAllText(PluginFile("Integration", "ShakeIt", "ShakeItEffectSupport.cs"));
            Assert.Contains("output.FrequencyBasedOnPreFilter = false;", support);
            Assert.Contains("output.UsePrehemptiveMode = false;", support);
        }

        private static string PluginFile(params string[] parts)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"))) dir = dir.Parent;
            var all = new string[parts.Length + 2];
            all[0] = dir.FullName; all[1] = "QAdvanceFeedback";
            Array.Copy(parts, 0, all, 2, parts.Length);
            return Path.Combine(all);
        }
    }
}

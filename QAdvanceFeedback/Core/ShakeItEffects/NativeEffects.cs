using System;
using QAdvanceFeedback.Core.Localization;

namespace QAdvanceFeedback.Core.ShakeItEffects
{
    /// <summary>The native QAdvanceFeedback ShakeIt effects.</summary>
    public enum NativeEffect
    {
        WheelLock,
        WheelSlip,
        GForceBottomFront,
        GForceBottomRear,
        GForceBackLow,
        GForceBackTop,
        ClutchBite,
        SlipAngle,
    }

    /// <summary>
    /// Per-effect identity (owner, 2026-11-18): the localized display name and description (string-table
    /// keys; the effects sit under the "QAdvanceFeedback" category, so the names carry no prefix) and the
    /// gain a newly added effect starts at (ShakeIt's own default is 50 %). A saved effect keeps its gain:
    /// ShakeIt always writes Gain to the profile.
    /// </summary>
    public static class NativeEffects
    {
        public static string NameKey(NativeEffect e) => "Effect." + e + ".Name";

        public static string DescriptionKey(NativeEffect e) => "Effect." + e + ".Description";

        /// <summary>
        /// True when <paramref name="text"/> is one of <paramref name="e"/>'s own descriptions - the current
        /// English or Chinese wording, or an earlier one. ShakeIt's "Add effect" copies the description into
        /// the effect's saved Description (the field Rename writes) and then shows it after the name; the
        /// description belongs to the "Add effect" list only (owner, 2026-11-20), so a saved copy is cleared.
        /// A name the user typed in Rename is never one of these, so it is kept.
        /// </summary>
        public static bool IsBuiltInDescription(NativeEffect e, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim(), key = DescriptionKey(e);
            foreach (string culture in new[] { "en", "zh-Hans" })
                if (Strings.TableFor(culture).TryGetValue(key, out string d) && d == t) return true;
            return Array.IndexOf(PreviousDescriptions(e), t) >= 0;
        }

        // The wordings before 2026-11-20 (English, then Chinese) - still saved in existing profiles.
        private static string[] PreviousDescriptions(NativeEffect e)
        {
            switch (e)
            {
                case NativeEffect.WheelLock: return new[] {
                    "QAdvanceFeedback's projected wheel lock (the same values as WheelLock.Projected.*), per wheel, front/rear, left/right or one channel.",
                    "QAdvanceFeedback 映射后的车轮抱死（数值与 WheelLock.Projected.* 相同），可按每个车轮、前/后、左/右或单通道输出。" };
                case NativeEffect.WheelSlip: return new[] {
                    "QAdvanceFeedback's projected wheel slip (the same values as WheelSlip.Projected.*), per wheel, front/rear, left/right or one channel.",
                    "QAdvanceFeedback 映射后的车轮打滑（数值与 WheelSlip.Projected.* 相同），可按每个车轮、前/后、左/右或单通道输出。" };
                case NativeEffect.GForceBottomFront: return new[] {
                    "QAdvanceFeedback G-Force, seat-bottom front pads (GForce.Bottom.FrontLeft / FrontRight).",
                    "QAdvanceFeedback G 力，坐垫前部震动单元（GForce.Bottom.FrontLeft / FrontRight）。" };
                case NativeEffect.GForceBottomRear: return new[] {
                    "QAdvanceFeedback G-Force, seat-bottom rear pads (GForce.Bottom.RearLeft / RearRight).",
                    "QAdvanceFeedback G 力，坐垫后部震动单元（GForce.Bottom.RearLeft / RearRight）。" };
                case NativeEffect.GForceBackLow: return new[] {
                    "QAdvanceFeedback G-Force, seat-back low pads (GForce.Back.LowLeft / LowRight).",
                    "QAdvanceFeedback G 力，靠背下部震动单元（GForce.Back.LowLeft / LowRight）。" };
                case NativeEffect.GForceBackTop: return new[] {
                    "QAdvanceFeedback G-Force, seat-back top pads (GForce.Back.TopLeft / TopRight).",
                    "QAdvanceFeedback G 力，靠背上部震动单元（GForce.Back.TopLeft / TopRight）。" };
                case NativeEffect.ClutchBite: return new[] {
                    "QAdvanceFeedback's clutch bite feel (the same value as Clutch.Bite.Output): strongest at the bite point, learned per car.",
                    "QAdvanceFeedback 的离合器结合手感（数值与 Clutch.Bite.Output 相同）：在结合点处最强，按车辆学习。" };
                case NativeEffect.SlipAngle: return new[] {
                    "QAdvanceFeedback's projected tyre slip angle (the same value as SlipAngle.Projected.All): builds through the grip window and peaks past Max, eased down in a held drift.",
                    "QAdvanceFeedback 映射后的轮胎侧偏角（数值与 SlipAngle.Projected.All 相同）：在抓地窗口内逐步增强，超过最大值后达到峰值，持续漂移时逐渐减弱。",
                    "QAdvanceFeedback 映射后的轮胎滑移角（数值与 SlipAngle.Projected.All 相同）：在抓地窗口内逐步增强，超过最大值后达到峰值，持续漂移时逐渐减弱。" };
                default: return new string[0];
            }
        }

        public static double DefaultGainPercent(NativeEffect e) =>
            e == NativeEffect.WheelLock || e == NativeEffect.WheelSlip || e == NativeEffect.SlipAngle ? 75.0 : 100.0;

        public static NativeEffect For(GForcePad pad)
        {
            switch (pad)
            {
                case GForcePad.BottomRear: return NativeEffect.GForceBottomRear;
                case GForcePad.BackLow: return NativeEffect.GForceBackLow;
                case GForcePad.BackTop: return NativeEffect.GForceBackTop;
                default: return NativeEffect.GForceBottomFront;
            }
        }
    }
}

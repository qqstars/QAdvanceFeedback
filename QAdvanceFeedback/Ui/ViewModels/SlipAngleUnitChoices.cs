using System.Collections.Generic;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>The unit dropdown's entries (owner, 2026-11-21 / 2026-11-22), shared by the Slip Angle tab and the
    /// ShakeIt Slip Angle panel: each unit is one list in the plugin config; Auto-detect is none.</summary>
    public static class SlipAngleUnitChoices
    {
        /// <summary>
        /// The current game's entries (owner, 2026-11-24; <see cref="SettingsHub.SlipAngleUnitOptions"/>): "Per-Wheel Angle
        /// (unit)" and "Car Angle"; Auto names the unit the check found, or says it is still checking.
        /// </summary>
        public static IReadOnlyList<Choice<SlipAngleUnitChoice>> For(string gameId, SlipAngleSourceUnits units)
        {
            var list = new List<Choice<SlipAngleUnitChoice>>();
            foreach (SlipAngleUnitChoice c in SettingsHub.SlipAngleUnitOptions(gameId))
            {
                string label;
                if (c == SlipAngleUnitChoice.AutoDetect)
                {
                    SlipAngleUnitChoice? found = SettingsHub.SlipAngleDetectedUnit(gameId, units);
                    label = found.HasValue && found != SlipAngleUnitChoice.CarAngle
                        ? string.Format(Strings.Get("SlipAngle.Unit.AutoDetect.Found"), Strings.Get("SlipAngle.Unit.Short." + found.Value))
                        : Strings.Get("SlipAngle.Unit.AutoDetect.Checking");
                }
                else label = Label(c);
                list.Add(new Choice<SlipAngleUnitChoice>(c, label));
            }
            return list;
        }

        /// <summary>True when both lists hold the same entries and labels (the view keeps its list otherwise).</summary>
        public static bool Same(IReadOnlyList<Choice<SlipAngleUnitChoice>> a, IReadOnlyList<Choice<SlipAngleUnitChoice>> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Value != b[i].Value || a[i].Label != b[i].Label) return false;
            return true;
        }

        private static string Label(SlipAngleUnitChoice c) => Strings.Get("SlipAngle.Unit." + c);
    }
}

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
        /// The current game's entries (owner, 2026-11-22; <see cref="SettingsHub.SlipAngleUnitOptions"/>): a hard-coded
        /// game's own unit is marked as such; Auto-detect names the unit the check found, or says it is still checking.
        /// </summary>
        public static IReadOnlyList<Choice<SlipAngleUnitChoice>> For(string gameId, SlipAngleSourceUnits units)
        {
            SlipAngleUnitChoice? hard = SettingsHub.SlipAngleHardCodedUnit(gameId);
            var list = new List<Choice<SlipAngleUnitChoice>>();
            foreach (SlipAngleUnitChoice c in SettingsHub.SlipAngleUnitOptions(gameId))
            {
                string label;
                if (c == SlipAngleUnitChoice.AutoDetect)
                {
                    SlipAngleUnitChoice? found = SettingsHub.SlipAngleDetectedUnit(gameId, units);
                    label = found.HasValue
                        ? string.Format(Strings.Get("SlipAngle.Unit.AutoDetect.Found"), Label(found.Value))
                        : Strings.Get("SlipAngle.Unit.AutoDetect.Checking");
                }
                else if (c == hard) label = string.Format(Strings.Get("SlipAngle.Unit.HardCoded"), Label(c));
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

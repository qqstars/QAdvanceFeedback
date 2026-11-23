using System.Collections.Generic;

namespace QAdvanceFeedback.Core.Localization
{
    // Per-area string files (StringTable*.<Area>.cs): each adds its own keys, so parallel work on
    // different tabs never edits the same file. A key must be added to the same area in both languages.
    public static partial class StringTableEn
    {
        private static Dictionary<string, string> WithAreas(Dictionary<string, string> d)
        {
            AddPage(d);
            AddLockSlip(d);
            AddCurve(d);
            AddGForce(d);
            AddGeneral(d);
            AddPanels(d);
            AddSlipAngle(d);
            return d;
        }
    }
}

using System;

namespace QAdvanceFeedback.Core.Projection
{
    /// <summary>Identifies one row of the curve-editor table. Ported verbatim from the sibling
    /// ReliableWheelLockSlip project's Core/ProjectorAnchorEditor.cs.</summary>
    public enum AnchorSlot { Start, Slightly, Moderate, Critical, End }

    /// <summary>
    /// Maps the curve editor's five table rows onto <see cref="ProjectorSettings"/>'s named fields,
    /// and centralises the "editing any cell switches the preset to Custom" rule. Ported verbatim
    /// from the sibling project - kept here as the pure model/mapping logic a later settings-UI task
    /// can build directly on top of (this class itself has no WPF/SimHub dependency and is not the
    /// UI the brief says a later task owns).
    /// </summary>
    public static class ProjectorAnchorEditor
    {
        public static double GetRaw(ProjectorSettings settings, AnchorSlot slot)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            switch (slot)
            {
                case AnchorSlot.Start: return settings.StartInput;
                case AnchorSlot.Slightly: return settings.SlightlyInput;
                case AnchorSlot.Moderate: return settings.ModerateInput;
                case AnchorSlot.Critical: return settings.CriticalInput;
                case AnchorSlot.End: return settings.EndInput;
                default: return 0.0;
            }
        }

        public static double GetOutput(ProjectorSettings settings, AnchorSlot slot)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            switch (slot)
            {
                case AnchorSlot.Start: return 0.0;
                case AnchorSlot.Slightly: return settings.SlightlyOutput;
                case AnchorSlot.Moderate: return settings.ModerateOutput;
                case AnchorSlot.Critical: return settings.CriticalOutput;
                case AnchorSlot.End: return 100.0;
                default: return 0.0;
            }
        }

        public static void SetRaw(ProjectorSettings settings, AnchorSlot slot, double? value)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (value == null) return;

            switch (slot)
            {
                case AnchorSlot.Start: settings.StartInput = value.Value; break;
                case AnchorSlot.Slightly: settings.SlightlyInput = value.Value; break;
                case AnchorSlot.Moderate: settings.ModerateInput = value.Value; break;
                case AnchorSlot.Critical: settings.CriticalInput = value.Value; break;
                case AnchorSlot.End: settings.EndInput = value.Value; break;
            }
            settings.Preset = ProjectorPreset.Custom;
        }

        public static void SetOutput(ProjectorSettings settings, AnchorSlot slot, double? value)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (value == null) return;
            if (slot == AnchorSlot.Start || slot == AnchorSlot.End) return;

            switch (slot)
            {
                case AnchorSlot.Slightly: settings.SlightlyOutput = value.Value; break;
                case AnchorSlot.Moderate: settings.ModerateOutput = value.Value; break;
                case AnchorSlot.Critical: settings.CriticalOutput = value.Value; break;
            }
            settings.Preset = ProjectorPreset.Custom;
        }
    }
}

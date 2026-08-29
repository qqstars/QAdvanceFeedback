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
        /// <summary>
        /// The "edited" state that corresponds to whichever base shape is currently selected, so an edit
        /// records WHICH preset it departed from instead of collapsing every edit into one anonymous
        /// "Custom". An already-custom preset stays as it is.
        /// </summary>
        private static ProjectorPreset CustomVariantOf(ProjectorPreset current)
        {
            switch (current)
            {
                case ProjectorPreset.Linear:
                case ProjectorPreset.LinearCustom:
                    return ProjectorPreset.LinearCustom;
                case ProjectorPreset.Curve:
                case ProjectorPreset.CurveCustom:
                    return ProjectorPreset.CurveCustom;
                default:
                    // Legacy Custom from an older save - leave it alone rather than guess a base.
                    return ProjectorPreset.Custom;
            }
        }

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

        /// <summary>
        /// PRE-RELEASE ADDITION: Start/End used to be hard-fixed at 0/100 (this method returned those
        /// literal numbers regardless of the settings object) - both are now driver-editable via
        /// <see cref="ProjectorSettings.StartOutput"/>/<see cref="ProjectorSettings.EndOutput"/>, so
        /// this reads the real configured value like every other slot.
        /// </summary>
        public static double GetOutput(ProjectorSettings settings, AnchorSlot slot)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            switch (slot)
            {
                case AnchorSlot.Start: return settings.StartOutput;
                case AnchorSlot.Slightly: return settings.SlightlyOutput;
                case AnchorSlot.Moderate: return settings.ModerateOutput;
                case AnchorSlot.Critical: return settings.CriticalOutput;
                case AnchorSlot.End: return settings.EndOutput;
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
            settings.Preset = CustomVariantOf(settings.Preset);
        }

        /// <summary>
        /// PRE-RELEASE ADDITION: Start/End are no longer rejected here (they used to be, since both
        /// were hard-fixed at 0/100 and not driver-editable) - editing either now behaves exactly like
        /// editing any other cell, including switching the preset to Custom, so a driver's own
        /// Start/End output choice is not silently overwritten the next time Linear/Curve is
        /// reselected.
        /// </summary>
        public static void SetOutput(ProjectorSettings settings, AnchorSlot slot, double? value)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (value == null) return;

            switch (slot)
            {
                case AnchorSlot.Start: settings.StartOutput = value.Value; break;
                case AnchorSlot.Slightly: settings.SlightlyOutput = value.Value; break;
                case AnchorSlot.Moderate: settings.ModerateOutput = value.Value; break;
                case AnchorSlot.Critical: settings.CriticalOutput = value.Value; break;
                case AnchorSlot.End: settings.EndOutput = value.Value; break;
            }
            settings.Preset = CustomVariantOf(settings.Preset);
        }
    }
}

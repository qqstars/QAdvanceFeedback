using System;
using System.Collections.Generic;
using System.Globalization;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// One row of the projection anchors table (Start / Slightly / Moderate / Critical / End): the same
    /// rows, columns and rules as the Wheel Lock tab's curve editor - raw and output through
    /// <see cref="ProjectorAnchorEditor"/> (an edit switches the preset to its customised variant), the
    /// flatten range on the three middle rows only (an edit does NOT change the preset).
    /// </summary>
    public sealed class AnchorRowViewModel : ObservableObject
    {
        private readonly ProjectorEditorViewModel _owner;
        private double? _raw, _output, _flatten;
        private string _defaultText = string.Empty;

        internal AnchorRowViewModel(ProjectorEditorViewModel owner, AnchorSlot slot, string label, string description, string outputHelp)
        {
            _owner = owner;
            Slot = slot;
            Label = label;
            Description = description;
            OutputHelp = outputHelp;
        }

        public AnchorSlot Slot { get; }
        public string Label { get; }
        public string Description { get; }
        /// <summary>Tooltip of the output editor (Start/End only, as on the Lock tab).</summary>
        public string OutputHelp { get; }
        public bool HasFlattenRange => Slot != AnchorSlot.Start && Slot != AnchorSlot.End;

        public double? Raw { get => _raw; set { if (SetProperty(ref _raw, value)) _owner.OnRawEdited(Slot, value); } }
        public double? Output { get => _output; set { if (SetProperty(ref _output, value)) _owner.OnOutputEdited(Slot, value); } }
        public double? FlattenRange { get => _flatten; set { if (SetProperty(ref _flatten, value)) _owner.OnFlattenEdited(Slot, value); } }

        /// <summary>"under default input value x, curve will return y" (+ a warning when the curve does
        /// not honour the typed pair) - the Lock tab's live column.</summary>
        public string DefaultText { get => _defaultText; internal set => SetProperty(ref _defaultText, value ?? string.Empty); }

        internal void Show(double raw, double output, double flatten)
        {
            SetProperty(ref _raw, raw, nameof(Raw));
            SetProperty(ref _output, output, nameof(Output));
            SetProperty(ref _flatten, flatten, nameof(FlattenRange));
        }
    }

    /// <summary>
    /// The projection anchors editor and its Normalized -> Projected curve, bound by a view (the Wheel
    /// Lock tab's curve editor, as a view model): preset, five anchor rows, the sampled curve (value
    /// coordinates, every 1 from 0 to 100, exactly as the Lock tab draws it) and the anchor dots. Holds a
    /// working copy; <see cref="Load"/> / <see cref="CopyTo"/> move it to and from settings.
    /// <see cref="Edited"/> is raised for driver edits only, never for a load.
    /// </summary>
    public sealed class ProjectorEditorViewModel : ObservableObject
    {
        private readonly ProjectionChannel _channel;
        private readonly ProjectorSettings _defaults;
        private ProjectorSettings _working;
        private ProjectorPreset _preset;
        private bool _loading;
        private IReadOnlyList<GraphPoint> _curve = new GraphPoint[0];
        private IReadOnlyList<GraphPoint> _anchors = new GraphPoint[0];

        public ProjectorEditorViewModel(ProjectionChannel channel, string slightlyLabel, string moderateLabel, string criticalLabel,
            string startDesc, string slightlyDesc, string moderateDesc, string criticalDesc, string endDesc)
        {
            _channel = channel;
            _defaults = ProjectorSettings.CreateShippedDefault(channel);
            _working = ProjectorSettings.CreateShippedDefault(channel);
            PresetChoices = new[]
            {
                new Choice<ProjectorPreset>(ProjectorPreset.Linear, Strings.Get("Curve.Preset.Linear")),
                new Choice<ProjectorPreset>(ProjectorPreset.Curve, Strings.Get("Curve.Preset.Curve")),
                new Choice<ProjectorPreset>(ProjectorPreset.LinearCustom, Strings.Get("Curve.Preset.LinearCustom")),
                new Choice<ProjectorPreset>(ProjectorPreset.CurveCustom, Strings.Get("Curve.Preset.CurveCustom")),
                new Choice<ProjectorPreset>(ProjectorPreset.Custom, Strings.Get("Curve.Preset.Custom")),
            };
            Start = new AnchorRowViewModel(this, AnchorSlot.Start, Strings.Get("Curve.StartPoint.Label"), startDesc, Strings.Get("Curve.StartOutput.Help"));
            Slightly = new AnchorRowViewModel(this, AnchorSlot.Slightly, slightlyLabel, slightlyDesc, null);
            Moderate = new AnchorRowViewModel(this, AnchorSlot.Moderate, moderateLabel, moderateDesc, null);
            Critical = new AnchorRowViewModel(this, AnchorSlot.Critical, criticalLabel, criticalDesc, null);
            End = new AnchorRowViewModel(this, AnchorSlot.End, Strings.Get("Curve.EndPoint.Label"), endDesc, Strings.Get("Curve.EndOutput.Help"));
            Load(_working);
        }

        /// <summary>Raised after a driver edit (marks the page dirty).</summary>
        public event Action Edited;

        public IReadOnlyList<Choice<ProjectorPreset>> PresetChoices { get; }
        public AnchorRowViewModel Start { get; }
        public AnchorRowViewModel Slightly { get; }
        public AnchorRowViewModel Moderate { get; }
        public AnchorRowViewModel Critical { get; }
        public AnchorRowViewModel End { get; }

        /// <summary>Normalized (0-100) -> projected, sampled every 1.</summary>
        public IReadOnlyList<GraphPoint> Curve { get => _curve; private set => SetProperty(ref _curve, value); }

        /// <summary>The five anchor dots (Start at output 0, End at 100 - where the Lock tab draws them).</summary>
        public IReadOnlyList<GraphPoint> Anchors { get => _anchors; private set => SetProperty(ref _anchors, value); }

        /// <summary>The working curve (read-only use: the source graph samples through it).</summary>
        public ProjectorSettings Working => _working;

        /// <summary>Preset (two-way): a named preset rewrites the anchors, exactly as the Lock tab's combo.</summary>
        public ProjectorPreset Preset
        {
            get => _preset;
            set
            {
                if (value == _preset) return;
                _working.ApplyPreset(value, _channel);
                ShowWorking();
                if (!_loading) Edited?.Invoke();
            }
        }

        public void Load(ProjectorSettings settings)
        {
            _working = new ProjectorSettings();
            (settings ?? _defaults).CopyTo(_working);
            ShowWorking();
        }

        public void CopyTo(ProjectorSettings target)
        {
            _working.CopyTo(target);
        }

        internal void OnRawEdited(AnchorSlot slot, double? value)
        {
            if (_loading || !value.HasValue) return;
            ProjectorAnchorEditor.SetRaw(_working, slot, value);
            AfterEdit();
        }

        internal void OnOutputEdited(AnchorSlot slot, double? value)
        {
            if (_loading || !value.HasValue) return;
            ProjectorAnchorEditor.SetOutput(_working, slot, value);
            AfterEdit();
        }

        internal void OnFlattenEdited(AnchorSlot slot, double? value)
        {
            if (_loading || !value.HasValue) return;
            switch (slot)
            {
                case AnchorSlot.Slightly: _working.SlightlyFlattenRange = value.Value; break;
                case AnchorSlot.Moderate: _working.ModerateFlattenRange = value.Value; break;
                case AnchorSlot.Critical: _working.CriticalFlattenRange = value.Value; break;
                default: return;
            }
            AfterEdit();
        }

        private void AfterEdit()
        {
            SetProperty(ref _preset, _working.Preset, nameof(Preset));
            Recompute();
            Edited?.Invoke();
        }

        private void ShowWorking()
        {
            _loading = true;
            try
            {
                SetProperty(ref _preset, _working.Preset, nameof(Preset));
                Start.Show(_working.StartInput, _working.StartOutput, 0.0);
                Slightly.Show(_working.SlightlyInput, _working.SlightlyOutput, _working.SlightlyFlattenRange);
                Moderate.Show(_working.ModerateInput, _working.ModerateOutput, _working.ModerateFlattenRange);
                Critical.Show(_working.CriticalInput, _working.CriticalOutput, _working.CriticalFlattenRange);
                End.Show(_working.EndInput, _working.EndOutput, 0.0);
            }
            finally { _loading = false; }
            Recompute();
        }

        private void Recompute()
        {
            var projector = new OutputProjector(_working);
            var curve = new List<GraphPoint>(101);
            for (int i = 0; i <= 100; i++) curve.Add(new GraphPoint(i, projector.Project(i)));
            Curve = curve;
            Anchors = new[]
            {
                new GraphPoint(_working.StartInput, 0.0),
                new GraphPoint(_working.SlightlyInput, _working.SlightlyOutput),
                new GraphPoint(_working.ModerateInput, _working.ModerateOutput),
                new GraphPoint(_working.CriticalInput, _working.CriticalOutput),
                new GraphPoint(_working.EndInput, 100.0),
            };
            Slightly.DefaultText = HelpText(projector, AnchorSlot.Slightly);
            Moderate.DefaultText = HelpText(projector, AnchorSlot.Moderate);
            Critical.DefaultText = HelpText(projector, AnchorSlot.Critical);
        }

        /// <summary>The Lock tab's anchor help text (SettingsControl.UpdateAnchorHelpText), verbatim rule.</summary>
        private string HelpText(OutputProjector projector, AnchorSlot slot)
        {
            string text = string.Format(Strings.Get("Curve.Anchor.DefaultFormat"),
                Num(ProjectorAnchorEditor.GetRaw(_defaults, slot)), Num(ProjectorAnchorEditor.GetOutput(_defaults, slot)));
            double actual = projector.Project(ProjectorAnchorEditor.GetRaw(_working, slot));
            if (Math.Abs(actual - ProjectorAnchorEditor.GetOutput(_working, slot)) > 0.05)
                text += " " + string.Format(Strings.Get("Curve.Anchor.LiveDiffersFormat"), Num(actual));
            return text;
        }

        private static string Num(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    }
}

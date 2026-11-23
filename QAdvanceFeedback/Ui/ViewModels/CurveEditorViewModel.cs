using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The "Output data and shaping" group of a Wheel Lock / Wheel Slip tab (docs\full-mvvm-plan.md §3.2;
    /// old SettingsControl.xaml.cs curve editor, GRAPH RENDERING and RefreshLockAnchorLabelsForPattern):
    /// <list type="bullet">
    /// <item>the Normalize Pattern (Lock names it by grip percentage, Slip by Perfect / Great / Good);
    /// Lock's Slightly / Ideal labels and descriptions and the pattern description live-switch with it,
    /// Slip's are fixed;</item>
    /// <item>preset, five anchors and three flatten ranges through the shared
    /// <see cref="ProjectorEditorViewModel"/> (<see cref="ProjectorAnchorEditor"/>: an anchor edit switches
    /// the preset to its customised variant, a flatten range edit does not);</item>
    /// <item>both graphs: normalized -> projected with the key-point markers (30 / 60 / 80, or 80 alone
    /// under Max-Grip-Only), and source -> projected sampled every 5 through the key data the page shows
    /// (<see cref="SourceToProjectedGraph"/>, shared with the ShakeIt panel's graph).</item>
    /// </list>
    /// Every edit goes into the DRAFT and waits for Apply (it marks the page dirty).
    /// </summary>
    public sealed class CurveEditorViewModel : PageSectionViewModel
    {
        private readonly ProjectionChannel _channel;
        private KeyDataPageViewModel _keyData;
        private IReadOnlyList<GraphPoint> _sourceGraph = new GraphPoint[0];

        public CurveEditorViewModel(SettingsPageContext ctx, bool isLock) : base(ctx)
        {
            IsLock = isLock;
            _channel = isLock ? ProjectionChannel.Lock : ProjectionChannel.Slip;
            string ch = isLock ? "Lock" : "Slip";
            ProjectorSettings defaults = ProjectorSettings.CreateShippedDefault(_channel);

            PatternChoices = new[]
            {
                new Choice<NormalizePattern>(NormalizePattern.MaxGripOnly, Strings.Get(isLock ? "Curve.NormalizePattern.MaxGripOnly" : "Curve.NormalizePattern.Slip.MaxGripOnly")),
                new Choice<NormalizePattern>(NormalizePattern.Mapping, Strings.Get(isLock ? "Curve.NormalizePattern.Mapping" : "Curve.NormalizePattern.Slip.Mapping")),
            };
            AnchorNote = Strings.Get("Curve." + ch + ".AnchorNote");
            CriticalLabel = Strings.Get("Curve.Anchor.Critical") + ":";
            CriticalDescription = Strings.Get("Curve." + ch + ".Critical.Desc");

            // Start/End quote the channel's SHIPPED input (CreateShippedDefault, never a hand-typed number).
            Editor = new ProjectorEditorViewModel(_channel, SlightlyLabel, ModerateLabel, CriticalLabel,
                string.Format(Strings.Get("Curve." + ch + ".StartPoint.Desc"), Num(defaults.StartInput)),
                SlightlyDescription, ModerateDescription, CriticalDescription,
                string.Format(Strings.Get("Curve." + ch + ".EndPoint.Desc"), Num(defaults.EndInput)));
            Editor.Load(DraftChannel.Projector);
            Editor.Edited += () => Guarded(OnEditorEdited);
            RecomputeSourceGraph();
        }

        public bool IsLock { get; }

        /// <summary>The shared editor (preset, anchor rows, the normalized curve and its anchor dots).</summary>
        public ProjectorEditorViewModel Editor { get; }

        public IReadOnlyList<Choice<NormalizePattern>> PatternChoices { get; }
        public string AnchorNote { get; }
        public string CriticalLabel { get; }
        public string CriticalDescription { get; }

        /// <summary>Normalize Pattern (two-way): a draft edit (Apply).</summary>
        public NormalizePattern Pattern
        {
            get => DraftChannel.NormalizePattern;
            set
            {
                if (!Edit(DraftChannel.NormalizePattern, value, v => DraftChannel.NormalizePattern = v)) return;
                RaisePatternDependents();
                _keyData?.OnPatternChanged();
            }
        }

        /// <summary>Lock only (Slip has no description of its pattern).</summary>
        public string PatternDescription => !IsLock ? string.Empty
            : Strings.Get(MaxGripOnly ? "Curve.NormalizePattern.MaxGripOnly.Desc" : "Curve.NormalizePattern.Mapping.Desc");

        public string SlightlyLabel => (IsLock
            ? Strings.Get(MaxGripOnly ? "Curve.Anchor.Lock.MaxGripOnly.Slightly" : "Curve.Anchor.Lock.Mapping.Slightly")
            : Strings.Get("Curve.Anchor.Slip.Slightly")) + ":";

        public string SlightlyDescription => IsLock
            ? Strings.Get(MaxGripOnly ? "Curve.Lock.MaxGripOnly.Slightly.Desc" : "Curve.Lock.Mapping.Slightly.Desc")
            : Strings.Get("Curve.Slip.Slightly.Desc");

        public string ModerateLabel => (IsLock
            ? Strings.Get(MaxGripOnly ? "Curve.Anchor.Lock.MaxGripOnly.Ideal" : "Curve.Anchor.Lock.Mapping.Ideal")
            : Strings.Get("Curve.Anchor.Slip.Ideal")) + ":";

        public string ModerateDescription => IsLock
            ? Strings.Get(MaxGripOnly ? "Curve.Lock.MaxGripOnly.Ideal.Desc" : "Curve.Lock.Mapping.Ideal.Desc")
            : Strings.Get("Curve.Slip.Ideal.Desc");

        /// <summary>
        /// The key data points on the NORMALIZED graph, at the positions the four-range curve maps them to by
        /// construction (S75 -> 30, S90 -> 60, SMax -> 80; Slip: Good / Great / Perfect); under Max-Grip-Only
        /// only SMax, since the lower two do not exist.
        /// </summary>
        public IReadOnlyList<GraphMarker> Markers => MaxGripOnly
            ? new[] { new GraphMarker(80.0, Strings.Get(IsLock ? "KeyData.Marker.SMax" : "KeyData.Marker.Perfect")) }
            : new[]
            {
                new GraphMarker(30.0, Strings.Get(IsLock ? "KeyData.Marker.S75" : "KeyData.Marker.Good")),
                new GraphMarker(60.0, Strings.Get(IsLock ? "KeyData.Marker.S90" : "KeyData.Marker.Great")),
                new GraphMarker(80.0, Strings.Get(IsLock ? "KeyData.Marker.SMax" : "KeyData.Marker.Perfect")),
            };

        /// <summary>Source -> projected in value coordinates (x = source 0..100, y = projected 0..100),
        /// sampled every 5 from the key data the page shows (typed, Auto readout or default).</summary>
        public IReadOnlyList<GraphPoint> SourceGraphCurve { get => _sourceGraph; private set => SetProperty(ref _sourceGraph, value); }

        /// <summary>
        /// Connects the channel's key data block: its shown values drive the source graph, and a pattern
        /// edit here reaches it (lower rows shown / hidden / filled). The parent tab view model calls this once.
        /// </summary>
        public void Attach(KeyDataPageViewModel keyData)
        {
            if (_keyData != null) _keyData.PropertyChanged -= OnKeyDataChanged;
            _keyData = keyData;
            if (_keyData != null) _keyData.PropertyChanged += OnKeyDataChanged;
            RecomputeSourceGraph();
        }

        /// <summary>The page's draft source changed: the source graph follows the key data shown for it.</summary>
        public void OnSourceChanged() => Guarded(RecomputeSourceGraph);

        protected override void OnDraftReloaded(DraftReloadedEventArgs e)
        {
            Editor.Load(DraftChannel.Projector);
            RaisePatternDependents();
            base.OnDraftReloaded(e);
        }

        private WheelChannelSettings DraftChannel => IsLock ? S.Lock : S.Slip;
        private bool MaxGripOnly => DraftChannel.NormalizePattern == NormalizePattern.MaxGripOnly;

        private void OnEditorEdited()
        {
            WheelChannelSettings ch = DraftChannel;
            if (ch.Projector == null) ch.Projector = new ProjectorSettings();
            Editor.CopyTo(ch.Projector);
            Ctx.Draft.MarkEdited();
            RecomputeSourceGraph();
        }

        private void OnKeyDataChanged(object sender, PropertyChangedEventArgs e)
        {
            string n = e.PropertyName;
            if (n == null || n == nameof(KeyDataPageViewModel.SMax) || n == nameof(KeyDataPageViewModel.S90) || n == nameof(KeyDataPageViewModel.S75))
                Guarded(RecomputeSourceGraph);
        }

        private void RaisePatternDependents()
        {
            OnPropertyChanged(nameof(Pattern));
            OnPropertyChanged(nameof(PatternDescription));
            OnPropertyChanged(nameof(SlightlyLabel));
            OnPropertyChanged(nameof(SlightlyDescription));
            OnPropertyChanged(nameof(ModerateLabel));
            OnPropertyChanged(nameof(ModerateDescription));
            OnPropertyChanged(nameof(Markers));
            RecomputeSourceGraph();
        }

        private void RecomputeSourceGraph()
        {
            // SourceToProjectedGraph draws on a 100 x 100 canvas (y down): flipped into value coordinates.
            SourceGraphCurve = SourceToProjectedGraph.Points(Editor.Working, IsLock, MaxGripOnly,
                    _keyData?.SMax, _keyData?.S90, _keyData?.S75, 100, 100)
                .Select(p => new GraphPoint(p.X, 100 - p.Y)).ToList();
        }

        private static string Num(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        /// <summary>A page action must never throw into WPF: logged, on the health panel, health list refreshed.</summary>
        private void Guarded(Action action) => Ctx.Guard(action);
    }
}

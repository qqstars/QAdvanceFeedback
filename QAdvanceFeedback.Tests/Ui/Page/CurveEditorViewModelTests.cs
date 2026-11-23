using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui.Page
{
    /// <summary>
    /// The "Output data and shaping" group of the Wheel Lock / Wheel Slip tabs (old SettingsControl.xaml.cs
    /// curve editor ~2866-2990, GRAPH RENDERING ~2990-3219, RefreshLockAnchorLabelsForPattern): normalize
    /// pattern, preset, the five anchors and three flatten ranges, the live labels and both graphs. Every
    /// edit goes into the DRAFT and waits for Apply.
    /// </summary>
    public class CurveEditorViewModelTests
    {
        private static string Json(object o) => JsonConvert.SerializeObject(o);

        [Fact]
        public void A_pattern_edit_writes_the_draft_and_marks_the_page_dirty_not_the_live_settings()
        {
            var rig = new PageRig();
            var vm = new CurveEditorViewModel(rig.Ctx, isLock: true);
            Assert.Equal(NormalizePattern.Mapping, vm.Pattern);

            vm.Pattern = NormalizePattern.MaxGripOnly;

            Assert.Equal(NormalizePattern.MaxGripOnly, rig.Ctx.Settings.Lock.NormalizePattern);
            Assert.Equal(NormalizePattern.Mapping, rig.Host.Settings.Lock.NormalizePattern);
            Assert.True(rig.Draft.IsDirty);
        }

        [Fact]
        public void Lock_anchor_labels_and_the_pattern_description_follow_the_pattern()
        {
            var rig = new PageRig();
            var vm = new CurveEditorViewModel(rig.Ctx, isLock: true);
            Assert.Equal(Strings.Get("Curve.Anchor.Lock.Mapping.Slightly") + ":", vm.SlightlyLabel);
            Assert.Equal(Strings.Get("Curve.Lock.Mapping.Ideal.Desc"), vm.ModerateDescription);
            Assert.Equal(Strings.Get("Curve.NormalizePattern.Mapping.Desc"), vm.PatternDescription);

            vm.Pattern = NormalizePattern.MaxGripOnly;

            Assert.Equal(Strings.Get("Curve.Anchor.Lock.MaxGripOnly.Slightly") + ":", vm.SlightlyLabel);
            Assert.Equal(Strings.Get("Curve.Anchor.Lock.MaxGripOnly.Ideal") + ":", vm.ModerateLabel);
            Assert.Equal(Strings.Get("Curve.Lock.MaxGripOnly.Slightly.Desc"), vm.SlightlyDescription);
            Assert.Equal(Strings.Get("Curve.NormalizePattern.MaxGripOnly.Desc"), vm.PatternDescription);
        }

        [Fact]
        public void Slip_labels_are_fixed_and_it_has_no_pattern_description()
        {
            var rig = new PageRig();
            var vm = new CurveEditorViewModel(rig.Ctx, isLock: false);
            vm.Pattern = NormalizePattern.MaxGripOnly;
            Assert.Equal(Strings.Get("Curve.Anchor.Slip.Slightly") + ":", vm.SlightlyLabel);
            Assert.Equal(Strings.Get("Curve.Anchor.Slip.Ideal") + ":", vm.ModerateLabel);
            Assert.Equal(Strings.Get("Curve.Slip.Critical.Desc"), vm.CriticalDescription);
            Assert.Equal(string.Empty, vm.PatternDescription);
            Assert.Equal(Strings.Get("Curve.NormalizePattern.Slip.MaxGripOnly"), vm.PatternChoices[0].Label);
            Assert.Equal(Strings.Get("Curve.Slip.AnchorNote"), vm.AnchorNote);
        }

        [Fact]
        public void Start_and_end_descriptions_quote_the_channels_shipped_input()
        {
            var rig = new PageRig();
            var vm = new CurveEditorViewModel(rig.Ctx, isLock: true);
            ProjectorSettings d = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            Assert.Equal(string.Format(Strings.Get("Curve.Lock.StartPoint.Desc"), d.StartInput.ToString("0.#", CultureInfo.InvariantCulture)), vm.Editor.Start.Description);
            Assert.Equal(string.Format(Strings.Get("Curve.Lock.EndPoint.Desc"), d.EndInput.ToString("0.#", CultureInfo.InvariantCulture)), vm.Editor.End.Description);
            Assert.Equal(Strings.Get("Curve.Anchor.Critical") + ":", vm.CriticalLabel);
            Assert.Equal(Strings.Get("Curve.Lock.AnchorNote"), vm.AnchorNote);
        }

        [Fact]
        public void An_anchor_edit_writes_the_draft_projector_switches_the_preset_and_dirties_the_page()
        {
            var rig = new PageRig();
            var vm = new CurveEditorViewModel(rig.Ctx, isLock: true);
            Assert.Equal(ProjectorPreset.Curve, vm.Editor.Preset);

            vm.Editor.Moderate.Raw = 55.0;

            Assert.Equal(55.0, rig.Ctx.Settings.Lock.Projector.ModerateInput);
            Assert.Equal(ProjectorPreset.CurveCustom, rig.Ctx.Settings.Lock.Projector.Preset);
            Assert.NotEqual(55.0, rig.Host.Settings.Lock.Projector.ModerateInput);
            Assert.True(rig.Draft.IsDirty);
        }

        [Fact]
        public void A_flatten_range_edit_keeps_the_preset()
        {
            var rig = new PageRig();
            var vm = new CurveEditorViewModel(rig.Ctx, isLock: false);
            vm.Editor.Critical.FlattenRange = 5.0;
            Assert.Equal(5.0, rig.Ctx.Settings.Slip.Projector.CriticalFlattenRange);
            Assert.Equal(ProjectorPreset.Curve, rig.Ctx.Settings.Slip.Projector.Preset);
            Assert.True(rig.Draft.IsDirty);
        }

        [Fact]
        public void A_preset_rewrites_the_anchors_in_the_draft()
        {
            var rig = new PageRig();
            var vm = new CurveEditorViewModel(rig.Ctx, isLock: true);
            vm.Editor.Preset = ProjectorPreset.Linear;

            var expected = new ProjectorSettings();
            rig.Host.Settings.Lock.Projector.CopyTo(expected);
            expected.ApplyPreset(ProjectorPreset.Linear, ProjectionChannel.Lock);
            Assert.Equal(Json(expected), Json(rig.Ctx.Settings.Lock.Projector));
            Assert.Equal(expected.ModerateInput, vm.Editor.Moderate.Raw);
        }

        [Fact]
        public void A_draft_reload_shows_the_adopted_values()
        {
            var rig = new PageRig();
            var vm = new CurveEditorViewModel(rig.Ctx, isLock: true);
            rig.Host.Settings.Lock.Projector.SlightlyOutput = 17.0;
            rig.Host.Settings.Lock.NormalizePattern = NormalizePattern.MaxGripOnly;

            rig.Adopt();

            Assert.Equal(17.0, vm.Editor.Slightly.Output);
            Assert.Equal(NormalizePattern.MaxGripOnly, vm.Pattern);
            Assert.Equal(Strings.Get("Curve.NormalizePattern.MaxGripOnly.Desc"), vm.PatternDescription);
            Assert.False(rig.Draft.IsDirty);
        }

        [Fact]
        public void The_normalized_graph_marks_the_key_points_at_30_60_80_or_only_80_under_max_grip_only()
        {
            var rig = new PageRig();
            var lockVm = new CurveEditorViewModel(rig.Ctx, isLock: true);
            Assert.Equal(new[] { 30.0, 60.0, 80.0 }, lockVm.Markers.Select(m => m.At));
            Assert.Equal(new[] { Strings.Get("KeyData.Marker.S75"), Strings.Get("KeyData.Marker.S90"), Strings.Get("KeyData.Marker.SMax") },
                lockVm.Markers.Select(m => m.Label));

            var slipVm = new CurveEditorViewModel(rig.Ctx, isLock: false);
            Assert.Equal(new[] { Strings.Get("KeyData.Marker.Good"), Strings.Get("KeyData.Marker.Great"), Strings.Get("KeyData.Marker.Perfect") },
                slipVm.Markers.Select(m => m.Label));

            lockVm.Pattern = NormalizePattern.MaxGripOnly;
            Assert.Equal(new[] { 80.0 }, lockVm.Markers.Select(m => m.At));
            Assert.Equal(Strings.Get("KeyData.Marker.SMax"), lockVm.Markers[0].Label);
        }

        [Fact]
        public void The_normalized_graph_is_the_editors_curve()
        {
            var rig = new PageRig();
            var vm = new CurveEditorViewModel(rig.Ctx, isLock: true);
            var p = new OutputProjector(rig.Ctx.Settings.Lock.Projector);
            Assert.Equal(101, vm.Editor.Curve.Count);
            Assert.Equal(p.Project(50), vm.Editor.Curve[50].Y, 9);
        }

        [Fact]
        public void The_source_graph_samples_the_shown_key_data_through_the_working_curve_and_follows_it()
        {
            var rig = new PageRig();
            var keyData = new KeyDataPageViewModel(rig.Ctx, isLock: true);
            var vm = new CurveEditorViewModel(rig.Ctx, isLock: true);
            vm.Attach(keyData);
            keyData.Auto = false;

            System.Collections.Generic.IReadOnlyList<GraphPoint> Expected() =>
                SourceToProjectedGraph.Points(rig.Ctx.Settings.Lock.Projector, true,
                        rig.Ctx.Settings.Lock.NormalizePattern == NormalizePattern.MaxGripOnly,
                        keyData.SMax, keyData.S90, keyData.S75, 100, 100)
                    .Select(g => new GraphPoint(g.X, 100 - g.Y)).ToList();

            Assert.Equal(21, vm.SourceGraphCurve.Count);
            Assert.Equal(Expected().Select(g => g.Y), vm.SourceGraphCurve.Select(g => g.Y));

            keyData.SMax = 40.0;
            Assert.Equal(Expected().Select(g => g.Y), vm.SourceGraphCurve.Select(g => g.Y));

            vm.Editor.Critical.Output = 90.0;
            Assert.Equal(Expected().Select(g => g.Y), vm.SourceGraphCurve.Select(g => g.Y));
        }

        [Fact]
        public void A_pattern_edit_reaches_the_attached_key_data()
        {
            var rig = new PageRig();
            var keyData = new KeyDataPageViewModel(rig.Ctx, isLock: true);
            var vm = new CurveEditorViewModel(rig.Ctx, isLock: true);
            vm.Attach(keyData);
            Assert.True(keyData.ShowLowerAnchors);

            vm.Pattern = NormalizePattern.MaxGripOnly;

            Assert.False(keyData.ShowLowerAnchors);
        }
    }
}

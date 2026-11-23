using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The plugin page's G-Force tab (docs\full-mvvm-plan.md §3.2), bound by <c>Ui\Views\GForcePageView</c>.
    /// Every input is a page edit on the DRAFT (applied with Apply); a ShakeIt-side change reaches the tab
    /// through the draft's reload. It holds:
    /// <list type="bullet">
    /// <item>Maximum G: three <see cref="GForcePageAxisViewModel"/> rows, the three output scales and the
    /// recommended-frequency note;</item>
    /// <item>Cornering split, Sustained feel, Motion feel and Left/right direction - plain draft fields;
    /// an empty box keeps its value (the old <c>Value ?? g.X</c>);</item>
    /// <item>the shake block - the existing <see cref="GForceShakeViewModel"/>, hosted: loaded from the
    /// draft (never an edit), its <see cref="GForceShakeViewModel.Edited"/> writes it back into the draft.
    /// Its rules stay there, the owner's mode-pick scale reset included;</item>
    /// <item>the preview graph data (<see cref="PreviewLeft"/> / <see cref="PreviewRight"/>), rebuilt when
    /// the shake's frequency, hold or feeling changes;</item>
    /// <item>the Test Effect - the shared <see cref="GForceTestViewModel"/> on a THROWAWAY copy of the
    /// draft's G-Force settings (the unapplied values included; nothing is saved, nothing marks dirty).</item>
    /// </list>
    /// While shown (<see cref="Activate"/>) the readouts refresh once a second; hiding the tab stops that
    /// timer and the Test Effect.
    /// </summary>
    public sealed class GForcePageViewModel : PageSectionViewModel, IActivatable
    {
        private static readonly TimeSpan ReadoutRefresh = TimeSpan.FromSeconds(1);

        private IDisposable _refresh;
        private GForceSettings _testSettings;
        private GForcePageShakePreview _preview;
        private bool _loadingShake;

        public GForcePageViewModel(SettingsPageContext ctx) : base(ctx)
        {
            AccelMax = new GForcePageAxisViewModel(ctx, SettingField.AccelMaxMode);
            DecelMax = new GForcePageAxisViewModel(ctx, SettingField.DecelMaxMode);
            LatMax = new GForcePageAxisViewModel(ctx, SettingField.LatMaxMode);
            foreach (GForcePageAxisViewModel axis in new[] { AccelMax, DecelMax, LatMax })
                axis.PropertyChanged += (s, e) => { if (e.PropertyName != nameof(GForcePageAxisViewModel.Readout)) _testSettings = null; };

            Shake = new GForceShakeViewModel();
            LoadShake();
            Shake.Edited += OnShakeEdited;
            Shake.PropertyChanged += OnShakePropertyChanged;
            _preview = BuildPreview();

            Test = new GForceTestViewModel(ctx.Host, SettingsForTest, ctx.Ticker, ctx.Ui);
        }

        private GForceSettings G => S.GForce;

        // ---------------- Maximum G ----------------

        public GForcePageAxisViewModel AccelMax { get; }
        public GForcePageAxisViewModel DecelMax { get; }
        public GForcePageAxisViewModel LatMax { get; }

        public double? AccelScale { get => G.AccelOutputScalePercent; set => EditNumber(G.AccelOutputScalePercent, value, v => G.AccelOutputScalePercent = v); }
        public double? BrakeScale { get => G.BrakeOutputScalePercent; set => EditNumber(G.BrakeOutputScalePercent, value, v => G.BrakeOutputScalePercent = v); }
        public double? LateralScale { get => G.LateralOutputScalePercent; set => EditNumber(G.LateralOutputScalePercent, value, v => G.LateralOutputScalePercent = v); }

        /// <summary>"Recommended pad frequency ...: {from} Hz at value 0, down to {to} Hz at value 100."</summary>
        public string RecommendedHzText => string.Format(Strings.Get("GForce.RecommendedHz.Note"), G.RecommendedFromHz, G.RecommendedToHz);

        // ---------------- Cornering split ----------------

        public double? BrakeBottomFrontLatSplit { get => G.BrakeBottomFrontLatSplitPercent; set => EditNumber(G.BrakeBottomFrontLatSplitPercent, value, v => G.BrakeBottomFrontLatSplitPercent = v); }
        public double? BrakeBottomRearLatSplit { get => G.BrakeBottomRearLatSplitPercent; set => EditNumber(G.BrakeBottomRearLatSplitPercent, value, v => G.BrakeBottomRearLatSplitPercent = v); }
        public double? BrakeBackLowLatSplit { get => G.BrakeBackLowLatSplitPercent; set => EditNumber(G.BrakeBackLowLatSplitPercent, value, v => G.BrakeBackLowLatSplitPercent = v); }
        public double? AccelBottomRearLatSplit { get => G.AccelBottomRearLatSplitPercent; set => EditNumber(G.AccelBottomRearLatSplitPercent, value, v => G.AccelBottomRearLatSplitPercent = v); }
        public double? AccelBackLowLatSplit { get => G.AccelBackLowLatSplitPercent; set => EditNumber(G.AccelBackLowLatSplitPercent, value, v => G.AccelBackLowLatSplitPercent = v); }
        public double? AccelBackTopLatSplit { get => G.AccelBackTopLatSplitPercent; set => EditNumber(G.AccelBackTopLatSplitPercent, value, v => G.AccelBackTopLatSplitPercent = v); }

        // ---------------- Sustained feel ----------------

        public double? BrakeBottomRearSustain { get => G.BrakeBottomRearSustainPercent; set => EditNumber(G.BrakeBottomRearSustainPercent, value, v => G.BrakeBottomRearSustainPercent = v); }
        public double? BrakeBackLowSustain { get => G.BrakeBackLowSustainPercent; set => EditNumber(G.BrakeBackLowSustainPercent, value, v => G.BrakeBackLowSustainPercent = v); }
        public double? AccelBottomRearSustain { get => G.AccelBottomRearSustainPercent; set => EditNumber(G.AccelBottomRearSustainPercent, value, v => G.AccelBottomRearSustainPercent = v); }
        public double? AccelBackLowSustain { get => G.AccelBackLowSustainPercent; set => EditNumber(G.AccelBackLowSustainPercent, value, v => G.AccelBackLowSustainPercent = v); }

        // ---------------- Motion feel ----------------

        public double? SustainTau { get => G.SustainTimeConstantSeconds; set => EditNumber(G.SustainTimeConstantSeconds, value, v => G.SustainTimeConstantSeconds = v); }
        public double? TransientTau { get => G.TransientTimeConstantSeconds; set => EditNumber(G.TransientTimeConstantSeconds, value, v => G.TransientTimeConstantSeconds = v); }
        public double? TransientGain { get => G.TransientGain; set => EditNumber(G.TransientGain, value, v => G.TransientGain = v); }
        public double? RetriggerStrictness { get => G.RetriggerStrictness; set => EditNumber(G.RetriggerStrictness, value, v => G.RetriggerStrictness = v); }
        public double? AutoTransitionScale { get => G.AutoTransitionAnimationScale; set => EditNumber(G.AutoTransitionAnimationScale, value, v => G.AutoTransitionAnimationScale = v); }
        public double? FixedTransitionScale { get => G.FixedTransitionAnimationScale; set => EditNumber(G.FixedTransitionAnimationScale, value, v => G.FixedTransitionAnimationScale = v); }

        // ---------------- Left/right direction ----------------

        /// <summary>The Lateral feedback direction dropdown (SelectedValue, two-way).</summary>
        public LateralDirectionMode LateralDirection
        {
            get => G.LateralDirection;
            set { if (Edit(G.LateralDirection, value, v => G.LateralDirection = v)) _testSettings = null; }
        }

        // ---------------- the shake block, its preview, the Test Effect ----------------

        /// <summary>The shake block (the existing view model, hosted on the draft).</summary>
        public GForceShakeViewModel Shake { get; }

        /// <summary>The preview's left trace, unit coordinates (seconds, level) - see <see cref="GForcePageShakePreview"/>.</summary>
        public IReadOnlyList<GraphPoint> PreviewLeft => _preview.Left;

        /// <summary>The preview's right trace.</summary>
        public IReadOnlyList<GraphPoint> PreviewRight => _preview.Right;

        /// <summary>The Test Effect group (the shared view model the G-Force ShakeIt panels use).</summary>
        public GForceTestViewModel Test { get; }

        // ---------------- lifetime ----------------

        /// <summary>The tab is shown: re-query the readouts now and once a second.</summary>
        public void Activate()
        {
            _refresh?.Dispose();
            RefreshReadouts();
            _refresh = Ctx.Ticker.Every(ReadoutRefresh, RefreshReadouts);
        }

        /// <summary>The tab is hidden: stop the readout timer; the Test Effect hands the outputs back to
        /// the live pipeline.</summary>
        public void Deactivate()
        {
            _refresh?.Dispose();
            _refresh = null;
            Test.Stop();
        }

        protected override void OnDraftReloaded(DraftReloadedEventArgs e)
        {
            _testSettings = null;
            LoadShake();
            base.OnDraftReloaded(e);
        }

        // ---------------- helpers ----------------

        private void RefreshReadouts()
        {
            AccelMax.RefreshReadout();
            DecelMax.RefreshReadout();
            LatMax.RefreshReadout();
        }

        /// <summary>A number box: empty keeps the value (old <c>Value ?? g.X</c>); otherwise a draft edit.</summary>
        private void EditNumber(double current, double? value, Action<double> write, [CallerMemberName] string propertyName = null)
        {
            if (!value.HasValue) return;
            if (Edit(current, value.Value, write, propertyName)) _testSettings = null;
        }

        private void LoadShake()
        {
            _loadingShake = true;
            try { Shake.Load(G); }
            finally { _loadingShake = false; }
        }

        private void OnShakeEdited()
        {
            if (_loadingShake) return;
            Shake.SaveTo(G);
            _testSettings = null;
            Ctx.Draft.MarkEdited();
        }

        private void OnShakePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(GForceShakeViewModel.Frequency):
                case nameof(GForceShakeViewModel.Sustain):
                case nameof(GForceShakeViewModel.Feeling):
                    _preview = BuildPreview();
                    OnPropertyChanged(nameof(PreviewLeft));
                    OnPropertyChanged(nameof(PreviewRight));
                    break;
            }
        }

        private GForcePageShakePreview BuildPreview() => GForcePageShakePreview.Build(Shake.Frequency, Shake.Sustain, Shake.Feeling);

        // The Test Effect's settings: a throwaway copy of the draft's G-Force block, rebuilt after any edit
        // or reload (not every frame), so the test runs on exactly what the page shows and can never write it.
        internal GForceSettings SettingsForTest()
        {
            if (_testSettings == null) _testSettings = SettingsJsonLeaves.DeepCopy(G);
            return _testSettings;
        }
    }
}

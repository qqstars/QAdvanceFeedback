using System;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The Custom source's cold-start reference (SMax / S90 / S75) under the key data points of the Wheel Lock /
    /// Wheel Slip tab - what the plugin ASSUMES until it has learned (owner, 2026-09-29). Only Custom has one:
    /// the presets ship measured references.
    /// <list type="bullet">
    /// <item>Shown only while the combo shows Custom; disabled while the channel is shadowed to Raw (Raw's
    /// reference is fixed, so an edit would change nothing).</item>
    /// <item>NEVER "---" (owner: "the cold-start reference should ALWAYS have particular value, duplicated from
    /// the known source, or the adjusted value by the user"): every arrival at Custom seeds it from the source
    /// left; if nothing is stored, Raw's reference stands in - shown without marking the page dirty, and saved by
    /// the next Apply (<see cref="StoreShownDefaultIfMissing"/>, the 1.1.0 rule).</item>
    /// <item>An edit writes the draft (applied on Apply) only when the three values form a usable reference;
    /// otherwise the stored one is kept (the old save's rule) while the editors show what was typed. Each value
    /// is capped at the hard SMax limit (<see cref="KeyDataPointSettings.MaxSMax"/>, 95).</item>
    /// </list>
    /// </summary>
    public sealed class CustomReferenceViewModel : PageSectionViewModel
    {
        private readonly SourcesViewModel _sources;
        private double? _sMax, _s90, _s75;

        public CustomReferenceViewModel(SettingsPageContext ctx, bool isLock, SourcesViewModel sources) : base(ctx)
        {
            IsLock = isLock;
            _sources = sources ?? throw new ArgumentNullException(nameof(sources));
            Header = Strings.Get("Sources.CustomRef.Header");
            Description = Strings.Get("Sources.CustomRef.Desc");
            SMaxLabel = Strings.Get("Sources.CustomRef.SMax");
            S90Label = Strings.Get("Sources.CustomRef.S90");
            S75Label = Strings.Get("Sources.CustomRef.S75");
            Refresh();
        }

        public bool IsLock { get; }
        public string Header { get; }
        public string Description { get; }
        public string SMaxLabel { get; }
        public string S90Label { get; }
        public string S75Label { get; }

        public bool IsVisible => _sources.Mode == SourceMode.Custom;
        public bool IsEnabled => !_sources.IsShadowing;

        public double? SMax { get => _sMax; set => Set(ref _sMax, value); }
        public double? S90 { get => _s90; set => Set(ref _s90, value); }
        public double? S75 { get => _s75; set => Set(ref _s75, value); }

        private KeyDataPointDefaultSet Stored
        {
            get
            {
                KeyDataPointDefaults d = S.KeyDataPointDefaults;
                return IsLock ? d?.LockCustom : d?.SlipCustom;
            }
        }

        /// <summary>Re-reads the stored reference (or Raw's stand-in) and the visibility.</summary>
        public void Refresh()
        {
            KeyDataPointDefaultSet set = Shown();
            _sMax = set?.SMax;
            _s90 = set?.S90;
            _s75 = set?.S75;
            OnPropertyChanged(null);
        }

        /// <summary>
        /// Called by Apply (the 1.1.0 rule, owner 2026-11-20): while the channel is on Custom with no usable
        /// stored reference, the shown stand-in is written into the draft so this Apply saves it - the value
        /// becomes the driver's just as a typed one would. Showing it never marks the page dirty.
        /// </summary>
        public void StoreShownDefaultIfMissing()
        {
            if (!IsVisible || S.KeyDataPointDefaults == null) return;
            KeyDataPointDefaultSet stored = Stored;
            if (stored != null && stored.IsUsable()) return;
            KeyDataPointDefaultSet shown = Shown();
            if (shown == null || !shown.IsUsable()) return;
            if (IsLock) S.KeyDataPointDefaults.LockCustom = shown;
            else S.KeyDataPointDefaults.SlipCustom = shown;
            Ctx.Draft.MarkEdited();
        }

        private KeyDataPointDefaultSet Shown()
        {
            KeyDataPointDefaultSet set = Stored;
            if (set != null && set.IsUsable()) return set;
            // The backstop: Raw is always present and always on a 0-100 scale, so its reference is the only
            // honest thing to assume about a source we know nothing about.
            KeyDataPointDefaults defaults = S.KeyDataPointDefaults ?? KeyDataPointDefaults.CreateShipped();
            return defaults.TryResolve(KnownFeedbackSource.QAdvanceFeedbackRaw, IsLock, out double a, out double b, out double c)
                ? new KeyDataPointDefaultSet(a, b, c) : null;
        }

        private void Set(ref double? field, double? value, [System.Runtime.CompilerServices.CallerMemberName] string name = null)
        {
            if (!IsEnabled || !value.HasValue) { OnPropertyChanged(name); return; }
            field = KeyDataPointSettings.AtMostMaxSMax(value.Value);
            OnPropertyChanged(name);

            var candidate = new KeyDataPointDefaultSet(_sMax ?? 0.0, _s90 ?? 0.0, _s75 ?? 0.0);
            if (!candidate.IsUsable() || S.KeyDataPointDefaults == null) return;   // keep the stored one
            KeyDataPointDefaultSet stored = Stored;
            if (stored != null && stored.SMax == candidate.SMax && stored.S90 == candidate.S90 && stored.S75 == candidate.S75) return;
            if (IsLock) S.KeyDataPointDefaults.LockCustom = candidate;
            else S.KeyDataPointDefaults.SlipCustom = candidate;
            Ctx.Draft.MarkEdited();
        }

        protected override void OnDraftReloaded(DraftReloadedEventArgs e) => Refresh();
    }
}

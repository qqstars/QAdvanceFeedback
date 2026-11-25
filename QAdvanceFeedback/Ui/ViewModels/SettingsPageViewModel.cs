using System;
using System.Collections.Generic;
using System.Windows.Input;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The plugin settings page (docs\full-mvvm-plan.md §3.1-3.2): owns the <see cref="SettingsDraft"/>,
    /// the hub subscription, Apply / Restore all defaults, the dirty state behind the Apply button and the
    /// unapplied-changes banner, and the tab view models.
    /// <para/>
    /// THE HUB SUBSCRIPTION LIVES AS LONG AS THIS VIEW MODEL, from construction to <see cref="Dispose"/>
    /// (owner-reported defect: the old page listened only while Loaded, with no catch-up before its first
    /// Loaded, so the first ShakeIt-side change was missed). Every hub event, from any origin, adopts the
    /// live changes into the draft (ShakeIt wins); <see cref="Activate"/> adopts too, for changes nobody
    /// signalled (runtime seeds). The plugin creates ONE of these and disposes it with itself.
    /// </summary>
    public sealed class SettingsPageViewModel : ObservableObject, IActivatable, IDisposable
    {
        private readonly ISettingsPageHost _host;
        private readonly IUiDispatcher _ui;
        private readonly SettingsDraft _draft;
        private readonly RelayCommand _apply;
        private SettingsHub _hub;
        private bool _disposed;
        private bool _savingSlipAngle;

        public SettingsPageViewModel(ISettingsPageHost host, IUiDispatcher ui, ITicker ticker,
            IDialogService dialogs, IClipboard clipboard)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _draft = new SettingsDraft(() => _host.Settings);
            Context = new SettingsPageContext(_draft, host, ui, ticker, dialogs, clipboard);

            _apply = new RelayCommand(() => Context.Guard(Apply), () => IsDirty);
            ApplyCommand = _apply;
            RestoreDefaultsCommand = new RelayCommand(RestoreDefaults);
            _draft.DirtyChanged += (s, e) => RaiseDirtyChanged();

            LockTab = new WheelChannelPageViewModel(Context, isLock: true);
            SlipTab = new WheelChannelPageViewModel(Context, isLock: false);
            // THE VIPER GAME BUTTON MOVES BOTH CHANNELS onto another source (the supported-game list is not
            // per channel): a pending key data edit on EITHER tab belongs to the source being left, so both
            // are written first (the old page's FlushKeyDataPersistNow before the list changed).
            LockTab.Sources.BeforeViperGameToggle += (s, e) => FlushKeyData();
            SlipTab.Sources.BeforeViperGameToggle += (s, e) => FlushKeyData();
            GForceTab = new GForcePageViewModel(Context);

            if (host is ISlipAngleTabHost slipAngleHost)
            {
                SlipAngleTab = new SlipAngleTabViewModel(slipAngleHost, ticker);
                SlipAngleTab.Load(_draft.Settings.SlipAngle);
                SlipAngleTab.Edited += OnSlipAngleEdited;
                SlipAngleTab.TelemetryPendingChanged += RaiseDirtyChanged;
            }
            if (host is IClutchHost clutchHost)
            {
                // Learning parameters: the plugin page only. It writes through the hub at once (never part of
                // Apply); the draft adopts those writes like any other.
                ClutchTab = new ClutchBiteViewModel(host, clutchHost, ticker, ui, showLearning: true);
                ClutchTab.Refresh();   // show the values before the tab is first opened
            }
            GeneralTab = new GeneralViewModel(Context);

            _draft.Reloaded += OnDraftReloaded;
            _hub = host.Hub;
            if (_hub != null) _hub.Changed += OnHubChanged;
        }

        public SettingsPageContext Context { get; }

        /// <summary>The page holds edits not yet applied: enables Apply and shows the banner - one fact,
        /// so the two can never disagree. The Slip Angle tab's per-wheel telemetry source is not a setting (it lives in
        /// QAdvanceFeedback.SlipAngleSource.json), so its pending edit counts here too.</summary>
        public bool IsDirty => _draft.IsDirty || (SlipAngleTab?.HasPendingTelemetry ?? false);

        private void RaiseDirtyChanged()
        {
            OnPropertyChanged(nameof(IsDirty));
            _apply.RaiseCanExecuteChanged();
        }

        /// <summary>Writes the page's edits into the live settings, then the plugin applies, saves and
        /// broadcasts them (the plugin's own broadcast - it knows the game and the Slip Angle tier).</summary>
        public ICommand ApplyCommand { get; }

        /// <summary>Asks first, then resets every setting to its shipped default (key data, retuned
        /// defaults and Custom sources carried across), applied and saved at once.</summary>
        public ICommand RestoreDefaultsCommand { get; }

        public string ApplyLabel => Strings.Get("Common.Apply");
        public string RestoreDefaultsLabel => Strings.Get("Common.RestoreAllDefaults");
        public string UnappliedBannerText => Strings.Get("Apply.Unapplied.Banner");

        public WheelChannelPageViewModel LockTab { get; }
        public WheelChannelPageViewModel SlipTab { get; }
        /// <summary>Null when the host is not an <see cref="ISlipAngleTabHost"/>.</summary>
        public SlipAngleTabViewModel SlipAngleTab { get; }
        public GForcePageViewModel GForceTab { get; }
        /// <summary>Null when the host is not an <see cref="IClutchHost"/>.</summary>
        public ClutchBiteViewModel ClutchTab { get; }
        public GeneralViewModel GeneralTab { get; }

        /// <summary>The page is shown: catch up with live changes nobody signalled.</summary>
        public void Activate()
        {
            if (_disposed) return;
            Context.Guard(() => _draft.AdoptExternal());
        }

        /// <summary>The page is hidden. The hub subscription stays (it must not miss a change).</summary>
        public void Deactivate() { }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_hub != null) _hub.Changed -= OnHubChanged;
            _hub = null;
        }

        private void OnHubChanged(object sender, SettingsChangedEventArgs e)
        {
            // Any thread (a ShakeIt panel, the config file watcher, the data thread): the draft is only
            // touched on the UI thread. Our own Apply's broadcast is harmless - after Apply nothing differs
            // from the baseline, and an adopt only touches leaves the live settings changed.
            _ui.Post(() =>
            {
                if (_disposed) return;
                Context.Guard(() => _draft.AdoptExternal());
                // The Slip Angle key data is keyed by the identity tier, which is not a setting: a tier /
                // availability change from the plugin re-reads it (the old page's RefreshFields(SourceAvailability)).
                if (SlipAngleTab != null && e != null && e.Origin != SettingsOrigin.PluginPage && Contains(e.Fields, SettingField.SourceAvailability))
                    Context.Guard(() => SlipAngleTab.RefreshFields(new[] { SettingField.SourceAvailability }));
            });
        }

        // ---------------------------------------------------------------- Slip Angle tab

        /// <summary>
        /// The Slip Angle tab keeps its own field state (it is shared with nothing else); the page's draft is
        /// its store: every driver edit is written into the draft at once (so Apply, the dirty state and the
        /// banner treat it like every other tab - the old page's MarkDirty + SaveToSettings), and a reload
        /// of the draft's Slip Angle subtree (a ShakeIt-side change, Restore defaults) loads it back. Its key
        /// data is not in the draft's hands: it applies and saves itself at once, like the Lock tab's.
        /// </summary>
        private void OnSlipAngleEdited() => Context.Guard(() =>
        {
            QAdvanceFeedbackSettings s = _draft.Settings;
            if (s.SlipAngle == null) s.SlipAngle = new SlipAngleChannelSettings();
            _savingSlipAngle = true;
            try { SlipAngleTab.SaveTo(s.SlipAngle); }
            finally { _savingSlipAngle = false; }
            _draft.MarkEdited();
        });

        private void OnDraftReloaded(object sender, DraftReloadedEventArgs e)
        {
            if (SlipAngleTab == null || _savingSlipAngle || !e.Touches("SlipAngle")) return;
            Context.Guard(() =>
            {
                // Key data: THE NEWER OUTSIDE WRITE WINS over a pending typed value (the old RefreshFields).
                if (e.Touches("SlipAngle.KeyData")) SlipAngleTab.RefreshFields(new[] { SettingField.SlipAngleKeyDataValues });
                SlipAngleTab.Load(_draft.Settings.SlipAngle);
            });
        }

        // ---------------------------------------------------------------- Apply / Restore

        private void Apply()
        {
            // 1.1.0 rule: a Custom channel's shown stand-in cold-start reference is saved by this Apply.
            LockTab.CustomReference.StoreShownDefaultIfMissing();
            SlipTab.CustomReference.StoreShownDefaultIfMissing();
            // The Slip Angle per-wheel telemetry source goes to its own file (a refused one stays on the tab, red;
            // the rest of the Apply goes through).
            SlipAngleTab?.ApplyTelemetrySource();
            _draft.Apply();
            // The plugin applies, saves and broadcasts the diff against what it last applied, with the
            // running game and the Slip Angle identity tier (ruling: the page does not broadcast again).
            _host.ApplySettings();
            // Whatever the plugin normalised while applying, and any change made live since the last
            // sync that was not edited here (Apply left it alone), comes back into the draft.
            _draft.AdoptExternal();
            RaiseDirtyChanged();
        }

        // TODAY'S RULE (Settings\SettingsControl.xaml.cs RestoreAllDefaults): confirm, then
        // QAdvanceFeedbackSettings.RestoreDefaults on the LIVE settings - key data, retuned defaults and
        // the Custom sources carried across - applied and saved at once, page clean. Running the same
        // method on a copy of live keeps that carry-across rule in one place; building from live (not
        // the draft) discards unapplied page edits exactly as the old reload-from-live did.
        private void RestoreDefaults()
        {
            if (!Context.Dialogs.Confirm(Strings.Get("Common.RestoreAllConfirm"), Strings.Get("Common.DialogTitle")))
                return;
            Context.Guard(() =>
            {
                _draft.AdoptExternal();
                QAdvanceFeedbackSettings values = SettingsJsonLeaves.DeepCopy(_host.Settings ?? _draft.Settings);
                values.RestoreDefaults();
                _draft.ReplaceDraft(values);
                Apply();
            });
        }

        private void FlushKeyData()
        {
            LockTab.KeyData.FlushPendingSave();
            SlipTab.KeyData.FlushPendingSave();
        }

        private static bool Contains(IReadOnlyCollection<SettingField> fields, SettingField field)
        {
            if (fields == null) return false;
            foreach (SettingField f in fields) if (f == field) return true;
            return false;
        }
    }
}

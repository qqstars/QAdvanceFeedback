using System;
using System.Globalization;
using System.Windows.Input;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The settings page's own key data rules for one Wheel Lock / Wheel Slip channel, on top of the shared
    /// <see cref="KeyDataPanelModel"/> rules (pure, ported from the old SettingsControl.xaml.cs
    /// RefreshKeyDataChannel / FillLowerAnchorsIfMappingSelected / CanRestoreKeyDataDefaults / FormatLearned).
    /// </summary>
    public static class KeyDataPageRules
    {
        /// <summary>
        /// The status line under the values (old RefreshKeyDataChannel): nothing under Auto; the "no game"
        /// note for Per-Game with no game (a dead end that must say so); the warm-up note ONLY for an unknown
        /// source with nothing configured and the gate still closed; otherwise the channel's invalid-triple
        /// message when the shown values (lower two derived under Max-Grip-Only) are not a valid triple.
        /// </summary>
        public static string Status(bool isLock, bool auto, bool perGameWithoutGame, bool knownSource, bool manualLive,
            double? sMax, double? s90, double? s75, bool maxGripOnly)
        {
            if (auto) return string.Empty;
            if (perGameWithoutGame) return Strings.Get("KeyData.PerGame.NoGame");

            bool configured = (sMax ?? 0.0) > 0.0;
            if (!knownSource && !configured && !manualLive) return Strings.Get("KeyData.Waiting");
            if (!configured) return string.Empty;

            double a = sMax ?? 0.0, b = s90 ?? 0.0, c = s75 ?? 0.0;
            if (maxGripOnly) KeyDataPointSettings.DeriveLowerAnchors(a, out b, out c);
            return KeyDataPointSettings.IsValid(a, b, c) ? string.Empty : Strings.Get(isLock ? "KeyData.Invalid" : "KeyData.Invalid.Slip");
        }

        /// <summary>The learned-value hint: one decimal, right-aligned in 5 characters so 9.3 and 10.5 line
        /// up in the monospace column (owner's requirement), or the pending text.</summary>
        public static string FormatLearned(double? learned) =>
            learned.HasValue && learned.Value > 0.0
                ? string.Format(Strings.Get("KeyData.LearnedPrefix"), learned.Value.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(5))
                : Strings.Get("KeyData.LearnedPending");

        /// <summary>
        /// Whether the dedicated reset shows: the source's whole key data configuration differs from what
        /// ships - never for the Custom MODE, which a driver's configuration can reach while classifying as
        /// a preset (Raw's own rows), and never for a source with no shipped default
        /// (<see cref="KeyDataPointSettings.MatchesDefaults"/>).
        /// </summary>
        public static bool CanRestore(KeyDataPointSettings k, SourceMode draftMode, string sourceIdentity, bool isLock, KeyDataPointDefaults defaults)
        {
            if (k == null || draftMode == SourceMode.Custom) return false;
            return !k.MatchesDefaults(sourceIdentity, isLock, defaults);
        }

        /// <summary>
        /// Switching to the three-point mapping reveals two rows that may be empty: fill them on the way in
        /// - the shipped defaults when the source is a shipped one and SMax is empty (all three) or still the
        /// shipped number (the missing companions), otherwise derived from SMax (0.90 / 0.70, one decimal).
        /// Values already there are never overwritten. Null when nothing needs filling.
        /// </summary>
        public static KeyDataTriple FillLowerAnchors(double? sMaxShown, double? s90Shown, double? s75Shown,
            string sourceIdentity, bool isLock, KeyDataPointDefaults defaults)
        {
            bool s90Missing = (s90Shown ?? 0.0) <= 0.0;
            bool s75Missing = (s75Shown ?? 0.0) <= 0.0;
            if (!s90Missing && !s75Missing) return null;

            double sMax = sMaxShown ?? 0.0;
            double s90 = s90Shown ?? 0.0, s75 = s75Shown ?? 0.0;
            bool shipped = KeyDataPointSettings.TryResolveShippedDefaults(sourceIdentity, isLock,
                defaults ?? KeyDataPointDefaults.CreateShipped(), out double dMax, out double d90, out double d75);

            if (shipped && sMax <= 0.0) return new KeyDataTriple(dMax, d90, d75);
            if (shipped && Math.Abs(sMax - dMax) < 0.05)
                return new KeyDataTriple(sMax, s90Missing ? d90 : s90, s75Missing ? d75 : s75);
            if (sMax > 0.0)
            {
                KeyDataPointSettings.DeriveLowerAnchors(sMax, out double r90, out double r75);
                return new KeyDataTriple(sMax, s90Missing ? Math.Round(r90, 1) : s90, s75Missing ? Math.Round(r75, 1) : s75);
            }
            return null;
        }
    }

    /// <summary>
    /// The key data block of a Wheel Lock / Wheel Slip tab (docs\full-mvvm-plan.md §3.2; old
    /// SettingsControl.xaml.cs "KEY DATA POINTS"): Auto / Per-Game, SMax / S90 / S75 (Perfect / Great / Good),
    /// the learned hints, the status line, the dedicated reset and the shadow note.
    /// <list type="bullet">
    /// <item>SAME RULES AS THE SHAKEIT PANEL'S <see cref="KeyDataViewModel"/>: what is shown is
    /// <see cref="KeyDataPanelModel.Resolve"/> (the one display rule, learned values by
    /// <see cref="IPanelHost.LearnedKeyData"/>) and typed values go through
    /// <see cref="KeyDataPanelModel.TryCommitManual"/>.</item>
    /// <item>THE PAGE'S DRAFT SOURCE: everything is keyed by the source the page shows (unapplied mode,
    /// rows, ShakeIt algorithm), or by Raw while that source is shadowed - never by the applied one.
    /// <see cref="OnSourceChanged"/> follows a source edit.</item>
    /// <item>APPLIES AND SAVES IMMEDIATELY, NEVER THROUGH APPLY (owner, 2026-09-29): the key data store is
    /// the LIVE settings' (the draft adopts it later, leaving the page clean). A typed value is written
    /// synchronously - the slot is latched as the driver's before any frame can run the one-time seed
    /// (owner-reported, 2026-09-30) - and saved 700 ms after the last edit; the toggles and the reset save
    /// at once; a pending save is written when the view goes away.</item>
    /// </list>
    /// </summary>
    public sealed class KeyDataPageViewModel : PageSectionViewModel, IActivatable
    {
        private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(700);
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

        private IDisposable _save, _refresh;
        private KeyDataTriple _committed;   // what our pending save wrote into the live slot
        private string _seen;               // the store's state for our slot when we last read or wrote it

        private string _identity;
        private string _game;
        private int _revision;
        private bool _auto, _perGame, _shadowing, _manualLive, _listening;
        private double? _sMax, _s90, _s75;
        private KeyDataTriple _learned;
        private KeyDataMaturity _maturity;

        public KeyDataPageViewModel(SettingsPageContext ctx, bool isLock) : base(ctx)
        {
            IsLock = isLock;
            string ch = isLock ? "Lock" : "Slip";
            Header = Strings.Get("KeyData.Section." + ch);
            SMaxLabel = Strings.Get("KeyData." + ch + ".SMax.Label");
            S90Label = Strings.Get("KeyData." + ch + ".S90.Label");
            S75Label = Strings.Get("KeyData." + ch + ".S75.Label");
            SMaxDesc = Strings.Get("KeyData." + ch + ".SMax.Desc");
            S90Desc = Strings.Get("KeyData." + ch + ".S90.Desc");
            S75Desc = Strings.Get("KeyData." + ch + ".S75.Desc");
            ResetCommand = new RelayCommand(() => Guarded(Reset));
            Reload();
        }

        public bool IsLock { get; }
        public string Header { get; }
        public string SMaxLabel { get; }
        public string S90Label { get; }
        public string S75Label { get; }
        public string SMaxDesc { get; }
        public string S90Desc { get; }
        public string S75Desc { get; }

        /// <summary>The identity everything here is keyed by: the draft's source, or Raw while shadowed.</summary>
        public string SourceIdentity => _identity;

        /// <summary>A live settings object exists (the plugin is running).</summary>
        public bool Available => Store != null;

        /// <summary>Auto Generate (two-way): saves at once, after a pending edit was settled under the flags it
        /// was typed under.</summary>
        public bool Auto
        {
            get => _auto;
            set { if (value != _auto) Guarded(() => CommitToggles(value, _perGame)); }
        }

        /// <summary>Per-Game (two-way): moves the slot; saves at once.</summary>
        public bool PerGame
        {
            get => _perGame;
            set { if (value != _perGame) Guarded(() => CommitToggles(_auto, value)); }
        }

        public double? SMax { get => _sMax; set { if (SetProperty(ref _sMax, value)) Guarded(CommitTyped); } }
        public double? S90 { get => _s90; set { if (SetProperty(ref _s90, value)) Guarded(CommitTyped); } }
        public double? S75 { get => _s75; set { if (SetProperty(ref _s75, value)) Guarded(CommitTyped); } }

        /// <summary>Per-Game is meaningless under Auto: hidden, not disabled (owner).</summary>
        public bool ShowPerGame => !_auto;

        /// <summary>Under Max-Grip-Only / Perfect Point Only the lower two rows are derived, not configured.</summary>
        public bool ShowLowerAnchors => !MaxGripOnly;

        /// <summary>Editable whenever Manual (the warm-up gate governs only the automatic seed).</summary>
        public bool ValuesEditable => Available && !_auto;

        /// <summary>The learned hint - Manual only, and for an unknown source only once the gate opened.</summary>
        public bool ShowLearnedHints => !_auto && (KnownSource || _manualLive);
        public bool ShowLowerLearnedHints => ShowLearnedHints && !MaxGripOnly;

        public string SMaxLearnedText => KeyDataPageRules.FormatLearned(_learned?.SMax);
        public string S90LearnedText => KeyDataPageRules.FormatLearned(_learned?.S90);
        public string S75LearnedText => KeyDataPageRules.FormatLearned(_learned?.S75);

        /// <summary>The learning indicators (docs\learning-maturity-indicator-report.md): Auto only - collapsed
        /// (no space) under Manual - and S90/S75 only while their rows show. Re-read with the learned values:
        /// at once on a game / source / Auto change, and once a second under Auto.</summary>
        public LearningIndicator SMaxIndicator => LearningIndicatorRules.From(_maturity?.SMax, _auto, OriginTip, _maturity?.Borrowed == true, _maturity?.OwnSMax);
        public LearningIndicator S90Indicator => LearningIndicatorRules.From(_maturity?.S90, _auto && ShowLowerAnchors, LowerTip, LowerBorrowed, _maturity?.OwnSMax);
        public LearningIndicator S75Indicator => LearningIndicatorRules.From(_maturity?.S75, _auto && ShowLowerAnchors, LowerTip, LowerBorrowed, _maturity?.OwnSMax);
        private bool LowerBorrowed => !IsLock && _maturity?.Borrowed == true;

        /// <summary>Where SMax comes from (borrowed / earlier session); Slip's derived S90 / S75 share it.</summary>
        private string OriginTip => LearningIndicatorRules.OriginToolTip(_maturity?.Origin);
        private string LowerTip => IsLock ? null : OriginTip;

        /// <summary>Empty when there is nothing to say (the view collapses it).</summary>
        public string Status => KeyDataPageRules.Status(IsLock, _auto, PerGameWithoutGame, KnownSource, _manualLive, _sMax, _s90, _s75, MaxGripOnly);

        /// <summary>The dedicated reset shows only when there is something to undo.</summary>
        public bool ShowReset => KeyDataPageRules.CanRestore(Store, DraftChannel?.SourceMode ?? SourceMode.Manual, _identity, IsLock, Defaults);

        /// <summary>"Showing and saving Plugin Internal's values" while the configured source is shadowed.</summary>
        public bool ShowShadowNote => _shadowing;

        /// <summary>Puts this source back to shipped key data (Auto, Global, numbers discarded) and saves at once.</summary>
        public ICommand ResetCommand { get; }

        // ------------------------------------------------------------------ inputs

        private WheelChannelSettings DraftChannel => IsLock ? S.Lock : S.Slip;
        private WheelChannelSettings LiveChannel => IsLock ? Ctx.Host.Settings?.Lock : Ctx.Host.Settings?.Slip;
        /// <summary>The key data store: the LIVE settings' (key data never waits for Apply).</summary>
        private KeyDataPointSettings Store => LiveChannel?.KeyDataPoints;
        private KeyDataPointDefaults Defaults => Ctx.Host.Settings?.KeyDataPointDefaults ?? S.KeyDataPointDefaults ?? KeyDataPointDefaults.CreateShipped();
        private bool MaxGripOnly => DraftChannel?.NormalizePattern == NormalizePattern.MaxGripOnly;
        private bool KnownSource => KeyDataPointSettings.IsExactShippedSource(_identity, IsLock);
        private bool PerGameWithoutGame => _perGame && string.IsNullOrEmpty(_game);
        private IKeyDataPageHost PageHost => Ctx.Host as IKeyDataPageHost;

        private string ComputeIdentity()
        {
            WheelChannelSettings draft = DraftChannel;
            _shadowing = draft != null && (PageHost?.SourceShadowsRaw(draft, IsLock) ?? false);
            return _shadowing ? RawSourceFallback.RawIdentity(IsLock) : KeyDataPanelModel.Identity(draft, IsLock);
        }

        /// <summary>A channel the shared resolver reads: the draft's source (Raw's while shadowed) and
        /// pattern, over the live key data store.</summary>
        private WheelChannelSettings ResolveChannel()
        {
            WheelChannelSettings draft = DraftChannel;
            var ch = new WheelChannelSettings
            {
                KeyDataPoints = Store,
                NormalizePattern = draft?.NormalizePattern ?? NormalizePattern.Mapping,
            };
            if (_shadowing || draft == null)
            {
                ch.SourceMode = SourceMode.Manual;
                ch.SourceFrontLeft = IsLock ? RawSourceFallback.LockFrontLeft : RawSourceFallback.SlipFrontLeft;
                ch.SourceFrontRight = IsLock ? RawSourceFallback.LockFrontRight : RawSourceFallback.SlipFrontRight;
                ch.SourceRearLeft = IsLock ? RawSourceFallback.LockRearLeft : RawSourceFallback.SlipRearLeft;
                ch.SourceRearRight = IsLock ? RawSourceFallback.LockRearRight : RawSourceFallback.SlipRearRight;
                ch.ScriptTypeFrontLeft = ch.ScriptTypeFrontRight = ch.ScriptTypeRearLeft = ch.ScriptTypeRearRight = ScriptType.Plain;
                ch.ShakeItAlgorithm = null;
            }
            else
            {
                ch.SourceMode = draft.SourceMode;
                ch.SourceFrontLeft = draft.SourceFrontLeft; ch.ScriptTypeFrontLeft = draft.ScriptTypeFrontLeft;
                ch.SourceFrontRight = draft.SourceFrontRight; ch.ScriptTypeFrontRight = draft.ScriptTypeFrontRight;
                ch.SourceRearLeft = draft.SourceRearLeft; ch.ScriptTypeRearLeft = draft.ScriptTypeRearLeft;
                ch.SourceRearRight = draft.SourceRearRight; ch.ScriptTypeRearRight = draft.ScriptTypeRearRight;
                ch.ShakeItAlgorithm = draft.ShakeItAlgorithm;
            }
            return ch;
        }

        // ------------------------------------------------------------------ triggers

        /// <summary>The page's draft source changed (mode, a row, the ShakeIt algorithm, or a reload): a
        /// source change is a change of scale, so the slot that now applies is shown.</summary>
        public void OnSourceChanged() => Guarded(() =>
        {
            if (string.Equals(ComputeIdentity(), _identity, StringComparison.Ordinal)) { RaiseDerived(); return; }
            Reload();
        });

        /// <summary>The draft's normalize pattern changed (the curve section): show / hide the lower rows,
        /// and fill them when the three-point mapping reveals them empty.</summary>
        public void OnPatternChanged() => Guarded(() =>
        {
            if (!MaxGripOnly)
            {
                KeyDataTriple fill = KeyDataPageRules.FillLowerAnchors(_sMax, _s90, _s75, _identity, IsLock, Defaults);
                if (fill != null)
                {
                    Show(fill.SMax, fill.S90, fill.S75);
                    CommitTyped();
                    return;
                }
            }
            RaiseDerived();
        });

        /// <summary>The view is shown: listen to the plugin's state pushes (game, learned values, gate, seed
        /// revision) while shown, and catch up at once with whatever changed while hidden - the page view
        /// model lives as long as the plugin, so a hidden page does no work.</summary>
        public void Activate()
        {
            if (!_listening)
            {
                _listening = true;
                Ctx.Host.PluginStateChanged += OnPluginStateChangedAnyThread;
            }
            Guarded(OnPluginStateChanged);
            _refresh?.Dispose();
            // The old page's 1 Hz graph / readout timer, while shown: under Auto the values move on their own.
            _refresh = Ctx.Ticker.Every(RefreshInterval, () => Guarded(() => { if (_auto) Refresh(); }));
        }

        public void Deactivate()
        {
            if (_listening)
            {
                _listening = false;
                Ctx.Host.PluginStateChanged -= OnPluginStateChangedAnyThread;
            }
            _refresh?.Dispose();
            _refresh = null;
            // THE DEBOUNCE MUST NOT OUTLIVE THE VIEW: closing the tab inside the 700 ms used to lose the edit.
            Guarded(SaveNow);
        }

        protected override void OnDraftReloaded(DraftReloadedEventArgs e)
        {
            string ch = IsLock ? "Lock" : "Slip";
            if (e.Touches(ch + ".KeyDataPoints") && !string.Equals(StoreState(), _seen, StringComparison.Ordinal))
            {
                // THE NEWER SHAKEIT-SIDE WRITE WINS: a pending save whose value is no longer in the slot is
                // dropped (the hub already saved theirs). The adoption of our OWN write (the store still in
                // the state we left it) changes nothing - a value on screen stays.
                if (_save != null && !SlotHolds(_committed)) CancelSave();
                Reload();
            }
            else if (e.Touches(ch, "KeyDataPointDefaults"))
            {
                // Pattern, source rows / mode, ShakeIt algorithm, or the Custom reference moved.
                if (!string.Equals(ComputeIdentity(), _identity, StringComparison.Ordinal) || e.Touches(ch + ".NormalizePattern", "KeyDataPointDefaults"))
                    Reload();
                else RaiseDerived();
            }
            // Never PropertyChanged(null) blindly: a value the driver typed (even an invalid one) stays.
        }

        // Any thread (the plugin's data thread); handled on the UI thread, only while still shown.
        private void OnPluginStateChangedAnyThread(object sender, EventArgs e) =>
            Ctx.Ui.Post(() => { if (_listening) Guarded(OnPluginStateChanged); });

        private void OnPluginStateChanged()
        {
            string game = Ctx.Host.CurrentGameId;
            int revision = Ctx.Host.KeyDataRevision;
            bool contextChanged = !string.Equals(game, _game, StringComparison.Ordinal) || revision != _revision
                                  || !string.Equals(ComputeIdentity(), _identity, StringComparison.Ordinal);
            // A change of game, source or a plugin seed (revision) is a change of slot; under Auto the
            // readout follows the learned values.
            if (contextChanged || _auto) Reload();
            else Refresh();
        }

        // ------------------------------------------------------------------ model -> view

        /// <summary>Re-reads the plugin-pushed state (learned values, gate) without touching the values.</summary>
        private void Refresh()
        {
            if (_auto) { Reload(); return; }
            _learned = Ctx.Host.LearnedKeyData(IsLock, _identity);
            _maturity = ReadMaturity();
            _manualLive = PageHost?.ManualGateReady(IsLock, _identity) ?? false;
            RaiseDerived();
        }

        private KeyDataMaturity ReadMaturity() => (Ctx.Host as ILearningMaturityHost)?.LearnedKeyDataMaturity(IsLock, _identity);

        /// <summary>The slot that applies now - toggles first, then its numbers - by the shared rule.</summary>
        private void Reload()
        {
            _identity = ComputeIdentity();
            _game = Ctx.Host.CurrentGameId;
            _revision = Ctx.Host.KeyDataRevision;
            _learned = Ctx.Host.LearnedKeyData(IsLock, _identity);
            _maturity = ReadMaturity();
            _manualLive = PageHost?.ManualGateReady(IsLock, _identity) ?? false;

            KeyDataPointSettings k = Store;
            KeyDataView view = KeyDataPanelModel.Resolve(ResolveChannel(), IsLock, _game, _learned, Defaults);
            SetProperty(ref _auto, k != null ? k.GetAutoGenerate(_identity) : view.Auto, nameof(Auto));
            SetProperty(ref _perGame, k != null && k.GetPerGame(_identity), nameof(PerGame));
            Show(view.SMax, view.S90, view.S75);
            _seen = StoreState();
            OnPropertyChanged(nameof(SourceIdentity));
            RaiseDerived();
        }

        private void Show(double? a, double? b, double? c)
        {
            SetProperty(ref _sMax, a, nameof(SMax));
            SetProperty(ref _s90, b, nameof(S90));
            SetProperty(ref _s75, c, nameof(S75));
        }

        private void RaiseDerived()
        {
            OnPropertyChanged(nameof(Available));
            OnPropertyChanged(nameof(ShowPerGame));
            OnPropertyChanged(nameof(ShowLowerAnchors));
            OnPropertyChanged(nameof(ValuesEditable));
            OnPropertyChanged(nameof(ShowLearnedHints));
            OnPropertyChanged(nameof(ShowLowerLearnedHints));
            OnPropertyChanged(nameof(SMaxLearnedText));
            OnPropertyChanged(nameof(S90LearnedText));
            OnPropertyChanged(nameof(S75LearnedText));
            OnPropertyChanged(nameof(SMaxIndicator));
            OnPropertyChanged(nameof(S90Indicator));
            OnPropertyChanged(nameof(S75Indicator));
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(ShowReset));
            OnPropertyChanged(nameof(ShowShadowNote));
        }

        // ------------------------------------------------------------------ view -> model (immediate)

        /// <summary>
        /// A typed value: written into the live slot NOW (refused under Auto, Per-Game with no game, or an
        /// invalid triple - which then stays on screen with its message), the disk save 700 ms after the
        /// last edit. A stored value the store clamped (SMax above 95) is shown as stored.
        /// </summary>
        private void CommitTyped()
        {
            KeyDataPointSettings k = Store;
            if (k != null && KeyDataPanelModel.TryCommitManual(k, _game, _identity, _sMax, _s90, _s75, MaxGripOnly))
            {
                if (k.TryGetManual(_game, _identity, out double a, out double b, out double c))
                {
                    _committed = new KeyDataTriple(a, b, c);
                    SetProperty(ref _sMax, a, nameof(SMax));
                    if (!MaxGripOnly)
                    {
                        SetProperty(ref _s90, b, nameof(S90));
                        SetProperty(ref _s75, c, nameof(S75));
                    }
                }
                ScheduleSave();
            }
            _seen = StoreState();
            RaiseDerived();
        }

        /// <summary>
        /// ORDER IS LOAD-BEARING (old OnKeyDataToggleChanged): a pending edit was already written into the
        /// slot of the flags it was typed under (synchronously), then the new choice is recorded against the
        /// page's source and everything is saved at once; then the slot that now applies is shown.
        /// </summary>
        private void CommitToggles(bool auto, bool perGame)
        {
            KeyDataPointSettings k = Store;
            if (k == null) return;
            CancelSave();
            k.SetAutoGenerate(_identity, auto);
            k.SetPerGame(_identity, perGame);
            Ctx.Host.ApplySettings();
            Reload();
        }

        private void Reset()
        {
            KeyDataPointSettings k = Store;
            if (k == null) return;
            // A pending edit would otherwise come back after the reset.
            CancelSave();
            k.RestoreSourceDefaults(_identity);
            Ctx.Host.ApplySettings();
            Reload();
        }

        /// <summary>Our slot's flags and numbers in the store, as one comparable value.</summary>
        private string StoreState()
        {
            KeyDataPointSettings k = Store;
            if (k == null) return null;
            string values = k.TryGetManual(_game, _identity, out double a, out double b, out double c) ? new KeyDataTriple(a, b, c).ToString() : "-";
            return k.GetAutoGenerate(_identity) + "|" + k.GetPerGame(_identity) + "|" + values;
        }

        private bool SlotHolds(KeyDataTriple t) =>
            t != null && Store != null && Store.TryGetManual(_game, _identity, out double a, out double b, out double c)
            && new KeyDataTriple(a, b, c).Equals(t);

        private void ScheduleSave()
        {
            _save?.Dispose();
            _save = Ctx.Ticker.Every(SaveDelay, () => Guarded(SaveNow));
        }

        private void CancelSave()
        {
            _save?.Dispose();
            _save = null;
            _committed = null;
        }

        /// <summary>Writes a pending key data save now instead of 700 ms after the last edit - the old page's
        /// FlushKeyDataPersistNow: the Viper game button calls it on BOTH tabs before it switches the
        /// source (a pending edit belongs to the source being left). Nothing pending: nothing happens.</summary>
        public void FlushPendingSave() => Guarded(SaveNow);

        /// <summary>Writes a pending save now (the values are already in the live settings).</summary>
        private void SaveNow()
        {
            if (_save == null) return;
            CancelSave();
            Ctx.Host.ApplySettings();
        }

        /// <summary>A page action must never throw into WPF: record it on the health panel instead.</summary>
        private void Guarded(Action action) => Ctx.Guard(action);
    }
}

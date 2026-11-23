using System;
using System.Threading.Tasks;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Core.Viper;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The Wheel Lock / Wheel Slip tab's "Sources" section (everything in it but the key data and the Custom
    /// reference): the orange fallback warning, the Source combo (Plugin Internal / ShakeIt Built-in / Viper /
    /// Custom - always visible and switchable), the Viper game button, the ShakeIt state note and algorithm
    /// block, the mode notes and the four <see cref="Rows"/>. Reads and writes the draft channel.
    /// <list type="bullet">
    /// <item><b>One switch path</b> (docs\full-mvvm-plan.md §1 item 3): a mode change takes the hub's own steps
    /// - entering Custom seeds its cold-start reference from the source being left, then
    /// <see cref="WheelChannelSettings.SwitchSourceMode"/> archives the current rows and restores (or presets)
    /// the new mode's - so a page switch and a ShakeIt-panel switch produce identical settings.</item>
    /// <item><b>A preset edit moves to Custom</b> (owner): a row TEXT change while a preset is selected seeds
    /// Custom's reference from that preset, archives the preset's rows untouched and makes the channel Custom;
    /// a no-op when already Custom (the driver's reference is never re-seeded). The script-type toggle alone
    /// does not move (as before).</item>
    /// <item><b>The Raw shadow</b>: a Viper channel on an unsupported running game, or a ShakeIt channel on a
    /// NotSetUp / NotSupported verdict (never while idle), reads and keys as Raw - asked of the plugin
    /// (<see cref="IWheelChannelPageHost"/>), for the SHOWN (draft) mode. The rows then show Raw, disabled;
    /// the draft rows are never overwritten, so the readout can never be saved over the configuration.</item>
    /// <item><see cref="CurrentSourceIdentity"/> (what the key data is keyed by): Raw's while shadowed, else
    /// the engine's own key rule on the draft channel (ShakeIt fingerprint included).</item>
    /// <item><see cref="SourceChanged"/>: after the page changed the mode, a row or the ShakeIt algorithm,
    /// after the shadow flipped, and after every draft reload.</item>
    /// </list>
    /// </summary>
    public sealed class SourcesViewModel : PageSectionViewModel
    {
        private bool _shadowing;
        private bool _viperAvailable;
        private bool _loadingAlgorithm;

        public SourcesViewModel(SettingsPageContext ctx, bool isLock) : base(ctx)
        {
            IsLock = isLock;
            Header = Strings.Get("Group.Sources");
            ModeLabel = Strings.Get("Sources.Mode.Label");
            ManualLabel = Strings.Get("Sources.Mode.Manual");
            ViperLabel = Strings.Get("Sources.Mode.Viper");
            CustomLabel = Strings.Get("Sources.Mode.Custom");
            SourcesNote = Strings.Get("Sources.Note");
            CustomNote = Strings.Get("Sources.Mode.CustomNote");
            ViperUnsupportedNote = Strings.Get("Sources.ViperUnsupported.Note");
            ViperSetupNote = Strings.Get(isLock ? "Sources.ViperSetup.Lock" : "Sources.ViperSetup.Slip");
            ViperUnavailableNote = Strings.Get("Sources.ViperUnavailable.Note");
            ManualNote = Strings.Get(isLock ? "Sources.Mode.ManualNote.Lock" : "Sources.Mode.ManualNote.Slip");

            Rows = new[] { new SourceRowViewModel(this, 0), new SourceRowViewModel(this, 1),
                           new SourceRowViewModel(this, 2), new SourceRowViewModel(this, 3) };
            Algorithm = new ShakeItAlgorithmViewModel(isLock);
            Algorithm.Edited += (field, value) => OnAlgorithmEdited();
            ToggleViperGameCommand = new RelayCommand(ToggleViperGame);
            RefreshCommand = new RelayCommand(Refresh);

            _viperAvailable = Ctx.Host.ViperAvailable;
            LoadAlgorithm();
            Refresh();
        }

        /// <summary>Raised after the page changed this channel's source (mode, a row, the ShakeIt algorithm),
        /// after the Raw shadow flipped, and after a draft reload: the key data and the curve re-key.</summary>
        public event EventHandler SourceChanged;

        /// <summary>Raised just BEFORE the Viper game button changes the supported-game list (which moves both
        /// channels onto a different source): a pending key data edit belongs to the source being left and
        /// must be written first (the old page's FlushKeyDataPersistNow).</summary>
        public event EventHandler BeforeViperGameToggle;

        public bool IsLock { get; }
        public string Header { get; }
        public string ModeLabel { get; }
        public string ManualLabel { get; }
        public string ViperLabel { get; }
        public string CustomLabel { get; }

        public SourceRowViewModel[] Rows { get; }

        /// <summary>The Source combo's ShakeIt item (listed while set up, or while shown / saved, then marked
        /// "(not set up)") and the ShakeIt state note.</summary>
        public ShakeItSourceStatusViewModel ShakeItStatus { get; } = new ShakeItSourceStatusViewModel();

        /// <summary>The "ShakeIt algorithm" block, shown only while the combo shows ShakeIt Built-in; its edits
        /// go into the draft (applied on Apply).</summary>
        public ShakeItAlgorithmViewModel Algorithm { get; }

        /// <summary>The orange "fallback estimate" warning: the APPLIED source is Plugin Internal and the
        /// selected branch is an estimate (what the engine reads now, not the draft).</summary>
        public FallbackWarningViewModel FallbackWarning { get; } = new FallbackWarningViewModel();

        public RelayCommand ToggleViperGameCommand { get; }

        /// <summary><see cref="Refresh"/> as a command (the combo's DropDownOpened).</summary>
        public RelayCommand RefreshCommand { get; }

        private WheelChannelSettings Channel => IsLock ? S.Lock : S.Slip;
        private WheelChannelSettings Saved => IsLock ? Ctx.Host.Settings?.Lock : Ctx.Host.Settings?.Slip;
        private IWheelChannelPageHost PageHost => Ctx.Host as IWheelChannelPageHost;

        // ------------------------------------------------------------------ mode

        /// <summary>The draft source mode (what the combo shows).</summary>
        public SourceMode Mode
        {
            get => Channel?.SourceMode ?? SourceMode.Manual;
            set
            {
                WheelChannelSettings ch = Channel;
                if (ch == null || ch.SourceMode == value) return;
                // The hub's own switch (one public method), so a page switch and a panel switch match.
                SettingsHub.SwitchSource(S, ch, value, IsLock);
                Ctx.Draft.MarkEdited();
                AfterSourceEdit();
            }
        }

        /// <summary>The combo binds its items' string Tags ("Manual", "ShakeIt", "Viper", "Custom", as the old
        /// XAML); an unknown or null tag (a cleared selection) is ignored.</summary>
        public string ModeTag
        {
            get => Mode.ToString();
            set
            {
                if (value != null && Enum.TryParse(value, out SourceMode mode)) Mode = mode;
                else OnPropertyChanged(nameof(ModeTag));
            }
        }

        // ------------------------------------------------------------------ rows

        internal string StoredText(int wheel)
        {
            WheelChannelSettings ch = Channel;
            if (ch == null) return null;
            switch (wheel)
            {
                case 0: return ch.SourceFrontLeft;
                case 1: return ch.SourceFrontRight;
                case 2: return ch.SourceRearLeft;
                default: return ch.SourceRearRight;
            }
        }

        private ScriptType StoredScriptType(int wheel)
        {
            WheelChannelSettings ch = Channel;
            if (ch == null) return ScriptType.Plain;
            switch (wheel)
            {
                case 0: return ch.ScriptTypeFrontLeft;
                case 1: return ch.ScriptTypeFrontRight;
                case 2: return ch.ScriptTypeRearLeft;
                default: return ch.ScriptTypeRearRight;
            }
        }

        /// <summary>The stored script type as shown: Plain when no evaluator can run a script (BUG 1).</summary>
        internal ScriptType ShownScriptType(int wheel) => Coerce(StoredScriptType(wheel));

        private ScriptType Coerce(ScriptType stored) => EvaluatorAvailable ? stored : ScriptType.Plain;

        internal bool EvaluatorAvailable => PageHost?.EvaluatorAvailable ?? false;

        internal SourceButtonMode ResolveButton(ScriptType scriptType) =>
            PageHost?.ResolveSourceButton(scriptType) ?? SourceButtonMode.None;

        /// <summary>A row's picker / editor action failed: logged, on the health panel, health list refreshed.</summary>
        internal void ReportFailure(Exception e) => Ctx.ReportFailure(e);

        internal Task<SourceEdit> EditSourceAsync(SourceButtonMode mode, string text, ScriptType scriptType) =>
            PageHost?.EditSourceAsync(mode, text, scriptType) ?? Task.FromResult<SourceEdit>(null);

        /// <summary>A driver edit of one row: <paramref name="text"/> and / or <paramref name="scriptType"/>
        /// (null = unchanged). A text change on a preset moves the channel to Custom first.</summary>
        internal void EditRow(int wheel, string text, ScriptType? scriptType)
        {
            WheelChannelSettings ch = Channel;
            if (ch == null || _shadowing) return;
            bool textChanges = text != null && !string.Equals(text, StoredText(wheel) ?? string.Empty, StringComparison.Ordinal);
            ScriptType? newType = scriptType.HasValue ? Coerce(scriptType.Value) : (ScriptType?)null;
            bool typeChanges = newType.HasValue && newType.Value != StoredScriptType(wheel);
            if (!textChanges && !typeChanges) return;

            if (textChanges && ch.SourceMode != SourceMode.Custom)
            {
                // Custom's reference is seeded from the preset being left, while we still know which it was;
                // the preset's rows go to its own archive untouched (a preset is this plugin's configuration).
                S.KeyDataPointDefaults?.SeedCustomFrom(KeyDataPointSettings.ClassifyExact(CurrentSourceIdentity, IsLock), IsLock);
                ch.ArchiveCurrentSources();
                ch.SourceMode = SourceMode.Custom;
            }

            switch (wheel)
            {
                case 0:
                    if (textChanges) ch.SourceFrontLeft = text;
                    if (typeChanges) ch.ScriptTypeFrontLeft = newType.Value;
                    break;
                case 1:
                    if (textChanges) ch.SourceFrontRight = text;
                    if (typeChanges) ch.ScriptTypeFrontRight = newType.Value;
                    break;
                case 2:
                    if (textChanges) ch.SourceRearLeft = text;
                    if (typeChanges) ch.ScriptTypeRearLeft = newType.Value;
                    break;
                default:
                    if (textChanges) ch.SourceRearRight = text;
                    if (typeChanges) ch.ScriptTypeRearRight = newType.Value;
                    break;
            }
            Ctx.Draft.MarkEdited();
            AfterSourceEdit();
        }

        // ------------------------------------------------------------------ identity and shadow

        /// <summary>The channel reads (and keys as) Raw right now: its shown source cannot work.</summary>
        public bool IsShadowing => _shadowing;

        /// <summary>The identity the key data points are keyed by: Raw's while shadowed, else the engine's key
        /// rule (<see cref="KeyDataPanelModel.Identity(WheelChannelSettings, bool)"/>) on the draft channel as
        /// shown (script types coerced like the rows).</summary>
        public string CurrentSourceIdentity
        {
            get
            {
                if (_shadowing) return RawSourceFallback.RawIdentity(IsLock);
                WheelChannelSettings ch = Channel;
                if (ch == null) return null;
                var shown = new WheelChannelSettings
                {
                    SourceMode = ch.SourceMode,
                    SourceFrontLeft = ch.SourceFrontLeft, ScriptTypeFrontLeft = Coerce(ch.ScriptTypeFrontLeft),
                    SourceFrontRight = ch.SourceFrontRight, ScriptTypeFrontRight = Coerce(ch.ScriptTypeFrontRight),
                    SourceRearLeft = ch.SourceRearLeft, ScriptTypeRearLeft = Coerce(ch.ScriptTypeRearLeft),
                    SourceRearRight = ch.SourceRearRight, ScriptTypeRearRight = Coerce(ch.ScriptTypeRearRight),
                    ShakeItAlgorithm = ch.ShakeItAlgorithm
                };
                return KeyDataPanelModel.Identity(shown, IsLock);
            }
        }

        /// <summary>Asks the plugin - so the page and the engine never disagree - for the SHOWN mode: the
        /// driver may have switched without applying, and the page describes what they are looking at.</summary>
        private bool ComputeShadowing()
        {
            IWheelChannelPageHost host = PageHost;
            if (host == null || Channel == null) return false;
            SourceMode shown = Mode;
            if (shown == SourceMode.ShakeIt)
                return host.ShakeItSourceBlocked(new WheelChannelSettings { SourceMode = SourceMode.ShakeIt }, IsLock);
            if (shown == SourceMode.Viper)
                return host.ViperSourceBlocked(new WheelChannelSettings { SourceMode = SourceMode.Viper }, Ctx.Host.CurrentGameId);
            return false;
        }

        // ------------------------------------------------------------------ notes

        public string SourcesNote { get; }
        public string ContractNote => Strings.Get(EvaluatorAvailable ? "Sources.ContractNote" : "Sources.ContractNote.NoEvaluator");
        public string CustomNote { get; }
        public string ViperUnsupportedNote { get; }
        public string ViperSetupNote { get; }
        public string ViperUnavailableNote { get; }
        public string ManualNote { get; }

        private bool IsViper => Mode == SourceMode.Viper;

        /// <summary>Viper's own "unsupported game" wording, while a Viper channel is shadowed.</summary>
        public bool ShowViperUnsupportedNote => _shadowing && IsViper;
        public bool ShowCustomNote => Mode == SourceMode.Custom;
        /// <summary>Not while the game is unsupported: it would describe a source this title cannot use.</summary>
        public bool ShowViperSetupNote => IsViper && !_shadowing;
        /// <summary>A missing Viper property evaluates cleanly to 0, so without this note there is no signal the
        /// source is dead (the failure the owner lost a session to). Not while unsupported: "install the
        /// plugin" would be misleading then.</summary>
        public bool ShowViperUnavailableNote => IsViper && !_viperAvailable && !_shadowing;
        /// <summary>Plugin Internal alone - the presets fill the rows themselves.</summary>
        public bool ShowManualNote => Mode == SourceMode.Manual;

        // ------------------------------------------------------------------ Viper game button

        /// <summary>Only on the Viper source, with a game running, and ONLY for a title this project does not
        /// ship knowledge of (owner, 2026-10-01): a shipped title has nothing to add and must not offer Remove.</summary>
        public bool ShowViperGameButton
        {
            get
            {
                string game = Ctx.Host.CurrentGameId;
                return IsViper && !string.IsNullOrWhiteSpace(game) && !ViperSupportedGames.IsShipped(game) && PageHost != null;
            }
        }

        public string ViperGameButtonLabel => !ShowViperGameButton ? string.Empty
            : Strings.Get(ViperSupportedGames.IsSupported(Ctx.Host.CurrentGameId, PageHost?.ViperDocument?.SupportedGames)
                ? "Sources.ViperGame.Remove" : "Sources.ViperGame.Add");

        /// <summary>
        /// Adds or removes the running game from the Viper supported-game list and SAVES IT AT ONCE: the list is
        /// a correction to what we know about somebody else's plugin, in its own file, and it changes which
        /// source the channel reads on the next frame - so it is not part of Apply and never marks the page
        /// dirty. Not per channel: both tabs follow (the SourceAvailability broadcast).
        /// </summary>
        private void ToggleViperGame()
        {
            IWheelChannelPageHost host = PageHost;
            string game = Ctx.Host.CurrentGameId;
            if (host == null || string.IsNullOrWhiteSpace(game)) return;

            ViperDocument current = host.ViperDocument;
            bool supported = ViperSupportedGames.IsSupported(game, current?.SupportedGames);
            ViperDocument next = supported ? ViperSupportedGames.WithoutGame(current, game) : ViperSupportedGames.WithGame(current, game);
            // WithGame / WithoutGame return the same instance when nothing changed: nothing to write.
            if (ReferenceEquals(next, current)) return;

            // Flush before switching, not after: a pending key data edit belongs to the source being left.
            BeforeViperGameToggle?.Invoke(this, EventArgs.Empty);

            host.ViperDocument = next;
            host.SaveViperDocument();
            Refresh();
            Ctx.Host.Hub?.NotifyApplied(new[] { SettingField.SourceAvailability }, SettingsOrigin.PluginPage);
        }

        // ------------------------------------------------------------------ refresh

        /// <summary>Re-samples the plugin's state (ShakeIt state, Viper availability, game, shadow) and
        /// re-reads every derived property. Raises <see cref="SourceChanged"/> when the shadow flipped.</summary>
        public void Refresh()
        {
            bool viper = Ctx.Host.ViperAvailable;
            bool viperChanged = viper != _viperAvailable;
            _viperAvailable = viper;

            ShakeItSourceState state = ShakeItSourceUi.StateOf(Ctx.Host, IsLock);
            ShakeItStatus.Refresh(Mode, Saved?.SourceMode, state);
            Algorithm.SetSource(Mode);
            RefreshFallbackWarning();

            bool shadow = ComputeShadowing();
            bool shadowChanged = shadow != _shadowing;
            _shadowing = shadow;

            OnPropertyChanged(null);
            foreach (SourceRowViewModel row in Rows) row.Refresh();
            if (shadowChanged) SourceChanged?.Invoke(this, EventArgs.Empty);

            // Viper's availability is ours to notice (the panels list Viper only while it publishes), as the old
            // page's re-sample did; ShakeIt's state is broadcast by the plugin itself.
            if (viperChanged) Ctx.Host.Hub?.NotifyApplied(new[] { SettingField.SourceAvailability }, SettingsOrigin.PluginPage);
        }

        /// <summary>The warning follows the applied source and the branch nativeness (refreshed once a second
        /// while the tab is shown).</summary>
        public void RefreshFallbackWarning() =>
            FallbackWarning.Refresh(Saved?.SourceMode, IsLock ? Ctx.Host.CurrentLockNativeness : Ctx.Host.CurrentSlipNativeness);

        private void AfterSourceEdit()
        {
            Refresh();
            SourceChanged?.Invoke(this, EventArgs.Empty);
        }

        private void LoadAlgorithm()
        {
            _loadingAlgorithm = true;
            try { Algorithm.Load(Channel?.ShakeItAlgorithm); }
            finally { _loadingAlgorithm = false; }
        }

        /// <summary>An algorithm edit goes into the draft; it moves a ShakeIt channel's key (its fingerprint).</summary>
        private void OnAlgorithmEdited()
        {
            WheelChannelSettings ch = Channel;
            if (_loadingAlgorithm || ch == null) return;
            Algorithm.SaveTo(ch.ShakeItAlgorithm ?? (ch.ShakeItAlgorithm = new ShakeItAlgorithmSettings()));
            Ctx.Draft.MarkEdited();
            AfterSourceEdit();
        }

        protected override void OnDraftReloaded(DraftReloadedEventArgs e)
        {
            LoadAlgorithm();
            Refresh();
            SourceChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

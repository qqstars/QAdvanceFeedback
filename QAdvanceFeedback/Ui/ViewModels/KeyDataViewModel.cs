using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The key data points of a WheelLock/WheelSlip ShakeIt effect, following the plugin page's pattern
    /// (rules in <see cref="KeyDataPanelModel"/>):
    /// <list type="bullet">
    /// <item><see cref="Pattern"/>: SMax only, or SMax/S90/S75.</item>
    /// <item><see cref="Auto"/>: values read-only under Auto.</item>
    /// <item>S90/S75 hidden under SMax only.</item>
    /// <item>Every change applies through the hub: values after 700 ms without typing; a pending edit is
    /// written before a pattern/Auto change and when the panel closes.</item>
    /// <item>Refreshed from the applied settings on hub changes and once a second under Auto (or when the
    /// plugin itself wrote key data) - never over an edit that is still pending.</item>
    /// </list>
    /// </summary>
    public sealed class KeyDataViewModel : PanelViewModelBase
    {
        private static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(700);
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

        private readonly bool _isLock;
        private IDisposable _commit, _refresh;
        private int _lastRevision = -1;

        private NormalizePattern _pattern;
        private bool _auto, _available, _valuesEditable, _showLowerAnchors, _showStatus;
        private double? _sMax, _s90, _s75;
        private string _status = string.Empty;
        private KeyDataView _current = new KeyDataView();
        private LearningIndicator _sMaxIndicator = LearningIndicator.Hidden, _s90Indicator = LearningIndicator.Hidden, _s75Indicator = LearningIndicator.Hidden;

        public KeyDataViewModel(bool isLock, IPanelHost host, ITicker ticker, IUiDispatcher ui) : base(host, ticker, ui)
        {
            _isLock = isLock;
            string ch = isLock ? "Lock" : "Slip";
            Header = Strings.Get("KeyData.Section." + ch);
            SMaxLabel = Strings.Get("KeyData." + ch + ".SMax.Label");
            S90Label = Strings.Get("KeyData." + ch + ".S90.Label");
            S75Label = Strings.Get("KeyData." + ch + ".S75.Label");
            PatternChoices = new[]
            {
                new Choice<NormalizePattern>(NormalizePattern.MaxGripOnly, Strings.Get(isLock ? "Curve.NormalizePattern.MaxGripOnly" : "Curve.NormalizePattern.Slip.MaxGripOnly")),
                new Choice<NormalizePattern>(NormalizePattern.Mapping, Strings.Get(isLock ? "Curve.NormalizePattern.Mapping" : "Curve.NormalizePattern.Slip.Mapping")),
            };
        }

        public string Header { get; }
        public string SMaxLabel { get; }
        public string S90Label { get; }
        public string S75Label { get; }
        public IReadOnlyList<Choice<NormalizePattern>> PatternChoices { get; }

        /// <summary>Raised after the applied key data was re-read (the graph follows it).</summary>
        public event Action Refreshed;

        /// <summary>What the channel uses now - the applied values the graph and the Test Effect use.</summary>
        public KeyDataView Current { get => _current; private set => SetProperty(ref _current, value); }

        public bool Available { get => _available; private set => SetProperty(ref _available, value); }
        public bool ValuesEditable { get => _valuesEditable; private set => SetProperty(ref _valuesEditable, value); }
        public bool ShowLowerAnchors { get => _showLowerAnchors; private set => SetProperty(ref _showLowerAnchors, value); }
        public string Status { get => _status; private set => SetProperty(ref _status, value); }
        public bool ShowStatus { get => _showStatus; private set => SetProperty(ref _showStatus, value); }

        /// <summary>The learning indicators (docs\learning-maturity-indicator-report.md): shown under Auto only,
        /// S90/S75 only while their rows are; refreshed with the values (hub changes, the Auto toggle, once a
        /// second under Auto).</summary>
        public LearningIndicator SMaxIndicator { get => _sMaxIndicator; private set => SetProperty(ref _sMaxIndicator, value); }
        public LearningIndicator S90Indicator { get => _s90Indicator; private set => SetProperty(ref _s90Indicator, value); }
        public LearningIndicator S75Indicator { get => _s75Indicator; private set => SetProperty(ref _s75Indicator, value); }

        /// <summary>Normalize Pattern (two-way): applies at once; a pending edit is written first.</summary>
        public NormalizePattern Pattern
        {
            get => _pattern;
            set
            {
                if (value == _pattern) return;
                if (_commit != null) CommitNow();
                SetProperty(ref _pattern, value);
                Host.Hub?.Set(_isLock ? SettingField.LockNormalizePattern : SettingField.SlipNormalizePattern, value, SettingsOrigin.ShakeItPanel);
                Refresh();
            }
        }

        /// <summary>Auto Generate (two-way): a pending manual edit is written before Auto takes over.</summary>
        public bool Auto
        {
            get => _auto;
            set
            {
                if (value == _auto) return;
                if (_commit != null) CommitNow();
                SetProperty(ref _auto, value);
                Host.Hub?.Set(_isLock ? SettingField.LockKeyDataAuto : SettingField.SlipKeyDataAuto, value, SettingsOrigin.ShakeItPanel);
                Refresh();
            }
        }

        public double? SMax { get => _sMax; set { if (SetProperty(ref _sMax, value)) ScheduleCommit(); } }
        public double? S90 { get => _s90; set { if (SetProperty(ref _s90, value)) ScheduleCommit(); } }
        public double? S75 { get => _s75; set { if (SetProperty(ref _s75, value)) ScheduleCommit(); } }

        private WheelChannelSettings Channel => _isLock ? Host.Settings?.Lock : Host.Settings?.Slip;

        protected override void OnActivated()
        {
            Refresh();
            _refresh = Ticker.Every(RefreshInterval, () =>
            {
                if (_auto || Host.KeyDataRevision != _lastRevision) Refresh();
            });
        }

        protected override void OnDeactivated()
        {
            _refresh?.Dispose();
            _refresh = null;
            if (_commit != null) CommitNow();   // a pending edit must not be lost when the panel closes
        }

        protected override void OnHubChanged(SettingsChangedEventArgs e)
        {
            foreach (SettingField f in e.Fields)
                if (Concerns(f)) { Refresh(); return; }
        }

        // The algorithm fields move a ShakeIt channel's key (its fingerprint), so the slot shown moves too.
        private bool Concerns(SettingField f) => ShakeItAlgorithmViewModel.Concerns(f, _isLock) || (_isLock
            ? f == SettingField.LockKeyDataAuto || f == SettingField.LockKeyDataValues || f == SettingField.LockNormalizePattern || f == SettingField.LockSourceMode
            : f == SettingField.SlipKeyDataAuto || f == SettingField.SlipKeyDataValues || f == SettingField.SlipNormalizePattern || f == SettingField.SlipSourceMode);

        /// <summary>Re-reads the applied key data. Never while an edit is still pending.</summary>
        public void Refresh()
        {
            if (_commit != null) return;

            WheelChannelSettings channel = Channel;
            Available = channel != null && Host.Hub != null;
            if (!Available)
            {
                ValuesEditable = false;
                SMaxIndicator = S90Indicator = S75Indicator = LearningIndicator.Hidden;
                return;
            }

            string identity = KeyDataPanelModel.Identity(channel, _isLock);
            KeyDataView view = KeyDataPanelModel.Resolve(channel, _isLock, Host.CurrentGameId,
                Host.LearnedKeyData(_isLock, identity), Host.Settings.KeyDataPointDefaults);
            _lastRevision = Host.KeyDataRevision;

            // Model -> view: backing fields + notifications, never the user-edit paths above.
            SetProperty(ref _pattern, channel.NormalizePattern, nameof(Pattern));
            SetProperty(ref _auto, view.Auto, nameof(Auto));
            SetProperty(ref _sMax, view.SMax, nameof(SMax));
            SetProperty(ref _s90, view.S90, nameof(S90));
            SetProperty(ref _s75, view.S75, nameof(S75));
            ValuesEditable = view.Editable;
            ShowLowerAnchors = view.ShowLowerAnchors;
            ShowStatus = view.PerGameWithoutGame;
            Status = view.PerGameWithoutGame ? Strings.Get("KeyData.PerGame.NoGame") : string.Empty;
            KeyDataMaturity maturity = view.Auto ? (Host as ILearningMaturityHost)?.LearnedKeyDataMaturity(_isLock, identity) : null;
            string originTip = LearningIndicatorRules.OriginToolTip(maturity?.Origin);
            string lowerTip = _isLock ? null : originTip;   // Slip's S90 / S75 derive from SMax
            bool borrowed = maturity?.Borrowed == true;
            bool lowerBorrowed = !_isLock && borrowed;   // Slip's S90 / S75 follow SMax; Lock's are its own anchors
            double? own = maturity?.OwnSMax;
            SMaxIndicator = LearningIndicatorRules.From(maturity?.SMax, view.Auto, originTip, borrowed, own);
            S90Indicator = LearningIndicatorRules.From(maturity?.S90, view.Auto && view.ShowLowerAnchors, lowerTip, lowerBorrowed, own);
            S75Indicator = LearningIndicatorRules.From(maturity?.S75, view.Auto && view.ShowLowerAnchors, lowerTip, lowerBorrowed, own);
            Current = view;
            Refreshed?.Invoke();
        }

        private void ScheduleCommit()
        {
            _commit?.Dispose();
            _commit = Ticker.Every(CommitDelay, CommitNow);
        }

        private void CommitNow()
        {
            _commit?.Dispose();
            _commit = null;
            if (_sMax.HasValue)
            {
                // Under SMax-only, S90/S75 are derived from SMax by the shared commit rule.
                Host.Hub?.Set(_isLock ? SettingField.LockKeyDataValues : SettingField.SlipKeyDataValues,
                    new KeyDataTriple(_sMax.Value, _s90 ?? 0.0, _s75 ?? 0.0), SettingsOrigin.ShakeItPanel);
            }
            Refresh();
        }
    }
}

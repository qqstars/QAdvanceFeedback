using System;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// A host that knows the plugin's published ShakeIt Built-in source state per channel
    /// (docs\shakeit-source-design.md §3-4). Optional on an <see cref="IPanelHost"/>: a host without it reads
    /// as set up (the option is shown, no note), which is also what a plugin that has not published yet
    /// would mean to the driver at worst.
    /// </summary>
    public interface IShakeItSourceStateHost
    {
        ShakeItSourceStates ShakeItSourceState { get; }
    }

    /// <summary>
    /// The ShakeIt Built-in source's UI rules (docs\shakeit-source-design.md §4), shared by the settings
    /// page (Wheel Lock / Wheel Slip tabs) and the WheelLock / WheelSlip ShakeIt panels:
    /// <list type="bullet">
    /// <item>The ShakeIt option is listed when the channel's state is not NotSetUp - or when it is the
    /// channel's selected (or saved) source, which is never rewritten: then it stays listed, marked
    /// "(not set up)".</item>
    /// <item>A channel whose source is ShakeIt gets a note for NotSetUp and NotSupported (it reads Raw then);
    /// none for SetUpIdle / Supported.</item>
    /// </list>
    /// </summary>
    public static class ShakeItSourceUi
    {
        public static bool ShowOption(ShakeItSourceState state, bool selected) =>
            selected || state != ShakeItSourceState.NotSetUp;

        public static bool IsMarked(ShakeItSourceState state) => state == ShakeItSourceState.NotSetUp;

        public static string OptionLabel(ShakeItSourceState state) =>
            Strings.Get(IsMarked(state) ? "Sources.Mode.ShakeIt.NotSetUp" : "Sources.Mode.ShakeIt");

        /// <summary>The note for a channel whose source is <paramref name="source"/>; empty when none applies.</summary>
        public static string Note(SourceMode? source, ShakeItSourceState state)
        {
            if (source != SourceMode.ShakeIt) return string.Empty;
            switch (state)
            {
                case ShakeItSourceState.NotSetUp: return Strings.Get("Sources.ShakeIt.NotSetUp.Note");
                case ShakeItSourceState.NotSupported: return Strings.Get("Sources.ShakeIt.NotSupported.Note");
                default: return string.Empty;
            }
        }

        /// <summary>The host's state for one channel; SetUpIdle when the host does not publish one.</summary>
        public static ShakeItSourceState StateOf(object host, bool isLock) =>
            (host as IShakeItSourceStateHost)?.ShakeItSourceState?.For(isLock) ?? ShakeItSourceState.SetUpIdle;
    }

    /// <summary>
    /// One settings-page channel's ShakeIt source status (Wheel Lock or Wheel Slip tab): whether the
    /// Source combo lists the ShakeIt option and with which label, and the state note under it. The page
    /// binds the combo item and the note to it and calls <see cref="Refresh"/> with the source the combo
    /// shows, the saved source and the plugin's published state.
    /// </summary>
    public sealed class ShakeItSourceStatusViewModel : ObservableObject
    {
        private bool _showOption = true;
        private string _optionLabel = Strings.Get("Sources.Mode.ShakeIt");
        private string _note = string.Empty;
        private bool _showNote;

        public bool ShowShakeItOption { get => _showOption; private set => SetProperty(ref _showOption, value); }
        public string ShakeItOptionLabel { get => _optionLabel; private set => SetProperty(ref _optionLabel, value); }
        public string Note { get => _note; private set => SetProperty(ref _note, value); }
        public bool ShowNote { get => _showNote; private set => SetProperty(ref _showNote, value); }

        /// <param name="shown">The source the combo shows (the draft).</param>
        /// <param name="saved">The channel's saved source (null when the plugin has none).</param>
        public void Refresh(SourceMode shown, SourceMode? saved, ShakeItSourceState state)
        {
            ShowShakeItOption = ShakeItSourceUi.ShowOption(state, shown == SourceMode.ShakeIt || saved == SourceMode.ShakeIt);
            ShakeItOptionLabel = ShakeItSourceUi.OptionLabel(state);
            Note = ShakeItSourceUi.Note(shown, state);
            ShowNote = Note.Length > 0;
        }
    }

    /// <summary>
    /// One channel's "ShakeIt algorithm" block (docs\shakeit-source-design.md §6, Task D's settings): what
    /// the hidden ShakeIt container reads while the channel's source is ShakeIt Built-in. Wheel Lock shows
    /// Use legacy iRacing algorithm, Lock sensibility (0-100, step 5) and Lock-only mode; Wheel Slip shows
    /// Use legacy iRacing algorithm and Mute slip while lock is active. The filters are not shown (they
    /// follow our pedal thresholds).
    /// <list type="bullet">
    /// <item>Visible only while the channel's (draft) source is ShakeIt Built-in (<see cref="SetSource"/>);
    /// the values are kept when the source changes.</item>
    /// <item>A driver edit raises <see cref="Edited"/> with the hub field and value - the settings page
    /// marks itself dirty (applied on Apply), a ShakeIt panel writes it through the hub at once.
    /// <see cref="Load"/> / <see cref="LoadField"/> never raise it.</item>
    /// </list>
    /// </summary>
    public sealed class ShakeItAlgorithmViewModel : ObservableObject
    {
        private readonly bool _isLock;
        private bool _visible;
        private bool _legacy = ShakeItAlgorithmDefaults.UseLegacyIracingAlgorithm;
        private double? _sensibility = ShakeItAlgorithmDefaults.LockSensibility;
        private bool _lockOnly = ShakeItAlgorithmDefaults.LockOnly;
        private bool _mute = ShakeItAlgorithmDefaults.MuteWhileLockActive;

        public ShakeItAlgorithmViewModel(bool isLock)
        {
            _isLock = isLock;
            Header = Strings.Get("ShakeItAlgorithm.Header");
            Note = Strings.Get("ShakeItAlgorithm.Note");
            LegacyLabel = Strings.Get("ShakeItAlgorithm.LegacyIracing");
            SensibilityLabel = Strings.Get("ShakeItAlgorithm.LockSensibility");
            LockOnlyLabel = Strings.Get("ShakeItAlgorithm.LockOnly");
            MuteLabel = Strings.Get("ShakeItAlgorithm.MuteWhileLockActive");
            OnLabel = Strings.Get("GForce.Shake.On");
            OffLabel = Strings.Get("GForce.Shake.Off");
        }

        /// <summary>A driver edit: the hub field and its new value.</summary>
        public event Action<SettingField, object> Edited;

        public bool IsLock => _isLock;
        public bool IsSlip => !_isLock;
        public string Header { get; }
        public string Note { get; }
        public string LegacyLabel { get; }
        public string SensibilityLabel { get; }
        public string LockOnlyLabel { get; }
        public string MuteLabel { get; }
        public string OnLabel { get; }
        public string OffLabel { get; }

        /// <summary>The block is shown: the channel's source is ShakeIt Built-in.</summary>
        public bool IsVisible { get => _visible; private set => SetProperty(ref _visible, value); }

        public void SetSource(SourceMode? source) => IsVisible = source == SourceMode.ShakeIt;

        public bool UseLegacyIracingAlgorithm
        {
            get => _legacy;
            set { if (SetProperty(ref _legacy, value)) Raise(LegacyField, value); }
        }

        /// <summary>Lock only. Snapped to 0-100 in steps of 5 on every set; a cleared box keeps the value.</summary>
        public double? LockSensibility
        {
            get => _sensibility;
            set
            {
                if (!value.HasValue) { OnPropertyChanged(nameof(LockSensibility)); return; }
                double snapped = ShakeItAlgorithmDefaults.NormaliseLockSensibility(value.Value);
                if (SetProperty(ref _sensibility, snapped)) Raise(SettingField.LockShakeItSensibility, snapped);
                else if (snapped != value.Value) OnPropertyChanged(nameof(LockSensibility));
            }
        }

        /// <summary>Lock only: SimHub's "Use lock only" (on) vs "Use lock and slip" (off).</summary>
        public bool LockOnly
        {
            get => _lockOnly;
            set { if (SetProperty(ref _lockOnly, value)) Raise(SettingField.LockShakeItLockOnly, value); }
        }

        /// <summary>Slip only: mute the ShakeIt slip while the ShakeIt lock is active.</summary>
        public bool MuteWhileLockActive
        {
            get => _mute;
            set { if (SetProperty(ref _mute, value)) Raise(SettingField.SlipShakeItMuteWhileLockActive, value); }
        }

        private SettingField LegacyField => _isLock ? SettingField.LockShakeItLegacyAlgorithm : SettingField.SlipShakeItLegacyAlgorithm;

        /// <summary>Whether <paramref name="field"/> is one of this channel's algorithm fields.</summary>
        public static bool Concerns(SettingField field, bool isLock) => isLock
            ? field == SettingField.LockShakeItLegacyAlgorithm || field == SettingField.LockShakeItSensibility || field == SettingField.LockShakeItLockOnly
            : field == SettingField.SlipShakeItLegacyAlgorithm || field == SettingField.SlipShakeItMuteWhileLockActive;

        /// <summary>Model -> view for every field (never an edit).</summary>
        public void Load(ShakeItAlgorithmSettings s)
        {
            s = s ?? new ShakeItAlgorithmSettings();
            SetProperty(ref _legacy, s.UseLegacyIracingAlgorithm, nameof(UseLegacyIracingAlgorithm));
            SetProperty(ref _sensibility, s.LockSensibility, nameof(LockSensibility));
            SetProperty(ref _lockOnly, s.LockOnly, nameof(LockOnly));
            SetProperty(ref _mute, s.MuteWhileLockActive, nameof(MuteWhileLockActive));
        }

        /// <summary>Model -> view for one hub field (never an edit), so this block's other unapplied edits
        /// stay. Returns false when the field is not this channel's.</summary>
        public bool LoadField(SettingField field, ShakeItAlgorithmSettings s)
        {
            if (!Concerns(field, _isLock)) return false;
            s = s ?? new ShakeItAlgorithmSettings();
            switch (field)
            {
                case SettingField.LockShakeItLegacyAlgorithm:
                case SettingField.SlipShakeItLegacyAlgorithm:
                    SetProperty(ref _legacy, s.UseLegacyIracingAlgorithm, nameof(UseLegacyIracingAlgorithm));
                    break;
                case SettingField.LockShakeItSensibility:
                    SetProperty(ref _sensibility, s.LockSensibility, nameof(LockSensibility));
                    break;
                case SettingField.LockShakeItLockOnly:
                    SetProperty(ref _lockOnly, s.LockOnly, nameof(LockOnly));
                    break;
                case SettingField.SlipShakeItMuteWhileLockActive:
                    SetProperty(ref _mute, s.MuteWhileLockActive, nameof(MuteWhileLockActive));
                    break;
            }
            return true;
        }

        /// <summary>Writes this channel's part into <paramref name="target"/>; the other channel's fields are
        /// left as they are.</summary>
        public void SaveTo(ShakeItAlgorithmSettings target)
        {
            if (target == null) return;
            target.UseLegacyIracingAlgorithm = _legacy;
            if (_isLock)
            {
                target.LockSensibility = _sensibility ?? target.LockSensibility;
                target.LockOnly = _lockOnly;
            }
            else
            {
                target.MuteWhileLockActive = _mute;
            }
        }

        /// <summary>The values shown, as settings (the draft the page keys its key data by).</summary>
        public ShakeItAlgorithmSettings ToSettings()
        {
            var s = new ShakeItAlgorithmSettings();
            SaveTo(s);
            return s;
        }

        private void Raise(SettingField field, object value) => Edited?.Invoke(field, value);
    }
}

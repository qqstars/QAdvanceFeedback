using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>A plugin stand-in for the settings page view models: a real settings object and a real
    /// <see cref="SettingsHub"/> over it (its apply-and-save hook only counts), like
    /// <see cref="FakePanelHost"/> for the ShakeIt panels. <see cref="ApplySettings"/> broadcasts like the
    /// plugin's (QAdvanceFeedback.ApplySettings -> BroadcastAppliedChanges): the shared fields that differ
    /// from what was last applied, origin PluginPage; the hub's own writes move that baseline, as the
    /// plugin's hub hook does.</summary>
    public class FakeSettingsPageHost : ISettingsPageHost
    {
        private QAdvanceFeedbackSettings _lastApplied;

        public FakeSettingsPageHost(QAdvanceFeedbackSettings settings = null)
        {
            Settings = settings ?? new QAdvanceFeedbackSettings();
            Hub = new SettingsHub(() => Settings, _ => { HubSaves++; _lastApplied = Snapshot(); }, () => CurrentGameId);
            _lastApplied = Snapshot();
        }

        private QAdvanceFeedbackSettings Snapshot() => Settings == null ? null
            : Newtonsoft.Json.JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(Newtonsoft.Json.JsonConvert.SerializeObject(Settings));

        /// <summary>Lines the page wrote to the (SimHub) log.</summary>
        public List<string> Errors { get; } = new List<string>();
        public void LogError(string message) => Errors.Add(message);

        /// <summary>Saves the hub ran for its own writes (a ShakeIt-side edit).</summary>
        public int HubSaves { get; private set; }

        /// <summary>Calls of <see cref="ApplySettings"/> (the page's Apply).</summary>
        public int ApplyCalls { get; private set; }

        /// <summary>Runs inside <see cref="ApplySettings"/>, e.g. to model the plugin normalising a value.</summary>
        public Action<QAdvanceFeedbackSettings> OnApply { get; set; }

        public QAdvanceFeedbackSettings Settings { get; set; }
        public SettingsHub Hub { get; }
        public string CurrentGameId { get; set; } = "TestGame";
        public QAdvanceFeedback.Core.SourceNativeness CurrentLockNativeness { get; set; }
        public QAdvanceFeedback.Core.SourceNativeness CurrentSlipNativeness { get; set; }
        public int KeyDataRevision { get; set; }
        public bool ViperAvailable { get; set; }
        public KeyDataTriple Learned { get; set; }
        public KeyDataTriple LearnedKeyData(bool isLock, string sourceIdentity) => Learned;

        public void ApplySettings()
        {
            ApplyCalls++;
            OnApply?.Invoke(Settings);
            QAdvanceFeedbackSettings now = Snapshot();
            IReadOnlyList<SettingField> changed = SettingsHub.Diff(_lastApplied, now, CurrentGameId);
            _lastApplied = now;
            Hub.NotifyApplied(changed, SettingsOrigin.PluginPage);
        }

        public QAdvanceFeedbackSettings CreateDefaultSettings() => QAdvanceFeedbackSettings.CreateDefault();

        public event EventHandler PluginStateChanged;
        public void RaisePluginStateChanged() => PluginStateChanged?.Invoke(this, EventArgs.Empty);
        public bool HasPluginStateListeners => PluginStateChanged != null;

        public TestEffectSession Session { get; } = new TestEffectSession();
        public void PublishGForceTest(GForceOutput outputOrNull) { }
        public void PublishLockSlipTest(ProjectedWheelLockSlipResult resultOrNull) { }
        public void PublishSlipAngleTest(SlipAngleProjectedResult resultOrNull) { }
    }

    /// <summary>Answers every confirm with <see cref="Answer"/> and records what was asked.</summary>
    public sealed class FakeDialogService : IDialogService
    {
        public bool Answer { get; set; } = true;
        public List<string> Confirms { get; } = new List<string>();
        public List<string> Informs { get; } = new List<string>();

        public bool Confirm(string message, string title)
        {
            Confirms.Add(message);
            return Answer;
        }

        public void Inform(string message, string title) => Informs.Add(message);
    }

    public sealed class FakeClipboard : IClipboard
    {
        public string Text { get; private set; }
        public void SetText(string text) => Text = text;
    }
}

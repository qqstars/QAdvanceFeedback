using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;

namespace QAdvanceFeedback.Tests.Ui.Page
{
    /// <summary>
    /// The plugin stand-in for the key data / curve / pulse sections: <see cref="FakeSettingsPageHost"/>
    /// (real settings, real hub) plus <see cref="IKeyDataPageHost"/> (shadow, manual gate) and a learned
    /// key data table keyed by (channel, source identity) - the one display rule is per identity.
    /// </summary>
    public sealed class KeyDataPageFakeHost : ISettingsPageHost, IKeyDataPageHost, ILearningMaturityHost
    {
        private readonly FakeSettingsPageHost _inner;

        public KeyDataPageFakeHost(QAdvanceFeedbackSettings settings = null)
        {
            _inner = new FakeSettingsPageHost(settings ?? QAdvanceFeedbackSettings.CreateDefault());
        }

        public FakeSettingsPageHost Inner => _inner;

        /// <summary>Learned triples by (isLock, identity).</summary>
        public Dictionary<(bool, string), KeyDataTriple> Learned { get; } = new Dictionary<(bool, string), KeyDataTriple>();
        public List<(bool, string)> LearnedAsked { get; } = new List<(bool, string)>();
        public bool Shadowing { get; set; }
        public bool GateReady { get; set; }

        public int ApplyCalls => _inner.ApplyCalls;

        public QAdvanceFeedbackSettings Settings { get => _inner.Settings; set => _inner.Settings = value; }
        public SettingsHub Hub => _inner.Hub;
        public string CurrentGameId { get => _inner.CurrentGameId; set => _inner.CurrentGameId = value; }
        public QAdvanceFeedback.Core.SourceNativeness CurrentLockNativeness => _inner.CurrentLockNativeness;
        public QAdvanceFeedback.Core.SourceNativeness CurrentSlipNativeness => _inner.CurrentSlipNativeness;
        public int KeyDataRevision { get => _inner.KeyDataRevision; set => _inner.KeyDataRevision = value; }
        public bool ViperAvailable => _inner.ViperAvailable;

        public KeyDataTriple LearnedKeyData(bool isLock, string sourceIdentity)
        {
            LearnedAsked.Add((isLock, sourceIdentity));
            return Learned.TryGetValue((isLock, sourceIdentity), out KeyDataTriple t) ? t : null;
        }

        /// <summary>What the plugin reports as each source's learning maturity (absent: nothing known).</summary>
        public Dictionary<(bool, string), KeyDataMaturity> Maturity { get; } = new Dictionary<(bool, string), KeyDataMaturity>();
        public KeyDataMaturity LearnedKeyDataMaturity(bool isLock, string sourceIdentity) =>
            Maturity.TryGetValue((isLock, sourceIdentity), out KeyDataMaturity m) ? m : null;
        public QAdvanceFeedback.Core.SlipAngle.SlipAngleWindowMaturity? LearnedSlipAngleMaturity(string sourceIdentity) => null;

        public void ApplySettings() => _inner.ApplySettings();
        public QAdvanceFeedbackSettings CreateDefaultSettings() => _inner.CreateDefaultSettings();

        public event EventHandler PluginStateChanged;
        public void RaisePluginStateChanged() => PluginStateChanged?.Invoke(this, EventArgs.Empty);
        public bool HasPluginStateListeners => PluginStateChanged != null;
        public void LogError(string message) => _inner.LogError(message);

        public bool SourceShadowsRaw(WheelChannelSettings draftChannel, bool isLock) => Shadowing;
        public bool ManualGateReady(bool isLock, string sourceIdentity) => GateReady;

        public TestEffectSession Session => _inner.Session;
        public void PublishGForceTest(GForceOutput outputOrNull) { }
        public void PublishLockSlipTest(ProjectedWheelLockSlipResult resultOrNull) { }
        public void PublishSlipAngleTest(SlipAngleProjectedResult resultOrNull) { }
    }

    /// <summary>A page context over a host, the way <see cref="SettingsPageViewModel"/> builds it.</summary>
    public sealed class PageRig
    {
        public PageRig(QAdvanceFeedbackSettings settings = null)
        {
            Host = new KeyDataPageFakeHost(settings);
            Ticker = new ManualTicker();
            Draft = new SettingsDraft(() => Host.Settings);
            Ctx = new SettingsPageContext(Draft, Host, new ImmediateDispatcher(), Ticker, new FakeDialogService(), new FakeClipboard());
        }

        public KeyDataPageFakeHost Host { get; }
        public ManualTicker Ticker { get; }
        public SettingsDraft Draft { get; }
        public SettingsPageContext Ctx { get; }

        /// <summary>What the page view model does on every hub event: adopt the live changes.</summary>
        public void Adopt() => Draft.AdoptExternal();
    }
}

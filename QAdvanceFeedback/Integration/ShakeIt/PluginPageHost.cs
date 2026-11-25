using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Core.Viper;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using SimHub.Plugins;

namespace QAdvanceFeedback.Integration.ShakeIt
{
    /// <summary>
    /// The running plugin as the settings page view models see it (<see cref="IPluginSettingsPageHost"/>:
    /// the page host and every area's host). It forwards to the plugin instance that owns the page - the
    /// member bodies are the plugin's own (<c>ShakeItSourceBlocked</c>, <c>ViperSourceBlocked</c>,
    /// <c>ViperDocument</c>, <c>ApplySettings</c>, the learned display rules) and the old page's picker /
    /// script editor / version code, so the page asks the plugin rather than re-deriving what the engine
    /// reads. Everything that touches SimHub or WPF lives here, never in a view model.
    /// </summary>
    public sealed class PluginPageHost : IPluginSettingsPageHost, ILearningMaturityHost
    {
        private readonly QAdvanceFeedback _plugin;
        private readonly PluginManager _pluginManager;
        private readonly PropertyPickerLauncher _picker = new PropertyPickerLauncher();
        private readonly SimHubExpressionEvaluator _evaluator = new SimHubExpressionEvaluator();
        private readonly ViperAvailabilityProvider _viperExport = new ViperAvailabilityProvider();
        private WeakReference<FrameworkElement> _view;

        public PluginPageHost(QAdvanceFeedback plugin, PluginManager pluginManager)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _pluginManager = pluginManager;
        }

        /// <summary>The page view now shown - the owner window of the script editor. WEAK: SimHub owns the
        /// view's lifetime and may discard it whenever the settings page closes.</summary>
        public void AttachView(FrameworkElement view) => _view = view == null ? null : new WeakReference<FrameworkElement>(view);

        // ---------------------------------------------------------------- IPanelHost / ITestEffectHost

        public QAdvanceFeedbackSettings Settings => _plugin.Settings;
        public SettingsHub Hub => _plugin.Hub;
        public string CurrentGameId => _plugin.CurrentGameId;
        public SourceNativeness CurrentLockNativeness => _plugin.CurrentLockNativeness;
        public SourceNativeness CurrentSlipNativeness => _plugin.CurrentSlipNativeness;
        public int KeyDataRevision => _plugin.KeyDataRevision;

        /// <summary>Re-sampled on every read: the Viper plugin only publishes once a supported game runs,
        /// so a page opened at the SimHub menu must not keep its first answer (owner-reported, 2026-09-30).</summary>
        public bool ViperAvailable
        {
            get
            {
                try { return _viperExport.IsAvailable(_plugin.PluginManager ?? _pluginManager); }
                catch (Exception) { return false; }
            }
        }

        public KeyDataTriple LearnedKeyData(bool isLock, string sourceIdentity) => _plugin.GetLearnedKeyDataForDisplay(isLock, sourceIdentity);

        // ---------------------------------------------------------------- ILearningMaturityHost

        public KeyDataMaturity LearnedKeyDataMaturity(bool isLock, string sourceIdentity) => _plugin.GetLearnedKeyDataMaturityForDisplay(isLock, sourceIdentity);
        public SlipAngleWindowMaturity? LearnedSlipAngleMaturity(string sourceIdentity) => _plugin.GetLearnedSlipAngleMaturityForDisplay(sourceIdentity);

        public TestEffectSession Session => QAdvanceFeedback.TestSession;
        public void PublishGForceTest(GForceOutput outputOrNull) => _plugin.PublishTestEffect(outputOrNull);
        public void PublishLockSlipTest(ProjectedWheelLockSlipResult resultOrNull) => _plugin.PublishLockSlipTest(resultOrNull);
        public void PublishSlipAngleTest(SlipAngleProjectedResult resultOrNull) => _plugin.PublishSlipAngleTest(resultOrNull);

        // ---------------------------------------------------------------- ISettingsPageHost

        public void ApplySettings() => _plugin.ApplySettings();
        public QAdvanceFeedbackSettings CreateDefaultSettings() => QAdvanceFeedbackSettings.CreateDefault();

        public event EventHandler PluginStateChanged
        {
            add => _plugin.SettingsPageStateChanged += value;
            remove => _plugin.SettingsPageStateChanged -= value;
        }

        public void LogError(string message) => SimHub.Logging.Current.Error(message);

        // ---------------------------------------------------------------- IWheelChannelPageHost / ISlipAngleTabHost (picker, editor)

        public bool EvaluatorAvailable => _evaluator.IsAvailable;

        public SourceButtonMode ResolveSourceButton(ScriptType scriptType) =>
            SourceButtonModeSelector.Resolve(scriptType, SimHubScriptEditor.IsAvailable, _picker.IsAvailable, _evaluator.IsAvailable);

        public async Task<SourceEdit> EditSourceAsync(SourceButtonMode mode, string text, ScriptType scriptType)
        {
            if (mode == SourceButtonMode.Picker)
            {
                string chosen = await _picker.PickPropertyAsync(text);
                return string.IsNullOrEmpty(chosen) ? null : new SourceEdit(chosen, scriptType);
            }
            if (mode == SourceButtonMode.Editor)
            {
                ScriptEditResult result = await SimHubScriptEditor.EditExpressionAsync(OwnerWindow(), text, scriptType);
                return result == null ? null : new SourceEdit(result.Expression ?? string.Empty, result.ScriptType);
            }
            return null;
        }

        private Window OwnerWindow()
        {
            FrameworkElement view = null;
            if (_view != null && _view.TryGetTarget(out FrameworkElement v)) view = v;
            return (view != null ? Window.GetWindow(view) : null) ?? Application.Current?.MainWindow;
        }

        public bool ShakeItSourceBlocked(WheelChannelSettings channel, bool isLockChannel) => _plugin.ShakeItSourceBlocked(channel, isLockChannel);
        public bool ViperSourceBlocked(WheelChannelSettings channel, string gameId) => _plugin.ViperSourceBlocked(channel, gameId);

        public ViperDocument ViperDocument
        {
            get => _plugin.ViperDocument;
            set => _plugin.ViperDocument = value;
        }

        public bool SaveViperDocument() => _plugin.SaveViperDocument();

        // ---------------------------------------------------------------- IKeyDataPageHost

        /// <summary>The old page's ChannelShadowsRaw, for the page's (draft) source mode.</summary>
        public bool SourceShadowsRaw(WheelChannelSettings draftChannel, bool isLock) =>
            _plugin.ShakeItSourceBlocked(draftChannel, isLock) || _plugin.ViperSourceBlocked(draftChannel, _plugin.CurrentGameId);

        public bool ManualGateReady(bool isLock, string sourceIdentity) => _plugin.ManualGateReadyForDisplay(isLock, sourceIdentity);

        // ---------------------------------------------------------------- IGeneralPageHost

        public IReadOnlyList<HealthEntry> HealthSnapshot() => HealthRegistry.Snapshot();

        /// <summary>The running DLL's file version, its assembly version as a fallback, "unknown" if neither
        /// can be read (a locked-down environment that denies FileVersionInfo must not take the page down).</summary>
        public string PluginVersion
        {
            get
            {
                try
                {
                    Assembly assembly = typeof(QAdvanceFeedback).Assembly;
                    string fileVersion = FileVersionInfo.GetVersionInfo(assembly.Location)?.FileVersion;
                    if (!string.IsNullOrWhiteSpace(fileVersion)) return fileVersion;
                    return assembly.GetName().Version?.ToString() ?? "unknown";
                }
                catch
                {
                    return "unknown";
                }
            }
        }

        public ShakeItConversionOutcome ImportShakeItPrecalibration(bool overrideExisting) => _plugin.ImportShakeItPrecalibration(overrideExisting);

        // ---------------------------------------------------------------- ISlipAngleTabHost

        public SlipAngleStatus Status => _plugin.LatestSlipAngleStatus ?? SlipAngleStatus.None;
        public SourceNativeness IdentityTier => _plugin.CurrentSlipAngleIdentityTier;
        public SlipAngleWindow? LearnedWindow(string sourceIdentity) => _plugin.GetLearnedSlipAngleWindowForDisplay(sourceIdentity);
        public void SaveLiveSettings() => _plugin.ApplySettings();
        public SlipAngleSourceStore NativeSources => _plugin.SlipAngleSources;

        // ---------------------------------------------------------------- IClutchHost

        public ClutchHostStatus ClutchStatus => PluginPanelHost.ClutchStatusOf(_plugin);
        public IReadOnlyList<string> ControllerAxes() => _plugin.ControllerAxes();
        public double? ReadAxis01(string name) => _plugin.ReadAxis01(name);
        public void ForgetClutchBite() => _plugin.ForgetClutchBite();
        public void PublishClutchTest(double? output0100) => _plugin.PublishClutchTest(output0100);
        public double FinalBiteForTest => _plugin.FinalBiteForTest;
        public string FinalBiteForTestKey => _plugin.FinalBiteForTestKey;

        // ---------------------------------------------------------------- IShakeItSourceStateHost

        public ShakeItSourceStates ShakeItSourceState => _plugin.ShakeItSourceState ?? ShakeItSourceStates.Initial;
    }
}

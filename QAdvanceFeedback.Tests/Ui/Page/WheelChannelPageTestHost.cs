using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Core.Viper;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;

namespace QAdvanceFeedback.Tests.Ui.Page
{
    /// <summary>A plugin stand-in for the Wheel Lock / Wheel Slip tab view models: <see cref="FakeSettingsPageHost"/>'s
    /// real settings and real hub, plus <see cref="IWheelChannelPageHost"/> and <see cref="IShakeItSourceStateHost"/>
    /// with the plugin's own blocked rules (QAdvanceFeedback.cs ShakeItSourceBlocked / ViperSourceBlocked).</summary>
    public sealed class WheelChannelPageTestHost : ISettingsPageHost, IWheelChannelPageHost, IShakeItSourceStateHost
    {
        public WheelChannelPageTestHost(QAdvanceFeedbackSettings settings = null)
        {
            Settings = settings ?? QAdvanceFeedbackSettings.CreateDefault();
            Hub = new SettingsHub(() => Settings, _ => HubSaves++, () => CurrentGameId);
        }

        public int HubSaves { get; private set; }
        public int ApplyCalls { get; private set; }
        public int ViperSaves { get; private set; }

        public QAdvanceFeedbackSettings Settings { get; set; }
        public SettingsHub Hub { get; }
        /// <summary>A title the Viper plugin computes for, so Viper is not shadowed unless a test says so.</summary>
        public string CurrentGameId { get; set; } = ViperSupportedGames.Shipped[0];
        public QAdvanceFeedback.Core.SourceNativeness CurrentLockNativeness { get; set; }
        public QAdvanceFeedback.Core.SourceNativeness CurrentSlipNativeness { get; set; }
        public int KeyDataRevision { get; set; }
        public bool ViperAvailable { get; set; } = true;
        public KeyDataTriple LearnedKeyData(bool isLock, string sourceIdentity) => null;

        public void ApplySettings() => ApplyCalls++;
        public QAdvanceFeedbackSettings CreateDefaultSettings() => QAdvanceFeedbackSettings.CreateDefault();

        public event EventHandler PluginStateChanged;
        public void RaisePluginStateChanged() => PluginStateChanged?.Invoke(this, EventArgs.Empty);
        public bool HasPluginStateListeners => PluginStateChanged != null;
        public List<string> Errors { get; } = new List<string>();
        public void LogError(string message) => Errors.Add(message);

        public TestEffectSession Session { get; } = new TestEffectSession();
        public void PublishGForceTest(GForceOutput outputOrNull) { }
        public void PublishLockSlipTest(ProjectedWheelLockSlipResult resultOrNull) { }
        public void PublishSlipAngleTest(SlipAngleProjectedResult resultOrNull) { }

        // ---- IShakeItSourceStateHost ----
        public ShakeItSourceStates ShakeItSourceState { get; set; } =
            new ShakeItSourceStates(QAdvanceFeedback.Core.ShakeItEffects.ShakeItSourceState.SetUpIdle,
                                    QAdvanceFeedback.Core.ShakeItEffects.ShakeItSourceState.SetUpIdle);

        public void SetShakeIt(bool isLock, ShakeItSourceState state) => ShakeItSourceState = isLock
            ? new ShakeItSourceStates(state, ShakeItSourceState.Slip)
            : new ShakeItSourceStates(ShakeItSourceState.Lock, state);

        // ---- IWheelChannelPageHost ----
        public bool EvaluatorAvailable { get; set; } = true;
        public bool PickerAvailable { get; set; } = true;
        public bool EditorAvailable { get; set; } = true;

        public SourceButtonMode ResolveSourceButton(ScriptType scriptType) =>
            SourceButtonModeSelector.Resolve(scriptType, EditorAvailable, PickerAvailable, EvaluatorAvailable);

        /// <summary>What the next picker / editor call returns (null = cancelled).</summary>
        public SourceEdit NextEdit { get; set; }
        public List<(SourceButtonMode Mode, string Text, ScriptType Type)> EditCalls { get; } = new List<(SourceButtonMode, string, ScriptType)>();

        public Task<SourceEdit> EditSourceAsync(SourceButtonMode mode, string text, ScriptType scriptType)
        {
            EditCalls.Add((mode, text, scriptType));
            return Task.FromResult(NextEdit);
        }

        public bool ShakeItSourceBlocked(WheelChannelSettings channel, bool isLockChannel)
            => channel != null
               && channel.SourceMode == SourceMode.ShakeIt
               && ShakeItSourceResolution.FallsBackToRaw(ShakeItSourceState.For(isLockChannel));

        public bool ViperSourceBlocked(WheelChannelSettings channel, string gameId)
            => channel != null
               && channel.SourceMode == SourceMode.Viper
               && !string.IsNullOrWhiteSpace(gameId)
               && !ViperSupportedGames.IsSupported(gameId, ViperDocument.SupportedGames);

        private ViperDocument _viper = ViperSupportedGames.CreateShippedDocument();
        public ViperDocument ViperDocument { get => _viper; set { if (value != null) _viper = value; } }
        public bool SaveViperDocument() { ViperSaves++; return true; }
    }
}

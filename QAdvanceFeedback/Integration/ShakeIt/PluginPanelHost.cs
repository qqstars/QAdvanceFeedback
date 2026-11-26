using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;

namespace QAdvanceFeedback.Integration.ShakeIt
{
    /// <summary>
    /// The running plugin as seen by the panel view models (ShakeIt panels and the plugin page's Test
    /// Effect). It forwards to <see cref="QAdvanceFeedback.Instance"/> at call time, so a panel built
    /// before the plugin started (or across a restart) still reaches the live plugin. Null-safe
    /// throughout: with no plugin, settings read as null and publishes do nothing.
    /// </summary>
    public sealed class PluginPanelHost : IPanelHost, IShakeItSourceStateHost, IClutchHost, ISlipAngleEffectHost, ILearningMaturityHost, ICalibrationResetHost
    {
        /// <summary>The plugin's published ShakeIt Built-in source state per channel (docs\shakeit-source-design.md §4);
        /// NotSetUp for both with no plugin.</summary>
        public ShakeItSourceStates ShakeItSourceState => Plugin?.ShakeItSourceState ?? ShakeItSourceStates.Initial;

        // ---- Slip Angle (docs\slip-angle-design.md 9.10): the data-thread snapshots only ----

        public global::QAdvanceFeedback.Core.SlipAngle.SlipAngleStatus SlipAngleStatus =>
            Plugin?.LatestSlipAngleStatus ?? global::QAdvanceFeedback.Core.SlipAngle.SlipAngleStatus.None;
        public global::QAdvanceFeedback.Core.SourceNativeness SlipAngleIdentityTier =>
            Plugin?.CurrentSlipAngleIdentityTier ?? global::QAdvanceFeedback.Core.SourceNativeness.Unknown;
        public global::QAdvanceFeedback.Core.SlipAngle.SlipAngleWindow? LearnedSlipAngleWindow(string sourceIdentity) =>
            Plugin?.GetLearnedSlipAngleWindowForDisplay(sourceIdentity);

        // ---- Clutch Bite (docs\clutch-bite-design.md) ----

        public ClutchHostStatus ClutchStatus => ClutchStatusOf(Plugin);

        /// <summary>The plugin's clutch snapshot as the clutch view models read it (shared with
        /// <see cref="PluginPageHost"/>); empty with no plugin.</summary>
        internal static ClutchHostStatus ClutchStatusOf(QAdvanceFeedback plugin)
        {
            QAdvanceFeedback.ClutchUiState s = plugin?.ClutchUi;
            if (s == null) return new ClutchHostStatus();
            return new ClutchHostStatus
            {
                Kind = s.Kind,
                Input = s.Input,
                Key = s.Key,
                Learned = s.Status?.Learned,
                Count = s.Status?.Count ?? 0,
                IsMatured = s.Status?.IsMatured ?? false,
                Final = s.Status?.Final ?? 50,
            };
        }

        public System.Collections.Generic.IReadOnlyList<string> ControllerAxes() => Plugin?.ControllerAxes() ?? new string[0];
        public double? ReadAxis01(string name) => Plugin?.ReadAxis01(name);
        public void ForgetClutchBite() => Plugin?.ForgetClutchBite();
        public void PublishClutchTest(double? output0100) => Plugin?.PublishClutchTest(output0100);
        public double FinalBiteForTest => Plugin?.FinalBiteForTest ?? Settings?.Clutch?.BitePoint ?? 50.0;
        public string FinalBiteForTestKey => Plugin?.FinalBiteForTestKey;

        public static readonly PluginPanelHost Default = new PluginPanelHost();

        private PluginPanelHost() { }

        private static QAdvanceFeedback Plugin => QAdvanceFeedback.Instance;

        public QAdvanceFeedbackSettings Settings => Plugin?.Settings;
        public SettingsHub Hub => Plugin?.Hub;
        public string CurrentGameId => Plugin?.CurrentGameId;
        public global::QAdvanceFeedback.Core.SourceNativeness CurrentLockNativeness => Plugin?.CurrentLockNativeness ?? global::QAdvanceFeedback.Core.SourceNativeness.Unknown;
        public global::QAdvanceFeedback.Core.SourceNativeness CurrentSlipNativeness => Plugin?.CurrentSlipNativeness ?? global::QAdvanceFeedback.Core.SourceNativeness.Unknown;
        public int KeyDataRevision => Plugin?.KeyDataRevision ?? 0;
        public TestEffectSession Session => QAdvanceFeedback.TestSession;

        public bool ViperAvailable
        {
            get
            {
                try
                {
                    QAdvanceFeedback plugin = Plugin;
                    return plugin?.PluginManager != null && new ViperAvailabilityProvider().IsAvailable(plugin.PluginManager);
                }
                catch { return false; }
            }
        }

        public KeyDataTriple LearnedKeyData(bool isLock, string sourceIdentity) => Plugin?.GetLearnedKeyDataForDisplay(isLock, sourceIdentity);

        public KeyDataMaturity LearnedKeyDataMaturity(bool isLock, string sourceIdentity) => Plugin?.GetLearnedKeyDataMaturityForDisplay(isLock, sourceIdentity);

        public global::QAdvanceFeedback.Core.SlipAngle.SlipAngleWindowMaturity? LearnedSlipAngleMaturity(string sourceIdentity) =>
            Plugin?.GetLearnedSlipAngleMaturityForDisplay(sourceIdentity);

        public bool HasCalibrationForCurrentKey() => Plugin?.HasCalibrationForCurrentKey() ?? false;
        public bool ResetCalibrationForCurrentKey() => Plugin?.ResetCalibrationForCurrentKey() ?? false;
        public bool PersistCalibration => Plugin?.Settings?.General?.PersistSlipLockCalibration ?? true;
        public void SetPersistCalibration(bool persist) => Plugin?.SetPersistCalibration(persist);

        public void PublishGForceTest(GForceOutput outputOrNull) => Plugin?.PublishTestEffect(outputOrNull);

        public void PublishLockSlipTest(ProjectedWheelLockSlipResult resultOrNull) => Plugin?.PublishLockSlipTest(resultOrNull);

        public void PublishSlipAngleTest(SlipAngleProjectedResult resultOrNull) => Plugin?.PublishSlipAngleTest(resultOrNull);
    }
}

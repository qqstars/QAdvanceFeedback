using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>What the ShakeIt panel view models need from the running plugin. The plugin implements it;
    /// tests use a stand-in with real settings and a real hub.</summary>
    public interface IPanelHost : ITestEffectHost
    {
        /// <summary>Null while the plugin is not running.</summary>
        QAdvanceFeedbackSettings Settings { get; }
        SettingsHub Hub { get; }
        string CurrentGameId { get; }
        /// <summary>Whether the Wheel Lock / Wheel Slip Raw branch now selected is a native signal or a fallback estimate.</summary>
        global::QAdvanceFeedback.Core.SourceNativeness CurrentLockNativeness { get; }
        global::QAdvanceFeedback.Core.SourceNativeness CurrentSlipNativeness { get; }
        int KeyDataRevision { get; }
        bool ViperAvailable { get; }
        /// <summary>The learned key data for a channel's source, by the one display rule
        /// (<see cref="KeyDataPanelModel.LearnedKeyData"/>) - the same values the settings page shows.
        /// Null when nothing is known (the panel then shows the source's default).</summary>
        KeyDataTriple LearnedKeyData(bool isLock, string sourceIdentity);
    }

    /// <summary>The effect container's routing (persisted by ShakeIt in its AggregationMode).</summary>
    public interface IRoutingTarget
    {
        EffectRouting Routing { get; set; }
    }

    /// <summary>One dropdown entry: the value and its localized label.</summary>
    public sealed class Choice<T>
    {
        public Choice(T value, string label) { Value = value; Label = label; }
        public T Value { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }
}

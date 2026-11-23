using System.Threading.Tasks;
using QAdvanceFeedback.Core.Viper;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// What the Wheel Lock / Wheel Slip tabs' source sections need from the running plugin beyond
    /// <see cref="ISettingsPageHost"/> (docs\full-mvvm-plan.md §3.2). Optional: the view models cast
    /// <c>Ctx.Host as IWheelChannelPageHost</c> and tolerate null (no evaluator, no picker / editor, nothing
    /// blocked, no Viper game list). The plugin implements it (Wave 2); the members mirror the plugin's own
    /// public members (<c>ShakeItSourceBlocked</c>, <c>ViperSourceBlocked</c>, <c>ViperDocument</c>,
    /// <c>SaveViperDocument</c>) and the Slip Angle tab host's picker / editor members, so the page asks the
    /// plugin rather than re-deriving what the engine reads. The ShakeIt source state comes through the
    /// existing optional <see cref="IShakeItSourceStateHost"/>.
    /// </summary>
    public interface IWheelChannelPageHost
    {
        /// <summary>SimHub's expression evaluator resolved: the P/J/N toggle is shown and a script type is
        /// honoured. False: Plain is the only reachable (and shown) script type.</summary>
        bool EvaluatorAvailable { get; }

        /// <summary>The Pick / Edit button of a row of <paramref name="scriptType"/>
        /// (<see cref="SourceButtonModeSelector.Resolve"/> over the editor / picker / evaluator availability).</summary>
        SourceButtonMode ResolveSourceButton(ScriptType scriptType);

        /// <summary>Runs the property picker or the script editor; null when cancelled.</summary>
        Task<SourceEdit> EditSourceAsync(SourceButtonMode mode, string text, ScriptType scriptType);

        /// <summary>The plugin's <c>ShakeItSourceBlocked</c>: the channel's ShakeIt Built-in source reads (and
        /// keys as) Raw right now (published state NotSetUp / NotSupported).</summary>
        bool ShakeItSourceBlocked(WheelChannelSettings channel, bool isLockChannel);

        /// <summary>The plugin's <c>ViperSourceBlocked</c>: a Viper channel on a running game the Viper
        /// plugin does not compute for (no game is not an unsupported game).</summary>
        bool ViperSourceBlocked(WheelChannelSettings channel, string gameId);

        /// <summary>The Viper supported-game list (never null on the plugin).</summary>
        ViperDocument ViperDocument { get; set; }

        /// <summary>Persists <see cref="ViperDocument"/> at once (its own file, not part of Apply).</summary>
        bool SaveViperDocument();
    }
}

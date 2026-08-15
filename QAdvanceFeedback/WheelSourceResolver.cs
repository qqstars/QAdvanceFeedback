using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Settings;
using SimHub.Plugins;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Resolves ONE of <see cref="WheelChannelSettings"/>'s four per-wheel source fields to a live
    /// 0-100 reading, for whichever <see cref="ScriptType"/> the field is currently set to:
    /// <list type="bullet">
    /// <item><see cref="ScriptType.Plain"/> - read directly via
    /// <see cref="PluginManager.GetPropertyValue"/> (works for any registered SimHub property,
    /// including this plugin's own Layer 3 Raw output, the shipped default).</item>
    /// <item><see cref="ScriptType.JavaScript"/>/<see cref="ScriptType.NCalc"/> - evaluated through
    /// <see cref="SimHubExpressionEvaluator"/>.</item>
    /// </list>
    /// Any failure (missing property, unavailable evaluator, a malformed expression, a non-numeric
    /// result) falls back to <paramref name="fallback"/> - the caller passes Layer 3's own already-
    /// computed Raw value for that wheel, so a broken custom source degrades to "something sane"
    /// rather than a zero or a thrown exception, exactly the same philosophy as every other
    /// degradation ladder in this plugin.
    /// </summary>
    public sealed class WheelSourceResolver
    {
        private readonly SimHubExpressionEvaluator _evaluator = new SimHubExpressionEvaluator();

        public double Resolve(PluginManager pluginManager, string source, ScriptType scriptType, double fallback)
        {
            if (string.IsNullOrWhiteSpace(source)) return fallback;

            if (scriptType == ScriptType.JavaScript || scriptType == ScriptType.NCalc)
            {
                return _evaluator.TryEvaluate(source, scriptType, out double evaluated) ? evaluated : fallback;
            }

            try
            {
                object raw = pluginManager?.GetPropertyValue(source);
                return ClampMath.CoerceOrFallback(raw, fallback);
            }
            catch (Exception e) when (e is InvalidCastException || e is FormatException || e is OverflowException)
            {
                return fallback;
            }
        }
    }
}

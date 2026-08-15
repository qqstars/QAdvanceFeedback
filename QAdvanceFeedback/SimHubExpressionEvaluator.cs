using System;
using System.Linq;
using System.Reflection;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Evaluates a SimHub expression - JavaScript OR NCalc, per the field's own declared
    /// <see cref="ScriptType"/> - via SimHub's own engine, so a Layer 4 wheel-source field can say
    /// more than a bare property name. Ported near-verbatim from the sibling ReliableWheelLockSlip
    /// project's identically-named, hard-won class, retargeted at THIS project's own
    /// <see cref="ScriptType"/> (Plain/JavaScript/NCalc, rather than the sibling's JavaScript/NCalc
    /// only - <see cref="ScriptType.Plain"/> is never passed to <see cref="TryEvaluate"/> at all; a
    /// Plain source is read directly via <c>PluginManager.GetPropertyValue</c> instead, see
    /// <see cref="WheelSourceResolver"/>).
    /// <para/>
    /// Resolved purely by reflection, with the same discipline as <see cref="PropertyPickerLauncher"/>:
    /// these are internal SimHub types with no public contract, so a compile-time reference would turn
    /// a future SimHub release that moves, renames or reshapes them into a hard load-time failure for
    /// the whole plugin instead of a graceful degrade. Deliberately outside Core/: this class exists
    /// only to talk to a live SimHub process and cannot be unit-tested.
    /// <para/>
    /// ANY failure - at resolution, or at a specific evaluation - retires this evaluator for the rest
    /// of the session and logs once. Callers MUST treat a <c>false</c> return as "source unavailable"
    /// (fall through to the next source/fallback), never as a reading of zero.
    /// </summary>
    public sealed class SimHubExpressionEvaluator
    {
        private const string EngineTypeName = "SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon.NCalcEngineBase";
        private const string ExpressionValueTypeName = "SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating.ExpressionValue";
        private const string InterpreterTypeName = "SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating.Interpreter";

        private bool _resolved;
        private bool _available;
        private bool _loggedOnce;

        private ConstructorInfo _engineCtor;
        private ConstructorInfo _expressionValueCtor;
        private Type _interpreterType;
        private MethodInfo _parseValueMethod;

        /// <summary>False if the engine could not be resolved, or broke and was retired for the rest
        /// of the session. The settings UI can use this to warn that expressions are unavailable and
        /// only plain property names will work.</summary>
        public bool IsAvailable
        {
            get
            {
                EnsureResolved();
                return _available;
            }
        }

        /// <summary>
        /// Evaluates <paramref name="expressionText"/> through SimHub's own engine, as JavaScript or
        /// NCalc according to <paramref name="scriptType"/> (never <see cref="ScriptType.Plain"/> -
        /// callers route that case through <c>PluginManager.GetPropertyValue</c> instead). Returns
        /// false - never throws - if the engine is unavailable, the expression throws or returns null,
        /// or the result cannot be converted to a finite double.
        /// </summary>
        public bool TryEvaluate(string expressionText, ScriptType scriptType, out double value)
        {
            value = 0.0;
            if (string.IsNullOrWhiteSpace(expressionText)) return false;

            EnsureResolved();
            if (!_available) return false;

            try
            {
                string interpreterName = scriptType == ScriptType.NCalc ? "NCalc" : "Javascript";
                object interpreterValue = Enum.Parse(_interpreterType, interpreterName);
                object engine = _engineCtor.Invoke(null);
                object expressionValue = _expressionValueCtor.Invoke(new[] { expressionText, interpreterValue });
                Func<string> contextInfo = () => "QAdvanceFeedback";

                object result = _parseValueMethod.Invoke(engine, new object[] { expressionValue, contextInfo, string.Empty });
                if (result == null) return false;

                double converted = Convert.ToDouble(result, System.Globalization.CultureInfo.InvariantCulture);
                if (!ClampMath.IsFinite(converted)) return false;

                value = converted;
                return true;
            }
            catch (Exception e)
            {
                _available = false;
                LogOnce("expression evaluation failed, configured sources will fall back to plain property names - " + e.Message);
                return false;
            }
        }

        private void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                Type engineType = FindType(EngineTypeName);
                Type expressionValueType = FindType(ExpressionValueTypeName);
                Type interpreterType = FindType(InterpreterTypeName);

                if (engineType == null || expressionValueType == null || interpreterType == null)
                    throw new InvalidOperationException(
                        $"type not found (engine={engineType != null}, expressionValue={expressionValueType != null}, interpreter={interpreterType != null})");

                _engineCtor = engineType.GetConstructor(Type.EmptyTypes);
                _expressionValueCtor = expressionValueType.GetConstructor(new[] { typeof(string), interpreterType });
                _interpreterType = interpreterType;

                _parseValueMethod = engineType.GetMethods()
                    .FirstOrDefault(m => m.Name == "ParseValue" && m.GetParameters().Length == 3);

                if (_engineCtor == null || _expressionValueCtor == null || _parseValueMethod == null)
                    throw new InvalidOperationException("a required constructor or method was not found");

                _available = true;
            }
            catch (Exception e)
            {
                _available = false;
                LogOnce("expression engine unavailable, configured sources will only accept plain property names - " + e.Message);
            }
        }

        private void LogOnce(string message)
        {
            if (_loggedOnce) return;
            _loggedOnce = true;
            try { SimHub.Logging.Current.Info("QAdvanceFeedback: " + message); }
            catch { /* logging must never be the thing that throws */ }
        }

        private static Type FindType(string fullName, string assemblyNameHint = "SimHub.Plugins")
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = assembly.GetType(fullName, throwOnError: false); }
                catch { type = null; }
                if (type != null) return type;
            }

            if (!string.IsNullOrEmpty(assemblyNameHint))
            {
                try
                {
                    Assembly loaded = Assembly.Load(assemblyNameHint);
                    return loaded.GetType(fullName, throwOnError: false);
                }
                catch { /* falls through to null below */ }
            }

            return null;
        }
    }
}

using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback
{
    /// <summary>
    /// The outcome of a completed edit - never constructed for a cancelled or failed edit (see
    /// <see cref="SimHubScriptEditor.EditExpressionAsync"/>'s remarks, which returns null instead).
    /// </summary>
    public sealed class ScriptEditResult
    {
        public string Expression { get; }
        public ScriptType ScriptType { get; }

        public ScriptEditResult(string expression, ScriptType scriptType)
        {
            Expression = expression;
            ScriptType = scriptType;
        }
    }

    /// <summary>
    /// Opens SimHub's own NCalc/JavaScript formula editor purely through reflection. Ported
    /// near-verbatim from the sibling ReliableWheelLockSlip project's identically-named class -
    /// that project's own report documents exactly why this works this way and what was tried and
    /// failed first (<c>SimHub.Plugins.UI.ChildWindowManager</c> does not exist in the shipped
    /// SimHub.Plugins.dll; the real editor,
    /// <c>SimHub.Plugins.OutputPlugins.EditorControls.BindingEditor</c>, derives from
    /// <c>SimHub.Plugins.UI.SHDialogContentBase</c>, which supplies its own
    /// <c>ShowDialogWindowAsync(DependencyObject, DialogOptions, double?, double?)</c> - the exact
    /// same 4-parameter shape <see cref="PropertyPickerLauncher"/> already resolves by shape rather
    /// than by name of a fixed base type).
    /// <para/>
    /// Keeps the same defensive discipline: resolve everything by reflection, never hard-reference a
    /// SimHub internal type at compile time, degrade to the plain editable text box if anything is
    /// missing, log once, and retire the editor for the session if it throws while showing.
    /// Deliberately outside <c>Core/</c>: this class exists only to talk to a live SimHub process and
    /// cannot be unit-tested.
    /// </summary>
    public static class SimHubScriptEditor
    {
        private const string BindingEditorTypeName = "SimHub.Plugins.OutputPlugins.EditorControls.BindingEditor";
        private const string BindingDataTypeName = "SimHub.Plugins.OutputPlugins.GraphicalDash.Models.DashboardBindingData";
        private const string ExpressionValueTypeName = "SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating.ExpressionValue";

        private static bool _resolved;
        private static bool _available;
        private static bool _loggedOnce;

        private static ConstructorInfo _bindingEditorCtor;
        private static ConstructorInfo _bindingDataCtor;
        private static ConstructorInfo _expressionValueCtor;

        private static PropertyInfo _formulaProperty;
        private static PropertyInfo _expressionProperty;
        private static PropertyInfo _useJavascriptProperty;

        private static MethodInfo _showDialogWindowAsyncMethod;

        /// <summary>False if the editor could not be resolved, or broke and was retired for the rest
        /// of the session. The settings UI should hide its "Edit script..." button when this is false
        /// and leave the text box editable regardless.</summary>
        public static bool IsAvailable
        {
            get
            {
                EnsureResolved();
                return _available;
            }
        }

        /// <summary>
        /// Shows SimHub's built-in formula editor and awaits until it closes. Returns the edited
        /// expression and resulting script type, or null if the editor is unavailable, the user
        /// cancelled, or anything at all went wrong.
        /// </summary>
        public static async Task<ScriptEditResult> EditExpressionAsync(
            Window owner, string currentExpression, ScriptType currentScriptType)
        {
            EnsureResolved();
            if (!_available) return null;

            try
            {
                object expressionValue = _expressionValueCtor.Invoke(null);
                _expressionProperty.SetValue(expressionValue, currentExpression ?? string.Empty);
                _useJavascriptProperty.SetValue(expressionValue, currentScriptType != ScriptType.NCalc);

                object bindingData = _bindingDataCtor.Invoke(null);
                _formulaProperty.SetValue(bindingData, expressionValue);

                object bindingEditor = _bindingEditorCtor.Invoke(null);
                if (!(bindingEditor is FrameworkElement editorElement))
                    throw new InvalidOperationException("BindingEditor is not a FrameworkElement");

                editorElement.DataContext = bindingData;

                Type dialogOptionsType = _showDialogWindowAsyncMethod.GetParameters()[1].ParameterType;
                object noOptions = Enum.Parse(dialogOptionsType, "None");

                object taskObj = _showDialogWindowAsyncMethod.Invoke(bindingEditor, new object[] { owner, noOptions, null, null });
                var task = (Task)taskObj;
                await task.ConfigureAwait(true);

                object dialogResult = task.GetType().GetProperty("Result")?.GetValue(task);
                if (dialogResult == null || !string.Equals(dialogResult.ToString(), "OK", StringComparison.Ordinal))
                    return null;

                object formulaBack = _formulaProperty.GetValue(bindingData);
                string expressionBack = _expressionProperty.GetValue(formulaBack) as string;
                bool isJavascriptBack = _useJavascriptProperty.GetValue(formulaBack) is bool b && b;

                return new ScriptEditResult(
                    expressionBack,
                    isJavascriptBack ? ScriptType.JavaScript : ScriptType.NCalc);
            }
            catch (Exception e)
            {
                _available = false;
                LogOnce("script editor failed while showing, falling back to the text box - " + e.Message);
                return null;
            }
        }

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                Type bindingEditorType = FindType(BindingEditorTypeName);
                Type bindingDataType = FindType(BindingDataTypeName);
                Type expressionValueType = FindType(ExpressionValueTypeName);

                if (bindingEditorType == null || bindingDataType == null || expressionValueType == null)
                    throw new InvalidOperationException(
                        "type not found (editor=" + (bindingEditorType != null) +
                        ", bindingData=" + (bindingDataType != null) +
                        ", expression=" + (expressionValueType != null) + ")");

                _bindingEditorCtor = bindingEditorType.GetConstructor(Type.EmptyTypes);
                _bindingDataCtor = bindingDataType.GetConstructor(Type.EmptyTypes);
                _expressionValueCtor = expressionValueType.GetConstructor(Type.EmptyTypes);

                _formulaProperty = bindingDataType.GetProperty("Formula");
                _expressionProperty = expressionValueType.GetProperty("Expression");
                _useJavascriptProperty = expressionValueType.GetProperty("UseJavascript");

                _showDialogWindowAsyncMethod = bindingEditorType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ShowDialogWindowAsync" && m.GetParameters().Length == 4);

                if (_bindingEditorCtor == null || _bindingDataCtor == null || _expressionValueCtor == null
                    || _formulaProperty == null || _expressionProperty == null || _useJavascriptProperty == null
                    || _showDialogWindowAsyncMethod == null)
                    throw new InvalidOperationException("a required constructor or member was not found");

                _available = true;
            }
            catch (Exception e)
            {
                _available = false;
                LogOnce("script editor unavailable, settings UI will use a plain text box - " + e.Message);
            }
        }

        private static void LogOnce(string message)
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

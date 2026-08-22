using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using QAdvanceFeedback.Core.Health;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Opens SimHub's own property picker dialog (<c>PropertiesPicker</c>, hosted by
    /// <c>SimHub.Plugins.UI.SHDialog</c>) purely through reflection. Ported near-verbatim from the
    /// sibling ReliableWheelLockSlip project's identically-named, hard-won class - it already solves
    /// this. A future SimHub release that moves, renames or reshapes the picker must degrade the
    /// settings UI to a plain, always-editable text box - it must never throw a
    /// <see cref="TypeLoadException"/> at load time and take the whole plugin down. Deliberately
    /// outside <c>Core/</c>: this class exists only to talk to a live SimHub process and cannot be
    /// unit-tested.
    /// </summary>
    public sealed class PropertyPickerLauncher
    {
        private const string PickerTypeName = "SimHub.Plugins.OutputPlugins.Dash.WPFUI.PropertiesPicker";
        private const string EngineTypeName = "SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon.NCalcEngineBase";
        private const string EntryTypeName = "SimHubWPF.Controls.EditorPropertyEntry";

        private bool _resolved;
        private bool _available;
        private bool _loggedOnce;

        private ConstructorInfo _engineCtor;
        private ConstructorInfo _pickerCtor;
        private PropertyInfo _resultProperty;
        private MethodInfo _getPropertyNameMethod;

        // ShowDialogWindowAsync(DependencyObject owner, DialogOptions options, double? width, double? height)
        // - inherited from SHDialogContentBase. Found by shape rather than by hardcoding that base
        // type's name, so a picker that stays where it is but grows a new base class still resolves.
        private MethodInfo _showDialogAsyncMethod;

        /// <summary>False if the picker could not be resolved, or broke and was retired for the rest
        /// of the session. The settings UI should hide its "Pick..." button when this is false and
        /// leave the text box editable regardless.</summary>
        public bool IsAvailable
        {
            get
            {
                EnsureResolved();
                return _available;
            }
        }

        /// <summary>
        /// Shows the picker and awaits until it closes, WITHOUT blocking the calling (UI) thread.
        /// Returns the chosen property name, or null if the picker is unavailable, the user
        /// cancelled, or anything at all went wrong - a broken picker must degrade the caller, never
        /// crash it.
        /// </summary>
        public async Task<string> PickPropertyAsync(string currentValue)
        {
            EnsureResolved();
            if (!_available) return null;

            try
            {
                object engine = _engineCtor.Invoke(null);
                object picker = _pickerCtor.Invoke(new[] { engine });

                Type optionsType = _showDialogAsyncMethod.GetParameters()[1].ParameterType;
                object noOptions = Enum.Parse(optionsType, "None");

                object taskObj = _showDialogAsyncMethod.Invoke(picker, new object[] { null, noOptions, null, null });
                var task = (Task)taskObj;
                await task.ConfigureAwait(true);

                object dialogResult = task.GetType().GetProperty("Result")?.GetValue(task);
                if (dialogResult == null || !string.Equals(dialogResult.ToString(), "OK", StringComparison.Ordinal))
                    return null;

                object entry = _resultProperty.GetValue(picker);
                if (entry == null) return null;

                return _getPropertyNameMethod.Invoke(entry, null) as string;
            }
            catch (Exception e)
            {
                _available = false;
                LogOnce("property picker failed while showing, falling back to the text box - " + e.Message);
                HealthRegistry.Report(HealthSubsystems.PropertyPicker, HealthSeverity.Degraded,
                    "Health.Impact.PropertyPicker", e.ToString());
                return null;
            }
        }

        private void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                Type pickerType = FindType(PickerTypeName);
                Type engineType = FindType(EngineTypeName);
                Type entryType = FindType(EntryTypeName);

                if (pickerType == null || engineType == null || entryType == null)
                    throw new InvalidOperationException(
                        $"type not found (picker={pickerType != null}, engine={engineType != null}, entry={entryType != null})");

                _engineCtor = engineType.GetConstructor(Type.EmptyTypes);
                _pickerCtor = pickerType.GetConstructor(new[] { engineType });
                _resultProperty = pickerType.GetProperty("Result");
                _getPropertyNameMethod = entryType.GetMethod("GetPropertyName", Type.EmptyTypes);
                _showDialogAsyncMethod = pickerType.GetMethods()
                    .FirstOrDefault(m => m.Name == "ShowDialogWindowAsync" && m.GetParameters().Length == 4);

                if (_engineCtor == null || _pickerCtor == null || _resultProperty == null
                    || _getPropertyNameMethod == null || _showDialogAsyncMethod == null)
                    throw new InvalidOperationException("a required constructor, property or method was not found");

                _available = true;
            }
            catch (Exception e)
            {
                _available = false;
                LogOnce("property picker unavailable, settings UI will use a plain text box - " + e.Message);
                HealthRegistry.Report(HealthSubsystems.PropertyPicker, HealthSeverity.Degraded,
                    "Health.Impact.PropertyPicker", e.ToString(), isSimHubCompatibilityIssue: true);
            }
        }

        private void LogOnce(string message)
        {
            if (_loggedOnce) return;
            _loggedOnce = true;
            try { SimHub.Logging.Current.Info("QAdvanceFeedback: " + message); }
            catch { /* logging must never be the thing that throws */ }
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = assembly.GetType(fullName, throwOnError: false); }
                catch { type = null; }
                if (type != null) return type;
            }
            return null;
        }
    }
}

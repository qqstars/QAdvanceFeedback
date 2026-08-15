using System;
using System.IO;
using Newtonsoft.Json;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Reads/writes this plugin's user-configuration file, <c>QAdvanceFeedback.config.json</c>
    /// (renamed from <c>plugin.QAdvanceFeedback.config.json</c> - see <see cref="Load"/>'s remarks for
    /// the one-time import of a file still under the old name), at an explicit path the caller
    /// resolves from <c>PluginManager.GetCommonStoragePath</c> - not through SimHub's own versioned
    /// ReadCommonSettings/SaveCommonSettings. Ported from the sibling ReliableWheelLockSlip project's
    /// <c>ConfigStore.cs</c> (per the brief: "port the sibling project's ConfigStore/RuntimeStore/
    /// RuntimeCache pattern, it already solves this"), retargeted at <see cref="QAdvanceFeedbackSettings"/>.
    /// <para/>
    /// Deliberately simple: this is NOT on the hot per-frame path (loaded once at Init, saved only at
    /// shutdown/on an explicit apply), so it needs none of <c>RuntimeStore</c>'s cache/timer
    /// machinery. Still writes atomically (temp file + replace) since a crash mid-write must not leave
    /// a truncated config that bricks the next start.
    /// <para/>
    /// <paramref name="logWarning"/>-style delegates on both methods are optional and default to
    /// doing nothing - this keeps the class free of any SimHub reference (only System.IO and
    /// Newtonsoft.Json), which is what lets it be linked directly into the SimHub-free test project.
    /// </summary>
    public static class ConfigStore
    {
        /// <summary>
        /// A missing or corrupt file degrades to <see cref="QAdvanceFeedbackSettings"/>'s own shipped
        /// defaults - never throws out of here, which is what lets Init call this unconditionally.
        /// <para/>
        /// If <paramref name="path"/> (the new name) does not exist yet but <paramref name="legacyPath"/>
        /// (the old name, <c>plugin.QAdvanceFeedback.config.json</c>) does, the legacy file is loaded
        /// instead - imported exactly ONCE, since the result is saved straight back out under the NEW
        /// name here, so a driver's tuning survives the rename rather than silently reverting to
        /// defaults, and every subsequent start finds the new file first and never looks at the old
        /// one again.
        /// </summary>
        public static QAdvanceFeedbackSettings Load(string path, Action<string> logWarning = null, string legacyPath = null)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(File.ReadAllText(path));
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                logWarning?.Invoke("QAdvanceFeedback: config load failed, using defaults - " + e.Message);
            }

            try
            {
                if (!string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath))
                {
                    var legacy = JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(File.ReadAllText(legacyPath));
                    if (legacy != null)
                    {
                        Save(path, legacy, logWarning);
                        return legacy;
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                logWarning?.Invoke("QAdvanceFeedback: legacy config import failed, using defaults - " + e.Message);
            }

            return new QAdvanceFeedbackSettings();
        }

        public static void Save(string path, QAdvanceFeedbackSettings settings, Action<string> logWarning = null)
        {
            if (string.IsNullOrEmpty(path) || settings == null) return;

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, json);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
            }
            catch (IOException e) { logWarning?.Invoke("QAdvanceFeedback: config save failed - " + e.Message); }
            catch (UnauthorizedAccessException e) { logWarning?.Invoke("QAdvanceFeedback: config save denied - " + e.Message); }
        }
    }
}

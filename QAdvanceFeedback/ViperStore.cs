using System;
using System.IO;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.Viper;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Reads and writes <c>QAdvanceFeedback.Viper.json</c> - see <see cref="ViperDocument"/> for what
    /// lives in it and why it is not part of the config file.
    /// <para/>
    /// Modelled on <see cref="ConfigStore"/> deliberately, down to the atomic write and the optional
    /// log delegate: same cadence (read once, write only on an explicit Apply), same "never throw out
    /// of here" contract, and the same freedom from any SimHub reference so the test project can link
    /// it directly.
    /// </summary>
    public static class ViperStore
    {
        /// <summary>The file name, appended to SimHub's common storage path by the caller - the same
        /// arrangement <see cref="ConfigStore"/> uses, which keeps this class free of SimHub.</summary>
        public const string FileName = "QAdvanceFeedback.Viper.json";

        /// <summary>
        /// The stored document, or the shipped one when there is no file yet.
        /// <para/>
        /// A MISSING FILE IS THE NORMAL FIRST RUN, not an error: it returns the shipped list WITHOUT
        /// writing anything, because writing is reserved for Apply. A corrupt or unreadable file
        /// degrades the same way and is reported to the health registry rather than thrown, so a bad
        /// file can never stop the plugin loading.
        /// <para/>
        /// Always sanitised (see <see cref="ViperDocument.Sanitise"/>), so callers never have to defend
        /// against a null list, a blank entry or a duplicate from a hand edit.
        /// </summary>
        public static ViperDocument Load(string path, Action<string> logWarning = null)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<ViperDocument>(File.ReadAllText(path));
                    if (loaded != null) return ViperDocument.Sanitise(loaded);
                }
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                logWarning?.Invoke("QAdvanceFeedback: Viper file load failed, using the shipped game list - " + e.Message);
                HealthRegistry.Report(HealthSubsystems.ConfigPersistence, HealthSeverity.Degraded,
                    "Health.Impact.ConfigPersistence", e.ToString());
            }

            return ViperSupportedGames.CreateShippedDocument();
        }

        /// <summary>
        /// Writes the document, atomically (temp file then replace) so a crash mid-write cannot leave a
        /// truncated file behind. Returns false rather than throwing when the write fails.
        /// </summary>
        public static bool Save(string path, ViperDocument document, Action<string> logWarning = null)
        {
            if (string.IsNullOrEmpty(path) || document == null) return false;

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                string json = JsonConvert.SerializeObject(ViperDocument.Sanitise(document), Formatting.Indented);
                string temp = path + ".tmp";
                File.WriteAllText(temp, json);

                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                return true;
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                logWarning?.Invoke("QAdvanceFeedback: Viper file save failed - " + e.Message);
                HealthRegistry.Report(HealthSubsystems.ConfigPersistence, HealthSeverity.Degraded,
                    "Health.Impact.ConfigPersistence", e.ToString());
                return false;
            }
        }
    }
}

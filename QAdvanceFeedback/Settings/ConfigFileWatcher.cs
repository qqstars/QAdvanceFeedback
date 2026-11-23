using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Newtonsoft.Json;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// Backup sync path (docs\shakeit-native-effects-design.md): notices the config file changing on
    /// disk from outside this process's own saves. Debounced; content identical to our own last save
    /// (<c>lastSavedHash</c>) or to the last content already reported is ignored; an unreadable or
    /// corrupt file is ignored (never treated as "defaults"). The callback runs on a thread-pool
    /// thread - the owner marshals it.
    /// </summary>
    public sealed class ConfigFileWatcher : IDisposable
    {
        private readonly string _path;
        private readonly Func<string> _lastSavedHash;
        private readonly Action<QAdvanceFeedbackSettings> _onExternalChange;
        private readonly int _debounceMs;
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _timer;
        private string _lastReportedHash;
        private volatile bool _disposed;

        public ConfigFileWatcher(string path, Func<string> lastSavedHash, Action<QAdvanceFeedbackSettings> onExternalChange, int debounceMs = 300)
        {
            _path = Path.GetFullPath(path);
            _lastSavedHash = lastSavedHash ?? (() => null);
            _onExternalChange = onExternalChange ?? throw new ArgumentNullException(nameof(onExternalChange));
            _debounceMs = debounceMs;
            _lastReportedHash = TryHashFile(_path);

            _timer = new Timer(_ => OnDebounced(), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(_path), Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            _watcher.Changed += (s, e) => Kick();
            _watcher.Created += (s, e) => Kick();
            _watcher.Renamed += (s, e) => Kick();
            _watcher.EnableRaisingEvents = true;
        }

        public static string HashOf(byte[] content)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(content));
        }

        /// <summary>Hash of the file's current content, or null when it cannot be read.</summary>
        public static string TryHashFile(string path)
        {
            byte[] bytes = TryReadAll(path);
            return bytes == null ? null : HashOf(bytes);
        }

        private void Kick()
        {
            if (_disposed) return;
            try { _timer.Change(_debounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }

        private void OnDebounced()
        {
            if (_disposed) return;
            try
            {
                byte[] bytes = TryReadAll(_path);
                if (bytes == null) return;
                string hash = HashOf(bytes);
                if (hash == _lastReportedHash || hash == _lastSavedHash()) { _lastReportedHash = hash; return; }

                QAdvanceFeedbackSettings loaded;
                try { loaded = JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(System.Text.Encoding.UTF8.GetString(bytes)); }
                catch (JsonException) { return; }
                if (loaded == null) return;
                loaded.Normalise();

                _lastReportedHash = hash;
                _onExternalChange(loaded);
            }
            catch (Exception) { /* a watcher failure must never take SimHub down */ }
        }

        private static byte[] TryReadAll(string path)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return null;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var ms = new MemoryStream())
                    {
                        fs.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
                catch (IOException) { Thread.Sleep(50); }
                catch (UnauthorizedAccessException) { Thread.Sleep(50); }
            }
            return null;
        }

        public void Dispose()
        {
            _disposed = true;
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _timer.Dispose();
        }
    }
}

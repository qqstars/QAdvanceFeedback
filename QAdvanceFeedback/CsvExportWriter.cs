using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Backs the General tab's "Export CSV" toggle: when
    /// <see cref="Settings.GeneralSettings.ExportCsv"/> is on, writes EVERY property this plugin
    /// computes (product AND diagnostic, regardless of whether diagnostics are actually published to
    /// SimHub) to a plain CSV file for troubleshooting, one row per frame. When off, nothing is
    /// written at all - <see cref="Start"/> is simply never called by the caller in that case.
    /// <para/>
    /// Deliberately generic over "a header row of names, then rows of values" rather than tied to any
    /// one fixed schema (unlike the sibling project's own <c>TelemetryCsv</c>, which parses a fixed
    /// telemetry-frame shape back and forth) - this plugin's CSV is write-only, for a human to open
    /// afterwards, not a format this plugin itself ever reads back in.
    /// <para/>
    /// SimHub-free (only System.IO/System.Text) so it is link-compiled into the test project and
    /// exercised against real temporary files, same convention as <c>ConfigStore</c>/<c>RuntimeStore</c>.
    /// </summary>
    public sealed class CsvExportWriter : IDisposable
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        private StreamWriter _writer;
        private int _columnCount;

        /// <summary>True while a file is open and accepting rows.</summary>
        public bool IsRecording => _writer != null;

        /// <summary>
        /// Opens <paramref name="path"/> (creating parent directories if needed) and writes the
        /// header row: "TimestampUtc" followed by every name in <paramref name="propertyNames"/>, in
        /// order. Safe to call again after <see cref="Stop"/> to start a new file. Any I/O failure is
        /// swallowed (via <paramref name="logWarning"/>) rather than thrown - a broken CSV export must
        /// never take the rest of the plugin down with it.
        /// </summary>
        public void Start(string path, IReadOnlyList<string> propertyNames, Action<string> logWarning = null)
        {
            Stop();
            if (string.IsNullOrEmpty(path) || propertyNames == null) return;

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                // FileShare.Read (not the StreamWriter(path, append) constructor's own default of
                // FileShare.Read - explicit here so a driver can open the CSV in another program
                // while a session is still recording, exactly the "troubleshooting" use case this
                // exists for) plus AutoFlush so every row is visible to a concurrent reader
                // immediately, not only once the file is eventually closed.
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _columnCount = propertyNames.Count;

                var header = new StringBuilder("TimestampUtc");
                foreach (string name in propertyNames)
                {
                    header.Append(',').Append(EscapeCsv(name));
                }
                _writer.WriteLine(header.ToString());
            }
            catch (IOException e) { logWarning?.Invoke("QAdvanceFeedback: CSV export could not start - " + e.Message); Stop(); }
            catch (UnauthorizedAccessException e) { logWarning?.Invoke("QAdvanceFeedback: CSV export denied - " + e.Message); Stop(); }
        }

        /// <summary>
        /// Writes one row: the current UTC timestamp, then each of <paramref name="values"/> in the
        /// same order the header in <see cref="Start"/> was written - <c>null</c> values render as an
        /// empty cell (the honest "no data this frame" reading, not a literal "null" or a 0). No-op
        /// if <see cref="IsRecording"/> is false, or if the row's length does not match the header's
        /// (a caller bug, not something worth crashing over).
        /// </summary>
        public void WriteRow(IReadOnlyList<object> values, Action<string> logWarning = null)
        {
            if (_writer == null || values == null || values.Count != _columnCount) return;

            try
            {
                var row = new StringBuilder(DateTime.UtcNow.ToString("O", Invariant));
                foreach (object value in values)
                {
                    row.Append(',').Append(FormatCell(value));
                }
                _writer.WriteLine(row.ToString());
            }
            catch (IOException e) { logWarning?.Invoke("QAdvanceFeedback: CSV export write failed - " + e.Message); Stop(); }
        }

        /// <summary>Closes the file (if open). Safe to call when nothing is open.</summary>
        public void Stop()
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
            _columnCount = 0;
        }

        public void Dispose() => Stop();

        private static string FormatCell(object value)
        {
            if (value == null) return string.Empty;
            if (value is double d) return double.IsNaN(d) || double.IsInfinity(d) ? string.Empty : d.ToString("R", Invariant);
            if (value is bool b) return b ? "1" : "0";
            return EscapeCsv(Convert.ToString(value, Invariant));
        }

        private static string EscapeCsv(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            bool needsQuoting = text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (!needsQuoting) return text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
    }
}

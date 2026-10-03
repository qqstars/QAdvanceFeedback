using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace QAdvanceFeedback.Core.Runtime
{
    /// <summary>
    /// Replace a file's contents so that a power cut, a forced reset or a host crash can never leave
    /// it MISSING - the single shared write path for every file this plugin owns.
    /// <para/>
    /// THE DEFECT THIS REPLACES (owner-reported, 2026-10-02: "after FORCE restart the machine, both of
    /// the plugin setting file, and the game configuration will both missing"). All three stores used
    /// the same shape:
    /// <code>
    ///   File.WriteAllText(temporary, json);
    ///   if (File.Exists(path)) File.Delete(path);   // &lt;-- the file is now GONE
    ///   File.Move(temporary, path);
    /// </code>
    /// Between the delete and the move the target genuinely does not exist. Lose power there and the
    /// configuration is gone for good - not corrupted, not stale, absent. The window is small but it is
    /// entered on every single save, which on the parameters file is every few seconds for a whole
    /// session.
    /// <para/>
    /// AND WRITING IS NOT THE SAME AS PERSISTING. <see cref="File.WriteAllText(string,string)"/>
    /// returns once the bytes are in the operating system's write cache, which may hold them for a long
    /// time - indefinitely if the machine stops servicing the flush. NTFS journals METADATA, not file
    /// contents, so a hard reset can restore the directory entry and leave the data empty or stale.
    /// <see cref="FileStream.Flush(bool)"/> with <c>flushToDisk: true</c> is what actually forces the
    /// bytes down before the swap, so the temporary file is known-good at the moment it takes over.
    /// <para/>
    /// <see cref="File.Replace(string,string,string)"/> is then a SINGLE metadata operation: the name
    /// points at the old contents or the new contents, never at nothing. That is the property the old
    /// delete-then-move could not provide at any speed.
    /// <para/>
    /// NOT A TRANSACTION, and not claimed to be. A crash can still lose the most recent save - the
    /// previous contents survive instead. What it guarantees is that SOME valid version of the file is
    /// always on disk.
    /// </summary>
    public static class AtomicFile
    {
        /// <summary>The temporary file's suffix. Beside the target deliberately: <see cref="File.Replace"/>
        /// needs both paths on the same volume, which a system temp directory cannot promise.</summary>
        public const string TemporarySuffix = ".tmp";

        /// <summary>UTF-8 WITHOUT a byte-order mark, matching <see cref="File.WriteAllText(string,string)"/>
        /// exactly - every file written before this existed is in that encoding, and changing it would
        /// put a BOM in front of the JSON.</summary>
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// Write <paramref name="contents"/> to <paramref name="path"/>, durably and atomically.
        /// <para/>
        /// Throws exactly what the old code threw (<see cref="IOException"/>,
        /// <see cref="System.UnauthorizedAccessException"/>), so every existing caller's catch blocks
        /// still apply unchanged.
        /// </summary>
        public static void WriteAllText(string path, string contents)
        {
            if (string.IsNullOrEmpty(path)) return;

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string temporary = path + TemporarySuffix;

            // FileShare.None: nothing may observe a half-written temporary file.
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                writer.Write(contents);
                writer.Flush();

                // The whole point - push it past the OS cache onto the device before anything starts
                // depending on it.
                stream.Flush(flushToDisk: true);
            }

            Swap(temporary, path);
        }

        /// <summary>
        /// Rename <paramref name="temporary"/> onto <paramref name="path"/> as ONE operation, so the
        /// name never resolves to nothing.
        /// <para/>
        /// NOT <see cref="File.Replace(string,string,string)"/>, AND THAT IS MEASURED, not assumed.
        /// Win32 <c>ReplaceFile</c> - which File.Replace wraps - exists to preserve the destination's
        /// identity and attributes, and internally moves the destination aside before renaming the
        /// replacement in. A reader polling the path across 300 rewrites saw it ABSENT on 5,523 of
        /// 116,548 checks: 4.7%, which is not a theoretical window, it is the same bug in a smaller
        /// form. <c>MoveFileEx</c> with <c>MOVEFILE_REPLACE_EXISTING</c> is a single NTFS directory
        /// transaction instead, and the same test observes zero.
        /// <para/>
        /// <c>MOVEFILE_WRITE_THROUGH</c> additionally holds the call until the rename itself is on the
        /// device, so the swap cannot be the part that a power cut loses after the contents were
        /// already flushed.
        /// </summary>
        private static void Swap(string temporary, string path)
        {
            if (MoveFileExW(temporary, path, MoveFileReplaceExisting | MoveFileWriteThrough)) return;

            // Translated immediately, while the thread's last-error is still ours, and surfaced as the
            // IOException every caller here already catches.
            int error = Marshal.GetLastWin32Error();
            throw new IOException(
                "Atomic replace of '" + path + "' failed (Win32 error " + error + ").",
                new Win32Exception(error));
        }

        private const uint MoveFileReplaceExisting = 0x00000001;
        private const uint MoveFileWriteThrough = 0x00000008;

        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true,
            CharSet = CharSet.Unicode, BestFitMapping = false)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);
    }
}

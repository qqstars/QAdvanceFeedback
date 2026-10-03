using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QAdvanceFeedback.Core.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// FILES THIS PLUGIN OWNS MUST SURVIVE A FORCED RESTART (owner-reported, 2026-10-02: "after FORCE
    /// restart the machine, both of the plugin setting file ... will both missing").
    /// <para/>
    /// All three stores wrote a temporary file, DELETED the target and then moved. Between those two
    /// steps the configuration did not exist - and that window was entered on every save, which for the
    /// parameters file is every few seconds for a whole session. Losing power inside it loses the file
    /// outright.
    /// </summary>
    public class AtomicFileTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _dir;

        public AtomicFileTests(ITestOutputHelper output)
        {
            _out = output;
            _dir = Path.Combine(Path.GetTempPath(), "qaf-atomic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string Path_(string name = "f.json") => Path.Combine(_dir, name);

        [Fact]
        public void A_first_write_creates_the_file_and_leaves_no_temporary_behind()
        {
            AtomicFile.WriteAllText(Path_(), "{\"a\":1}");

            Assert.Equal("{\"a\":1}", File.ReadAllText(Path_()));
            Assert.False(File.Exists(Path_() + AtomicFile.TemporarySuffix), "the temporary file must be consumed");
        }

        [Fact]
        public void A_rewrite_replaces_the_contents_and_still_leaves_no_temporary()
        {
            AtomicFile.WriteAllText(Path_(), "first");
            AtomicFile.WriteAllText(Path_(), "second");

            Assert.Equal("second", File.ReadAllText(Path_()));
            Assert.False(File.Exists(Path_() + AtomicFile.TemporarySuffix));
        }

        [Fact]
        public void The_encoding_is_UTF8_with_NO_byte_order_mark()
        {
            // Every file written before this helper existed came from File.WriteAllText, which emits
            // no BOM. Emitting one now would put three bytes in front of the JSON.
            AtomicFile.WriteAllText(Path_(), "{}");

            byte[] bytes = File.ReadAllBytes(Path_());
            Assert.True(bytes.Length >= 2);
            Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB, "a BOM was written");

            // And non-ASCII still round-trips, so the encoding really is UTF-8 and not ANSI.
            AtomicFile.WriteAllText(Path_(), "{\"name\":\"赛道\"}");
            Assert.Equal("{\"name\":\"赛道\"}", File.ReadAllText(Path_(), Encoding.UTF8));
        }

        [Fact]
        public async Task THE_TARGET_IS_NEVER_ABSENT_WHILE_A_REWRITE_IS_IN_PROGRESS()
        {
            // The actual property that was broken. A reader hammering the path across many concurrent
            // rewrites must never observe it missing - which the old delete-then-move could not
            // promise, and this does.
            AtomicFile.WriteAllText(Path_(), "{\"v\":0}");

            var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            int missing = 0, reads = 0;

            Task reader = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    reads++;
                    if (!File.Exists(Path_())) Interlocked.Increment(ref missing);
                }
            });

            for (int i = 1; i <= 300 && !stop.IsCancellationRequested; i++)
            {
                try { AtomicFile.WriteAllText(Path_(), "{\"v\":" + i + "}"); }
                catch (IOException) { /* the reader may hold a transient handle; not what is under test */ }
            }

            stop.Cancel();
            await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(5)));

            _out.WriteLine($"reads={reads} missing={missing}");
            Assert.Equal(0, missing);
            Assert.True(File.Exists(Path_()));
        }

        [Fact]
        public async Task A_valid_version_is_always_readable_even_under_concurrent_rewrites()
        {
            // The weaker but more useful guarantee: whatever a reader DOES get is a complete document,
            // never a half-written one.
            AtomicFile.WriteAllText(Path_(), "{\"v\":0}");

            var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            int truncated = 0, succeeded = 0;

            Task reader = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    string text;
                    try { text = File.ReadAllText(Path_()); }
                    catch (IOException) { continue; }   // momentary sharing conflict, not corruption
                    if (string.IsNullOrEmpty(text)) continue;
                    succeeded++;
                    if (!text.StartsWith("{") || !text.EndsWith("}")) Interlocked.Increment(ref truncated);
                }
            });

            for (int i = 1; i <= 300 && !stop.IsCancellationRequested; i++)
            {
                try { AtomicFile.WriteAllText(Path_(), "{\"v\":" + i + "}"); }
                catch (IOException) { }
            }

            stop.Cancel();
            await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(5)));

            _out.WriteLine($"successful reads={succeeded} truncated={truncated}");
            Assert.Equal(0, truncated);
        }

        [Fact]
        public void No_store_still_uses_the_delete_then_move_shape()
        {
            // The regression guard: a fourth store, or a revert, must not reintroduce it.
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback.sln")))
                dir = dir.Parent;
            string root = Path.Combine(dir.FullName, "QAdvanceFeedback");

            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains("\\obj\\") || file.Contains("\\bin\\")) continue;
                if (Path.GetFileName(file) == "AtomicFile.cs") continue;

                string text = File.ReadAllText(file);
                bool deletesThenMoves = text.Contains("File.Delete(") && text.Contains("File.Move(");
                Assert.False(deletesThenMoves,
                    $"{Path.GetFileName(file)} still deletes-then-moves; use AtomicFile.WriteAllText");
            }
        }
    }
}

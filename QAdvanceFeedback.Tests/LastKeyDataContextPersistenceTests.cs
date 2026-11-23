using System;
using System.IO;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>The last driving context (game, car) the key data was shown for, persisted in
    /// QAdvanceFeedback.Parameters.json so that after a restart - SimHub on the menu, no game running -
    /// the settings page and the ShakeIt panels can still form the EXACT persist key and show its value
    /// (owner, 2026-11-19).</summary>
    public class LastKeyDataContextPersistenceTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "qaf-kdctx-" + Guid.NewGuid() + ".json");
        private RuntimeStore _store;

        public void Dispose()
        {
            _store?.Dispose();
            if (File.Exists(_path)) File.Delete(_path);
        }

        [Fact]
        public void The_context_survives_a_restart()
        {
            _store = new RuntimeStore(_path, flushInterval: TimeSpan.FromHours(1));
            _store.SaveLastKeyDataContext("fh6", "car1");
            _store.Flush();
            _store.Dispose();

            _store = new RuntimeStore(_path, flushInterval: TimeSpan.FromHours(1));
            Assert.True(_store.TryLoadLastKeyDataContext(out string game, out string car));
            Assert.Equal("fh6", game);
            Assert.Equal("car1", car);
        }

        [Fact]
        public void An_older_file_has_no_context()
        {
            File.WriteAllText(_path, "{ \"Version\": 11 }");
            _store = new RuntimeStore(_path, flushInterval: TimeSpan.FromHours(1));
            Assert.False(_store.TryLoadLastKeyDataContext(out _, out _));
        }
    }
}

using System;
using System.IO;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>CsvExportWriter.cs is SimHub-free and link-compiled into this test project, so these
    /// tests exercise the real class against real temporary files.</summary>
    public class CsvExportWriterTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "qaf-csv-" + Guid.NewGuid() + ".csv");
        private readonly CsvExportWriter _writer = new CsvExportWriter();

        public void Dispose()
        {
            _writer.Dispose();
            try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best effort */ }
        }

        [Fact]
        public void Start_writes_a_header_row_with_every_property_name()
        {
            _writer.Start(_path, new[] { "QAdvanceFeedback.WheelLock.Raw.All", "QAdvanceFeedback.GForce.Bottom.FrontLeft" });
            _writer.Stop(); // release the file handle before reading it back with a separate handle

            string[] lines = File.ReadAllLines(_path);
            Assert.Equal("TimestampUtc,QAdvanceFeedback.WheelLock.Raw.All,QAdvanceFeedback.GForce.Bottom.FrontLeft", lines[0]);
        }

        [Fact]
        public void WriteRow_appends_a_data_row_matching_the_header_order()
        {
            _writer.Start(_path, new[] { "A", "B" });
            _writer.WriteRow(new object[] { 12.5, true });
            _writer.Stop();

            string[] lines = File.ReadAllLines(_path);
            Assert.Equal(2, lines.Length);
            Assert.EndsWith(",12.5,1", lines[1]);
        }

        [Fact]
        public void Null_values_render_as_an_empty_cell_not_the_literal_word_null()
        {
            _writer.Start(_path, new[] { "A" });
            _writer.WriteRow(new object[] { null });
            _writer.Stop();

            string[] lines = File.ReadAllLines(_path);
            Assert.EndsWith(",", lines[1]);
            Assert.DoesNotContain("null", lines[1], StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_row_with_the_wrong_column_count_is_silently_ignored()
        {
            _writer.Start(_path, new[] { "A", "B" });
            _writer.WriteRow(new object[] { 1.0 }); // wrong count
            _writer.Stop();

            string[] lines = File.ReadAllLines(_path);
            Assert.Single(lines); // only the header
        }

        [Fact]
        public void Stop_closes_the_file_so_it_can_be_read_by_another_process()
        {
            _writer.Start(_path, new[] { "A" });
            _writer.WriteRow(new object[] { 1.0 });
            _writer.Stop();

            Assert.False(_writer.IsRecording);
            string[] lines = File.ReadAllLines(_path); // would throw if still locked for write
            Assert.Equal(2, lines.Length);
        }

        [Fact]
        public void WriteRow_before_Start_does_nothing_and_does_not_throw()
        {
            _writer.WriteRow(new object[] { 1.0 }); // no Start call
            Assert.False(_writer.IsRecording);
        }

        [Fact]
        public void A_value_containing_a_comma_is_quoted()
        {
            _writer.Start(_path, new[] { "A" });
            _writer.WriteRow(new object[] { "hello, world" });
            _writer.Stop();

            string[] lines = File.ReadAllLines(_path);
            Assert.Contains("\"hello, world\"", lines[1]);
        }

        [Fact]
        public void Starting_again_begins_a_fresh_file_not_an_appended_one()
        {
            _writer.Start(_path, new[] { "A" });
            _writer.WriteRow(new object[] { 1.0 });
            _writer.Start(_path, new[] { "A" }); // restart
            _writer.Stop();

            string[] lines = File.ReadAllLines(_path);
            Assert.Single(lines); // only the fresh header - the old row is gone
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using QAdvanceFeedback.Core.Runtime;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Builds the parameters file's JSON SECTION BY SECTION (owner, 2026-11-21: "ONLY read and update the sections
    /// ONLY, but not refresh the entire file everytime just one small data being updated";
    /// docs\parameters-file-format-report.md). Each top-level field of <see cref="RuntimeDocument"/> is serialised on
    /// its own and its text cached; a write regenerates only the sections the cache reports changed and reuses the
    /// cached text of every other. The composed text is identical to serialising the whole document at once with the
    /// same settings, so the reader needs no change.
    /// <para>A file still has to be written whole (JSON sections change length, so nothing can be patched in place);
    /// what this saves is the serialisation of every unchanged section, and - through <c>changed</c> - the write itself
    /// when the regenerated sections came out exactly as before.</para>
    /// Not thread-safe: <see cref="RuntimeStore"/> calls it under its write lock.
    /// </summary>
    public sealed class RuntimeDocumentWriter
    {
        private readonly JsonSerializerSettings _settings = ShakeItCalibrationContractResolver.Settings(Formatting.Indented);
        private readonly List<JsonProperty> _properties;
        private readonly Dictionary<string, string> _text = new Dictionary<string, string>(StringComparer.Ordinal);

        public RuntimeDocumentWriter()
        {
            var contract = (JsonObjectContract)_settings.ContractResolver.ResolveContract(typeof(RuntimeDocument));
            _properties = contract.Properties.Where(p => !p.Ignored && p.Readable).ToList();
        }

        /// <summary>Sections serialised since construction (diagnostics and tests).</summary>
        public int SectionsSerialized { get; private set; }

        /// <summary>
        /// The whole document's JSON. Only sections named in <paramref name="changedSections"/> (field names; null =
        /// every section), and sections never serialised before, are regenerated. <paramref name="changed"/> is true
        /// when any regenerated section's text differs from its cached text (or was not cached).
        /// </summary>
        public string Compose(RuntimeDocument document, ISet<string> changedSections, out bool changed)
        {
            changed = false;
            string newLine = Environment.NewLine;
            var sb = new StringBuilder(64 * 1024);
            sb.Append('{');
            bool first = true;
            foreach (JsonProperty p in _properties)
            {
                if (p.ShouldSerialize != null && !p.ShouldSerialize(document)) continue;
                string name = p.UnderlyingName;
                if (changedSections == null || changedSections.Contains(name) || !_text.TryGetValue(name, out string text))
                {
                    object value = p.ValueProvider.GetValue(document);
                    text = JsonConvert.SerializeObject(value, p.PropertyType, _settings).Replace(newLine, newLine + "  ");
                    SectionsSerialized++;
                    if (!_text.TryGetValue(name, out string old) || !string.Equals(old, text, StringComparison.Ordinal))
                    {
                        changed = true;
                        _text[name] = text;
                    }
                }
                if (!first) sb.Append(',');
                first = false;
                sb.Append(newLine).Append("  ").Append(JsonConvert.ToString(p.PropertyName)).Append(": ").Append(text);
            }
            sb.Append(newLine).Append('}');
            return sb.ToString();
        }
    }
}

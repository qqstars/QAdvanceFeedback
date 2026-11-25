using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Ui.Mvvm;

namespace MockHost.ViewModels
{
    /// <summary>One language the plugin's UI ships.</summary>
    internal sealed class LanguageOption
    {
        public LanguageOption(string culture, string name) { Culture = culture; Name = name; }
        public string Culture { get; }
        public string Name { get; }
    }

    /// <summary>
    /// The Language tab: which of the plugin's UI languages (its string tables: English and Simplified Chinese) the
    /// plugin's settings page and ShakeIt editors use. A pick sets <see cref="Strings.Culture"/> and asks the window to
    /// rebuild them (<see cref="Changed"/>); it is kept in PluginsData\Common\MockHost.Settings.json for the next start.
    /// </summary>
    internal sealed class LanguageViewModel : ObservableObject
    {
        public const string FileName = "MockHost.Settings.json";

        private readonly string _path;
        private LanguageOption _selected;

        public LanguageViewModel(string path)
        {
            _path = path;
            Options = new[]
            {
                new LanguageOption("en-US", "English"),
                new LanguageOption("zh-Hans", "简体中文 (Simplified Chinese)"),
            };
            string saved = Read();
            _selected = Options.FirstOrDefault(o => string.Equals(o.Culture, saved, StringComparison.OrdinalIgnoreCase)) ?? Options[0];
            Apply(_selected);
        }

        public IReadOnlyList<LanguageOption> Options { get; }

        /// <summary>Raised after the language changed: the plugin's page and editors are to be rebuilt.</summary>
        public event Action Changed;

        public LanguageOption Selected
        {
            get => _selected;
            set
            {
                if (value == null || !SetProperty(ref _selected, value)) return;
                Apply(value);
                Write(value.Culture);
                Changed?.Invoke();
            }
        }

        private static void Apply(LanguageOption option)
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(option.Culture);
            Strings.Culture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        private sealed class SettingsFile
        {
            public string Language { get; set; }
        }

        private string Read()
        {
            try { return File.Exists(_path) ? JsonConvert.DeserializeObject<SettingsFile>(File.ReadAllText(_path))?.Language : null; }
            catch (Exception) { return null; }
        }

        private void Write(string culture)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path)));
                File.WriteAllText(_path, JsonConvert.SerializeObject(new SettingsFile { Language = culture }, Formatting.Indented));
            }
            catch (Exception) { /* a convenience only */ }
        }
    }
}

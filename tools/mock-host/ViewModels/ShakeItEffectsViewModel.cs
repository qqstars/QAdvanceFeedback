using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using MockHost.ShakeIt;
using Newtonsoft.Json;
using QAdvanceFeedback.Ui.Mvvm;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;

namespace MockHost.ViewModels
{
    /// <summary>One added effect: SimHub's part of the editor (name, enabled, gain) and the container whose own
    /// editor the view shows.</summary>
    internal sealed class EffectItemViewModel : ObservableObject
    {
        private readonly Action _changed;
        private string _description;
        private bool _isEnabled = true;
        private double _gain;

        public EffectItemViewModel(EffectType type, EffectsContainerBase container, Guid id, Action changed)
        {
            Type = type;
            Container = container;
            ContainerId = id;
            _changed = changed;
            _gain = QAdvanceFeedback.Core.ShakeItEffects.NativeEffects.DefaultGainPercent(type.Effect);
            // The effect's routing is changed inside its own editor: save the profile when it does.
            if (container is INotifyPropertyChanged notifying)
                notifying.PropertyChanged += (s, e) => { if (e.PropertyName == "AggregationMode") { OnPropertyChanged(nameof(Header)); _changed(); } };
        }

        public EffectType Type { get; }
        public EffectsContainerBase Container { get; }
        public Guid ContainerId { get; }

        /// <summary>The name the user gave the effect; empty shows the effect type's own name, as in ShakeIt.</summary>
        public string Description
        {
            get => _description;
            set { if (SetProperty(ref _description, string.IsNullOrWhiteSpace(value) ? null : value)) { OnPropertyChanged(nameof(Header)); _changed(); } }
        }

        public bool IsEnabled { get => _isEnabled; set { if (SetProperty(ref _isEnabled, value)) _changed(); } }
        public double Gain { get => _gain; set { if (SetProperty(ref _gain, Math.Max(0, Math.Min(100, value)))) _changed(); } }

        public string Header
        {
            get
            {
                string name = Description ?? Type.Name;
                return EffectContainers.HasRouting(Type) ? name + "  (" + EffectContainers.AggregationMode(Container) + ")" : name;
            }
        }
    }

    /// <summary>
    /// The ShakeIt Effects tab: "Add Effect" for the plugin's ShakeIt effects, the list of added effects and the
    /// selected one's editor. The list is SimHub's profile in miniature, kept in
    /// PluginsData\Common\MockHost.ShakeItProfile.json (the effects' own settings live in the plugin's files).
    /// </summary>
    internal sealed class ShakeItEffectsViewModel : ObservableObject
    {
        public const string FileName = "MockHost.ShakeItProfile.json";

        private readonly string _path;
        private EffectType _selectedType;
        private EffectItemViewModel _selected;
        private bool _loading;
        private string _message = string.Empty;

        public ShakeItEffectsViewModel(string path)
        {
            _path = path;
            _effectTypes = EffectContainers.Types();
            _selectedType = _effectTypes.FirstOrDefault();
            Effects = new ObservableCollection<EffectItemViewModel>();
            AddEffectCommand = new RelayCommand(AddSelected, () => SelectedEffectType != null);
            RemoveEffectCommand = new RelayCommand(RemoveSelected, () => SelectedEffect != null);
            Load();
        }

        public IReadOnlyList<EffectType> EffectTypes { get => _effectTypes; private set => SetProperty(ref _effectTypes, value); }
        private IReadOnlyList<EffectType> _effectTypes;

        /// <summary>The language changed: every effect is built again (its editor reads its labels when it is built),
        /// with the same settings, routing and selection.</summary>
        public void Reload()
        {
            Save();
            int selected = SelectedEffect == null ? -1 : Effects.IndexOf(SelectedEffect);
            QAdvanceFeedback.Core.ShakeItEffects.NativeEffect? type = SelectedEffectType?.Effect;
            SelectedEffect = null;
            Effects.Clear();
            EffectTypes = EffectContainers.Types();
            SelectedEffectType = EffectTypes.FirstOrDefault(t => t.Effect == type) ?? EffectTypes.FirstOrDefault();
            Load();
            SelectedEffect = selected >= 0 && selected < Effects.Count ? Effects[selected] : Effects.FirstOrDefault();
        }

        public ObservableCollection<EffectItemViewModel> Effects { get; }
        public RelayCommand AddEffectCommand { get; }
        public RelayCommand RemoveEffectCommand { get; }
        public string ProfilePath => Path.GetFullPath(_path);
        public string Message { get => _message; private set => SetProperty(ref _message, value); }
        public bool HasEffects => Effects.Count > 0;

        public EffectType SelectedEffectType
        {
            get => _selectedType;
            set { if (SetProperty(ref _selectedType, value)) AddEffectCommand.RaiseCanExecuteChanged(); }
        }

        public EffectItemViewModel SelectedEffect
        {
            get => _selected;
            set { if (SetProperty(ref _selected, value)) RemoveEffectCommand.RaiseCanExecuteChanged(); }
        }

        private void AddSelected()
        {
            EffectItemViewModel item = Add(SelectedEffectType, null, Guid.NewGuid());
            SelectedEffect = item;
            Save();
        }

        private EffectItemViewModel Add(EffectType type, string aggregationMode, Guid id)
        {
            var item = new EffectItemViewModel(type, EffectContainers.Create(type, aggregationMode), id, Save);
            Effects.Add(item);
            OnPropertyChanged(nameof(HasEffects));
            return item;
        }

        private void RemoveSelected()
        {
            int index = Effects.IndexOf(SelectedEffect);
            if (index < 0) return;
            Effects.RemoveAt(index);
            OnPropertyChanged(nameof(HasEffects));
            SelectedEffect = Effects.Count == 0 ? null : Effects[Math.Min(index, Effects.Count - 1)];
            Save();
        }

        // ---- persistence: SimHub's container fields ---------------------------------------------------

        private sealed class ProfileFile
        {
            public List<ContainerEntry> EffectsContainers { get; set; } = new List<ContainerEntry>();
        }

        private sealed class ContainerEntry
        {
            public string ContainerType { get; set; }
            public Guid ContainerId { get; set; }
            public string Description { get; set; }
            public bool IsEnabled { get; set; } = true;
            public double Gain { get; set; }
            public string AggregationMode { get; set; }
        }

        private void Load()
        {
            if (!File.Exists(_path)) return;
            _loading = true;
            try
            {
                var file = JsonConvert.DeserializeObject<ProfileFile>(File.ReadAllText(_path));
                foreach (ContainerEntry entry in file?.EffectsContainers ?? new List<ContainerEntry>())
                {
                    EffectType type = EffectContainers.Find(entry.ContainerType);
                    if (type == null) { Message = "Skipped an unknown effect type: " + entry.ContainerType; continue; }
                    EffectItemViewModel item = Add(type, entry.AggregationMode, entry.ContainerId == Guid.Empty ? Guid.NewGuid() : entry.ContainerId);
                    item.Description = entry.Description;
                    item.IsEnabled = entry.IsEnabled;
                    item.Gain = entry.Gain;
                }
                SelectedEffect = Effects.FirstOrDefault();
            }
            catch (Exception e)
            {
                Message = "Could not read " + FileName + ": " + e.Message;
            }
            finally
            {
                _loading = false;
            }
        }

        public void Save()
        {
            if (_loading) return;
            try
            {
                var file = new ProfileFile
                {
                    EffectsContainers = Effects.Select(e => new ContainerEntry
                    {
                        ContainerType = e.Type.ContainerType.Name,
                        ContainerId = e.ContainerId,
                        Description = e.Description,
                        IsEnabled = e.IsEnabled,
                        Gain = e.Gain,
                        AggregationMode = EffectContainers.HasRouting(e.Type) ? EffectContainers.AggregationMode(e.Container) : null,
                    }).ToList(),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path)));
                File.WriteAllText(_path, JsonConvert.SerializeObject(file, Formatting.Indented,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                Message = "Saved " + Effects.Count + " effect" + (Effects.Count == 1 ? "" : "s") + " to " + FileName + ".";
            }
            catch (Exception e)
            {
                Message = "Could not save " + FileName + ": " + e.Message;
            }
        }
    }
}

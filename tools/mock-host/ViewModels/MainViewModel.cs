namespace MockHost.ViewModels
{
    /// <summary>The window's tabs other than the plugin's own settings page.</summary>
    internal sealed class MainViewModel
    {
        public MainViewModel(ShakeItEffectsViewModel shakeIt, GameSimulationViewModel simulation, LanguageViewModel language)
        {
            ShakeIt = shakeIt;
            Simulation = simulation;
            Language = language;
            // A new language: the effect editors are rebuilt in it (the window rebuilds the plugin's settings page).
            language.Changed += shakeIt.Reload;
        }

        public ShakeItEffectsViewModel ShakeIt { get; }
        public GameSimulationViewModel Simulation { get; }
        public LanguageViewModel Language { get; }
    }
}

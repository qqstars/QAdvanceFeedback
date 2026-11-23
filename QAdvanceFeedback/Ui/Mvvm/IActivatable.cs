namespace QAdvanceFeedback.Ui.Mvvm
{
    /// <summary>A view model that runs only while a view of it is on screen. The view declares
    /// <c>v:ViewModelLifetime.Activate="True"</c>; the behaviour calls <see cref="Activate"/> on Loaded and
    /// <see cref="Deactivate"/> on Unloaded of the element whose DataContext this is.</summary>
    public interface IActivatable
    {
        void Activate();
        void Deactivate();
    }
}

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace QAdvanceFeedback.Ui.Mvvm
{
    /// <summary>
    /// Base of every QAdvanceFeedback view model (docs\architecture.md, "UI: MVVM"). Views bind to the
    /// properties; a change raises <see cref="PropertyChanged"/> once, and only when the value changed.
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

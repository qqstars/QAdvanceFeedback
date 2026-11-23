using System;
using System.Collections.Generic;
using QAdvanceFeedback.Ui.Mvvm;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>The MVVM building blocks every QAdvanceFeedback view model uses (docs\architecture.md,
    /// "UI: MVVM").</summary>
    public class MvvmFoundationTests
    {
        private sealed class Sample : ObservableObject
        {
            private int _value;
            public int Value { get => _value; set => SetProperty(ref _value, value); }
        }

        [Fact]
        public void SetProperty_raises_PropertyChanged_once_and_only_on_change()
        {
            var s = new Sample();
            var raised = new List<string>();
            s.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            s.Value = 5;
            s.Value = 5;

            Assert.Equal(new[] { "Value" }, raised);
        }

        [Fact]
        public void RelayCommand_executes_and_reports_CanExecute()
        {
            int runs = 0;
            bool allowed = false;
            var command = new RelayCommand(() => runs++, () => allowed);
            int changed = 0;
            command.CanExecuteChanged += (_, __) => changed++;

            Assert.False(command.CanExecute(null));
            allowed = true;
            command.RaiseCanExecuteChanged();
            Assert.True(command.CanExecute(null));
            command.Execute(null);

            Assert.Equal(1, runs);
            Assert.Equal(1, changed);
        }

        [Fact]
        public void ManualTicker_fires_every_interval_and_stops_when_disposed()
        {
            var ticker = new ManualTicker(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            int ticks = 0;
            IDisposable subscription = ticker.Every(TimeSpan.FromMilliseconds(100), () => ticks++);

            ticker.Advance(TimeSpan.FromMilliseconds(350));
            Assert.Equal(3, ticks);
            Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, 350, DateTimeKind.Utc), ticker.UtcNow);

            subscription.Dispose();
            ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(3, ticks);
        }
    }
}

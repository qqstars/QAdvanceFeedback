using QAdvanceFeedback.Core;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    public class FallbackWarningViewModelTests
    {
        [Fact]
        public void Refresh_shows_text_only_for_plugin_internal_and_fallback()
        {
            var vm = new FallbackWarningViewModel();
            Assert.False(vm.ShowWarning);
            Assert.Equal(string.Empty, vm.Text);

            vm.Refresh(SourceMode.Manual, SourceNativeness.Fallback);
            Assert.True(vm.ShowWarning);
            Assert.Equal("Not natively supported by this game — using the fallback estimate (speed/RPM).", vm.Text);

            vm.Refresh(SourceMode.ShakeIt, SourceNativeness.Fallback);
            Assert.False(vm.ShowWarning);
            Assert.Equal(string.Empty, vm.Text);
        }

        [Theory]
        [InlineData(SourceMode.ShakeIt, SourceNativeness.Fallback)]
        [InlineData(SourceMode.Viper, SourceNativeness.Fallback)]
        [InlineData(SourceMode.Custom, SourceNativeness.Fallback)]
        [InlineData(SourceMode.Manual, SourceNativeness.Native)]
        [InlineData(SourceMode.Manual, SourceNativeness.Unknown)]
        public void Does_not_apply(SourceMode mode, SourceNativeness n) =>
            Assert.False(FallbackWarningViewModel.Applies(mode, n));

        [Fact]
        public void Does_not_apply_without_a_channel()
        {
            Assert.False(FallbackWarningViewModel.Applies(null, SourceNativeness.Fallback));
        }

        [Fact]
        public void Raises_property_changed_when_refreshed()
        {
            var vm = new FallbackWarningViewModel();
            int n = 0;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FallbackWarningViewModel.ShowWarning)) n++; };
            vm.Refresh(SourceMode.Manual, SourceNativeness.Fallback);
            vm.Refresh(SourceMode.Manual, SourceNativeness.Fallback);
            Assert.Equal(1, n);
        }
    }
}

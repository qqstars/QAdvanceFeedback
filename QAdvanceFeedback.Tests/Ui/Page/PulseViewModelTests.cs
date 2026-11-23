using System.Collections.Generic;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui.Page
{
    /// <summary>The Pulse group of the Wheel Lock / Wheel Slip tabs (old LoadChannel / SaveChannel pulse
    /// lines): edits go into the draft and wait for Apply.</summary>
    public class PulseViewModelTests
    {
        [Fact]
        public void Edits_write_the_draft_and_mark_the_page_dirty()
        {
            var rig = new PageRig();
            var vm = new PulseViewModel(rig.Ctx, isLock: true);
            Assert.False(vm.Enabled);

            vm.Enabled = true;
            vm.GapMs = 800;
            vm.MinValue = 25;

            Assert.True(rig.Ctx.Settings.Lock.Pulse.Enabled);
            Assert.Equal(800, rig.Ctx.Settings.Lock.Pulse.GapMs);
            Assert.Equal(25, rig.Ctx.Settings.Lock.Pulse.MinValue);
            Assert.False(rig.Host.Settings.Lock.Pulse.Enabled);
            Assert.False(rig.Ctx.Settings.Slip.Pulse.Enabled);
            Assert.True(rig.Draft.IsDirty);
        }

        [Fact]
        public void An_emptied_editor_leaves_the_value_unchanged()
        {
            var rig = new PageRig();
            var vm = new PulseViewModel(rig.Ctx, isLock: false);
            vm.GapMs = null;
            vm.MinValue = null;
            Assert.Equal(PulseSettings.DefaultGapMs, rig.Ctx.Settings.Slip.Pulse.GapMs);
            Assert.Equal(PulseSettings.DefaultGapMs, vm.GapMs);
            Assert.False(rig.Draft.IsDirty);
        }

        [Fact]
        public void A_gap_below_the_5Hz_floor_is_shown_as_the_floor()
        {
            var rig = new PageRig();
            var vm = new PulseViewModel(rig.Ctx, isLock: true);
            vm.GapMs = 50;
            Assert.Equal(PulseSettings.MinGapMs, vm.GapMs);
            Assert.Equal(PulseSettings.MinGapMs, rig.Ctx.Settings.Lock.Pulse.GapMs);
        }

        [Fact]
        public void A_draft_reload_re_reads_every_field()
        {
            var rig = new PageRig();
            var vm = new PulseViewModel(rig.Ctx, isLock: true);
            var changed = new List<string>();
            vm.PropertyChanged += (s, e) => changed.Add(e.PropertyName);
            rig.Host.Settings.Lock.Pulse.Enabled = true;

            rig.Adopt();

            Assert.True(vm.Enabled);
            Assert.Contains(null, changed);
        }
    }
}

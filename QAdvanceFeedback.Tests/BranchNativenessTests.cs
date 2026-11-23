using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="BranchNativeness"/> - the pure, SimHub-free classifier deciding whether a
    /// Wheel Lock/Slip Raw branch is a native game signal or a speed/RPM fallback estimate. The table
    /// under test is the spec's section 7 classification table.
    /// </summary>
    public class BranchNativenessTests
    {
        [Theory]
        [InlineData(WheelSlipBranchNames.WheelSpeedLock)]
        [InlineData(WheelSlipBranchNames.Rps)]
        [InlineData(WheelSlipBranchNames.RpsAndDummyRadius)]
        public void Lock_native_branches_classify_as_Native_for_Lock(string branch)
        {
            Assert.Equal(SourceNativeness.Native, BranchNativeness.ForLock(branch));
        }

        [Theory]
        [InlineData(WheelSlipBranchNames.PrecalibratedSlip)]
        [InlineData(WheelSlipBranchNames.SlipData)]
        [InlineData(WheelSlipBranchNames.WheelsSpeed)]
        public void Slip_native_branches_classify_as_Native_for_Slip(string branch)
        {
            Assert.Equal(SourceNativeness.Native, BranchNativeness.ForSlip(branch));
        }

        [Theory]
        [InlineData(WheelSlipBranchNames.BrakingVsSpeed)]
        [InlineData(WheelSlipBranchNames.RpmVsSpeedLegacy)]
        [InlineData(WheelSlipBranchNames.RpmVsSpeedAssumedLegacy)]
        [InlineData(WheelSlipBranchNames.NoCapabilitiesSuppliedLockFallback)]
        [InlineData(WheelSlipBranchNames.NoCapabilitiesSuppliedSlipFallback)]
        public void Fallback_branches_classify_as_Fallback_for_Lock(string branch)
        {
            Assert.Equal(SourceNativeness.Fallback, BranchNativeness.ForLock(branch));
        }

        [Theory]
        [InlineData(WheelSlipBranchNames.BrakingVsSpeed)]
        [InlineData(WheelSlipBranchNames.RpmVsSpeedLegacy)]
        [InlineData(WheelSlipBranchNames.RpmVsSpeedAssumedLegacy)]
        [InlineData(WheelSlipBranchNames.NoCapabilitiesSuppliedSlipFallback)]
        [InlineData(WheelSlipBranchNames.NoCapabilitiesSuppliedLockFallback)]
        public void Fallback_branches_classify_as_Fallback_for_Slip(string branch)
        {
            Assert.Equal(SourceNativeness.Fallback, BranchNativeness.ForSlip(branch));
        }

        [Theory]
        [InlineData(WheelSlipBranchNames.DirectSlipFeedback)]
        [InlineData(WheelSlipBranchNames.NoBranchMatched)]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("some unrecognised branch")]
        public void Unknown_inputs_classify_as_Unknown_for_Lock(string branch)
        {
            Assert.Equal(SourceNativeness.Unknown, BranchNativeness.ForLock(branch));
        }

        [Theory]
        [InlineData(WheelSlipBranchNames.DirectSlipFeedback)]
        [InlineData(WheelSlipBranchNames.NoBranchMatched)]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("some unrecognised branch")]
        public void Unknown_inputs_classify_as_Unknown_for_Slip(string branch)
        {
            Assert.Equal(SourceNativeness.Unknown, BranchNativeness.ForSlip(branch));
        }

        [Theory]
        [InlineData(WheelSlipBranchNames.WheelSpeedLock)]
        [InlineData(WheelSlipBranchNames.Rps)]
        [InlineData(WheelSlipBranchNames.RpsAndDummyRadius)]
        public void Lock_native_branch_asked_of_ForSlip_is_Unknown(string branch)
        {
            Assert.Equal(SourceNativeness.Unknown, BranchNativeness.ForSlip(branch));
        }

        [Theory]
        [InlineData(WheelSlipBranchNames.PrecalibratedSlip)]
        [InlineData(WheelSlipBranchNames.SlipData)]
        [InlineData(WheelSlipBranchNames.WheelsSpeed)]
        public void Slip_native_branch_asked_of_ForLock_is_Unknown(string branch)
        {
            Assert.Equal(SourceNativeness.Unknown, BranchNativeness.ForLock(branch));
        }

        [Fact]
        public void Every_branch_constant_is_classified_for_both_channels()
        {
            List<string> branches = typeof(WheelSlipBranchNames)
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
                .ToList();

            Assert.NotEmpty(branches);

            foreach (string branch in branches)
            {
                Assert.True(BranchNativeness.IsClassified(branch),
                    $"Branch '{branch}' is not explicitly classified in BranchNativeness; add it to the table.");
            }
        }
    }
}

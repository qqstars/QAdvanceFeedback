using System.Reflection;
using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests the exact reflection mechanism <c>AlgorithmFactory</c> uses to resolve a withheld
    /// <c>Private\QAdvanceFeedback\</c> implementation at runtime (see that factory's own remarks, and
    /// <c>..\Private\README.md</c>, for the full picture) - purely against
    /// <see cref="PrivateTypeResolver"/> itself, which is SimHub-free and link-compiles into this
    /// net8.0 test project via the ordinary Core\**\*.cs wildcard, so it is fully exercised here
    /// without needing a live SimHub session (unlike the SimHub-touching
    /// AlgorithmFactory/ITelemetryAdapter side of the same mechanism, which cannot be constructed
    /// outside a running SimHub process - see this project's own established convention for what
    /// counts as unit-testable vs. only verifiable by inspection).
    /// </summary>
    public class PrivateTypeResolverTests
    {
        private static readonly Assembly ThisAssembly = typeof(PrivateTypeResolverTests).Assembly;

        public interface IWidget
        {
            int Value { get; }
        }

        public sealed class RealWidget : IWidget
        {
            public int Value => 42;
        }

        public sealed class NotAWidget
        {
        }

        public sealed class WidgetWithNoParameterlessConstructor : IWidget
        {
            public WidgetWithNoParameterlessConstructor(int seed) { Value = seed; }
            public int Value { get; }
        }

        [Fact]
        public void An_existing_type_that_implements_T_is_constructed_and_returned()
        {
            IWidget result = PrivateTypeResolver.CreateOrFallback<IWidget>(
                ThisAssembly, typeof(RealWidget).FullName, () => throw new System.Exception("fallback should not run"));

            Assert.IsType<RealWidget>(result);
            Assert.Equal(42, result.Value);
        }

        [Fact]
        public void A_type_name_that_does_not_exist_falls_back()
        {
            bool fallbackRan = false;
            IWidget result = PrivateTypeResolver.CreateOrFallback<IWidget>(
                ThisAssembly, "QAdvanceFeedback.Tests.ThisTypeDoesNotExist_" + System.Guid.NewGuid().ToString("N"),
                () => { fallbackRan = true; return null; });

            Assert.True(fallbackRan);
            Assert.Null(result);
        }

        [Fact]
        public void A_type_that_exists_but_does_not_implement_T_falls_back_rather_than_throwing()
        {
            bool fallbackRan = false;
            IWidget result = PrivateTypeResolver.CreateOrFallback<IWidget>(
                ThisAssembly, typeof(NotAWidget).FullName,
                () => { fallbackRan = true; return new RealWidget(); });

            Assert.True(fallbackRan);
            Assert.IsType<RealWidget>(result);
        }

        [Fact]
        public void A_type_with_no_public_parameterless_constructor_falls_back_rather_than_throwing()
        {
            bool fallbackRan = false;
            IWidget result = PrivateTypeResolver.CreateOrFallback<IWidget>(
                ThisAssembly, typeof(WidgetWithNoParameterlessConstructor).FullName,
                () => { fallbackRan = true; return new RealWidget(); });

            Assert.True(fallbackRan);
            Assert.IsType<RealWidget>(result);
        }

        [Fact]
        public void A_null_assembly_falls_back_rather_than_throwing()
        {
            bool fallbackRan = false;
            IWidget result = PrivateTypeResolver.CreateOrFallback<IWidget>(
                null, typeof(RealWidget).FullName,
                () => { fallbackRan = true; return new RealWidget(); });

            Assert.True(fallbackRan);
            Assert.IsType<RealWidget>(result);
        }

        // ------------------------------------------------------------------------------------
        // The actual mechanism AlgorithmFactory relies on for Layer 3, exercised end to end
        // against the real type names (not a fake IWidget) - proving that when the Private engine
        // IS present (as it is in this working copy - see the .csproj's conditional Include), the
        // real algorithm is what gets resolved, not the stub.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Resolving_the_real_LegacyWheelLockSlipEngine_type_name_matches_whether_Private_is_present_in_this_build()
        {
            // Self-adapting rather than hard-assuming Private\ is present: this same test file
            // compiles and runs whether ..\Private\QAdvanceFeedback\LegacyWheelLockSlipEngine.cs is
            // linked into this build (this working copy, right now) or not (a clean third-party
            // clone with no Private\ implementation supplied yet - see the .csproj's Exists()
            // guard) - in EITHER case the resolver must do the right thing, so the assertion below
            // checks reality first rather than assuming it.
            const string realEngineTypeName = "QAdvanceFeedback.Core.LegacyWheelLockSlipEngine";
            bool privateTypeIsLinkedIntoThisBuild = ThisAssembly.GetType(realEngineTypeName, throwOnError: false) != null;

            bool fallbackRan = false;
            ILegacyWheelLockSlipEngine result = PrivateTypeResolver.CreateOrFallback<ILegacyWheelLockSlipEngine>(
                ThisAssembly, realEngineTypeName,
                () => { fallbackRan = true; return new InertLegacyWheelLockSlipEngine(); });

            if (privateTypeIsLinkedIntoThisBuild)
            {
                Assert.False(fallbackRan);
                Assert.IsNotType<InertLegacyWheelLockSlipEngine>(result);
            }
            else
            {
                Assert.True(fallbackRan);
                Assert.IsType<InertLegacyWheelLockSlipEngine>(result);
            }
        }

        [Fact]
        public void Resolving_a_name_the_private_engine_does_not_have_falls_back_to_the_stub()
        {
            bool fallbackRan = false;
            ILegacyWheelLockSlipEngine result = PrivateTypeResolver.CreateOrFallback<ILegacyWheelLockSlipEngine>(
                ThisAssembly, "QAdvanceFeedback.Core.ThisAlgorithmTypeNameDoesNotExist",
                () => { fallbackRan = true; return new InertLegacyWheelLockSlipEngine(); });

            Assert.True(fallbackRan);
            Assert.IsType<InertLegacyWheelLockSlipEngine>(result);
        }
    }
}

using System;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Proves the exact C# language mechanism <see cref="PropertyPublisher.Register{TPlugin}"/>
    /// depends on: <c>IPluginExtensions.AttachDelegate&lt;T,U&gt;</c> infers <c>T</c> from the STATIC
    /// type of its receiver at the call site, not <c>receiver.GetType()</c> at runtime. This is a
    /// plain C# generic-type-inference fact (verified independently of SimHub, not a SimHub runtime
    /// behaviour) - exactly the fact the sibling ReliableWheelLockSlip project's own report and this
    /// project's <c>docs\layer123-report.md</c> cite as the root cause of "Plugin.ReliableWheel.*"
    /// (should have been "ReliableWheel.*").
    /// <para/>
    /// WHY THIS TEST DOES NOT LINK-COMPILE THE REAL <see cref="PropertyPublisher"/> CLASS: that class
    /// references <c>SimHub.Plugins</c> (a .NET Framework 4.8 assembly) for <c>IPlugin</c>/
    /// <c>AttachDelegate</c>; this test project targets net8.0, and a net48 SimHub.Plugins.dll cannot
    /// be loaded by the net8.0 test host at runtime (confirmed directly - referencing it broke test
    /// discovery for the ENTIRE assembly, not just one test, with "could not find dependent assembly").
    /// Reflecting on <see cref="PropertyPublisher"/>'s own compiled signature from here is therefore not
    /// possible without either a second, SimHub-referencing net48 test project or a live SimHub
    /// process - neither of which exists. This test instead reproduces the IDENTICAL generic-method
    /// SHAPE locally (a fake interface + a fake "AttachDelegate"-shaped extension method with the same
    /// T-inferred-from-receiver signature) and proves the underlying mechanism directly: a generic
    /// wrapper preserves the caller's concrete type, a non-generic (fixed-interface-parameter) wrapper
    /// collapses it to the interface - EXACTLY mutation (b)'s failure mode. See
    /// <c>docs\wiring-ui-report.md</c> for why <see cref="PropertyPublisher.Register{TPlugin}"/>'s OWN
    /// signature (visible directly in <c>PropertyPublisher.cs</c>) is the shipped fix this proves the
    /// necessity of, and for how mutation (b) was actually exercised against the real file.
    /// </summary>
    public class PropertyPublisherStructureTests
    {
        private interface IFakePlugin { }

        private sealed class FakeConcretePlugin : IFakePlugin { }

        private static class FakeAttachDelegateExtensions
        {
            // Mirrors SimHub's real IPluginExtensions.AttachDelegate<T,U> shape exactly: T is a
            // generic parameter on the receiver, inferred by the compiler from the STATIC type of
            // whatever is passed as `plugin` at the call site - never from plugin.GetType().
            public static void AttachDelegate<T, U>(T plugin, string name, Func<U> getter, System.Collections.Generic.List<string> registeredNames)
                where T : IFakePlugin
            {
                // Mirrors PluginManager.GetName(name, pluginType) = pluginType.Name + "." + name,
                // decompiled and confirmed for the real SimHub.Plugins.dll (see docs\layer123-report.md).
                registeredNames.Add(typeof(T).Name + "." + name);
            }
        }

        /// <summary>The FIX: generic in the concrete plugin type - mirrors
        /// <see cref="PropertyPublisher.Register{TPlugin}"/>'s own signature exactly.</summary>
        private static void PublishGeneric<TPlugin>(TPlugin plugin, System.Collections.Generic.List<string> registeredNames)
            where TPlugin : IFakePlugin
        {
            FakeAttachDelegateExtensions.AttachDelegate(plugin, "Foo", () => 0.0, registeredNames);
        }

        /// <summary>THE BUG mutation (b) describes: a plain interface parameter instead of a generic
        /// one - every property would register under the INTERFACE's name, regardless of the
        /// concrete plugin type.</summary>
        private static void PublishNonGeneric(IFakePlugin plugin, System.Collections.Generic.List<string> registeredNames)
        {
            FakeAttachDelegateExtensions.AttachDelegate(plugin, "Foo", () => 0.0, registeredNames);
        }

        [Fact]
        public void A_generic_publisher_preserves_the_callers_concrete_plugin_type_name()
        {
            var registered = new System.Collections.Generic.List<string>();
            var plugin = new FakeConcretePlugin();

            PublishGeneric(plugin, registered);

            Assert.Equal("FakeConcretePlugin.Foo", registered[0]);
            Assert.DoesNotContain("IFakePlugin.Foo", registered);
        }

        [Fact]
        public void A_non_generic_IPlugin_typed_publisher_collapses_every_name_to_the_interface_MUTATION_b()
        {
            // This is exactly mutation (b): PropertyPublisher.Register taking a plain IPlugin
            // parameter instead of being generic in TPlugin. Demonstrated here via the local mirror
            // shape (see this class's own remarks for why the real class cannot be reflected on from
            // this net8.0 test project) - proves the SAME generic-inference fact applies regardless
            // of which concrete IPlugin-shaped type is passed in.
            var registered = new System.Collections.Generic.List<string>();
            var plugin = new FakeConcretePlugin();

            PublishNonGeneric(plugin, registered);

            // The bug: registered under the INTERFACE name, not the concrete plugin's own name -
            // exactly the "Plugin.ReliableWheel.*" / would-be "IPlugin.QAdvanceFeedback.*" mistake.
            Assert.Equal("IFakePlugin.Foo", registered[0]);
            Assert.NotEqual("FakeConcretePlugin.Foo", registered[0]);
        }
    }
}

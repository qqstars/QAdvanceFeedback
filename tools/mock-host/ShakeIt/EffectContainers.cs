using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Controls;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Integration.ShakeIt;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;

namespace MockHost.ShakeIt
{
    /// <summary>One entry of the "Add Effect" list: a QAdvanceFeedback ShakeIt effect type.</summary>
    internal sealed class EffectType
    {
        public EffectType(NativeEffect effect, Type containerType)
        {
            Effect = effect;
            ContainerType = containerType;
        }

        public NativeEffect Effect { get; }
        public Type ContainerType { get; }
        public string Name => Strings.Get(NativeEffects.NameKey(Effect));
        public string Description => Strings.Get(NativeEffects.DescriptionKey(Effect));
        public override string ToString() => Name;
    }

    /// <summary>
    /// The plugin's ShakeIt effect containers, created the way the mock host can: SimHub's container base
    /// constructor loads WoteverLocalization.dll (only a SimHub install has it), so a container is created
    /// WITHOUT its constructor. The plugin's editors only read and write the container's routing
    /// (<c>aggregationMode</c>) and its lazily created view model, which is all that is set up here.
    /// </summary>
    internal static class EffectContainers
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Every effect type (a new list each time, so a list shown again re-reads the names in the current
        /// language).</summary>
        public static EffectType[] Types() => Enum.GetValues(typeof(NativeEffect)).Cast<NativeEffect>()
            .Select(e => new EffectType(e, typeof(QAdvanceFeedbackWheelLockContainer).Assembly.GetType(
                "QAdvanceFeedback.Integration.ShakeIt.QAdvanceFeedback" + e + "Container", throwOnError: true)))
            .ToArray();

        public static EffectType Find(string containerTypeName) =>
            Types().FirstOrDefault(t => string.Equals(t.ContainerType.Name, containerTypeName, StringComparison.Ordinal));

        public static bool HasRouting(EffectType type) => typeof(QAdvanceFeedbackWheelChannelContainerBase).IsAssignableFrom(type.ContainerType);

        public static EffectsContainerBase Create(EffectType type, string aggregationMode)
        {
            var container = (EffectsContainerBase)FormatterServices.GetUninitializedObject(type.ContainerType);
            // Wheel Lock / Wheel Slip route their output (one channel, front/rear, left/right or per wheel);
            // the other effects have one fixed layout and never read the routing.
            if (HasRouting(type)) AggregationModeField.SetValue(container, aggregationMode ?? EffectRoutingNames.Mono);
            return container;
        }

        public static string AggregationMode(EffectsContainerBase container) => (string)AggregationModeField.GetValue(container);

        /// <summary>The container's own editor, exactly what ShakeIt shows for it.</summary>
        public static UserControl EditControl(EffectsContainerBase container) => container.EditControl;

        /// <summary>The container's view model (internal, created on first use), shared by its editor and graph.</summary>
        public static object ViewModel(EffectsContainerBase container) =>
            container.GetType().GetProperty("ViewModel", AnyInstance)?.GetValue(container);

        private static FieldInfo AggregationModeField =>
            typeof(EffectsContainerBase).GetField("aggregationMode", AnyInstance)
            ?? throw new MissingFieldException(nameof(EffectsContainerBase), "aggregationMode");
    }
}

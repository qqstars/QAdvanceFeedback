using System;
using System.Reflection;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.ShakeItEffects;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;
using SimHub.Plugins.DataPlugins.ShakeItV3.Outputs.Audio;

namespace QAdvanceFeedback.Integration.ShakeIt
{
    /// <summary>Shared helpers for the native QAdvanceFeedback ShakeIt effects.</summary>
    internal static class ShakeItEffectSupport
    {
        public static QAdvanceFeedback Plugin => QAdvanceFeedback.Instance;

        /// <summary>Applies a <see cref="ToneDefault"/> to a freshly created ShakeIt shaker output.</summary>
        public static void Apply(ToneOutput output, ToneDefault d)
        {
            if (output == null || d == null) return;
            output.Frequency = d.LowHz;
            output.UseHighFrequency = d.UseHigh;
            output.HighFrequency = d.HighHz;
            output.UseWhiteNoise = d.UseNoise;
            output.WhiteNoise = d.NoisePercent;
            output.FrequencyBasedOnPreFilter = false;
            output.UsePrehemptiveMode = false;
        }

        /// <summary>
        /// A new container's identity (owner, 2026-11-18): the gain a newly added effect starts at (a
        /// loaded profile then restores its saved gain - ShakeIt always writes Gain), and the effect's
        /// name and description in this plugin's language. ShakeIt builds both from the container's
        /// metadata attribute (English) into get-only properties, so they are swapped for constant
        /// translatable strings of the same type. Best effort: on any SimHub change the attribute's
        /// English text simply stays. ShakeIt's "Add effect" list reads the attribute itself.
        /// </summary>
        public static void ApplyIdentity(EffectsContainerBase container, NativeEffect effect)
        {
            if (container == null) return;
            container.Gain = NativeEffects.DefaultGainPercent(effect);
            Localize(container, "<ContainerName>k__BackingField", Strings.Get(NativeEffects.NameKey(effect)));
            Localize(container, "<ContainerDescription>k__BackingField", Strings.Get(NativeEffects.DescriptionKey(effect)));
        }

        /// <summary>
        /// Clears the effect's saved copy of its own description (<see cref="NativeEffects.IsBuiltInDescription"/>),
        /// so the effect header shows only the localized name, as Wheel Lock / Wheel Slip / Clutch Bite do. Called
        /// after "Add effect" (from <c>LoadDefaultSettings</c>, which ShakeIt runs right after copying the
        /// description in) and after a profile loads (OnDeserialized). A renamed effect keeps its name.
        /// </summary>
        public static void ClearBuiltInDescription(EffectsContainerBase container, NativeEffect effect)
        {
            if (container == null) return;
            try
            {
                if (NativeEffects.IsBuiltInDescription(effect, container.Description)) container.Description = null;
            }
            catch (Exception) { /* leave ShakeIt's saved text as it is */ }
        }

        private static void Localize(EffectsContainerBase container, string backingField, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                FieldInfo field = typeof(EffectsContainerBase).GetField(backingField, BindingFlags.Instance | BindingFlags.NonPublic);
                object current = field?.GetValue(container);
                if (current == null) return;
                MethodInfo fromConstant = current.GetType().GetMethod("FromConstant", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                object localized = fromConstant?.Invoke(null, new object[] { text });
                if (localized != null && field.FieldType.IsInstanceOfType(localized)) field.SetValue(container, localized);
            }
            catch (Exception) { /* keep ShakeIt's own (English) name */ }
        }

        /// <summary>Value of <paramref name="snapshot"/> when fresh, else default.</summary>
        public static T FreshOrDefault<T>(FrameSnapshot<T> snapshot) where T : class =>
            snapshot != null && snapshot.IsFresh(FrameSnapshot.NowTicks(), FrameSnapshot.StaleTicks) ? snapshot.Value : null;
    }
}

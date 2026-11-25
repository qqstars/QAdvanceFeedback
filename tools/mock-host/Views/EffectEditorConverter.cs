using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MockHost.ShakeIt;
using QAdvanceFeedback.Ui.ViewModels;
using QAdvanceFeedbackViews;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;

namespace MockHost.Views
{
    /// <summary>
    /// An effect container to the part of ShakeIt's editor the plugin contributes: the container's own edit control,
    /// then (Wheel Lock / Wheel Slip / Slip Angle) the graph ShakeIt shows in its response-curve slot. Both share the
    /// container's one view model, as in SimHub.
    /// </summary>
    internal sealed class EffectEditorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is EffectsContainerBase container)) return null;
            try
            {
                var stack = new StackPanel();
                stack.Children.Add(EffectContainers.EditControl(container));
                switch (EffectContainers.ViewModel(container))
                {
                    case WheelChannelEffectViewModel wheel: stack.Children.Add(new GraphView(wheel.Graph)); break;
                    case SlipAngleEffectViewModel slipAngle: stack.Children.Add(new SlipAngleGraphView(slipAngle)); break;
                }
                return stack;
            }
            catch (Exception e)
            {
                return new TextBlock
                {
                    Text = "The effect editor could not be created: " + (e.InnerException ?? e),
                    TextWrapping = TextWrapping.Wrap,
                };
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}

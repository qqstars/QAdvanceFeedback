using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.ShakeItEffects;
using SimHub.Plugins;
using SimHub.Plugins.DataPlugins.ShakeItV3;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;
using SimHub.Plugins.DataPlugins.ShakeItV3.Settings;

namespace QAdvanceFeedback.Integration.ShakeIt
{
    /// <summary>
    /// "Set up" half of the ShakeIt source detection (docs\shakeit-source-design.md §3.1, §7): once a
    /// second, on the UI thread (the profile collections are WPF-bound UI objects), counts the enabled
    /// QAdvanceFeedback effects in both ShakeIt plugins' current profiles and publishes the result as a
    /// volatile immutable <see cref="ShakeItProfileSnapshot"/> for the data thread.
    /// <para/>
    /// Plugin-owned and always running (page open or not), so the plugin knows the state without its
    /// settings page; the page reads the same snapshot rather than inspecting a second time. Every tick
    /// is guarded per plugin: a failure reports <see cref="HealthSubsystems.ShakeItProfile"/> (as a SimHub
    /// compatibility issue); only when both plugins fail is the snapshot failed ("not set up").
    /// </summary>
    public sealed class ShakeItProfileInspector : IDisposable
    {
        private readonly IReadOnlyList<IShakeItProfileReader> _readers;
        private DispatcherTimer _timer;
        private ShakeItProfileSnapshot _latest = ShakeItProfileSnapshot.None;

        public ShakeItProfileInspector(PluginManager pluginManager)
            : this(new IShakeItProfileReader[]
            {
                new SimHubShakeItProfileReader<ShakeITMotorsV3Plugin>("ShakeIt Motors", pluginManager),
                new SimHubShakeItProfileReader<ShakeITBSV3Plugin>("ShakeIt Bass Shakers", pluginManager)
            })
        {
        }

        public ShakeItProfileInspector(IReadOnlyList<IShakeItProfileReader> readers)
        {
            _readers = readers ?? Array.Empty<IShakeItProfileReader>();
        }

        /// <summary>Latest inspection result (any thread).</summary>
        public ShakeItProfileSnapshot Latest => Volatile.Read(ref _latest);

        /// <summary>Starts the 1 Hz inspection on WPF's dispatcher (no-op without one) and inspects once now
        /// when already on it.</summary>
        public void Start()
        {
            try
            {
                Dispatcher dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null) return;
                dispatcher.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        if (_timer != null || _disposed) return;
                        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
                            (s, e) => InspectNow(), dispatcher);
                        InspectNow();
                        _timer.Start();
                    }
                    catch (Exception e)
                    {
                        HealthRegistry.Report(HealthSubsystems.ShakeItProfile, HealthSeverity.Degraded,
                            "Health.Impact.ShakeItProfile", e.ToString());
                    }
                }));
            }
            catch (Exception e)
            {
                HealthRegistry.Report(HealthSubsystems.ShakeItProfile, HealthSeverity.Degraded,
                    "Health.Impact.ShakeItProfile", e.ToString());
            }
        }

        /// <summary>One inspection (UI thread). Never throws.</summary>
        public void InspectNow()
        {
            Volatile.Write(ref _latest, ShakeItProfileInspection.Inspect(_readers));
        }

        private volatile bool _disposed;

        public void Dispose()
        {
            _disposed = true;
            DispatcherTimer timer = _timer;
            _timer = null;
            if (timer == null) return;
            try
            {
                if (timer.Dispatcher.CheckAccess()) timer.Stop();
                else timer.Dispatcher.BeginInvoke((Action)timer.Stop);
            }
            catch (Exception)
            {
                // shutting down: nothing left to protect
            }
        }
    }

    /// <summary>
    /// Reads one ShakeIt plugin's current profile through SimHub's public API:
    /// <c>PluginManager.GetPlugin&lt;T&gt;()</c>, <c>Settings.CurrentProfile.GetAllContainers()</c>, each
    /// container's <c>IsEnabled</c> and its <c>Parent</c> group chain (as SimHub's own
    /// <c>GetEffectiveIsEnabled</c> walks it).
    /// </summary>
    internal sealed class SimHubShakeItProfileReader<TPlugin> : IShakeItProfileReader where TPlugin : class, IPlugin
    {
        private readonly PluginManager _pluginManager;

        public SimHubShakeItProfileReader(string name, PluginManager pluginManager)
        {
            Name = name;
            _pluginManager = pluginManager;
        }

        public string Name { get; }

        public IReadOnlyList<ShakeItContainerInfo> ReadContainers()
        {
            TPlugin plugin = _pluginManager?.GetPlugin<TPlugin>();
            // Held as object: ShakeItProfile's interface list names an assembly (GongSolutions drag/drop)
            // this plugin does not reference, so it is only used through IContainerGroup.
            object profile = Settings(plugin)?.CurrentProfile;
            if (!(profile is IContainerGroup root)) return null;

            var result = new List<ShakeItContainerInfo>();
            foreach (EffectsContainerBase container in root.GetAllContainers())
            {
                if (container == null) continue;
                var ancestors = new List<bool>();
                // Depth-capped: a malformed (cyclic) parent chain must not hang the UI thread.
                for (GroupContainer group = container.Parent as GroupContainer; group != null && ancestors.Count < 64; group = group.Parent as GroupContainer)
                    ancestors.Add(group.IsEnabled);
                result.Add(new ShakeItContainerInfo(container.GetType().Name, container.IsEnabled, ancestors));
            }
            return result;
        }

        private static ShakeItSettings Settings(TPlugin plugin)
        {
            switch (plugin)
            {
                case ShakeITMotorsV3Plugin motors: return motors.Settings;
                case ShakeITBSV3Plugin bassShakers: return bassShakers.Settings;
                default: return null;
            }
        }
    }
}

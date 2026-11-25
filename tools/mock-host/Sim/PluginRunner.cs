using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using GameReaderCommon;
using QAdvanceFeedback.Core.Health;
using SimHub.Plugins;

namespace MockHost.Sim
{
    /// <summary>
    /// The plugin's lifecycle as SimHub runs it: <c>Init</c> once, <c>DataUpdate</c> every frame on a background
    /// thread (60 Hz, in game or not), <c>End</c> on exit (which saves the plugin's files).
    /// </summary>
    internal sealed class PluginRunner : IDisposable
    {
        private const double FrameRateHz = 60.0;

        private readonly MockGameManager _gameManager = new MockGameManager();
        private readonly QAdvanceFeedback.QAdvanceFeedback _plugin = new QAdvanceFeedback.QAdvanceFeedback();
        private readonly HashSet<string> _publishedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private SimulationState _state = new SimulationState();
        private SimulationState _appliedState;
        private Thread _thread;
        private volatile bool _running;
        private double _wave = 1.0;
        private bool _ended;

        public PluginRunner()
        {
            PluginManager = SimHubStandIn.CreatePluginManager(_gameManager);
            _plugin.PluginManager = PluginManager;   // SimHub sets it before Init
            _plugin.Init(PluginManager);
            FinishInitWithoutShakeItProfile();
        }

        /// <summary>Notes from the mock host itself (shown on the Game Simulation tab).</summary>
        public string InitNote { get; private set; } = string.Empty;

        // Init's last step creates the ShakeIt profile inspector, whose readers name SimHub's ShakeIt plugin
        // types; those need GongSolutions.WPF.DragDrop.dll, which only a SimHub install has, so Init throws
        // there - after everything else is set up. The mock finishes that step with an inspector that has no
        // readers (no SimHub ShakeIt profile to read) and clears the Init failure it caused.
        private void FinishInitWithoutShakeItProfile()
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo field = typeof(QAdvanceFeedback.QAdvanceFeedback).GetField("_shakeItInspector", any);
            if (field == null || field.GetValue(_plugin) != null) return;
            var initFailure = HealthRegistry.Snapshot().FirstOrDefault(h => h.Subsystem == HealthSubsystems.Init);
            if (initFailure == null || initFailure.Detail?.Contains("GongSolutions") != true) return;

            try
            {
                // The constructor that takes the reader list: IReadOnlyList<IShakeItProfileReader> (an internal type).
                Type inspectorType = field.FieldType;
                ConstructorInfo ctor = inspectorType.GetConstructors(any).First(c =>
                {
                    ParameterInfo[] ps = c.GetParameters();
                    return ps.Length == 1 && ps[0].ParameterType.IsGenericType
                        && ps[0].ParameterType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>);
                });
                Type readerType = ctor.GetParameters()[0].ParameterType.GetGenericArguments()[0];
                object inspector = ctor.Invoke(new object[] { Array.CreateInstance(readerType, 0) });
                field.SetValue(_plugin, inspector);
                inspectorType.GetMethod("Start", any, null, Type.EmptyTypes, null).Invoke(inspector, null);
                HealthRegistry.Clear(HealthSubsystems.Init);
                InitNote = "No SimHub ShakeIt profile in the mock host: the plugin sees no QAdvanceFeedback effect in a ShakeIt profile.";
            }
            catch (Exception e)
            {
                InitNote = "Could not finish the plugin's Init (ShakeIt profile inspector): " + e.Message;
            }
        }

        public PluginManager PluginManager { get; }

        public QAdvanceFeedback.QAdvanceFeedback Plugin => _plugin;

        // Read by the Game Simulation tab's status line (any thread).
        public long Frames => Interlocked.Read(ref _frames);
        private long _frames;
        public double LastFrameMilliseconds => Volatile.Read(ref _lastFrameMs);
        private double _lastFrameMs;
        public string LastError => Volatile.Read(ref _lastError);
        private string _lastError;
        public int PropertyCount => SimHubStandIn.PropertyCount(PluginManager);

        /// <summary>The plugin's own settings page, as SimHub gets it.</summary>
        public Control CreateSettingsControl() => _plugin.GetWPFSettingsControl(PluginManager);

        /// <summary>A new settings page in the current language: the plugin keeps ONE page view model (its labels are
        /// read when it is built), so the old one is disposed and dropped first - as if SimHub had been restarted in
        /// the new language.</summary>
        public Control RecreateSettingsControl()
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic;
            FieldInfo page = typeof(QAdvanceFeedback.QAdvanceFeedback).GetField("_settingsPage", any);
            if (page?.GetValue(_plugin) is IDisposable old)
            {
                try { old.Dispose(); }
                catch (Exception e) { Trace.WriteLine("MockHost: disposing the old settings page failed - " + e); }
                page.SetValue(_plugin, null);
            }
            return CreateSettingsControl();
        }

        public void Use(SimulationState state) => Volatile.Write(ref _state, state ?? new SimulationState());

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "MockHost DataUpdate" };
            _thread.Start();
        }

        private void Loop()
        {
            var clock = Stopwatch.StartNew();
            var frameTicks = TimeSpan.FromSeconds(1.0 / FrameRateHz).Ticks;
            long next = clock.Elapsed.Ticks;
            StatusDataBase previous = null;
            while (_running)
            {
                SimulationState s = Volatile.Read(ref _state);
                double t = clock.Elapsed.TotalSeconds;
                Volatile.Write(ref _wave, s.AutoWave && s.WavePeriodSeconds > 0.05 ? Math.Sin(2.0 * Math.PI * t / s.WavePeriodSeconds) : 1.0);
                long started = clock.ElapsedTicks;
                try
                {
                    if (!ReferenceEquals(s, _appliedState)) Apply(s);
                    GameData data = GameFrameBuilder.Build(s, Volatile.Read(ref _wave), previous);
                    _plugin.DataUpdate(PluginManager, ref data);
                    previous = data?.NewData;
                }
                catch (Exception e)
                {
                    Volatile.Write(ref _lastError, e.GetType().Name + ": " + e.Message);
                }
                Volatile.Write(ref _lastFrameMs, (clock.ElapsedTicks - started) * 1000.0 / Stopwatch.Frequency);
                Interlocked.Increment(ref _frames);

                next += frameTicks;
                long wait = next - clock.Elapsed.Ticks;
                if (wait > 0) Thread.Sleep(TimeSpan.FromTicks(wait));
                else next = clock.Elapsed.Ticks;   // fell behind: do not try to catch up
            }
        }

        // A new simulation snapshot: the game manager's capabilities and the extra SimHub properties.
        private void Apply(SimulationState s)
        {
            _appliedState = s;
            _gameManager.CurrentGameName = s.GameName ?? string.Empty;
            _gameManager.UseCapabilities(GameFrameBuilder.Capabilities(s));
            foreach (string gone in _publishedProperties.Where(n => !s.Properties.ContainsKey(n)).ToList())
            {
                SimHubStandIn.RemoveProperty(PluginManager, gone);
                _publishedProperties.Remove(gone);
            }
            bool wave = s.AutoWave && s.WaveProperties;
            foreach (KeyValuePair<string, object> p in s.Properties)
            {
                object value = p.Value;
                Func<object> provider = wave && value is double d ? (Func<object>)(() => d * Volatile.Read(ref _wave)) : () => value;
                SimHubStandIn.SetProperty(PluginManager, p.Key, provider);
                _publishedProperties.Add(p.Key);
            }
        }

        public void Dispose()
        {
            _running = false;
            _thread?.Join(TimeSpan.FromSeconds(2));
            if (_ended) return;
            _ended = true;
            try { _plugin.End(PluginManager); }
            catch (Exception e) { Trace.WriteLine("MockHost: plugin End failed - " + e); }
        }
    }
}

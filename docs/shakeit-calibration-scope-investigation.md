# ShakeIt calibration scope investigation - is it per-profile?

Investigation only. No plugin code changed. All evidence below is from decompiling SimHub's own
`SimHub.Plugins.dll` (namespace `SimHub.Plugins.DataPlugins.ShakeItV3` and
`SimHub.Plugins.DataPlugins.ShakeItV3.Calibration`) plus `GameReaderCommon.dll`
(`GameReaderCommon.Feedback.FeedbackCapabilities`), using `ilspycmd` output reused from a prior
interrupted decompile pass (found under the session scratch directory, not re-decompiled).

## CONCLUSION - REFUTED

**The per-profile hypothesis is REFUTED.** ShakeIt calibration is not keyed by ShakeIt profile at
all - profiles never appear anywhere in the calibration key. It is keyed by
**game code + track + car**, held **entirely in memory for the life of the running SimHub process**,
and is **never persisted to disk** (the save path is a literal no-op). Switching, or creating, a
ShakeIt profile has zero effect on calibration state one way or the other. What actually produced
100 vs. 34 is that **both** the Sauber and F1 Generic readings were cold (a lap or two of samples,
nowhere near the ~7000-sample maturity threshold), and the effect's output is a *self-referencing,
percentile-relative* score against whatever extremes have been observed **so far in that specific
car+track bucket this session** - not a fixed physical unit. Sauber's wet, first-run Silverstone lap
produced an early large slip/lock-up excursion that saturated its own still-forming calibration
ceiling (reading near 100%); F1 Generic's dry, second-run Abu Dhabi lap's peak event was smaller
relative to its own (separately-forming) ceiling, landing at 34% front / 6% rear. Both numbers are
essentially noise from an immature calibration window, not a meaningful car-to-car comparison.

---

## 1. How calibration is scoped and keyed

### 1a. The manager: `CalibrationSettingsManager` (`SimHub.Plugins.DataPlugins.ShakeItV3.CalibrationSettingsManager`)

```csharp
public class CalibrationSettingsManager
{
    private Dictionary<string, CalibrationDataProvider> data = new Dictionary<string, CalibrationDataProvider>();
    public string SettingsPrefix { get; }
    public CalibrationSettingsManager(string settingsPrefix) { SettingsPrefix = settingsPrefix; }

    public CalibrationDataProvider GetSettings(string gameCode)
    {
        CalibrationDataProvider orAdd = data.GetOrAdd(gameCode + SettingsPrefix, delegate {
            var p = new CalibrationDataProvider(gameCode);
            p.GameCode = gameCode; p.Init(); return p;
        }, uselock: true);
        orAdd.GameCode = gameCode;
        return orAdd;
    }

    public void Save() { }                                   // <-- literally empty
    internal void Reset(string gameCode) { data[gameCode + SettingsPrefix] = new CalibrationDataProvider(gameCode); }
    internal void Unload() { data.Clear(); }
}
```

- One `CalibrationSettingsManager` is constructed **once**, in `ShakeITV3PluginBase`'s constructor,
  for the lifetime of the plugin instance (i.e. once per SimHub run, not once per profile):
  `calibrationSettingsManager = new CalibrationSettingsManager(SettingsPrefix);`
  (`SimHub.Plugins.DataPlugins.ShakeItV3.ShakeITV3PluginBase..ctor`, line 111 of the decompiled
  source). `SettingsPrefix` here is a constant per **plugin type** ("ShakeIt Motors" vs. "ShakeIt
  Bass Shakers" pass different prefixes) - it is the only sense in which anything resembling a
  "scope" string is baked in, and it has nothing to do with ShakeIt profiles.
- `GetSettings(gameCode)` keys its dictionary by `gameCode + SettingsPrefix` only. It is called
  fresh every `DataUpdate` (`ShakeITV3PluginBase.DataUpdate`, line 149):
  `calibrationSettingsManager.GetSettings(data.GameName).GetCalibratedFeedback(...)`. **No profile
  identifier is part of this key.**
- `Save()` is an empty method. `SaveSettings()` (`ShakeITV3PluginBase.SaveSettings`, line 497-502)
  calls `calibrationSettingsManager.Save()` (no-op) and then `calibrationSettingsManager.Unload()`
  which clears the whole in-memory dictionary. So **calibration data does not survive a SimHub
  restart, full stop** - regardless of profile.

### 1b. The per-metric key: `CalibrationDataProvider.GetKey` / `GetSlipCalibration`

```csharp
public ICalibrationData GetSlipCalibration(GameData data, bool front, bool store = false)
{
    string key = GetKey(data, carOrGameSpecific: true);
    return GetCalibrationData(key, "Slip", store);
}

private string GetKey(GameData data, bool carOrGameSpecific)
{
    return KeyCache.GetOrAdd(new Tuple<string,string,bool>(data.NewData.TrackIdWithConfig, data.NewData.CarModel, carOrGameSpecific), delegate {
        string text  = carOrGameSpecific ? (data.NewData.TrackIdWithConfig ?? "") : "";
        string text2 = carOrGameSpecific ? (data.NewData.CarModel ?? "") : "";
        return text + ";" + text2;
    });
}
```

For the Wheels lock/slip effects (`GetSlipCalibration`, `carOrGameSpecific: true`) the resulting
`CalibrationData` dictionary key is effectively `"{TrackIdWithConfig};{CarModel};Slip"`. **This is
per (track, car) - a completely different axis from ShakeIt profiles.** Two different cars driven
in the same profile get two independent, never-shared `CalibrationData` buckets; the same car
driven in the same profile at two different tracks *also* gets two independent buckets.

`CalibrationData.GetOrAdd` (`CalibrationDataProvider.GetCalibrationData`, line 184-228) creates a
brand-new, empty `CalibrationData` (`Count = 0`) the first time a given track+car+metric key is
seen in the current in-memory dictionary - i.e. the first time that pairing is driven since the
last SimHub restart (or explicit `Reset`).

### 1c. Where profiles DO enter the picture - only as a blend weight, not a scope

`ShakeItProfile.AutoCalibrationRatio` (int, JSON name `AutoCalibrationRatio2`, default `100`,
`SimHub.Plugins.DataPlugins.ShakeItV3.Settings.ShakeItProfile`) is fed in every frame:

```csharp
// ShakeITV3PluginBase.DataUpdate, line 150
calibratedFeedback.CalibrationDataProvider.AutocalibrationDataRatio = (double)currentProfile.AutoCalibrationRatio / 100.0;
```

This only sets a *ratio field* on the **same shared, car+track-keyed** `CalibrationDataProvider`
that every profile for that game shares. It changes how much weight autocalibration gets **when a
shipped reference curve exists** (see section 3) - it does not create, own, or scope any
calibration storage. A brand-new profile does not "start cold" because it is new; it inherits
whatever car+track buckets already exist in the shared, in-memory `CalibrationSettingsManager` for
that plugin type + game, for exactly as long as SimHub has been running.

### Persistence - confirmed absent

Searching the whole decompiled ShakeItV3 source tree, `CalibrationSettingsManager`/
`CalibrationDataProvider` are referenced **only** from `ShakeITV3PluginBase`, and the only "save"
path (`CalibrationSettingsManager.Save()`) is empty. `CalibrationDataProvider` itself carries
`[JsonProperty("CalibrationDataV5")]` on its `CalibrationData` dictionary (suggesting a JSON
serialization format was designed for it at some point), but nothing in this build ever calls
`ToJsonFile`/`FromJsonFile` on a `CalibrationDataProvider` or on `CalibrationSettingsManager`. The
only file I/O the calibration subsystem performs is `GamesCalibration.FromFile()`/`.Save()`, and
that is the **shipped/known-data** side (section 3 below), not the user's accumulated
autocalibration.

**Practical implication:** every car+track pairing's autocalibration is cold on every SimHub
restart, no matter which profile (old or new) is loaded.

---

## 2. Is it per-profile? (the crux)

No. See 1a-1c: the key is `gameCode + SettingsPrefix` (manager level) then
`TrackIdWithConfig + CarModel + metricName` (bucket level). A ShakeIt profile ID or name is never
part of either key, is never used to look up a `CalibrationDataProvider`, and is never used to
reset one implicitly. The only thing that resets a bucket is `CalibrationSettingsManager.Reset`,
which is wired to `ShakeItSettings.ResetCalibration()` -> `CalibrationResetRequested` -> the
explicit **"ResetCalibration"** action/button (`ShakeITV3PluginBase.ConfigureControls`, line 351) -
an explicit user action, or a full SimHub restart (in-memory dictionary cleared on load, never
repopulated from disk).

---

## 3. What "leverage SimHub calibration" vs. "use known data" actually does

The setting is `ShakeItProfile.AutoCalibrationRatio` (serialized key `AutoCalibrationRatio2`, `int`
0-100, default 100). Its UI copy (extracted from
`dataplugins/shakeitv3/ui/profiles/shakeitprofileproperties.baml` via `strings`):

- Label: **"Autocalibration behaviour (slip, suspensions for compatible games)"**
- Description: **"Automatic calibration will 'learn' your driving style, track and cars to smooth
  out the experience. You can choose here how much the autocalibration will affect the compatible
  effects."**
- Slider endpoints: **"Predefined calibration"** (0) vs. **"Automatic calibration"** (100).

So the profile setting the owner has at **0** means "use predefined/known data, ignore learned
data" - matching their own description of the setting.

The mechanics, in `PreloadedCalibrationData.GetPercentile`
(`SimHub.Plugins.DataPlugins.ShakeItV3.Calibration.PreloadedCalibrationData`):

```csharp
public double GetPercentile(double percentile, bool includeZero = false)
{
    double num = MeasuredMaximum * CorrectionFactor * percentile / 100.0;   // shipped/known curve
    if (AutocalibrationDataRatio > 0.0)
        num = (AutoCalibrationData.GetPercentile(percentile, includeZero) - num) * AutocalibrationDataRatio + num;
    return num;
}
```

At ratio `0` this reduces to `num = MeasuredMaximum * CorrectionFactor * percentile / 100` - purely
the shipped reference curve, with zero contribution from the session's own learned data, and
`IsReady` is forced `true` (no cold/"Calibrating" state at all):

```csharp
public bool IsReady { get { if (AutocalibrationDataRatio != 0.0) return AutoCalibrationData.IsReady; return true; } }
```

**But this entire code path is gated behind a shipped `PreloadedCalibrationData` object actually
existing for that game + metric.** `CalibrationDataProvider.GetCalibrationData`
(lines 184-228) resolves it like this:

```csharp
PreloadedCalibrationData valueOrDefault = gamePrecalibration.PrecalibrationData.GetValueOrDefault(metricName);
if (valueOrDefault != null)
{
    Logging.Current.Info("Loaded " + gamePrecalibration.GameCodes + " " + metricName + " pre-calibration data");
    return valueOrDefault;
}
Logging.Current.Info("Warn no " + metricName + " pre-calibration data available, using autocalibration as a fallback");
return d;   // d = the plain, cold, session-accumulating CalibrationData - AutoCalibrationRatio is IGNORED here
```

`gamePrecalibration` comes from `GamesCalibration.Get(GameCode)`
(`SimHub.Plugins.DataPlugins.ShakeItV3.Calibration.GamesCalibration`), which loads shipped/known
game data from three JSON files relative to SimHub's working directory:
`ShakeIt\ShakeItPrecalibration\GameData.json`, `GameData_Custom.json`, `GameData_External.json`
(`GamesCalibration.FromFile`). If the game code doesn't match any existing `GameCodes` entry (`Get`
uses VB `Like`-style wildcard matching, `LikeOperator.LikeString(...)`), a brand-new, **empty**
`GameCalibration` is created and persisted for that game code - i.e. with **no**
`PrecalibrationData` entries at all.

**I could not find any of these `GameData*.json` files under the tool-provided locations**
(`lib\`, the extracted `.simhub-refs-work\out\{app}\` install dump, or the decompiled source tree).
They are runtime/user-data files SimHub writes/reads relative to its working directory, not part
of the assemblies, so **whether F1 25 has a shipped "Slip" precalibration entry is not
determinable from the assemblies alone.**

**What would settle it, concretely:** grep the owner's actual SimHub log for the line

```
no Slip pre-calibration data available, using autocalibration as a fallback
```

(logged once per game code the first time a "Slip"-metric calibration bucket is resolved). If that
line appears for an F1 25 session, it proves conclusively that `AutoCalibrationRatio = 0` is
currently **inert** for the Wheels lock/slip effect on F1 25 - the code falls straight to the cold,
session-accumulating `CalibrationData` regardless of the slider position, because
`AutoCalibrationRatio`/ratio-blending logic only ever runs on a `PreloadedCalibrationData` instance,
and `d is PreloadedCalibrationData` is false whenever no shipped entry exists.

**Does flipping the setting help?** Only if F1 25 *does* ship "Slip" precalibration data (unknown -
see above). If it does not (which the presence of that log line would confirm), flipping the slider
between 0 and 100 makes **zero difference** to the Wheels lock/slip effect for F1 25 - it is not a
usable workaround for this specific symptom on this specific game, until/unless SimHub ships that
data. This is a configuration setting whose effect is entirely dependent on unshipped/undiscoverable
external data for this game, not a fixable knob on the owner's side beyond confirming which branch
is actually active via the log line above.

---

## 4. Which calibration branch actually runs, and how it produces 100 vs. 34

`WheelSlipEffect.GetEffectValue` (`SimHub.Plugins.DataPlugins.ShakeItV3.Effects.WheelSlipEffect`)
dispatches through an `if`/`else if` chain based on `FeedbackCapabilities` flags (fixed, static
booleans set once by the F1 25 game-reader plugin - not present in the available assemblies, so
their exact values for F1 25 are **not independently verifiable from what I have access to**). In
declared order:

1. `!c.IsLock && WheelSlipCalibrationProvider?.SlipScale.HasValue` -> `GetDirectSlip`
2. `c.IsLock && DetectLockFromWheelsRPSAndDummyRadius` -> `LockFromWheelsRPSAndDummyRadius`
3. `c.IsLock && DetectLockFromWheelsSpeed` -> `GetLockFromWheelSpeed`
4. `WheelSlipUseSimpleBraking` -> `GetSimpleBraking`
5. `c.IsLock && WheelsRPS` -> `GetRpsLock`
6. `WheelsSlipDirectMode` -> (state only, no computation)
7. `WheelsSlip` -> `GetSlipFeedback`
8. ... (wheel-speed / RPM-speed fallbacks)

**Note on the given capability values:** with `WheelsRPS = 1` as stated, branch 5 (`GetRpsLock`)
would fire for the *lock* channel (`c.IsLock == true`) before branch 7 is ever reached, unless F1
25's `DetectLockFromWheelsRPSAndDummyRadius`/`DetectLockFromWheelsSpeed`/`WheelSlipUseSimpleBraking`
flags differ from what was assumed, or the real `FeedbackCapabilities.WheelsRPS` for F1 25 is
actually `false` despite raw per-frame `WheelRPS[]` telemetry being present in logs (these are two
different things - `FeedbackCapabilities` are static per-game-plugin flags, not derived from
per-frame data availability). **I cannot confirm from the assemblies which branch F1 25 actually
takes for the lock channel** - the F1 25 game-reader plugin that sets these flags is not among the
decompiled/available DLLs. I've traced `GetSlipFeedback` below as instructed/as the stated observed
branch, but flag this discrepancy plainly rather than paper over it.

### `GetSlipFeedback` (both channels, per the stated observation)

```csharp
private double GetSlipFeedback(GameData data, CalibratedFeedbackData calibratedFeedback, EffectsContainerBase effectsContainer)
{
    ICalibrationData slipCalibration = calibratedFeedback.CalibrationDataProvider.GetSlipCalibration(data, wheelIdx < 2);
    double p15 = slipCalibration.GetPercentile(15.0);
    double p99 = slipCalibration.GetPercentile(99.0);
    if (p15 != p99)
    {
        double val = 0.0;
        if (data.NewData.Brake > 20.0 && calibratedFeedback.FeedbackCapabilities.WheelsRPS) { /* RPS-spread term */ }
        return Math.Max(val, MathExtensions.Offset(p15, p99, Math.Abs(calibratedFeedback.Data.WheelSlip[wheelIdx]), clamp: false));
    }
    return 0.0;
}
```

`MathExtensions.Offset(min, max, value, clamp:false)` remaps `value` linearly from `[p15, p99]` to
`[0, 1]` **without clamping** - a value at or above `p99` reads out at or above `1.0` (100% after
`Gain=100`). `p15`/`p99` are **not fixed physical thresholds** - they come from
`CalibrationData.GetPercentile` (`SimHub.Plugins.DataPlugins.ShakeItV3.Calibration.CalibrationData`):

```csharp
public double GetPercentile(double percentile, bool includeZero = false)
{
    if (percentile == 0.0) return 0.0;
    double? p = GetPercentileInternal(percentile, includeZero);
    if (!p.HasValue) return Math.Max(1.0, Max * 0.9) * percentile / 100.0;   // cold fallback
    return p ?? 1.0;
}
```

`GetPercentileInternal` -> `GetPercentileValue` returns `null` (forcing the cold fallback) whenever
fewer than 500 non-zero samples exist for that bucket yet. `Max` is simply the largest absolute
value **ever observed so far in this specific, still-cold, track+car bucket this session**
(`CalibrationData.AddValue`: `Max = Math.Max(Math.Abs(value), Max)`), and `IsReady` requires
`Count >= 7000` (item 6, below) before the distribution is considered mature. Neither `p15` nor
`p99` are stable until then - both are literally "whatever's been seen so far," recomputed as new
extremes arrive.

### Why Sauber -> ~100 and F1 Generic -> ~34/6 in the same short session

Both cars/tracks were being calibrated **for the first time this SimHub run** (new profile,
different car+track keys as shown in section 1b) - **neither** bucket had anywhere near 7000
samples; both are firmly in the cold/unstable regime described above. In that regime, the effect's
output is best read as "how big was this instant's slip relative to the biggest slip/lock event
*this specific car+track pairing has produced so far this session*" - a moving, self-referencing
ceiling, not an absolute physical quantity:

- **Sauber, wet, Silverstone, driven first:** wet conditions plus running with a completely
  unformed calibration window means the first real braking lock-up/slip event immediately becomes
  (or dominates) that bucket's own `Max`/`p99`. Because `Offset(..., clamp:false)` reads relative to
  a ceiling that the event itself just set, that same event (and anything similar shortly after)
  reads out at or near the 100% ceiling almost by construction.
- **F1 Generic, dry, Abu Dhabi, driven second:** dry grip produces smaller and less frequent
  slip/lock excursions in the first place, and this bucket is *also* independently cold (different
  car+track key - it gets none of Sauber's accumulated samples). Its own still-forming `p99` is
  smaller, but the peak event observed in that single dry lap apparently was not as extreme relative
  to its own emerging ceiling, landing around 34% (front, `wheelIdx < 2`) and lower still at the rear
  (rear slip under braking is characteristically smaller than front in these cars, and with fewer
  rear lock-up events the rear bucket's own `p99` estimate is even less mature).

Both readings are artifacts of two independent, immature, percentile-relative calibration windows -
**not a meaningful car-to-car comparison, and not evidence that either car's "true" wheel lock is
100 or 34.** The values will keep shifting, lap over lap, as each bucket accumulates more samples
and its `p15`/`p99` window stabilizes - this is inherent to the algorithm, and would happen to
*any* two never-before-driven car+track pairings in a brand-new profile, or in the "old, correct"
profile too, if it were pointed at two car+track combinations it had never encountered before.

### Reconciling with the "older profile behaves correctly" observation

This is best explained by **accumulated in-session sample volume for that specific car+track
pairing**, not by profile identity:

- Calibration buckets are shared across all profiles for the same game+plugin-type (section 1c);
  an "older profile" gets no special data of its own.
- However, a profile the owner has used for months, on car+track pairings driven repeatedly, is
  far more likely to have already pushed those *specific* car+track buckets past the 7000-sample
  `IsReady` threshold at some point within a normal, sufficiently long session (7000 samples of
  active driving is roughly a couple of minutes at typical update rates) - producing a stable,
  representative `p15`/`p99` window that does not swing wildly lap to lap.
- The brand-new profile's test, by construction, covered only **the first lap or two of two
  car+track pairings neither had ever been driven in this SimHub run** - squarely inside the cold,
  unstable regime described above, regardless of which profile hosted it.

---

## 5. `IsReady` / `Count >= 7000` and the pre-maturity `GetPercentile` fallback

Both already covered above; summarized together as requested:

- `CalibrationData.IsReady => Count >= 7000` - `Count` increments on every sample fed via
  `AddValue` (including zeros), so 7000 samples is the maturity bar the UI's "Calibrating"/
  `CalibrationCompletion` indicator (`WheelSlipEffect.SetCalibrationState`,
  `effectsContainer.Calibrating = !calibrationData.IsReady`) is built on.
- Before that (or before 500 non-zero samples exist for a given percentile query),
  `GetPercentile` returns `Math.Max(1.0, Max * 0.9) * percentile / 100.0` - a fallback derived
  purely from **the largest value observed so far**, itself still moving during this exact window.
- These two interact to produce the described 100-vs-34 outcome directly: while `IsReady` is false
  (both cars, this session), the "ceiling" the effect measures itself against is not a stable,
  learned distribution - it is "the biggest thing that has happened yet," which is exactly why an
  early wet lock-up on Sauber can saturate its own reference practically immediately, while a
  smaller dry excursion on F1 Generic does not saturate its own (separately cold) reference to the
  same degree.

---

## Recommendations for the owner

1. **Do not judge, tune, or compare exported Wheels lock/slip values from a single lap on a
   never-before-driven car+track pairing.** The 0-100 scale is a live, percentile-relative score
   against that specific car+track's own accumulating sample history, not a fixed physical unit,
   until `IsReady` (`Count >= 7000`) is reached for that pairing. Drive a few continuous minutes on
   the SAME car+track before trusting the numbers; watch the "Calibrating"/completion indicator in
   the ShakeIt UI for that effect.
2. **This has nothing to do with profile age.** Reusing the "older" profile only "helps" if it
   happens to be driven on car+track pairings that already reached maturity earlier in a long
   session - it carries no calibration data of its own, and a new profile pointed at already-warm
   car+track pairings (within the same SimHub run) would behave identically to the old one.
3. **Calibration never survives a SimHub restart**, for any profile - `CalibrationSettingsManager.Save()`
   is an empty no-op. Every fresh SimHub launch starts every car+track pairing cold again. If the
   owner wants consistent numbers session-to-session, that consistency has to come from driving long
   enough each time, not from profile choice.
4. **Check whether the "leverage SimHub calibration vs. known data" setting (`AutoCalibrationRatio`,
   currently 0 = "Predefined calibration") is doing anything at all for F1 25**, by searching the
   SimHub log for: `no Slip pre-calibration data available, using autocalibration as a fallback`.
   If present, the setting is currently inert for this game/metric and is not a usable fix for this
   symptom until SimHub ships F1 25 precalibration data - I could not determine from the assemblies
   whether that data exists (the `ShakeIt\ShakeItPrecalibration\GameData*.json` files are runtime
   data, not part of any decompiled assembly, and were not found under any of the tool-provided
   paths).
5. Separately, for the ShakeIt Bass Shakers "legacy iRacing algorithm sometimes doesn't react at
   all" symptom: `WheelSlipEffect.GetRpmSpeedSlipLegacy` (the `Speed && RPM` capability branch,
   reached only if none of branches 1-6 above fire) does **not** consult `CalibrationData` /
   `ICalibrationData` at all - it computes directly from raw ground-speed/RPM ratios and a
   `gearRatiosAverage` `TimeMovingAverage` per gear that is cleared on every `Idle()` call. It is
   gated behind `data.NewData.Brake > 20.0` or `Throttle > 40.0 && Clutch < 5.0` thresholds with no
   calibration fallback in between (falls straight through to `return num2` only under those
   conditions) - a "no reaction" report for this specific algorithm is a distinct code path from the
   calibration cold-start issue investigated here, not caused by the same mechanism, though it will
   likely present with the same "feels wrong until you've been driving a while" character.

## What I could not determine from the assemblies

- **Whether F1 25 has any shipped `PrecalibrationData` entry for the `"Slip"` metric** (or for
  `RPSToSpeedFront`/`RPSToSpeedRear`/`Suspension`). This is external, runtime JSON data
  (`ShakeIt\ShakeItPrecalibration\GameData.json` etc.), not present in any of the given assemblies
  or extracted install dump. The SimHub log line quoted in section 3/recommendation 4 is the
  concrete way to settle this on the owner's own machine.
- **The exact `FeedbackCapabilities` flag values F1 25 reports** (`WheelsRPS`,
  `DetectLockFromWheelsRPSAndDummyRadius`, `DetectLockFromWheelsSpeed`, `WheelSlipUseSimpleBraking`,
  `WheelsSlipDirectMode`). These are set by the F1 25-specific game-reader plugin, which is not
  among the decompiled/available DLLs. Given the values stated in the brief (`WheelsRPS = 1`), the
  documented `WheelSlipEffect.GetEffectValue` dispatch order would route the **lock** channel
  through `GetRpsLock` (branch 5) rather than `GetSlipFeedback` (branch 7) unless one of the
  earlier-listed flags differs from what was assumed - I flagged this discrepancy rather than
  quietly resolving it either way.

## Files/types cited (for follow-up)

- `SimHub.Plugins.DataPlugins.ShakeItV3.CalibrationSettingsManager`
- `SimHub.Plugins.DataPlugins.ShakeItV3.Calibration.CalibrationDataProvider`
- `SimHub.Plugins.DataPlugins.ShakeItV3.Calibration.CalibrationData`
- `SimHub.Plugins.DataPlugins.ShakeItV3.Calibration.GameCalibration` / `GamesCalibration`
- `SimHub.Plugins.DataPlugins.ShakeItV3.Calibration.PreloadedCalibrationData`
- `SimHub.Plugins.DataPlugins.ShakeItV3.Calibration.ICalibrationData` / `ICalibrationDataExtensions`
- `SimHub.Plugins.DataPlugins.ShakeItV3.ShakeITV3PluginBase` (`DataUpdate`, `SaveSettings`, `ConfigureControls`)
- `SimHub.Plugins.DataPlugins.ShakeItV3.Settings.ShakeItProfile` (`AutoCalibrationRatio`)
- `SimHub.Plugins.DataPlugins.ShakeItV3.Effects.WheelSlipEffect` (`GetEffectValue`, `GetSlipFeedback`, `GetRpsLock`, `GetRpmSpeedSlipLegacy`)
- `SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers.WheelsLockContainer`
- `GameReaderCommon.Feedback.FeedbackCapabilities`
- `dataplugins/shakeitv3/ui/profiles/shakeitprofileproperties.baml` (extracted UI strings for `AutoCalibrationRatio`)

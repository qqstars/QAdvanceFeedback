# ShakeIt source via the native effect — design

Date: 2026-11-19. Status: approved by the owner ("Go", 2026-11-19). Supersedes the export-property
path of the ShakeIt source described in `docs\shakeit-native-effects-design.md` ("ShakeIt source:
ShakeIt's own built-in calculation", resolution steps 2–3) and retires `docs\shakeit-export-guide.md`.

Marked **[call]** = assistant decision during design, open for the owner to revisit.

## 1. Why

Today a Wheel Lock / Wheel Slip channel on the **ShakeIt Built-in** source reads, per wheel:
1. the built-in values (`ShakeItBuiltInTap`) when fresh (< 500 ms);
2. else the legacy exported properties `ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.<Wheel>` /
   `…WheelSlip.IRacing.<Wheel>`;
3. else, silently, that wheel's Raw value (`WheelSourceResolver` fallback).

Problems the owner hit and the review found:
- The tap only runs while one of our WheelLock / WheelSlip / G-Force effects is enabled **and a game
  is running**, so at the menu the page reports the ShakeIt source **unsupported** unless the
  export profile is imported — even with our effect added.
- Step 3 learns Raw-scaled values under the **ShakeIt** source key, polluting the ShakeIt
  calibration (Viper's fallback already switches values *and* key to Raw; ShakeIt's does not).
- The export profile is a workaround the native effect makes unnecessary.
- ShakeIt's own lock/slip algorithm settings are hard-coded in the hidden containers.

## 2. Goals / non-goals

Goals: detect in real time whether the ShakeIt source can work; show it only when it can (or when
already selected); read ShakeIt's algorithm only through our native effect; fall back cleanly to Raw
(values AND learning key); expose ShakeIt's lock/slip algorithm settings when that source is chosen;
keep everything already learned under the ShakeIt key.

Non-goals: reading ShakeIt's own Wheels Lock/Slip effect settings from the profile (rejected option
A); configuring ShakeIt's brake/throttle filters separately (they follow our thresholds, §6).

Anti-goals: silently rewriting a driver's saved source; Raw values learned under the ShakeIt key;
any behaviour change for Plugin Internal, Viper or Custom sources.

## 3. Detection (two checks, both live)

1. **Set up** — at least one **QAdvanceFeedback** effect is enabled in the **current profile** of
   either ShakeIt plugin (`ShakeITBSV3Plugin` Bass Shakers, `ShakeITMotorsV3Plugin` Motors), read
   through SimHub's public API: `Settings.CurrentProfile.GetAllContainers()` and each container's
   `IsEnabled` (a container inside a disabled group does not count). ShakeIt's own Wheels Lock/Slip
   effects are NOT required — our effect evaluates hidden copies of ShakeIt's algorithm with the
   game data ShakeIt hands it. Checked once a second on the UI thread (the profile collections are UI
   objects), cached as a plain flag for the data thread. Our effects also stamp a **heartbeat** every
   time ShakeIt processes them (in game or not — they are `IsAllowedOutOfGame`), which confirms
   ShakeIt is actually running them; set up = enabled-in-profile AND heartbeat fresh (≤ 2 s) **[call]**.
   Every QAdvanceFeedback effect counts and feeds the built-in values: WheelLock, WheelSlip, the
   four G-Force effects, Clutch Bite and Slip Angle.
2. **Data arrives (in game)** — ShakeIt's own lock/slip effect reports the current game supported
   (`GetIsSupportedOnCurrentGame(caps)` on the hidden containers, with the game's capabilities known
   once a game is selected) AND the built-in values are fresh while driving. If the game reports
   unsupported, or the values stay stale for 3 s of in-game time while set up **[call]**, the game
   counts as **not supported** for this session (re-checked on a game switch).

> **As built (2026-11-19).**
> - **Ruling R41 — two kinds of "not supported".** A game that ShakeIt itself reports unsupported
>   (`GetIsSupportedOnCurrentGame` false) stays **not supported until a game switch**. A verdict
>   reached only through staleness (3 s of in-game time without fresh values) is **not** sticky: it
>   recovers to Supported on the first fresh values, and a later dropout needs the full 3 s again. A
>   game switch clears both.
> - **"Driving" mirrors ShakeIt's own gate.** Driving = game running AND not paused AND not in a menu
>   AND ShakeIt's FeedbackData gate (`OldData != null && NewData.FeedbackData != null &&
>   OldData.FeedbackData != null`, the condition `ShakeITV3PluginBase.DataUpdate` uses). Without
>   feedback data (session load, first frames, tow/reset, garage) the state is Set up / idle and the
>   staleness clock resets, so 3 s must be continuous driving time.
> - **Health.** Profile inspection reports under its own `ShakeItProfile` subsystem (compatibility
>   flag set), not `ShakeItEffects`; one clean inspection clears it. Each ShakeIt plugin (Bass
>   Shakers, Motors) is read and guarded separately: a failing reader is reported and the other one's
>   count still stands; the snapshot reads "not set up" only when every present reader failed.
> - Built in `Core\ShakeItEffects\ShakeItSourceAvailability.cs` (pure state machine, per channel,
>   0.5 s-debounced broadcaster) and `Integration\ShakeIt\ShakeItProfileInspector.cs` (1 Hz
>   `DispatcherTimer`, always running while the plugin runs). Lock and Slip have separate machines,
>   because ShakeIt's support check can answer differently for lock and slip on the same game.

## 4. States, UI and what is read (per channel)

| State | ShakeIt option in the Source list (tab + ShakeIt panel) | Note shown | What the channel reads | Learning key |
|---|---|---|---|---|
| Not set up | **Hidden** (unless currently selected — then shown, marked) | "Not set up — add a QAdvanceFeedback effect to the active ShakeIt profile. Using Raw." | Raw | Raw |
| Set up, no game / menu | Shown | none | — | — |
| Set up, game supported, values fresh | Shown | none | ShakeIt built-in | ShakeIt (with algorithm fingerprint, §6) |
| Set up, game not supported / no values | Shown | "Not supported in this game — using Raw." | Raw | Raw |

- The fallback is decided **per channel**, never per wheel (as Viper's), and uses
  `RawSourceFallback.RawIdentity` so ShakeIt learning never receives Raw values.
- A driver's saved source is **never rewritten**: a hidden-but-selected ShakeIt option stays in the
  list, marked, until another source is picked.
- The notes are VM-bound (MVVM rule), on the Wheel Lock / Wheel Slip tabs and their ShakeIt panels,
  next to the existing orange fallback warning (which stays Plugin-Internal-only).
- The state is broadcast through the existing `SettingField.SourceAvailability` on change
  (debounced like Task 5's nativeness).

> **As built (2026-11-19).**
> - **Page (Wheel Lock / Wheel Slip tab):** the ShakeIt option is listed when the state is not
>   "not set up", **or** the draft combo shows ShakeIt, **or** ShakeIt is the **saved** source. So
>   while ShakeIt is saved, it stays listed even if the driver picks a different draft source, until
>   Apply — the driver can switch back. It is labelled "ShakeIt Built-in (not set up)" whenever the
>   state is "not set up". A hidden option is also **disabled** (`IsEnabled` bound with
>   `Visibility`), so arrow keys and type-ahead cannot reach it.
> - **ShakeIt panel:** applies immediately, so "selected" and "saved" are the same; the option is
>   listed only while set up or currently selected. The selected item survives a label flip
>   (marked ↔ unmarked).
> - **Notes:** shown only when the shown source is ShakeIt: "not set up" and "not supported" carry
>   the texts in the table; "set up / idle" and "supported" carry none.
> - **Live refresh at the menu:** the page now listens to the plugin's Runtime `SourceAvailability`
>   broadcast (it used to ignore it) and re-samples; the panels already did. Expected latency about
>   1–2.5 s (1 Hz inspection + 2 s heartbeat + 0.5 s debounce; not measured in SimHub).
> - **Engine vs page timing:** the engine routes each frame from the live (undebounced) state; in
>   game, "set up / idle" (waiting for first values) reads Raw under the Raw key. The page shadows to
>   Raw only on a verdict ("not set up" / "not supported"), never while idle, so it does not re-key
>   to Raw while the driver sets ShakeIt up at the menu.

## 5. Export-property source removed

- The `ShakeITMotorsV3Plugin.Export…` reads, `MotorsExportAvailabilityProvider`/
  `MotorsExportAvailabilityResolver` usage for the ShakeIt source, and the page's export setup text
  are removed. `docs\shakeit-export-guide.md` is marked retired (kept for history) and both READMEs
  stop recommending the export profile.
- The ShakeIt source **identity string stays the legacy export property names** (as today), so the
  existing learning and key-data slots carry over unchanged at default algorithm settings.
- `docs\setup\QAdvanceFeedback - WheelLockSlip.siprofile` stays in the repo for history, unreferenced.
- Trade-off accepted: drivers who used only the export profile must add one of our effects.

> **As built (2026-11-19).**
> - The export path is removed: `MotorsExportAvailabilityProvider.cs` and
>   `Core\MotorsExport\MotorsExportAvailabilityResolver.cs` (and their tests) are deleted; ShakeIt
>   mode never reads its configured export fields. `MotorsExportPropertyNames` is kept — it builds the
>   ShakeIt identity string and supplies the wheel suffixes.
> - The health id `ShakeItExport` (which, after the export path went, only Viper's property reads
>   used) is renamed **`ViperAvailability`** ("Viper plugin detection"). Health ids are in-memory
>   only; nothing persisted changes.
> - **Canonical ShakeIt identity.** On the ShakeIt Built-in route the learning identity is always the
>   canonical ShakeIt string (`ShakeItSourceResolution.ShakeItIdentity`, byte-identical to the legacy
>   export names, plus the §6 fingerprint), never taken from the stored source rows — so tap values can
>   never be learned under another key even if the rows hold something else.
> - **Shadowed save.** A preset source (ShakeIt **or** Viper) applied while its channel is shadowed to
>   Raw stores that preset's own rows (`WheelChannelSettings.KeepPresetSourcesWhileShadowed`), not the
>   Raw rows on screen. Manual and Custom keep their stored rows.
> - The routing lives in `Core\ShakeItEffects\ShakeItSourceResolution.cs`: Supported + fresh tap →
>   ShakeIt values under the ShakeIt key; anything else → Raw values under `RawSourceFallback.RawIdentity`,
>   decided per channel. `QAdvanceFeedback.ShakeItSourceBlocked(channel, isLock)` (published state) is the
>   page's shadow input, the twin of `ViperSourceBlocked`.

## 6. ShakeIt algorithm settings (option B)

Shown only when the channel's source is **ShakeIt Built-in**, as a "ShakeIt algorithm" block in that
channel's Sources section (Wheel Lock tab for Lock, Wheel Slip tab for Slip) and on the matching
ShakeIt panel, in addition to our own settings. Hidden for every other source; values kept when the
source changes. Synced page ↔ panel through new `SettingsHub` fields.

| Wheel Lock (`Lock.ShakeItAlgorithm`) | Wheel Slip (`Slip.ShakeItAlgorithm`) |
|---|---|
| Use legacy iRacing algorithm — default **on** | Use legacy iRacing algorithm — default **on** |
| Lock sensibility — default **50** | Mute slip while lock is active — default **off** |
| Lock sensibility (extended), Lock-only mode, Low-quality algorithm level — ShakeIt's own defaults | Low-quality algorithm level — ShakeIt's own default |

- Ranges/steps match ShakeIt's own editor (the builder verifies them against SimHub's Wheels
  Lock/Slip editor in `lib\SimHub.Plugins.dll` before coding; record them in the report).
- The hidden containers read these settings; a change applies on the next frame.
- **Mute slip while lock is active** is applied by us (ShakeIt's implementation dereferences the real
  profile, which the hidden copy lacks): when on, the Slip channel's ShakeIt source reads 0 on frames
  where the Lock built-in value is active (> 0).
- **Filters are not configurable here; they follow our own pedal thresholds** (one set of
  thresholds for every source):

  | Hidden ShakeIt setting | Driven by | Old hard-coded value |
  |---|---|---|
  | Lock brake filter | Wheel Lock brake threshold (`LockBrakeThresholdPercent`, default 20) | 20 |
  | Slip use brake filter + brake filter | Wheel Slip brake threshold (`SlipBrakeThresholdPercent`, default 100); filter on only when the threshold < 100 **[call]** | on, 99 |
  | Slip use throttle filter + throttle filter | Wheel Slip throttle threshold (`SlipThrottleThresholdPercent`, default 40); on when > 0 | on, 10 |

  Values rounded to ShakeIt's whole-number 0–100. **The defaults differ from the old export profile
  (Slip throttle 40 vs 10; Slip brake disabled vs 99)**, so ShakeIt-source Slip engages later than
  before — accepted as "one set of thresholds". The builder must confirm the filters' semantics in
  SimHub's code first (lock brake filter = "engage above this brake %"; slip brake/throttle filter
  match our Slip checks); if a semantic differs, stop and report rather than guess.
- **Learning key**: the ShakeIt source identity gains a fingerprint of the *algorithm* settings
  (legacy, sensibility, sensibility-ex, lock-only, low-quality level, mute). At the defaults the
  fingerprint is empty, so the key equals today's. Pedal thresholds are NOT part of the key (they
  gate when, not the scale — the same rule as Raw today). Manual key data (SMax/S90/S75) is per
  source key, so it follows.

> **As built (2026-11-19).** Settings live in `Settings\ShakeItAlgorithmSettings.cs`
> (`WheelChannelSettings.ShakeItAlgorithm`; an old config without the block loads the defaults, a null
> block is repaired on load); the pure rules are in `Core\ShakeItEffects\ShakeItAlgorithm.cs`.
>
> | Wheel Lock (`Lock.ShakeItAlgorithm`) | Wheel Slip (`Slip.ShakeItAlgorithm`) |
> |---|---|
> | Use legacy iRacing algorithm — default **on** | Use legacy iRacing algorithm — default **on** |
> | ShakeIt lock sensibility — default **50**, range 0–100, step 5 (snapped everywhere: on load, in the snapshot, in the fingerprint and in the UI) | Mute slip while lock is active — default **off** |
> | Lock-only mode (`IsLock`) — default **on** (ShakeIt's own default for a new effect) | |
>
> - **Dropped (evidence from SimHub's `WheelsLockContainer` / `WheelsSlipContainer` code):**
>   "Lock sensibility (extended)" is `LockSensibilityEx`, a non-persisted alias whose getter and setter
>   are `LockSensibility` itself — a second control would edit the same value. "Low-quality algorithm
>   level" is a non-persisted, capability-derived warning level that SimHub recomputes per game in
>   `SetFeedbackCapabilities` and that no algorithm reads (it only drives ShakeIt's support warning
>   text). Neither is a setting or part of the fingerprint.
> - **Filter rule as built** (SimHub's filters were checked: same units 0–100, same direction, Slip's
>   two filters OR-ed like ours):
>
>   | Hidden ShakeIt setting | As built |
>   |---|---|
>   | Lock brake filter | `round(LockBrakeThresholdPercent)` (0–100, midpoint away from zero) |
>   | Slip use brake filter | on when `SlipBrakeThresholdPercent < 100` **and** `SlipThrottleThresholdPercent > 0` |
>   | Slip use throttle filter | on when `SlipThrottleThresholdPercent > 0` |
>   | Slip brake / throttle filter values | `round(threshold)` |
>
>   Correction to the table above: at throttle threshold 0 our Slip gate (`brake ≥ B OR throttle ≥ 0`)
>   is always open, so the faithful mapping is **both** filters off; leaving the brake filter on would
>   have gated ShakeIt's slip by brake alone. Accepted edge differences: SimHub's filters are exclusive
>   (`>`) where ours are inclusive (`≥`); SimHub opens its gate when the game has no brake/throttle data
>   (ours fails closed); ShakeIt's RPS-and-dummy-radius lock branch ignores the filter.
> - **Mute as built:** when on, all four Slip wheels read 0 (and slip is not evaluated) on any frame
>   where **any** wheel of our hidden Lock value is > 0. The hidden Lock is always computed by our tap,
>   so this applies even when the profile has no Lock effect. ShakeIt's own 100 ms hold is not emulated.
> - **Fingerprint as built:** `#alg=legacy:X,sens:N,lockOnly:X` (Lock) and `#alg=legacy:X,mute:X`
>   (Slip), appended to the canonical ShakeIt identity; empty at the defaults (key = today's string);
>   thresholds excluded. Each channel's key holds only its own settings.
> - **Key data:** shipped key-data defaults are looked up by source **type** (the fingerprint is
>   stripped, `ShakeItAlgorithmFingerprint.BaseIdentity`), so every fingerprint gets ShakeIt's shipped
>   defaults; manual slots, the Auto flag and learning are keyed **with** the fingerprint. On the page,
>   draft key-data edits are keyed by the **draft** fingerprint, consistent with draft source rows.
> - The settings reach ShakeIt's thread as an immutable snapshot (built on the data thread each frame,
>   reused when unchanged); the tap reconfigures its hidden containers only on a new snapshot. Five
>   `SettingsHub` fields sync page ↔ panel (`Lock/SlipShakeItLegacyAlgorithm`, `LockShakeItSensibility`,
>   `LockShakeItLockOnly`, `SlipShakeItMuteWhileLockActive`).

## 7. Threading and resilience

- Profile inspection runs on the UI thread at 1 Hz inside a guarded call (`HealthRegistry`
  `ShakeItEffects`, compatibility issue flagged if SimHub's API shape moved); the result is a
  volatile plain snapshot for the data thread.
- The tap, heartbeat, support check and hidden-container configuration run on ShakeIt's processing
  thread inside the existing `SafeCall` guard; settings reach it as an immutable snapshot.
- Any failure degrades to "not set up" → Raw with the Raw key; never throws into ShakeIt or the
  plugin.

> **As built (2026-11-19).**
> - **Tap snapshot stamping.** Each frame's tap values are stamped with the algorithm/threshold snapshot
>   they were computed under (`ShakeItTapValues.Algorithm`).
> - **Freshness check.** `ShakeItSourceResolution.TapComputedUnder` compares that stamp by reference
>   with the current snapshot, and the match is part of "values fresh". A tap from an older snapshot
>   is therefore not fresh. The channel reads Raw under the Raw key until ShakeIt produces values under
>   the new settings, so old-settings values never reach the new fingerprinted key.
> - **After a pedal-threshold change**, Raw routing lasts a frame or so, because the snapshot also
>   carries the filters.
> - **Menu changes.** The snapshot is built and handed to ShakeIt (`ShakeItBuiltInTap.Configure`)
>   every frame, before the in-session gate. A change made at the menu therefore applies before the
>   first in-game frame.

## 8. Tests (net8, pure parts)

Availability state machine (all four states, 2 s heartbeat, 3 s staleness, game switch re-check);
per-channel Raw fallback with Raw identity; source-list visibility incl. hidden-but-selected;
threshold → filter mapping (incl. 100 → brake filter off, 0 → throttle filter off, rounding);
fingerprint empty at defaults and changing per setting; mute emulation; settings load/normalise
(old config without `ShakeItAlgorithm` loads defaults); hub round-trip; VM visibility of the block;
MVVM/Localization/DirtyTracking green; existing tests unedited except those asserting the removed
export path (each replacement recorded in the report).

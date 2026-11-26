# Parameters file: single values and the section writer - report (2026-11-21)

## Why

The owner found `QAdvanceFeedback.Parameters.json` very large, mostly `SlipAngleLearners` and the SMax evidence
(`LockScaleEvidence` / `SlipScaleEvidence`), and the whole file was rewritten every 5 seconds whenever anything
changed. The requirements, in the owner's words:

- "use single values instead arrays for the parameters", keeping every function;
- "once the SimHub reloaded, the SMax/S90/S75 and the confidence ratio/status will restore exactly the same", while
  "we can accept the restart is not 100% exactly the same" for learning that continues afterwards;
- "ONLY read and update the sections ONLY, but not refresh the entire file everytime just one small data being
  updated";
- the write timer: "use thread timer, set startTimeout as 5 seconds, interval as infinite; in the tick, use
  try-finally, in the finnally section, change the thread timer with startTimeout as 5 seconds, intervel as infite;
  once the plugin being destroying, such as disposed(), appdomain.unloaded(), process_terminate(), then change the
  timer into startTimeout infinite, interval infinite, then dispose the timer, and write the very last time";
- startup: if the parameters file is missing but the temporary file exists, rename it and use it.

Runtime file version 15. An older file still loads; it is saved in the new format from then on.

## SMax evidence

See `learning-maturity-indicator-report.md`: each distribution is its counts, totals and 16 percentiles, rebuilt
smoothly on load so each percentile reads back exactly; restart = continue.

## Slip Angle learner

What was saved before, per learner (nine per car key): the grip curve's 120 bins x (count, P90, spread), a
2,400-value seed buffer, the last 16 crossing snapshots, the last 16 Best values and the last 5 early Max values -
13.4-20.7 KB each on the F1 / FH6 logs.

What is saved now (`SlipAngleLearnerState`, all single values):

| Part | Saved as | Read back |
|---|---|---|
| Grip curve | `Curve` (`GripCurveSummary`): first / last populated bin, which bins are populated (two 64-bit masks), total samples, peak G, and landmarks (`GripCurveKnot`: angle, G, count, spread) at the first and last bin, the plateau edges (97% of the peak) and their outside neighbours, where the rising side falls below 95 / 90 / 85 / 75 / 60 / 50 / 40 / 25 / 10% of the peak, and where the falling side drops below 90 / 75% | every populated bin rebuilt on straight lines between the landmarks (counts on a log scale); holes stay holes; bins still seeding start empty |
| What is shown | `LearnedMin` / `LearnedBest` / `LearnedMax`, `WindowQuality` | exactly, until the next learnable frame reaches the curve |
| Crossing snapshots | count; the older ones and the newest (the level epoch) each by lowest / highest; the confirmation cluster (`ClusterRun`, `ClusterLow`, `ClusterHigh`) | rebuilt evenly between low and high for corroboration; toward confirmation they count only as the saved cluster, so even spacing can never invent a cluster of three |
| Best history | count, mean, standard deviation | rebuilt with exactly that mean and deviation (the stability term reads nothing else) |
| Early Max history | count, lowest, highest, mean | rebuilt with exactly those (its stability term reads nothing else) |
| Everything else (Max estimator, counters, prior, early estimate) | unchanged single values | exactly |

A load followed by a save with no driving writes back exactly what it read (the loaded summaries are kept until
new driving changes them), so repeated restarts never drift. Saved size: 1.7-3.0 KB per learner.

### Replay: F1 and FH6 logs, straight run vs save and restore at 10 / 25 / 50 / 75%

24 cases (two logs x All / Front / Rear x four cut points, each log driven three times):

- **Restore: exact in 24 of 24** - window, LED, window quality, consistent crossings, early confidence.
- **Learning after the restart:** FH6 (12 cases) and F1 Rear (4) end on the straight run's window. F1 All / Front,
  once confirmed, end within about 15%: e.g. All 7.8 / 9.0 / 9.2 vs 4.9 / 7.8 / 8.0, Front 7.9 / 8.5 / 8.7 vs
  8.0 / 9.9 / 10.2.
- **Why F1 differs:** past 7 degrees the F1 curve jumps from bin to bin (2.8, 5.0, 3.9, 4.5 g), and those jumps decide
  which frames are believable crossings. A curve rebuilt from landmarks is smooth there. Restoring the exact old
  arrays gives identical results, and nudging every bin by 0.1% changes nothing, so the difference is the detail the
  summary leaves out, not instability. Keeping it would mean saving the bins again.
- Two problems found and fixed on the way: filling every bin between the first and last populated one invented curve
  where FH6 had none (a false confirmation at Max 11.5) - fixed by the populated masks; one count and spread for every
  bin made the busiest bins 30x jumpier - fixed by per-landmark counts and spreads.

## The writer

`RuntimeStore` + `RuntimeDocumentWriter` + `RuntimeCache`:

- **Sections.** Every save marks the top-level section it changed (`RuntimeCache.MarkSection`). A write regenerates
  only those sections' JSON and reuses the cached text of the others; if every regenerated section came out as
  already written, nothing is written. The composed text is exactly what serialising the whole document would give,
  so reading is unchanged. A file still has to be written whole - JSON sections change length, so nothing can be
  patched in place; what is saved is the serialisation of every unchanged section, and the write itself when nothing
  really changed.
- **Timer (owner's pattern).** One `System.Threading.Timer`, due 5 s, period infinite. The tick writes synchronously
  in try/finally; the finally re-arms it (5 s, infinite). So the next write counts from the end of the previous one,
  writes never overlap and nothing queues. (Before: a periodic timer handing writes to `Task.Run` with an in-flight
  flag.)
- **One write lock** shared by the tick, `Flush` (Plugin.End), `Clear` and shutdown, so the newest snapshot is always
  the last on disk.
- **Shutdown, once, whichever comes first:** `Dispose` (Plugin.End), `AppDomain.DomainUnload`, `AppDomain.ProcessExit` -
  the timer is changed to infinite / infinite, disposed (waiting up to 10 s for a tick that is writing), and the very
  last write is made.
- **Durability unchanged:** `AtomicFile` writes the temporary file, flushes it to the disk, then swaps it in with one
  replace. This replaces the owner's "write the copy, delete the main file, rename" on purpose: between the delete
  and the rename the file does not exist, which is the owner-reported 2026-10-02 defect (both files missing after a
  forced restart).
- **A failed write** marks everything dirty and the file stale: the next tick regenerates and writes it all.
- **Startup recovery** (`AtomicFile.RecoverTemporary`, the one place that knows the temporary-file convention): both files present - the swap never happened, the temporary file is deleted and the main file
  used; main file missing - a temporary file that parses as a parameters document is renamed into place and used,
  one that does not is left alone (the next write overwrites it) and the plugin starts fresh.
- A loaded file is stamped with the current version when next written. An OLDER file is rewritten by the first tick
  after the restart, without waiting for any save (owner, 2026-11-22: "the parameter file, were not updated, when
  restart the simhub" - before, an older file stayed as it was until enough new driving saved every section): the
  store marks every section dirty (`RuntimeStore.LoadedOlderVersion`), and the plugin re-exports the Slip Angle
  learners right after their import so that write already carries the v15 single values.

Found on the way and fixed: the cache's snapshot copy left out `GForceLatLearnedMaxima`, so the learned lateral
G-force maxima never reached the file.

## Tests

`SlipAngleWindowLearnerTests` (a load then save writes back what it read, the save holds single values only, an
older file with arrays continues identically, a restored learner continues close and is exact at the restart; the
quality, impact and sanitise tests moved to the new fields), `ParametersFileWriterTests` (the composed text equals
the whole-document serialiser, only changed sections are regenerated, an unchanged save is not a change and is not
written, lateral maxima reach the file, the three startup recovery cases, the version stamp),
`RuntimeStoreFlushSafetyTests` (one-shot timer re-armed in the finally after the write, a failed write leaves
everything dirty, shutdown stops / disposes / writes and hooks the host, `Dispose` alone writes the last seconds,
the final flush wins). `ScaleEvidencePersistenceTests` expects version 15.

## Version 17 (1.2.2, owner 2026-11-25): finished ShakeIt calibrations saved compact

A ShakeIt calibration (`ShakeItCalibration`, one per `track;car;metric`) is fed until it has more than 7000 positive
points and never again, so from then on its histogram is frozen. The plugin reads only whole percentiles from it
(Slip: the 15th and 99th) and the average (RPS-to-speed). A FINISHED entry is now saved as its table of percentiles
1..100 (`pt`, 100 numbers) plus Max / Sum / Count / points, instead of its histogram (`v`, often thousands of
buckets) - `CalibrationData.PercentileTable`. The compact form is written from the frozen object when the file is
saved (no second writer on the live data); a loaded compact entry answers from the table and is never fed. Every value
read is exactly the same (`ShakeItCalibrationCompactTests`: all 100 whole percentiles, 15 / 99, the average, the
count). A still-learning entry keeps its histogram. An older file's finished histograms load as before and are written
compact from then on; an older plugin reading a compact entry sees no histogram and starts that calibration again.
`ScaleEvidencePersistenceTests` expects version 17.

## Version 18 (1.2.2, owner 2026-11-25): `ShakeItCalibration` renamed `SlipLockCalibration`

The plugin LEARNS this calibration while you drive - it only borrows ShakeIt's method - and uses it for wheel slip
(`Slip`) and wheel lock (`RPSToSpeedFront` / `RPSToSpeedRear`) only, so the section is named `SlipLockCalibration`
(`RuntimeDocument.SlipLockCalibration`, `RuntimeStore.Load/SaveSlipLockCalibration`). The sections READ from ShakeIt's
own files keep their names: `ShakeItPrecalibration`, `ShakeItGameBounds`, `ShakeItSourceTimestamps`. An older file's
`ShakeItCalibration` is read (a read-only field, never written) and moved on load, then written under the new name
(`RuntimeStoreTests.An_older_files_ShakeItCalibration_section_is_moved_to_SlipLockCalibration`).
`ScaleEvidencePersistenceTests` expects version 18.

### Also in Version 18: ShakeIt's preset files synced on every start

The start-up import no longer checks file times. On every start the plugin reads all three of SimHub's preset files
(`GameData.json`, `GameData_Custom.json`, `GameData_External.json`, in that order - a later one wins for the same
game) and UPSERTS `ShakeItPrecalibration` / `ShakeItGameBounds`: new games added, changed values taken, a game SimHub
dropped kept (`ShakeItPrecalibrationConverter.Convert(root, state, overrideExisting)`). "Override current data if
exists?" is now a saved General setting (`GeneralSettings.ShakeItImportOverride`, default ON) that decides a game already
held: on, it takes SimHub's values (full upsert); off, it is kept and only new games / metrics are added. These sections
hold only SimHub's data - nothing in the plugin learns into them; an unchanged result is not rewritten. The
`ShakeItSourceTimestamps` section is read from an older file and dropped (never written). The button runs the same sync
at once, with the switch as shown.

## Version 19 (1.2.2, owner 2026-11-25): the Slip Angle states saved lean

- **Fields at their starting value are left out.** For the Slip Angle state types (`SlipAngleKeyState`,
  `SlipAngleLearnerState`, `GripCurveSummary`, `GripCurveBinsState`, `SlipAngleBalanceState`, the speed-band states and
  `CorroboratedLevelState`) a field is written only when it differs from the same field of a FRESHLY CONSTRUCTED
  instance - not from zero (`GripCurveSummary.FirstBin` starts at -1, for example). Loading fills a missing field from
  that same constructor, so nothing is lost, and an older plugin reads exactly the same state
  (`ShakeItCalibrationContractResolver.CreateProperty`).
- **A key that was never taught is not saved.** A Slip Angle learner key whose saved state equals
  `KeyedSlipAngleLearner.FreshState` for its angle source - created, never fed, nothing borrowed (the car-angle
  estimate's front axle, which never learns) - is left out of `SlipAngleLearners`; it is created the same way when it
  is needed again. The converter is set on the dictionary TYPE, because the file is written section by section as well
  as whole (`NeverFedKeysOmitted`).
- **Measured** on the mock host's file (loaded and saved once, nothing driven): the file 54.9 kB -> 17.9 kB; the
  `SlipAngleLearners` section 31.4 kB -> 7.4 kB, 21 -> 14 entries (exactly the 7 never-taught front keys left out).
- Tests: `SlipAngleLeanPersistenceTests` (a live never-fed key equals `FreshState` and is left out, through the real
  section writer too; taught keys round-trip field for field, also through an older reader; a field set back to 0 from a
  non-zero start is still written). `ScaleEvidencePersistenceTests` expects version 19.

## Version 20 (1.2.2, owner 2026-11-25): a finished calibration keeps only what is read

"I want to makes the parameters.json as small as possible after learned."

- A finished calibration entry (more than 7000 points, never fed again) is **compacted** the frame it stops learning,
  and on load. It keeps the point counter `p` (marks it finished), `s` and `c` (RPS-to-speed's average), and, for the
  Slip metric only, `p15` / `p99` (the two percentiles `DispatchBranchFormulas` reads). The histogram `v`, `mx` and
  Version 17's 100-value table `pt` are gone, in memory too. Slip entry: about 70 characters (was about 700); Lock
  (RPS-to-speed) entry: about 50.
- The kept percentiles are exactly what the next read would have returned (memoised values included), so the
  published value does not move. A percentile a compact entry did not keep makes it start learning again rather than
  answer a wrong value (nothing asks for one today).
- Older files: a full histogram and a Version 17-19 `pt` table both load (`pt`'s 15th / 99th values are kept) and are
  written in the new form. `IsFinished` is no longer written.
- Tests: `ShakeItCalibrationCompactTests`.

## Saving: the save takes the snapshot (1.2.2, owner 2026-11-25)

"Following the same pattern that using thread timer to save, and only take the snapshot when the new saving thread
being created ... compare if data changed; if so, make a copy of parameter file; modify the copied file, WITH THE ONLY
CHANGED PARTS ... replace the original."

- **Before:** the data thread exported ten learner families into the cache on every frame (60 a second) and marked
  them changed whether or not anything was learned; every 5-second tick then re-serialised those sections to find
  them unchanged. The calibration was handed over BY REFERENCE, so the tick serialised histograms the data thread was
  still feeding (a race).
- **Now:** nothing is exported per frame. The 5-second one-shot timer's tick (re-armed after its write, as before) runs
  at the lowest thread priority and raises `RuntimeStore.SnapshotRequested`. The data thread checks that flag once
  per frame (one volatile read); only when it is raised does it hand over a copy of the learned data
  (`SaveLearningSnapshotNow`) and answer `SnapshotTaken`. The calibration is copied per entry, and only the entries
  whose revision moved since the last snapshot. The tick waits at most 250 ms for the answer, and asks only while
  frames are learning. With no game running it asks nothing.
- The tick then regenerates only the changed sections, compares them with what is on disk, and writes nothing when
  they came out the same. Otherwise it writes the unchanged sections' cached text plus the regenerated ones to the
  temporary file and swaps it in with one replace (`AtomicFile`), as before.
- **Saved at once:** a game / car / track change (the key just left is handed over and `SaveSoon` runs the tick now),
  a calibration reset, and plugin End / process exit.
- **The data thread never waits for a save.** It no longer takes the cache's lock per frame, and the save never takes
  the engine lock.

## The config file is never written on the data thread (1.2.2, owner 2026-11-25)

"Surface change CANNOT block the main thread to write ... READ from the memory ... change the Save config thread timer,
with start delay as 100 ms, and interval as infinite ... the finally will change the thread timer into start delay
5 seconds."

- **Before:** three saves ran ON the data thread, inside the frame: a Lock / Slip key data seed, a Slip Angle key
  data seed (which can follow a surface change), and a new Slip Angle unit list. Each serialised the whole settings
  object and wrote it with a flush to the disk, which can take tens of milliseconds.
- **Now** (`ConfigSaveScheduler`): those saves only mark the config changed and arm one one-shot timer, due 100 ms,
  period infinite. A request within 100 ms of the previous one re-arms it, so a burst ends in one write 100 ms after
  its last request.
- The tick writes on the timer's thread at the lowest priority, inside try/finally, under one write lock. A tick that
  fires while the previous write is still running waits for it. The finally re-arms the timer for 5 s, or for 100 ms
  again when another request came in during the write. A tick with nothing requested writes nothing, and a failed
  write stays requested and is retried.
- Plugin Init, the settings page's Apply and End still save synchronously, through the same lock (`SaveNow`). End
  then disposes the scheduler: the timer is stopped, a running write is waited for, and a pending request is written.
- **Loading:** a game, car, track or surface change reads only memory. The settings, every learner and the Slip
  Angle source catalog are loaded once at start and kept current. The one file check that ran on the data thread, the
  Slip Angle source file re-read on a game change (`ReloadIfChanged`), now runs on a pool thread. The data thread
  keeps reading the catalog lock-free (`SlipAngleRawSourceTable.Catalog`), and a changed file swaps the catalog in
  with one reference write.
- Not changed: the diagnostic CSV export (off by default) still writes one row per frame on the data thread.
- Tests: `ConfigSaveSchedulerTests`.

## Version 21 (1.2.2, owner 2026-11-25): hashed source keys, the game in the calibration key

**Learner keys.** Every learner key keeps its borrowable parts readable (`game|#|car|#|SOURCE|#|surface`). Only the
source part is stored hashed, as `21:<number>`: Lock / Slip and their scale, anchors and evidence; Clutch, including
`LastClutchKey`; Slip Angle (`21:<number>/all`, with the axle kept readable). Examples:
`AssettoCorsaEvo|#|BMW M2|#|21:1469598103934665603|#|sealed`, and `F12025|#|Haas|#|21:…/front|#|sealed`.

- **The hash** is 64-bit FNV-1a over the string's UTF-8 bytes, written as a decimal number (`StableHash`). The same
  string gives the same number on any machine, process or .NET version. A 32-bit hash would give roughly a 1-in-100
  chance of two sources colliding at thousands of keys.
- **What is hashed (rule 21):**
  - **Lock / Slip / Clutch:** the composite identity. That is each wheel's script type and source, with script text
    already reduced to its stable 8-digit hash, plus the ShakeIt algorithm settings when the source is ShakeIt.
    Clutch identities are `GameClutch`, `Axis:<name>` or `None`. Because this is exactly today's identity text,
    every Version 20 key converts one-to-one.
  - **Slip Angle:** the identity (Native / NativeRatio / car-angle fallback) plus the game's whole definition. That
    is the row's canonical settings (angle sources, transform, surface sources and values, unit, provisional,
    vertical acceleration; id and note excluded), plus the unit in use. A changed definition learns under a new key.
- **`Core\Keys\KeysDefinition.cs`:**
  - abstract `KeyRule`, with `LatestVersion = "21"`, which the parameters file's `Version` is read from;
  - `KeyRule21`;
  - `KeyRuleFactory.CreateKeyRule("21")`, where every released rule stays for good;
  - `Keys.GetKey<T>(config)`, which writes the latest version, and `Keys.Matches<T>(key, config)`, which uses the rule
    of the version the key names;
  - the config types `LearnerSourceKeyConfig` and `SlipAngleSourceKeyConfig`.
- **Where the hash is applied:** in one place, `KeyedGripLearner.MakeKey`, which every learner key goes through. The
  parsers (`ColdStartTierResolver.TryParseKey`, `KeyedScaleLearner.ParseKey`) turn it back into the identity through
  `SourceKeys`, a table filled as keys are built. So everything that classifies a source still reads the readable
  identity in memory: Raw / ShakeIt / Viper, the Slip Angle scale, borrowing. A hash not seen yet this session parses
  as itself, and rebuilding it gives the same key.
- **Loading a Version 20 file:** every learner section and `LastClutchKey` is rekeyed in place
  (`SourceKeys.RekeyLearners`), after the older key migrations, which now emit the hashed form too. The ratio-scale
  drop of Slip Angle windows now applies only to files below Version 16 (`RuntimeStore.LoadedVersion`).
- **The fix for "Version 21 but still the old keys"** (owner-reported, 2026-11-25). The first build converted the keys
  only in the learners' in-memory copies. The store's own document, the one the writer saves, kept the readable keys,
  and a section is replaced only when its learner next hands data over while driving. So the first write stamped
  Version 21 on old keys. The store now converts the document itself as it loads (`RuntimeStore.RekeyToLatest`):
  - every learner section and `LastClutchKey`;
  - the calibration (`CalibrationDataProvider.MigrateLegacyKeys`);
  - the vestigial cross-car seeds, which are dropped;
  - the one key stored inside a value: each SMax evidence entry's borrowing latch `Latches[].ReferenceKey`, the key it
    borrowed from (owner-reported). A file whose section keys are hashed but whose latch references are not counts as
    an older file too.

  A file stamped 21 that still holds readable source parts is treated as Version 20 (`LoadedVersion` 20) and repaired
  the same way. Its calibration keys that already start with a game the file has learners for are kept as they are.
  Checked on the 1.0.9 log file: every section came out hashed straight after start, with no driving; 11 calibration
  entries were kept as legacy and every `DT__` one was dropped.
- **Version 20 in the factory** (owner): `KeyRule20` is the rule of a source part with no version prefix, the readable
  identity itself. `Keys.VersionOf` returns "20" for such a part, so `Keys.Matches` recognises an old key by its own
  rule.
- **The config file:** the Lock / Slip key data slots (`global|src:…`, `game:…|src:…`) and per-source flags store the
  same hashed source. A Version 20 config is converted once at start (`KeyDataPointSettings.RekeySources`) and
  saved. Slip Angle key data stays keyed by its short identity.

**Calibration key** (`game;track;car;metric`):
- SimHub keeps one calibration store per game, so its key needs no game. This plugin keeps one for all games, so the
  game now comes first. For example, `F12025;Melbourne;Haas;Slip`.
- A SimHub-generated track (`DT_…`, new every session on Forza Horizon's open world) or car (`DC_…`) is left out. FH6
  therefore keeps one calibration per car across sessions: `FH6;Car_3304;Slip`.
- Surface and the ShakeIt algorithm settings play no part. The feed uses only telemetry, as in SimHub's own
  `CalibrationDataProvider.Update`, and the settings act when the calibration is read.
- **A file from before Version 21:**
  - Entries with a generated id are dropped.
  - Every other entry is kept as `~legacy;track;car;metric` and moved under the game the first time that track and
    car are driven or read.
  - Reset clears the legacy entry too.

## "Persist Calibration data?" (1.2.2, owner 2026-11-25)

- **Where:** on the Wheel Lock and Wheel Slip settings tabs, left of Reset calibration. It's one view model instance
  shared by both tabs, bound two-way, so a change on one shows on the other at once. It's not on the ShakeIt panels.
- **Setting:** `GeneralSettings.PersistSlipLockCalibration`, default on.
- **When it takes effect:** immediately, not with Apply. The config is saved at once. Turning it off deletes every
  calibration in memory and in the parameters file, and the Reset button hides.
- **While it's off:** the calibration still learns during the session, but it is not saved. The page draft adopts the
  change from the settings broadcast (`SettingField.SlipLockCalibrationPersist`), so Apply cannot undo it.
- Tests: `KeysDefinitionTests`.

## Reset calibration (1.2.2, owner 2026-11-25)

The Wheel Lock and Wheel Slip settings tabs and their ShakeIt panels show **Reset calibration (this track and car)**
while the current calibration key (track + car, the key the game reports) has data. Wheel Slip and Wheel Lock share
the calibration, so a reset from either deletes all three entries of the key (Slip, RPS-to-speed front and rear) and
saves soon. The button then hides everywhere, until driving learns the key again. MVVM: `CalibrationResetViewModel`
(`IsVisible`, `ResetCommand`) over the optional `ICalibrationResetHost` capability; tests in
`SaveSnapshotAndCalibrationResetTests`.

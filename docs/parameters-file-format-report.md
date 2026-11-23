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

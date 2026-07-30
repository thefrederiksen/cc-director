# Director memory investigation - SOREN_NORTH - 2026-07-30

Machine: SOREN_NORTH, 63.78 GB installed, Windows 11 Enterprise 26200.
Investigated because the machine was sitting at ~53 GB used with many agent sessions running.

**Headline: the sessions were not the problem. Two things were - a leaked microphone
recorder inside the Director holding 7.5 GB, and a pile of orphaned build servers holding
another 5-16 GB. Both are fixable, and one of them is a product defect that also explains
our microphone contention problems.**

The product defect is tracked as issue #2333 and is marked release-blocking.

---

## 1. Starting state

| Measure | Value |
|---|---|
| Physical installed | 63.78 GB |
| Physical in use | 53.1 GB (84%) |
| Committed | 82.8 GB of a 105.78 GB limit |
| **Commit peak this boot** | **106.28 GB - the machine had already hit its ceiling** |
| Processes / threads / handles | 805 / 11,418 / 338,152 |

The commit peak is the number that mattered. Physical pressure shows as slowness; hitting
the commit limit shows as allocation failures and applications dying. We had already been there.

Where the physical memory sat:

| Owner | Working set |
|---|---|
| Process working sets | 46.0 GB |
| Kernel paged pool | 4.7 GB |
| Kernel nonpaged pool | 2.2 GB |
| Drivers / locked / other | 0.35 GB |

Note that ~7 GB lives in the kernel pools and belongs to no process at all. Any report built
purely by summing process memory silently loses that and looks wrong.

By category, at the busiest sample:

| Category | Procs | Working set | Commit |
|---|---|---|---|
| build (MSBuild, test hosts, Roslyn) | 62 | 12.03 GB | 8.30 GB |
| windows | 167 | 6.44 GB | 4.55 GB |
| director | 4 | 6.05 GB | 10.33 GB |
| agent (claude) | 12 | 4.78 GB | 10.88 GB |
| desktop apps | 182 | 4.47 GB | 6.79 GB |
| webview2 | 44 | 2.68 GB | 3.84 GB |
| browser | 39 | 2.24 GB | 2.61 GB |
| vm / wsl / docker | 17 | 1.03 GB | 3.27 GB |
| database | 7 | 0.76 GB | 3.23 GB |
| mcp node servers | 55 | 0.70 GB | 3.34 GB |

## 2. Were sessions left open?

No. Checked directly:

- 14 Director sessions registered, 12-13 live `claude.exe` processes.
- **Zero** orphaned `conhost.exe` (145 present, every one with a live parent).
- **Zero** orphaned WebView2 trees.

Cost of one agent session, measured as the agent process plus every descendant:

| | Working set | Commit | Child processes |
|---|---|---|---|
| Typical session | 0.5 - 0.8 GB | 1.1 - 1.4 GB | 8 - 14 |
| Heaviest observed | 1.55 GB | 1.66 GB | 29 |
| All 12 together | 8.2 GB | - | - |

Sessions are honest consumers. Closing them would have cost working state and freed little.
Soren had already closed 10-15 sessions before this investigation, so peak session load was
higher earlier - that was real memory, but it is not what was left behind.

## 3. Root cause: a leaked dictation recorder (7.5 GB)

The main `cc-director.exe` (up 23.6 h, 9 sessions) held 5-6 GB working set and 9.4 GB commit.
The control arm made it obvious: `cc-director1.exe`, up 4.1 h with 3 sessions, held 0.46 GB.

Runtime counters (`dotnet-counters`, no pause, no dump) split managed from native immediately:

| Counter | main Director | slot 1 Director |
|---|---|---|
| Large Object Heap | **7.53 GB** | 0.08 GB |
| Gen 2 | 1.17 GB | 0.09 GB |
| GC committed | 8.73 GB | 0.19 GB |

Ninety times the large-object retention for three times the sessions. Only 525 MB of the
7.5 GB was fragmentation, so the rest was **live, retained** objects - not collection lag.

A heap walk (ClrMD attached to the live process) found the whole heap is five byte arrays:

```
Heap total : 9.74 GB across 2,618,804 objects

   8,128 MB     14,308  System.Byte[]              <- five arrays are 7,644 MB of this
     971 MB    433,849  TerminalCell[]
     621 MB     24,094  Free

=== OBJECTS >= 100 MB : 5 ===
  2,048.0 MB  System.Byte[]   gen=3 (Large Object Heap)
  2,048.0 MB  System.Byte[]   gen=3
  2,048.0 MB  System.Byte[]   gen=3
  1,200.0 MB  System.Byte[]   gen=3
    300.0 MB  System.Byte[]   gen=3
```

All five have the identical retention path back to a GC root:

```
[root: Stack] NAudio.Wave.WaveInEvent
  -> EventHandler<NAudio.Wave.WaveInEventArgs>
    -> CcDirector.Avalonia.Voice.MicAudioCapture
      -> Action<System.Byte[]>
        -> CcDirector.Avalonia.Voice.BatchDictationRecorder
          -> System.IO.MemoryStream
            -> System.Byte[]          <- 2,048 MB
```

### What this means

`BatchDictationRecorder` accumulates captured microphone audio into an unbounded
`MemoryStream`, with no cap of any kind:

```csharp
// src/CcDirector.Avalonia/Voice/BatchDictationRecorder.cs
private readonly MemoryStream _audio = new();

private void AppendChunk(byte[] chunk)
{
    if (chunk.Length == 0) return;
    lock (_audioLock)
    {
        _audio.Write(chunk, 0, chunk.Length);   // no size limit, no duration limit
    }
    ...
}
```

Capture format is 24,000 Hz, 16-bit, mono = **48 KB per second** (`MicAudioCapture.SampleRate`).
That converts the array sizes directly into recording time:

| Array | Audio captured |
|---|---|
| 2,048 MB | 12.4 hours |
| 2,048 MB | 12.4 hours |
| 2,048 MB | 12.4 hours |
| 1,200 MB | 7.3 hours |
| 300 MB | 1.8 hours |

The three at exactly 2,048 MB have hit the .NET single-array ceiling (2 GB) and can grow no
further. The Director had been up 23.6 hours.

The live-versus-garbage census confirms these are leaks, not collection lag:

| Type | Total in heap | **Reachable (live)** | Garbage |
|---|---|---|---|
| `BatchDictationRecorder` | 76 | **6** | 70 |
| `MicAudioCapture` | 75 | **5** | 70 |

Seventy of each are dead and awaiting collection - that part is normal. But **six recorders
and five microphone captures are still rooted and still running**, each with a live NAudio
capture thread appending 48 KB every second, forever.

### Two distinct defects

1. **No bound on dictation length.** No human dictation is 12 hours. The buffer should have
   a hard cap (a few minutes of audio) after which capture stops and either transcribes or
   fails loudly. Today the only thing that stops growth is the 2 GB array limit.
2. **Recorders are leaked.** Five to six simultaneous live recorders is wrong on its own -
   started and never disposed. `DisposeAsync` exists and unsubscribes correctly, so some
   path is not calling it. Because the root is a *thread stack* (NAudio's capture thread),
   the garbage collector can never reclaim these no matter how much pressure builds.

### This is not only a memory bug

Five leaked recorders means **five microphone captures held open at once**. That is very
likely the same defect behind the Car Mode microphone-contention symptoms - a recorder that
never released the device. Fixing the leak should fix both.

**Cost of the leak: 48 KB/s per leaked recorder = 172 MB/hour each; with five live, about
0.86 GB/hour.** That matches a Director growing to 9 GB over a day.

## 4. Second finding: closed sessions are retained (~1.2 GB)

Also from the census - these are all **reachable**, so something still holds them:

| Type | Live instances |
|---|---|
| `CircularTerminalBuffer` | 138 |
| `Session` | 138 |
| `SessionViewModel` | 151 |
| `TerminalCell[]` (scrollback lines) | 433,849 (971 MB) |

The Director was running **9** sessions. It holds **138**.

Per-session cost inside the Director is modest and correctly bounded:

- 2 MB raw circular terminal buffer (`AgentOptions.DefaultBufferSizeBytes`, default 2 MB,
  not overridden in the live config)
- terminal scrollback capped at 5,000 lines per parser, two parsers per session
  (`Session.HtmlMaxScrollback` / `StreamMaxScrollback`) - roughly 7 MB
- **~9-10 MB per session in total**

So history is kept in memory, but it is capped per session and does **not** grow without
bound as a session gets longer. The growth is in the **number of retained sessions**: about
129 closed sessions never released, at ~9-10 MB each, is roughly **1.2 GB**.

This needs a follow-up: confirm whether retention is deliberate (a history feature) or an
event-handler subscription that outlives the session. Not yet proven either way.

## 5. Third finding: orphaned build servers (5-16 GB, fluctuating)

MSBuild worker nodes with `/nodeReuse:true` outlive the build that started them. Observed
between 46 and 81 of them holding 8-16 GB.

**A trap worth recording.** The obvious test - "parent process is dead, so it is garbage" -
is WRONG here, and would have broken live builds. Node reuse means a parked node gets
adopted by a *later* build, so a node with a dead parent may be hard at work for someone
else. Measured directly:

- 81 nodes had a dead parent. All 81 looked reclaimable by that test.
- A 45-second CPU watch showed **52 of them were actively working** for the two running
  continuous-integration builds.
- Of the 29 that looked idle, **9 more woke up** during the next 45 seconds.

Short idle windows are not proof either. The safe mechanism is the supported one:

```
dotnet build-server shutdown
```

A node already connected to a build will not accept a new connection, so this command
self-selects only genuinely free nodes. It is safe to run at any time, including during
active builds.

**Result when run:** 23 nodes shut down gracefully, **4.94 GB of node memory freed**,
physical down 3.88 GB, commit down 4.16 GB. The 46 nodes serving live builds were untouched
and no build was disturbed.

## 6. What to do

### Product fixes (the real work)

1. **Cap the dictation buffer.** Hard limit on captured audio - a few minutes. On reaching
   it, stop capture and surface the reason. Never let a buffer run to the 2 GB array ceiling.
2. **Guarantee recorder disposal.** Find the path that leaks `BatchDictationRecorder`, and
   add a watchdog that disposes any recorder capturing beyond the cap. Consider enforcing a
   single live recorder, since more than one is always wrong.
3. **Investigate session retention** - 138 retained for 9 running.
4. **Validate configuration.** `DefaultBufferSizeBytes` is read with a bare `GetInt32()` and
   no clamp; a bad value would allocate that much per session with no guard.

### Operational

5. **Run `dotnet build-server shutdown` routinely** - after continuous-integration jobs, or
   on a schedule. Consider `MSBUILDDISABLENODEREUSE=1` for the build agents, which trades a
   little build speed for never accumulating parked nodes.
6. **Restart the main Director periodically** until fix 1 and 2 ship. It reclaims ~9 GB.
7. **Restart the two build agents when idle** (`D:\Agents\N_Net8_1`, `N_Net8_2`) - they had
   been up since 2026-07-29 06:19.

### For the planned Director memory panel

The panel should show, and offer to reclaim:

- physical / commit / **commit peak against the limit** (commit is the number that kills)
- memory by owner: this Director, each session tree, builds, everything else
- its own managed heap by generation, with the **Large Object Heap called out** - that single
  number is what turned this investigation from "the Director is big" into a named cause
- reclaimable now: free build servers, with a one-click `dotnet build-server shutdown`
- a warning when its own Large Object Heap crosses a threshold, since that is the signature
  of exactly this class of bug

## 7. Method, so this is repeatable

Cheapest first; each step narrows the next.

1. **True totals** - `GetPerformanceInfo` via P/Invoke, not the sum of process working sets
   (that misses ~7 GB of kernel pools).
2. **Group by process tree** - separates the Director itself from what it spawned. This is
   what showed the Director's own process was the largest single consumer.
3. **`dotnet-counters collect --process-id <pid> --counters System.Runtime`** - no pause, no
   dump. Splits managed from native and gives per-generation heap sizes. One command turned
   "big" into "the Large Object Heap is big".
4. **Compare against a control** - the short-lived slot-1 Director. Without it, 7.5 GB is
   just a number; with it, it is an anomaly.
5. **Heap walk with ClrMD** - attach to the live process, find objects over a size threshold,
   then walk breadth-first from every GC root to get the retention path. The path is what
   names the cause; a type histogram alone would only have said "byte arrays".
6. **Live-versus-garbage census** - an object present in the heap is not necessarily still
   needed. Only reachability from a root separates a leak from collection lag. This is what
   proved 6 live recorders (leak) versus 70 dead ones (normal).

`dotnet-dump collect --type Heap` failed on this process with "Value does not fall within
the expected range" - a known limitation on very large heaps. `dotnet-gcdump` (53 MB, 3.6
seconds) and live ClrMD attach both worked fine and are the better tools here anyway.

## 8. Artifacts

Both tools are committed alongside this document so the analysis can be repeated. They are
read-only: they inspect processes and heaps and change nothing.

| Path | What |
|---|---|
| `scripts/memory-analysis/memory_map.py` | Standalone machine memory map; console report plus a self-contained HTML treemap. Needs `psutil`. |
| `scripts/memory-analysis/heapscan/` | Heap scanner built on ClrMD: big-object finder, root-path walker, live-versus-garbage census. |

Raw output from this investigation was left on the machine at `D:\memory-analysis\`
(`heapscan-live.txt` has the five retention paths, `heapscan-census.txt` the census,
`gcdump-report.txt` the type histogram, and the `.gcdump` snapshot opens in Visual Studio or
PerfView). Those are machine-local and not committed - the findings above are the record.

Rerun the machine map any time:

```
python scripts/memory-analysis/memory_map.py --html memory-map.html
```

Rerun the heap analysis against a live Director:

```
cd scripts/memory-analysis/heapscan
dotnet build -c Release
bin/Release/net10.0/heapscan.exe <director-pid> --suspend \
    --census=CircularTerminalBuffer --census=BatchDictationRecorder
```

`--suspend` briefly pauses the target for a consistent heap. Omit it to attach without
pausing, at the cost of occasional invalid reads on a busy heap.

## 9. Caveats

- All figures are a snapshot. Build activity moved the total by 8-10 GB during the session;
  physical in use ranged 43.9 - 54.2 GB while nothing structural changed.
- The session-retention finding (138 held for 9 running) is measured but its **cause is not
  yet established**. Do not treat "closed sessions leak" as proven - only "138 session
  objects are reachable" is proven.
- The microphone-contention link is a strong inference from five live captures, not a
  reproduced failure.
- The build agents were mid-build throughout and were **not** restarted.

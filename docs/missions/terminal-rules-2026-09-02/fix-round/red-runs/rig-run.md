# Row 4, re-run against the regenerated migration, with the content comparison

`scripts\terminal-rules-screen-proof.ps1 -Slot 18 -GatewayPort 7996`, run 2026-09-02, against the
migration regenerated on the new main snapshot. Slot 18 because slots 1 to 17 are all reserved by
scheduled tasks on this machine; the script refuses any slot below 6 by design.

Both processes were throwaway, both roots were created by the run, and the teardown removed them:

```
[screen-proof] removed the rig root ...\screen-proof-20260902-121724-41cbbc96, database included
```

## The good run, 12:17

```
STEP 1 PASS: the throwaway Director is connected to the throwaway Gateway
session activity states observed, in order: Working -> WaitingForInput
STEP 2 PASS: the Gateway logged storing a screen pushed by the rig Director
STEP 2b PASS, machine up: [GatewayScreenReader] sid=5552a4de-...: pulled the screen over the TUNNEL
read 27550 characters of terminal buffer into ...\results\terminal-buffer.txt
the three run markers are present in the terminal buffer, so the comparison below has a real subject
signalling Local\cc-director-shutdown-d67aac9c-... (pid 2744)
STEP 3 PASS, machine stopped: the route refused before the read: {"error":"session not found on any director"}
read-back verdict rests on ...\results\stored-row.txt, which names session 5552a4de-a187-4ad9-917d-52fdfa3ddc9c
  and all three of this run's markers
STEP 4 PASS: the real store read the screen back over the real migrated schema, machine offline
ROW 4 PROVEN.
```

Note step 2b: with the machine UP the reader says **pulled the screen over the TUNNEL**. Under the old
design this step could report either STORED or TUNNEL; a live read now always tunnels, and that line is
the running system saying so.

### The row that was read back, and the terminal it came from

```
session=5552a4de-a187-4ad9-917d-52fdfa3ddc9c capturedAt=2026-09-02T16:18:06.0820000Z
director=d67aac9c-b27b-4e7d-add4-ed8d041ad90d agent=RawCli state=WaitingForInput
bufferBytes=2750036 rows=40 hasGrid=True cursor=(39,72) visible=True alternateScreen=False
```

The last four stored rows, and the same four lines from the Director's own terminal buffer read over the
separate `buffer` verb while it was still up:

```
  stored | TR_SCREEN_PROOF_20260902-121724_ALPHA
  stored | TR_SCREEN_PROOF_20260902-121724_BRAVO
  stored | TR_SCREEN_PROOF_20260902-121724_CHARLIE
  stored | C:\Users\soren\AppData\Local\Temp\screen-proof-20260902-121724-41cbbc96>

  terminal | TR_SCREEN_PROOF_20260902-121724_ALPHA
  terminal | TR_SCREEN_PROOF_20260902-121724_BRAVO
  terminal | TR_SCREEN_PROOF_20260902-121724_CHARLIE
  terminal | C:\Users\soren\AppData\Local\Temp\screen-proof-20260902-121724-41cbbc96>

MARKER LINES THIS RUN PRINTED, and the stored row each was found at:
  TR_SCREEN_PROOF_20260902-121724_ALPHA -> stored row 35
  TR_SCREEN_PROOF_20260902-121724_BRAVO -> stored row 36
  TR_SCREEN_PROOF_20260902-121724_CHARLIE -> stored row 37
```

All forty stored rows were checked, not only these; every nonblank one was found in the terminal text.
The rows above them are the tail of the `dir /s /b C:\Windows\System32` flood that put the session into
Working, and they matched too. The markers are at rows 35, 36 and 37 - increasing, which is the order
assertion. `bufferBytes=2750036` is the mark taken inside the parser's own lock (finding 2), so it counts
the bytes this frame reflects.

## The known-bad run, 12:13 - the rig now catches what it used to certify

The inspector's exact mutation in `GatewayScreenSink` - every pushed screen's rows replaced by the
single row "MANGLED CONSTANT" - was applied, the Director was republished from the worktree by the rig
itself, and the same script was run:

```
STEP 1 PASS ... STEP 2 PASS ... STEP 2b PASS ... STEP 3 PASS
--- the read-back test failure, in full ---
  the stored screen does NOT contain the line this run printed:
  'TR_SCREEN_PROOF_20260902-121354_ALPHA'. The rows that were stored are: MANGLED CONSTANT
ROW 4 FAILED: the read-back test did not pass (exit 1)
```

That is the whole of finding 4 answered end to end: the mutation that previously left the suite green
AND the rig printing ROW 4 PROVEN now fails the row, and the failure message quotes what was stored.
The mutation was reverted and the good run above was made afterwards on the reverted build.

## Two defects in the rig itself, found by running it known-bad

Both were in the instrument rather than the product, and both are the same shape as the mission's own
findings, so they are recorded rather than quietly fixed.

**Its failure path printed nothing.** The script runs with `ErrorActionPreference = Stop`, and
PowerShell 5.1 turns a native executable's standard error into a terminating error under that setting -
so a FAILING read-back test aborted the script at the `dotnet test` line, with the test's own diagnosis
still inside a stream nobody captured and the log file holding only its two header lines. The row failed
and the reason was unreadable. A proof whose failure path prints nothing is the same defect as a proof
that cannot fail. The call now lowers the preference for that one command, captures the output, and
prints the failure lines into the transcript.

**Its verdict was parsed out of somebody else's console wording.** The skip guard matched `Passed: 1` in
the runner's summary line. On the first good run after the log capture changed, the comparison PASSED
and the row still failed, because that line was not where the parser expected it. A verdict that depends
on the format of another tool's output is not a verdict about the system. It now rests on an ARTIFACT
THE RUN PRODUCED: the read-back test writes `stored-row.txt` only on its success path, and the script
requires that file to exist and to name this run's session and all three of this run's markers. A
skipped test writes nothing and fails that immediately, which is the property the old guard was reaching
for.

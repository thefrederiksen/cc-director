# PR 2271 adversarial review

Commit reviewed: `4ea20e73` (`Start, stop and restart a NAMED Director, not just the machine's only one`).

Result: fail. The six findings below are defects introduced by this change; none is merely an older defect exposed by it.

Clean verification before adding the review tests:

- `CcDirector.Launcher.Tests`: 96/96 passed on `net10.0-windows` and 96/96 passed on `net10.0`.
- `CcDirector.Gateway`: build succeeded with zero warnings and zero errors.

With the six review tests: 102 total per target framework, 96 passed and the same six failed on each.

## 1. Critical — Recursive-delete path traversal

Production code:

- `src/CcDirector.Core/Instances/NamedInstanceRegistry.cs:129-130`
- `src/CcDirector.Core/Instances/NamedInstanceRegistry.cs:146-175`

What breaks and under exactly what conditions:

`Delete` accepts the caller's raw trimmed/lowercased string, combines it under `instances`, and recursively deletes the resulting path without validating a canonical slug, requiring a registry match, or proving that the resolved path remains inside the intended instance home.

An authenticated request to the launcher's `/director/delete` route, or the Gateway's `delete-instance` relay, can supply an instance such as `spare\..\..\outside`. `DeleteAsync` first attempts to stop that raw name; when no process is found it proceeds to `NamedInstanceRegistry.Delete`. The registry lookup finds no entry, but deletion is attempted anyway. The combined path escapes `instances/<slug>` and deletes the selected directory recursively.

The failing test creates a marker under the temporary shared root but outside `instances`, invokes `Delete` with that traversal, and proves the marker is deleted. Related traversals can select the default instance home, the entire `instances` tree, or directories above it. Exact `default`, casing variants, surrounding whitespace, null, and empty values are refused; traversal bypasses that protection because the refusal only compares the uncanonicalized string with `default`.

Failing test:

- `NamedInstanceLifecycleTests.Delete_PathTraversalCannotEscapeTheNamedInstanceHome`

Test file:

- `D:\ReposFred\devthrottle-review-f5bcfdb3\src\CcDirector.Launcher.Tests\NamedInstanceLifecycleTests.cs`

Classification: defect introduced by PR 2271. `HomeFor`, `Delete`, and the destructive endpoint were added by this change.

## 2. Critical — A named REST fallback becomes a default-instance action

Production code:

- `src/CcDirector.Launcher/LauncherHost.cs:136-148`
- `src/CcDirector.Gateway/Api/LauncherLifecycleRelay.cs:107-111`
- `src/CcDirector.Gateway/Api/LauncherLifecycleRelay.cs:231-235`

What breaks and under exactly what conditions:

`ReadInstanceAsync` returns null without reading the request whenever `ContentLength` is null. A valid chunked or otherwise transfer-encoded JSON request therefore loses its `instance` value.

The Gateway's REST fallback uses `PostAsJsonAsync` with `JsonContent`; its size is not known up front and it is sent chunked over HTTP/1.1. The exact production condition is that the persistent launcher stream is absent and the Gateway falls back to REST for a named `start`, `stop`, or `restart`. The launcher interprets the unread named body as null and acts on the default Director. The same failure is reachable through any direct client that sends valid chunked JSON. A malformed JSON body is also caught and converted to the default target.

For `stop` and `restart`, this can interrupt the default Director carrying the machine's real sessions instead of the requested spare. `start` starts or reports the default rather than the named instance. `delete` is less destructive in this exact path because its route checks the resulting null and returns 400.

Failing test:

- `LauncherNamedInstanceEndpointTests.LifecycleBody_WithNoContentLength_StillCarriesTheNamedInstance`

Test file:

- `D:\ReposFred\devthrottle-review-f5bcfdb3\src\CcDirector.Launcher.Tests\LauncherNamedInstanceEndpointTests.cs`

Classification: defect introduced by PR 2271. `ReadInstanceAsync` and the named REST body were added by this change.

## 3. Critical — A stale registration with a reused PID can kill another Director or process

Production code:

- `src/CcDirector.Launcher/DirectorSupervisor.cs:318-324`
- `src/CcDirector.Launcher/DirectorSupervisor.cs:460-491`

What breaks and under exactly what conditions:

On Windows, registration-derived discovery accepts any live PID. The installed-executable check is explicitly skipped for the registration path, and the registration parser discards the production registration's `StartedAt` value.

The exact condition is that the target instance leaves a stale registration and Windows later reuses its PID. `IsInstanceRunning` accepts the replacement process as the requested instance. `StopAsync`, `RestartAsync`, or `DeleteAsync` then tries the stale control port. If that endpoint is absent or unreachable, the fallback force-kills the replacement PID. If the replacement is another Director instance, it runs the same executable, so adding an image-path comparison alone still cannot distinguish it from the intended target. A reuse-safe check needs process start time or another per-process/per-instance identity.

The failing test launches a live replacement process from the exact path that the supervisor regards as the installed Director, writes a target registration with its PID but a start time one day in the past, and proves `IsInstanceRunning("spare")` incorrectly returns true.

Failing test:

- `DirectorSupervisorNamedInstanceTests.IsInstanceRunning_StaleRegistrationWhosePidWasReused_IsRejected`

Test file:

- `D:\ReposFred\devthrottle-review-f5bcfdb3\src\CcDirector.Launcher.Tests\DirectorSupervisorNamedInstanceTests.cs`

Classification: defect introduced by PR 2271. Before this change, Windows discovery used the installed-image scan. This change made registration-first targeting active on Windows without a reuse-safe identity check.

## 4. High — The caller name and registry slug diverge after the first start

Production code:

- `src/CcDirector.Launcher/DirectorSupervisor.cs:99`
- `src/CcDirector.Launcher/DirectorSupervisor.cs:173-188`
- `src/CcDirector.Launcher/DirectorSupervisor.cs:419-420`
- `src/CcDirector.Core/Instances/NamedInstanceRegistry.cs:104`
- `src/CcDirector.Core/Instances/NamedInstanceRegistry.cs:338-349`

What breaks and under exactly what conditions:

The launcher only trims and lowercases the caller's name. Registry creation additionally turns spaces, underscores, and dots into hyphens, removes other punctuation, and makes the result unique.

The exact condition is a caller name that requires registry slugification, such as `My Spare`. The first start passes `my spare` to `EnsureRegistered`; the registry creates `my-spare`, and `Start` correctly launches the returned slug. A second start with the same `My Spare` spelling asks `NamedInstanceRegistry.Get("my spare")`, which cannot find `my-spare`, so it creates and launches `my-spare-2`. A subsequent stop, restart, or delete with the same caller spelling looks under `instances/my spare`, not the instance the first request launched. Pure canonical slugs such as `spare` do not trigger this particular defect.

Failing test:

- `NamedInstanceLifecycleTests.Start_TheSameDisplayNameTwice_ReusesTheRegistrySlug`

Test file:

- `D:\ReposFred\devthrottle-review-f5bcfdb3\src\CcDirector.Launcher.Tests\NamedInstanceLifecycleTests.cs`

Classification: defect introduced by PR 2271. `EnsureRegistered` and the launcher-to-registry naming bridge were added by this change.

## 5. High — Concurrent starts create and launch two instances

Production code:

- `src/CcDirector.Launcher/DirectorSupervisor.cs:173-188`

What breaks and under exactly what conditions:

`EnsureRegistered` performs `Get` and `Create` as two separate registry operations. Each individual operation takes the registry's lock, but the complete get-or-create transaction does not.

The exact condition is two overlapping start requests for the same previously unregistered canonical name, with both `Get` calls completing before either `Create`. Both requests observe absence. The two `Create` calls then serialize, but registry uniqueness deliberately assigns `spare` to one and `spare-2` to the other. Each `Start` receives a different returned slug and launches it. This leaves two running instances for one requested identity and an extra registry/home entry that later ordinary `spare` requests do not manage.

The failing test uses a barrier in the injected gateway lookup to ensure both absence checks complete before either create and deterministically observes `spare` and `spare-2`.

Failing test:

- `NamedInstanceLifecycleTests.Start_TheSameNewNameConcurrently_CreatesOnlyOneInstance`

Test file:

- `D:\ReposFred\devthrottle-review-f5bcfdb3\src\CcDirector.Launcher.Tests\NamedInstanceLifecycleTests.cs`

Classification: defect introduced by PR 2271. The non-atomic launcher get-or-create path is new.

## 6. High — A partial delete reports success, forgets the instance, and can resurrect its data

Production code:

- `src/CcDirector.Core/Instances/NamedInstanceRegistry.cs:161-184`
- `src/CcDirector.Core/Instances/NamedInstanceRegistry.cs:289-296`

What breaks and under exactly what conditions:

`Delete` removes and persists the registry entry first. It then catches every recursive-directory-delete exception and still returns `true` because an entry used to exist.

The exact condition is that Windows cannot remove the home because a file is held open, permissions or antivirus block removal, or another filesystem failure occurs. The launcher and Gateway report success, the instance is absent from the registry, and all or part of its data remains on disk. A later start with the same name finds no registry entry, reuses the freed slug, and calls `ScaffoldInstanceHome`. That method deliberately refuses to overwrite an existing config, so the supposedly deleted sessions, settings, and gateway credential are resurrected under a newly created registry identity.

The failing test holds a file in the instance home open with `FileShare.None`. `Delete` returns without an error and the registration is gone, proving that the failure is hidden and the operation is not retryable from truthful state.

Failing test:

- `NamedInstanceLifecycleTests.Delete_WhenTheHomeCannotBeRemoved_DoesNotReportSuccessOrForgetTheRegistration`

Test file:

- `D:\ReposFred\devthrottle-review-f5bcfdb3\src\CcDirector.Launcher.Tests\NamedInstanceLifecycleTests.cs`

Classification: defect introduced by PR 2271. `Delete` and its failure ordering and handling were added by this change.

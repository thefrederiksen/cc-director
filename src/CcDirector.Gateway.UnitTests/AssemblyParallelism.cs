using Xunit;

// THE PARALLELISM CAP IS LOAD-BEARING, NOT TUNING - and it is compiled in rather than configured.
//
// This assembly deliberately runs WITHOUT the machine-wide Gateway test lock, which means it now runs
// BESIDE other working trees' suites instead of waiting for them. The lock used to hand this suite an idle
// machine as a side effect; nothing does any more. Left at xUnit's default of one thread per core, it opened
// twenty-four threads onto an already-saturated twenty-four-core box and starved its own timing-sensitive
// tests: three consecutive runs failed 1, then 8, then 7 tests - a DIFFERENT set every time, and every one
// of them passing in isolation. Capped, the same suite under the same load passed 2752 of 2752.
//
// IT LIVES IN CODE BECAUSE THE CONFIG FILE DID NOT WORK. The first attempt was an xunit.runner.json with
// maxParallelThreads, copied to the output directory - where it demonstrably landed, and was demonstrably
// ignored: the failures came straight back (3, then 1) while the same value passed on the command line.
// A setting that is silently not applied is worse than no setting, because the build looks configured. An
// assembly attribute cannot be missed by a runner or dropped by a copy step.
//
// The number is deliberately modest rather than "cores minus a few": the whole point of leaving the lock is
// that several working trees run at once, so this suite must be a good neighbour rather than assume the
// machine is its own. Raise it only with a measurement taken while other suites are running.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]

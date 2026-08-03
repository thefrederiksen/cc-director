using Xunit;

// PARALLEL, BUT CAPPED. Not disabled - the distinction is the whole point of this assembly.
//
// The project this was split out of runs SEQUENTIALLY, via
// [assembly: CollectionBehavior(DisableTestParallelization = true)] in TestParallelization.cs. That
// attribute serialises roughly four thousand tests in order to protect about sixteen classes that
// assert on wall-clock deadlines, and the result was eleven minutes of wall clock on a quiet machine
// to burn twenty-five seconds of CPU. The reasoning behind it was sound for CI's two-to-four vCPU
// runner and was never revisited for a 24-core workstation.
//
// Nothing in THIS assembly has a timing assumption - that was the selection rule for what moved here -
// so nothing here needs the protection, and every test that did stayed behind.
//
// The cap exists for a different reason than the serialisation did: this assembly no longer waits for
// the machine-wide Gateway lock, so it runs BESIDE other working trees' suites. Uncapped it would open
// one thread per core onto an already-busy machine and starve itself, which is a measured effect - the
// Gateway unit tests failed 1, then 8, then 7 tests on three consecutive uncapped runs, a different set
// each time, all passing in isolation.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]

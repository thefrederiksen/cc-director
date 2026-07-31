namespace CcDirector.Core.Memory;

/// <summary>
/// Keeps the recent history of memory readings and turns them into a confirmed level and a leak
/// verdict.
///
/// It exists because both of the useful things here need TIME, and a single sample cannot supply
/// it. A level must hold across several readings before it is worth announcing, because build
/// activity moved the measured machine by 8-10 GB within minutes; and a leak is a trend, not a
/// number. Everything is fed in from outside - no timer, no clock of its own - so a test can play
/// hours of history through it in a millisecond.
///
/// Thread-safe: a background sampler writes while a rendering thread reads.
/// </summary>
public sealed class MemoryPressureTracker
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly List<MachineMemoryReading> _machine = new();
    private readonly List<ManagedHeapReading> _heap = new();

    private MemoryPressureLevel _confirmed = MemoryPressureLevel.Normal;
    private MemoryPressureLevel _candidate = MemoryPressureLevel.Normal;
    private int _candidateStreak;

    /// <param name="capacity">
    /// How many readings to keep. The default holds roughly two hours at one reading a minute,
    /// which is long enough for a leak to show a slope and short enough that a fixed leak stops
    /// being reported quickly.
    /// </param>
    public MemoryPressureTracker(int capacity = 120)
    {
        if (capacity < LargeObjectHeapLeakRule.MinimumReadings)
            throw new ArgumentOutOfRangeException(nameof(capacity),
                $"Capacity must be at least {LargeObjectHeapLeakRule.MinimumReadings} readings.");

        _capacity = capacity;
    }

    /// <summary>The level that has actually held for long enough to announce.</summary>
    public MemoryPressureLevel ConfirmedLevel
    {
        get { lock (_gate) return _confirmed; }
    }

    /// <summary>Readings held right now, for tests and diagnostics.</summary>
    public int ReadingCount
    {
        get { lock (_gate) return _machine.Count; }
    }

    /// <summary>
    /// Record one sample. Either part may be null - a machine reading fails on unsupported
    /// platforms, and the heap may not be sampled every time.
    /// </summary>
    /// <returns>True when the confirmed level CHANGED, which is the moment worth acting on.</returns>
    public bool Record(MachineMemoryReading? machine, ManagedHeapReading? heap)
    {
        lock (_gate)
        {
            if (heap is not null)
            {
                _heap.Add(heap);
                if (_heap.Count > _capacity) _heap.RemoveAt(0);
            }

            if (machine is null)
                return false;

            _machine.Add(machine);
            if (_machine.Count > _capacity) _machine.RemoveAt(0);

            var instant = MemoryPressureRule.LevelFor(machine);

            if (instant == _candidate)
            {
                _candidateStreak++;
            }
            else
            {
                _candidate = instant;
                _candidateStreak = 1;
            }

            // Falling back to Normal is allowed to happen as fast as rising, so a machine that
            // recovers stops shouting immediately. Only ALARM needs the streak, because a false
            // alarm is the thing that destroys trust in the warning.
            bool confirmNow =
                _candidate == MemoryPressureLevel.Normal ||
                _candidateStreak >= MemoryPressureRule.ConsecutiveReadingsToConfirm;

            if (!confirmNow || _candidate == _confirmed)
                return false;

            _confirmed = _candidate;
            return true;
        }
    }

    /// <summary>The leak verdict over the readings held, or Undetermined when there are too few.</summary>
    public LeakVerdict JudgeLeak()
    {
        lock (_gate)
        {
            return LargeObjectHeapLeakRule.Judge(_heap.ToArray());
        }
    }

    /// <summary>Build the facts for the fold from what is held, plus whatever the caller knows.</summary>
    public MemoryPressureFacts BuildFacts(int? reclaimableBuildServers)
    {
        lock (_gate)
        {
            return new MemoryPressureFacts(
                Machine: _machine.Count > 0 ? _machine[^1] : null,
                OwnHeap: _heap.Count > 0 ? _heap[^1] : null,
                Leak: _heap.Count >= LargeObjectHeapLeakRule.MinimumReadings
                    ? LargeObjectHeapLeakRule.Judge(_heap.ToArray())
                    : null,
                ConfirmedLevel: _confirmed,
                ReclaimableBuildServers: reclaimableBuildServers);
        }
    }
}

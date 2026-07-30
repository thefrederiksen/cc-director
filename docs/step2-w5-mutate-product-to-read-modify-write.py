"""
Mutate the PRODUCT code, not a test double: replace the concurrency store's explicit
ON CONFLICT DO UPDATE ... GREATEST writes with a change-tracked read-then-save, which is
what the entity contract forbids. Run the threaded interleaved-writer test against it and
watch it lose a maximum, then restore with:

    git checkout -- src/CcDirector.Gateway/Stats/GatewaySessionConcurrencyStore.cs

WHY THE THREADED TEST AND NOT THE DETERMINISTIC ONE. The deterministic race interleaves the
two containers' DECISIONS, and this store makes its decision from its in-memory shadow, not
from a database read. A read-modify-write inside Observe would re-read the row at write time
and see the other container's eight, so it would decline to write seven and the deterministic
race would PASS with the defect present. The lost update for a read-then-save lives in the
window between ITS read and ITS write, and only genuine concurrency opens that window. So the
product mutation is judged by ManyContainersHammeringOneHour, where four containers really do
race, and the deterministic race keeps its own job: proving the assertion can fail at all.
"""
import io
import sys

PATH = 'src/CcDirector.Gateway/Stats/GatewaySessionConcurrencyStore.cs'

OLD = """                    if (peakAdvanced)
                        ctx.Database.ExecuteSqlRaw(sql.UpsertPeak, tenantValue, liveCount, workingCount, nowUtc);

                    if (hourAdvanced)
                        ctx.Database.ExecuteSqlRaw(sql.UpsertHour, tenantValue, key, liveCount, workingCount,
                            sh.CurSessions.Count, sh.CurMachines.Count, sh.CurRepos.Count);
"""

NEW = """                    // FAULT INJECTED - DO NOT COMMIT. The contract's forbidden shape: read the row through
                    // the change tracker, work the maximum out in memory, save an absolute value.
                    if (peakAdvanced)
                    {
                        var peakRow = ctx.ConcurrencyPeaks.FirstOrDefault(p => p.Tenant == tenantValue);
                        if (peakRow is null)
                        {
                            ctx.ConcurrencyPeaks.Add(new ConcurrencyPeakEntity
                            {
                                Tenant = tenantValue,
                                LiveMax = liveCount,
                                LiveMaxAtUtc = liveCount > 0 ? nowUtc : null,
                                WorkingMax = workingCount,
                                WorkingMaxAtUtc = workingCount > 0 ? nowUtc : null,
                            });
                        }
                        else
                        {
                            if (liveCount > peakRow.LiveMax) { peakRow.LiveMax = liveCount; peakRow.LiveMaxAtUtc = nowUtc; }
                            if (workingCount > peakRow.WorkingMax) { peakRow.WorkingMax = workingCount; peakRow.WorkingMaxAtUtc = nowUtc; }
                        }
                        ctx.SaveChanges();
                    }

                    if (hourAdvanced)
                    {
                        var hourRow = ctx.ConcurrencyHours.FirstOrDefault(h => h.Tenant == tenantValue && h.HourUtc == key);
                        if (hourRow is null)
                        {
                            ctx.ConcurrencyHours.Add(new ConcurrencyHourEntity
                            {
                                Tenant = tenantValue,
                                HourUtc = key,
                                MaxLive = liveCount,
                                MaxWorking = workingCount,
                                DistinctSessions = sh.CurSessions.Count,
                                DistinctMachines = sh.CurMachines.Count,
                                DistinctRepos = sh.CurRepos.Count,
                            });
                        }
                        else
                        {
                            if (liveCount > hourRow.MaxLive) hourRow.MaxLive = liveCount;
                            if (workingCount > hourRow.MaxWorking) hourRow.MaxWorking = workingCount;
                            if (sh.CurSessions.Count > hourRow.DistinctSessions) hourRow.DistinctSessions = sh.CurSessions.Count;
                            if (sh.CurMachines.Count > hourRow.DistinctMachines) hourRow.DistinctMachines = sh.CurMachines.Count;
                            if (sh.CurRepos.Count > hourRow.DistinctRepos) hourRow.DistinctRepos = sh.CurRepos.Count;
                        }
                        ctx.SaveChanges();
                    }
"""

text = io.open(PATH, encoding='utf-8').read()
if OLD not in text:
    print('FAILED: the upsert block was not found verbatim; the file has moved on. No change made.')
    sys.exit(1)
io.open(PATH, 'w', encoding='utf-8').write(text.replace(OLD, NEW))
print('Product mutated to change-tracked read-then-save. Restore with git checkout when done.')

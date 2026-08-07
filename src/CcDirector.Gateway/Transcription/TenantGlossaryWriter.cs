using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// THE ONLY PLACE A TENANT'S GLOSSARY IS WRITTEN, and the reason every write to it is atomic.
///
/// THE DEFECT THIS EXISTS TO CLOSE (found in review of issue #2484). Every writer used to do an
/// unguarded read-modify-write: <c>LoadFromDisk</c>, change the document in memory, <c>WriteToDisk</c>.
/// Nothing serialised those three steps, so a person's Cockpit save landing between another caller's read
/// and write was silently ERASED - including a wrong-spellings list they had just edited - and two
/// concurrent additions could lose one of the terms. The writers even shared one <c>&lt;path&gt;.tmp</c>
/// staging file, so two in flight at once could tear each other's bytes.
///
/// WHY THAT WAS A CORRECTNESS BUG AND NOT A TIDINESS ONE. The owner's ruling lets an agent add a word with
/// NO confirmation step, and it is justified by one sentence: the worst an agent can do is leave a stray
/// extra word, never lose a correction the person relies on. An add that can erase a concurrent human edit
/// makes that sentence FALSE - add-only would be true of the verb and false of the effect - so the whole
/// grant would rest on a premise the code did not honour. The guard and the handler decide WHAT may be
/// written; this class is what makes the result of writing it survivable.
///
/// THE INVARIANT: every outcome equals some SERIAL ordering of the writes that raced. A person's whole
/// document save may legitimately overwrite an agent's term (an explicit human save is authoritative, and
/// pruning is exactly what the person is for), and an agent's add may legitimately land on top of a save.
/// What may never happen is the torn middle - the agent's word kept while the person's curation is dropped -
/// because that is a half of each write, which is no ordering at all.
///
/// HOW. Two gates, because there are two ways to race:
///   * a per-tenant monitor, for two requests inside THIS Gateway process - the common case, and the cheap
///     one;
///   * an exclusive per-tenant LOCK FILE, for two Gateway PROCESSES writing the same glossary directory.
/// Both are held across the whole read-modify-write, which is the point - a lock taken only around the
/// write would still lose the update, because the loss happens between the read and the write.
///
/// EXACTLY WHAT THE FILE LOCK IS PROVEN TO DO, and nothing wider. It serialises two processes against a
/// LOCAL file system: <c>CrossProcessGlossaryLockTests</c> races a real second operating-system process
/// against this one, forty writes each, and without the file lock half of them vanish (80 terms expected,
/// 40 actual). That is measured, and it is the whole of the claim.
///
/// IT IS NOT PROVEN on the hosted deployment's NETWORK FILE SHARE. Whether the operating system's file
/// locking is honoured there is unknown and unreachable from this repository's test rig, so a Gateway
/// running two containers against one share is NOT covered by evidence here - only by the assumption that
/// the share honours the lock. That gap is tracked as its own issue rather than papered over. The reason
/// this paragraph exists at all: the in-process monitor makes single-process tests pass whether the file
/// lock works or not, so a claim about processes could sit here indefinitely with nothing checking it - as
/// this one did until review asked.
///
/// The critical section is deliberately SYNCHRONOUS end to end. An await inside it would let the
/// continuation resume on another thread while the monitor was held by the first, which is the standard way
/// a lock like this stops meaning anything.
/// </summary>
public static class TenantGlossaryWriter
{
    /// <summary>How long to keep trying for the cross-process lock before giving up. Generous, because the
    /// work inside it is a small file read and write - reaching this means another process is wedged, which
    /// is worth failing loudly over rather than writing anyway.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The in-process gate, one per tenant. Keyed by the tenant's own identifier rather than by
    /// path, so it cannot be defeated by two spellings of the same file.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Read this tenant's glossary, apply <paramref name="change"/>, and write the result - as ONE step that
    /// no other writer can interleave with. Returns the re-read document.
    /// </summary>
    /// <param name="tenant">The tenant whose glossary is being written. Required and valid.</param>
    /// <param name="change">Given the CURRENT document, returns the document to store. Called INSIDE the
    /// lock and possibly after a wait, so it must derive its result from the document it is handed and never
    /// from one the caller read earlier - reading outside and mutating inside is the very race this closes.</param>
    public static DictationDictionary Mutate(TenantId tenant, Func<DictationDictionary, DictationDictionary> change)
    {
        var path = TenantGlossary.PathFor(tenant);
        lock (Gates.GetOrAdd(tenant.Value, _ => new object()))
        {
            using var crossProcess = AcquireFileLock(path);
            var current = DictionaryLoader.LoadFromDisk(path);
            DictionaryLoader.WriteToDisk(path, change(current));
            return DictionaryLoader.LoadFromDisk(path);
        }
    }

    /// <summary>
    /// Replace this tenant's whole glossary - the Cockpit editor's save. It takes the SAME lock as
    /// <see cref="Mutate"/>, which is the half that is easy to miss: a replace needs no read of its own, but
    /// if it can land in the middle of somebody else's read-modify-write then that writer's stale copy is
    /// written back over it and the person's save vanishes. Serialising only the read-modify-writes against
    /// each other would leave exactly the human-edit-lost case the review found.
    /// </summary>
    public static DictationDictionary Replace(TenantId tenant, DictationDictionary replacement)
        => Mutate(tenant, _ => replacement);

    /// <summary>
    /// Change this tenant's glossary AND record which session added what, as ONE atomic pair - the whole
    /// point being that neither half can be observed without the other.
    ///
    /// WHY THIS EXISTS SEPARATELY FROM <see cref="Mutate"/> (second review finding on #2484). The provenance
    /// write used to sit in the endpoint, OUTSIDE this lock, appending unlocked and swallowing its own
    /// failure to return success. So two sessions could both land their terms while one session's trail
    /// entries vanished - and a word in the glossary with no trail entry is exactly the state the owner's
    /// grant was justified against, because it is un-traceable and therefore un-sweepable. Racing tests over
    /// glossary state could not see it: the glossary was perfectly correct in every one of those runs.
    ///
    /// THE ORDER IS THE GUARANTEE, and it is chosen for which failure is survivable. The trail is written
    /// BEFORE the glossary, so:
    ///   * trail write fails -> it throws, the glossary is never written, and the caller is told the add
    ///     failed. Nothing was added and nothing was recorded - consistent;
    ///   * glossary write fails -> it throws after a trail entry exists. That over-reports: the trail names
    ///     a word that is not in the dictionary, which a person reading it can see is absent and which
    ///     removes nothing;
    ///   * both succeed -> correct.
    /// The one state that is UNREACHABLE is the forbidden one - a term present with no record of who added
    /// it. Writing the glossary first would have made that the common failure instead.
    ///
    /// There is no swallowing catch anywhere on this path, deliberately. A silent provenance failure is a
    /// silent loss of the guarantee the owner traded the confirmation step for, and an add that returns
    /// success having lost it is worse than an add that fails.
    /// </summary>
    /// <param name="tenant">The tenant whose glossary is being written. Required and valid.</param>
    /// <param name="change">Given the CURRENT document, returns the document to store and the terms that
    /// were ACTUALLY new. Runs inside the lock.</param>
    /// <param name="sessionId">The calling session, or null when the caller is a person - a person's own
    /// edit records nothing, so there is nothing to keep atomic and the pair collapses to the glossary write.</param>
    /// <param name="directorId">The Director that session belongs to; may be empty.</param>
    /// <param name="nowUtc">The time to stamp, so a test does not depend on the clock.</param>
    public static DictationDictionary MutateAndRecord(
        TenantId tenant,
        Func<DictationDictionary, (DictationDictionary Updated, IReadOnlyList<string> Added)> change,
        string? sessionId,
        string directorId,
        DateTime nowUtc)
    {
        var path = TenantGlossary.PathFor(tenant);
        lock (Gates.GetOrAdd(tenant.Value, _ => new object()))
        {
            using var crossProcess = AcquireFileLock(path);
            var current = DictionaryLoader.LoadFromDisk(path);
            var (updated, added) = change(current);

            // Inside the lock, and BEFORE the glossary write. Any failure here propagates and the glossary
            // is left untouched.
            if (sessionId is not null)
                GlossaryAdditionLog.Record(tenant, sessionId, directorId, added, nowUtc);

            DictionaryLoader.WriteToDisk(path, updated);
            return DictionaryLoader.LoadFromDisk(path);
        }
    }

    /// <summary>
    /// Hold an exclusive OS lock for this tenant's glossary, so two Gateway PROCESSES over one file share
    /// cannot both be inside a read-modify-write. A dedicated <c>.lock</c> file rather than the glossary
    /// itself, because the write path replaces the glossary by rename - locking a file that is about to be
    /// atomically replaced locks the wrong inode the moment it succeeds.
    /// </summary>
    private static FileStream AcquireFileLock(string glossaryPath)
    {
        var lockPath = glossaryPath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lockPath))!);

        var deadline = DateTime.UtcNow + LockTimeout;
        var delayMilliseconds = 5;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // Somebody else is mid-write. Back off and try again - this is the lock doing its job, not
                // an error, so it is not logged per attempt.
                Thread.Sleep(delayMilliseconds);
                delayMilliseconds = Math.Min(delayMilliseconds * 2, 100);
            }
            catch (IOException ex)
            {
                // Past the deadline. Fail loudly rather than writing anyway: writing without the lock is
                // precisely the data loss this class exists to prevent, and a silent unlocked write would
                // reintroduce it at the worst possible moment.
                FileLog.Write($"[TenantGlossaryWriter] AcquireFileLock FAILED after {LockTimeout.TotalSeconds}s: {lockPath} - {ex.Message}");
                throw new IOException(
                    $"Could not take the dictation glossary lock at '{lockPath}' within {LockTimeout.TotalSeconds} seconds. " +
                    "Another process is holding it. The glossary was NOT written - writing without the lock " +
                    "would risk erasing a concurrent edit.", ex);
            }
        }
    }
}

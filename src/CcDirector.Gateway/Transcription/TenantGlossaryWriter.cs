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
///   * an exclusive per-tenant LOCK FILE, for two Gateway processes over one shared file share. That is not
///     hypothetical here: a hosted deploy has run two containers against one share, and a lock that lived
///     only in memory would be silently useless in exactly the window a deploy opens.
/// Both are held across the whole read-modify-write, which is the point - a lock taken only around the
/// write would still lose the update, because the loss happens between the read and the write.
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

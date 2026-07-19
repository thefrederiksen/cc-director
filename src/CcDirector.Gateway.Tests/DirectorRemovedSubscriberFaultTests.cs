using System;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A faulting <c>OnDirectorRemoved</c> subscriber must not escape the registry.
///
/// Every raise of that event happens on a thread-pool thread with NO enclosing try/catch - a
/// FileSystemWatcher callback (file deleted / renamed) or the stale-sweep timer. An exception thrown by a
/// subscriber there is UNHANDLED, so it does not merely fail the removal: it terminates the whole Gateway
/// process. One subscriber writes the tenant-scoped snooze store, and when that store's database was
/// unavailable the throw came up exactly this path and killed the process - observed as a test run that
/// ABORTED partway through while still reporting exit code 0, which is also why it is worth a guard rather
/// than a comment.
///
/// Revert-prove: change <c>DirectorRegistry.RaiseDirectorRemoved</c> back to a bare
/// <c>OnDirectorRemoved?.Invoke(...)</c> and <see cref="A_throwing_subscriber_does_not_escape_the_removal"/>
/// goes RED - the exception propagates out of the removal call instead of being logged and contained.
/// </summary>
public sealed class DirectorRemovedSubscriberFaultTests
{
    [Fact]
    public async Task A_throwing_subscriber_does_not_escape_the_removal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-dr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var registry = new DirectorRegistry(dir);
        try
        {
            var good = 0;
            // Two subscribers: the first throws (the snooze-store shape), the second must still be reached -
            // containing a fault must not silently drop the remaining subscribers along with it.
            registry.OnDirectorRemoved += _ => throw new InvalidOperationException("subscriber blew up");
            registry.OnDirectorRemoved += _ => good++;

            registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-fault",
                TailnetEndpoint = "http://127.0.0.1:1/",
                MachineName = "M",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            // The removal path the watcher and the sweeper both reach. It must return normally.
            var removed = registry.Remove("dir-fault");

            Assert.True(removed);
            Assert.Equal(1, good);
            await Task.CompletedTask;
        }
        finally
        {
            registry.Dispose();
            try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }
}

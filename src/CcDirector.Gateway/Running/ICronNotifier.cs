using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Delivers a cron run-complete notification (issue #622). The firing engine
/// (<see cref="CronEngine"/>) calls this once per fire AFTER it has decided the notification opts
/// in (per the job's <see cref="CronJobDto.NotifyOn"/> policy). The implementation rides the
/// existing fleet notification channel - the per-Director doorbell event ring (#330) observed at
/// <c>GET /directors/{id}/events</c> - and, when the job carries a webhook URL, also POSTs the same
/// payload there. Delivery is best-effort: a notification failure never affects the fire itself.
/// </summary>
public interface ICronNotifier
{
    /// <summary>
    /// Build a deep link to the session, rooted on the GATEWAY'S own address, or empty when there is
    /// no session. The firing engine calls this to stamp the link on the payload it hands back to
    /// <see cref="NotifyRunCompletedAsync"/>.
    ///
    /// It takes no Director: the link used to be rooted on the resolved Director's endpoint, and the
    /// remove-the-network-port mission deleted that endpoint, so every notification on a current
    /// fleet silently carried no link at all. Dropping the parameter rather than ignoring it is what
    /// stops the next implementation quietly reintroducing the dependency.
    /// </summary>
    string BuildSessionLink(string? sessionId);

    /// <summary>
    /// Deliver one run-complete notification for <paramref name="job"/> using
    /// <paramref name="payload"/>. <paramref name="directorId"/> is the Director the fire resolved
    /// to (empty when the fire never resolved one, e.g. a not-started failure) and decides which
    /// per-Director event ring the in-fleet notification files under. Never throws to the caller -
    /// the firing engine isolates the fire from any delivery failure.
    /// </summary>
    Task NotifyRunCompletedAsync(CronJobDto job, string directorId, CronRunCompletedPayload payload, CancellationToken ct);
}

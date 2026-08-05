using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace CcRecorder.Platforms.Android;

/// <summary>
/// Minimal foreground service whose only job is to keep the app process alive
/// (and the persistent notification visible) while recording, so Android does
/// not reclaim it when the screen locks or the app is backgrounded. The actual
/// capture + segment rotation runs in <see cref="AndroidAudioRecorder"/> in the
/// same process.
///
/// The service holds a partial wake lock for the whole recording. Without it,
/// Doze suspends the app's CPU once the screen has been off for a while (about
/// half an hour), the segment-roll timer stops firing, and Android then kills
/// the process - which is exactly how a 30-minute conference recording died at
/// a clean segment boundary while the speaker kept talking. The wake lock only
/// counts while the app is on the battery-optimization exemption list (Doze
/// ignores wake locks otherwise), so <c>MainPage</c> requests that exemption
/// when recording starts. Recording is unlimited by design: the lock has no
/// timeout and is released only when the recording is stopped.
/// </summary>
[Service(
    Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMicrophone)]
public sealed class RecorderForegroundService : Service
{
    public const string ChannelId = "cc_recorder_capture";
    private const int NotificationId = 4801;

    private PowerManager.WakeLock? _wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Sticky restart after a process death: there is no live capture, so a
        // "Recording in progress" notification would be a lie. Stop immediately.
        // The interrupted recording itself is recovered (and clearly marked as
        // interrupted) by the recorder's next upload pass.
        if (!AndroidAudioRecorder.CaptureLive)
        {
            // Kick the background upload worker so the orphaned recording is
            // recovered and uploaded now, not on the next app open. If the
            // enqueue fails the next app open still drains the queue.
            try { UploadScheduler.EnqueueNow(this); } catch { }
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        CreateChannel();

        // Tapping the notification opens the app; the chronometer counts up
        // live so a glance shows capture is still running, not just that a
        // notification was once posted.
        var launch = PackageManager?.GetLaunchIntentForPackage(PackageName ?? "");
        var tapToOpen = launch is null
            ? null
            : PendingIntent.GetActivity(this, 0, launch, PendingIntentFlags.Immutable);

        // Statement calls, not a fluent chain: the binding marks each SetX
        // return as nullable, which turns a chain into a wall of warnings.
        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetContentTitle("CC Recorder");
        builder.SetContentText("Recording in progress");
        builder.SetSmallIcon(global::Android.Resource.Drawable.PresenceAudioOnline);
        builder.SetOngoing(true);
        builder.SetShowWhen(true);
        builder.SetWhen(Java.Lang.JavaSystem.CurrentTimeMillis());
        builder.SetUsesChronometer(true);
        if (tapToOpen is not null) builder.SetContentIntent(tapToOpen);
        var notification = builder.Build()
            ?? throw new InvalidOperationException("Recording notification could not be built.");

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification,
                global::Android.Content.PM.ForegroundService.TypeMicrophone);
        else
            StartForeground(NotificationId, notification);

        AcquireWakeLock();

        // Sticky: if the process is killed, Android tries to restart the service.
        return StartCommandResult.Sticky;
    }

    private void AcquireWakeLock()
    {
        if (_wakeLock is not null) return;
        var pm = (PowerManager?)GetSystemService(PowerService)
            ?? throw new InvalidOperationException(
                "PowerManager unavailable; cannot keep the CPU awake for capture.");
        var wl = pm.NewWakeLock(WakeLockFlags.Partial, "CcRecorder:capture")
            ?? throw new InvalidOperationException(
                "Wake lock could not be created; cannot keep the CPU awake for capture.");
        wl.SetReferenceCounted(false);
        // No timeout on purpose: the recording has no time limit. Released in
        // OnDestroy, which runs when the recorder stops this service.
        wl.Acquire();
        _wakeLock = wl;
    }

    public override void OnDestroy()
    {
        if (_wakeLock is not null)
        {
            if (_wakeLock.IsHeld) _wakeLock.Release();
            _wakeLock = null;
        }
        base.OnDestroy();
    }

    private void CreateChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var mgr = (NotificationManager?)GetSystemService(NotificationService);
        if (mgr is null) return;
        if (mgr.GetNotificationChannel(ChannelId) is not null) return;
        var channel = new NotificationChannel(ChannelId, "Recording", NotificationImportance.Low)
        {
            Description = "Active audio recording",
        };
        mgr.CreateNotificationChannel(channel);
    }
}

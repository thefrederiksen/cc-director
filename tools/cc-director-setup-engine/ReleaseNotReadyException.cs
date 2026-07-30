namespace CcDirector.Setup.Engine;

/// <summary>
/// Raised when the release GitHub calls "latest" exists but is not yet USABLE: it carries no
/// release-manifest.json, so there is nothing to resolve versions or checksums against.
///
/// This is a DIFFERENT condition from a failed fetch, and conflating the two is what made it
/// invisible. It has one ordinary cause: a release becomes "latest" the moment it is published,
/// and its assets used to be attached minutes afterwards. Measured on v1.8.8 - published
/// 10:48:48Z, assets attached 10:54:11Z - a launcher checked at 10:54:05Z, six seconds before the
/// manifest existed, and reported "update check failed". The release was fine; the check was
/// simply early.
///
/// The workflow now attaches every asset to a DRAFT and publishes afterwards, so this window
/// should no longer exist. This type survives that fix on purpose: it is the difference between
/// "wait a few minutes, this will resolve itself" and "something is wrong", and a caller that
/// cannot tell them apart has to guess. Both guesses made here were wrong - the launcher reported
/// FAILED, and the Director's own updater (a separate code path, issue #1030) reported UP TO DATE.
/// </summary>
public sealed class ReleaseNotReadyException : Exception
{
    /// <summary>The tag of the release that is published but incomplete (for example "v1.8.8").</summary>
    public string Tag { get; }

    /// <summary>The asset that is missing. Always release-manifest.json today.</summary>
    public string MissingAsset { get; }

    public ReleaseNotReadyException(string tag, string missingAsset)
        : base($"Release {tag} is published but has no {missingAsset} yet; its assets are still being attached.")
    {
        Tag = tag;
        MissingAsset = missingAsset;
    }

    /// <summary>
    /// The plain-English, ASCII-only message a user-facing surface shows. It says what is happening
    /// and what happens next, so the state cannot be mistaken for either "up to date" or "broken".
    /// </summary>
    public string UserMessage() =>
        $"Version {Tag.TrimStart('v', 'V')} has just been published and its download files are still " +
        "being uploaded. Nothing is wrong - the update will be picked up automatically within a few minutes.";
}

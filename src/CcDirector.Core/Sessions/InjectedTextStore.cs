using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>Whose injected text is live for this machine.</summary>
public enum InjectedTextSource
{
    /// <summary>The text DevThrottle ships. Updates to it arrive with the application.</summary>
    Ours,

    /// <summary>The user's own text. It does not receive our updates - that is the trade they made.</summary>
    Yours,
}

/// <summary>
/// Owns the text DevThrottle injects into agents at launch: which version is live, where both live
/// on disk, and how the user's version is saved and withdrawn.
///
/// TWO FILES, BOTH ALWAYS PRESENT:
///   ours.txt  - the shipped default, rewritten from the application on every launch. It is here
///               even when the user is running their own text, so they can always read the current
///               default and adopt it. This is the owner's requirement that our updates are always
///               there, just not necessarily used.
///   yours.txt - the user's version. Exists only once they write one.
///
/// YOURS OR OURS, NEVER A MERGE. Deliberately: a three-way reconciliation of prose nobody asked for
/// is how you end up injecting a sentence neither party wrote.
///
/// ours.txt IS A COPY, NOT THE SOURCE. The shipped default's source of truth is
/// <see cref="FleetPreambleTemplate.Default"/>, in the application. The file exists so the text is
/// visible on disk and diffable; if it is edited by hand it is overwritten on the next launch. This
/// is what stops the Settings tab showing one default while sessions launch with another.
/// </summary>
public sealed class InjectedTextStore
{
    private readonly string _directory;
    private readonly Func<InjectedTextConfig> _readConfig;
    private readonly Action<bool> _writeUseYours;

    /// <summary>The store over the real per-user Director data directory and the real config.json.</summary>
    public InjectedTextStore() : this(CcStorage.InjectedText()) { }

    /// <summary>Testable constructor over an explicit directory, using the real config.json.</summary>
    public InjectedTextStore(string directory)
        : this(directory, InjectedTextConfig.Get, InjectedTextConfig.SetUseYours) { }

    /// <summary>
    /// Testable constructor over an explicit directory AND an explicit choice of whose text is live.
    ///
    /// The choice is a dependency rather than a global read because it is not incidental: a test that
    /// reaches into the real config.json passes or fails depending on how the developer running it has
    /// their own machine set up, which is not a test, it is a coin toss. It also made unrelated tests -
    /// the Pi preamble writer's - quietly depend on this setting the moment the writer started honouring
    /// it.
    /// </summary>
    public InjectedTextStore(string directory, Func<InjectedTextConfig> readConfig, Action<bool> writeUseYours)
    {
        _directory = directory;
        _readConfig = readConfig;
        _writeUseYours = writeUseYours;
    }

    /// <summary>
    /// A store whose text is definitely the DevThrottle default, over a throwaway directory. For tests
    /// that need a preamble rendered and do not care whose it is - they must not become sensitive to
    /// the developer's own choice.
    /// </summary>
    public static InjectedTextStore AlwaysOurs(string directory)
        => new(directory, () => new InjectedTextConfig(UseYours: false), _ => { });

    /// <summary>Where the shipped default is written for the user to read.</summary>
    public string OursPath => Path.Combine(_directory, "ours.txt");

    /// <summary>Where the user's own text lives, if they have written one.</summary>
    public string YoursPath => Path.Combine(_directory, "yours.txt");

    /// <summary>The text DevThrottle ships, straight from the application - never from disk.</summary>
    public static string Ours => FleetPreambleTemplate.Default;

    /// <summary>True when the user has written their own text, whether or not it is live.</summary>
    public bool HasYours => File.Exists(YoursPath);

    /// <summary>
    /// Write the shipped default to disk so it is always readable, even when the user's text is live.
    /// Called at launch. Best-effort: failing to write this COPY must never stop a session starting,
    /// because the real default is in the application and rendering does not depend on this file.
    /// </summary>
    public void EnsureOursWritten()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = OursPath;

            // Only write when it differs, so the file's timestamp means "our text changed" rather
            // than "the application started".
            if (File.Exists(path) && File.ReadAllText(path) == Ours)
                return;

            File.WriteAllText(path, Ours);
            FileLog.Write($"[InjectedTextStore] wrote the shipped injected text to {path}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[InjectedTextStore] EnsureOursWritten failed for '{_directory}': {ex.Message}");
        }
    }

    /// <summary>
    /// Which version is live. This is the user's CHOICE, and nothing else - deliberately NOT
    /// "their choice, if their file happens to be readable".
    ///
    /// An earlier version of this method returned Ours when the user had chosen theirs but the file
    /// was missing. That reads like sensible defensiveness and is the exact bug this whole feature
    /// exists to prevent: someone declines our text, their file later goes missing, and we silently
    /// resume injecting our text - including our policy - into their agents, with the Settings tab
    /// still saying theirs is live. A missing file is now a loud failure in
    /// <see cref="ActiveTemplate"/>, never a quiet reversal of their decision.
    /// </summary>
    public InjectedTextSource ActiveSource()
        => _readConfig().UseYours ? InjectedTextSource.Yours : InjectedTextSource.Ours;

    /// <summary>
    /// The template that will actually be injected into the next session.
    /// </summary>
    /// <exception cref="InjectedTextUnavailableException">
    /// The user's text is live but cannot be read. This does NOT fall back to ours, and the reason is
    /// the whole point of the feature: the user declined our text, so injecting it anyway because we
    /// hit a file error would be the exact thing they opted out of - silently, and with our policy in
    /// it. The caller fails loudly instead. See the callers for what a session does with that.
    /// </exception>
    public string ActiveTemplate()
    {
        if (ActiveSource() == InjectedTextSource.Ours)
            return Ours;

        try
        {
            // File.ReadAllText on a missing path throws, which is the behaviour wanted here: chosen
            // but absent is a failure, not a reason to reach for our text.
            return File.ReadAllText(YoursPath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[InjectedTextStore] ActiveTemplate FAILED reading '{YoursPath}': {ex.Message}");
            throw new InjectedTextUnavailableException(
                $"Your injected text could not be read from {YoursPath} ({ex.Message}). " +
                "DevThrottle has not substituted its own text, because you turned that off. " +
                "Fix or restore the file, or switch back to the DevThrottle version in " +
                "Settings, Injected text.", ex);
        }
    }

    /// <summary>Read the user's text, or null when they have not written one.</summary>
    public string? ReadYours() => HasYours ? File.ReadAllText(YoursPath) : null;

    /// <summary>
    /// Save the user's text and make it live. Rejects a template that cannot render, so the failure
    /// lands on the person editing it rather than on seven agents at launch.
    /// </summary>
    /// <exception cref="FleetPreambleTemplateException">The text is not a renderable template.</exception>
    public void SaveYours(string text)
    {
        var problem = FleetPreambleRenderer.Validate(text);
        if (problem is not null)
            throw new FleetPreambleTemplateException(problem);

        Directory.CreateDirectory(_directory);
        File.WriteAllText(YoursPath, text);
        _writeUseYours(true);
        FileLog.Write($"[InjectedTextStore] saved the user's injected text to {YoursPath}; it is now live");
    }

    /// <summary>
    /// Go back to the DevThrottle version. The user's text is KEPT on disk, not deleted - switching
    /// back to ours is a reversible choice, and silently destroying the paragraphs they wrote because
    /// they wanted to compare against the default would be unforgivable.
    /// </summary>
    public void UseOurs()
    {
        _writeUseYours(false);
        FileLog.Write("[InjectedTextStore] the DevThrottle injected text is now live; the user's version is kept");
    }

    /// <summary>Make the user's text live again, if they have one.</summary>
    public void UseYours()
    {
        if (!HasYours)
            throw new InvalidOperationException(
                "There is no custom injected text to switch to. Write one first.");

        _writeUseYours(true);
        FileLog.Write("[InjectedTextStore] the user's injected text is now live");
    }
}

/// <summary>
/// Thrown when the user's injected text is live but unreadable. Deliberately NOT recoverable by
/// substituting the DevThrottle default - see <see cref="InjectedTextStore.ActiveTemplate"/>.
/// </summary>
public class InjectedTextUnavailableException : Exception
{
    public InjectedTextUnavailableException(string message, Exception inner) : base(message, inner) { }
}

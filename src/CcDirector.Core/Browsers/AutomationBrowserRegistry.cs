using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Browsers;

/// <summary>
/// The persistent store of DevThrottle's automation browsers: the single JSON list at
/// <c>&lt;appdata&gt;/browsers/registry.json</c> plus the rules that keep it consistent (unique names,
/// unique ports) and the allocation of a fresh debug port and on-disk directory for a new browser.
///
/// This is the DATA layer. Behavior that touches a live browser process (launch, is-it-up, sign-in,
/// stop) lives in <see cref="AutomationBrowserService"/>. Kept static and file-backed to match the
/// sibling <see cref="BrowserDefaultStore"/>; a process-wide lock serializes the read-modify-write so
/// the rail UI and a Control-API call in the same Director cannot clobber each other's edits.
///
/// No-fallback rule (CLAUDE.md / CodingStyle): a malformed registry.json THROWS rather than being
/// silently reset - we never destroy the user's list to hide a parse problem. Lookups that miss THROW
/// with a message naming the browser that was asked for.
/// </summary>
public static class AutomationBrowserRegistry
{
    /// <summary>The first debug port we hand out. Chosen well above the Director's own range
    /// (the listener era's 7879-7898, kept clear so old installs mid-upgrade cannot collide) and above the personal harness's historical 9224, so a
    /// DevThrottle browser never collides with either.</summary>
    public const int PortRangeStart = 9310;

    /// <summary>Upper bound for automation-browser debug ports. 190 slots is far more browsers than a
    /// human would ever create; exhausting it THROWS rather than silently reusing a live port.</summary>
    public const int PortRangeEnd = 9499;

    private static readonly object WriteLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The browsers root directory (<c>&lt;appdata&gt;/browsers</c>). Created on demand.</summary>
    public static string RootDirectory() => CcStorage.Browsers();

    /// <summary>Absolute path of the registry file.</summary>
    public static string RegistryPath() => Path.Combine(RootDirectory(), "registry.json");

    /// <summary>
    /// Read every registered browser. A missing file yields an empty list (a machine with no browsers
    /// yet is not an error). A present-but-unparseable file THROWS.
    /// </summary>
    public static IReadOnlyList<AutomationBrowser> Load()
    {
        var path = RegistryPath();
        if (!File.Exists(path))
        {
            FileLog.Write("[AutomationBrowserRegistry] Load: no registry.json yet, returning empty list");
            return Array.Empty<AutomationBrowser>();
        }

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<AutomationBrowser>();

        var list = JsonSerializer.Deserialize<List<AutomationBrowser>>(json, JsonOptions)
            ?? throw new InvalidOperationException($"registry.json parsed to null: {path}");
        FileLog.Write($"[AutomationBrowserRegistry] Load: browsers={list.Count}");
        return list;
    }

    /// <summary>Returns the browser whose id or name matches <paramref name="idOrName"/> (id first,
    /// then case-insensitive name), or null when none match. Null is not an error here - callers that
    /// require the browser should use <see cref="Get"/>.</summary>
    public static AutomationBrowser? Find(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;
        var all = Load();
        return all.FirstOrDefault(b => string.Equals(b.Id, idOrName, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(b => string.Equals(b.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns the browser matching <paramref name="idOrName"/> or THROWS naming what was asked
    /// for (no silent null).</summary>
    public static AutomationBrowser Get(string idOrName)
        => Find(idOrName)
           ?? throw new KeyNotFoundException($"No automation browser named \"{idOrName}\". Run 'browser list' to see the browsers on this machine.");

    /// <summary>
    /// Register a brand-new browser: validates the name is non-empty and unused, mints a unique slug id,
    /// allocates a free debug port and a dedicated user-data directory under the browsers root, persists
    /// the entry, and returns it. Does NOT launch anything - that is <see cref="AutomationBrowserService"/>.
    /// </summary>
    /// <param name="name">Human-facing label; must be non-empty and not already used (case-insensitive).</param>
    /// <param name="kind">Which browser this drives.</param>
    /// <param name="isPortFree">Predicate used to probe candidate ports; defaults to a real loopback bind.
    /// Injectable so port allocation is unit-testable without opening sockets.</param>
    public static AutomationBrowser Create(string name, BrowserKind kind, Func<int, bool>? isPortFree = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A browser name is required.", nameof(name));

        lock (WriteLock)
        {
            var list = Load().ToList();

            if (list.Any(b => string.Equals(b.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A browser named \"{name.Trim()}\" already exists. Pick a different name or remove the existing one.");

            var id = UniqueSlug(name, list);
            var port = AllocatePort(list, isPortFree ?? IsPortFreeToBind);
            var dir = Path.Combine(RootDirectory(), id);

            var browser = new AutomationBrowser(
                Id: id,
                Name: name.Trim(),
                Kind: kind,
                UserDataDir: dir,
                Port: port,
                CreatedUtc: DateTime.UtcNow,
                LastSignedInUtc: null);

            list.Add(browser);
            SaveList(list);
            FileLog.Write($"[AutomationBrowserRegistry] Create: id={id}, kind={kind}, port={port}, dir={dir}");
            return browser;
        }
    }

    /// <summary>Replace the stored entry that has the same <see cref="AutomationBrowser.Id"/> as
    /// <paramref name="updated"/>. THROWS if no such id exists.</summary>
    public static void Update(AutomationBrowser updated)
    {
        if (updated is null) throw new ArgumentNullException(nameof(updated));

        lock (WriteLock)
        {
            var list = Load().ToList();
            var index = list.FindIndex(b => string.Equals(b.Id, updated.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                throw new KeyNotFoundException($"No automation browser with id \"{updated.Id}\" to update.");
            list[index] = updated;
            SaveList(list);
            FileLog.Write($"[AutomationBrowserRegistry] Update: id={updated.Id}");
        }
    }

    /// <summary>Rename a browser's human-facing label (the id/port/directory are unchanged). THROWS if the
    /// new name is empty or already used by a DIFFERENT browser.</summary>
    public static AutomationBrowser Rename(string idOrName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("A new name is required.", nameof(newName));

        lock (WriteLock)
        {
            var list = Load().ToList();
            var index = IndexOfOrThrow(list, idOrName);
            var target = list[index];

            if (list.Any(b => !string.Equals(b.Id, target.Id, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(b.Name, newName.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Another browser is already named \"{newName.Trim()}\".");

            var renamed = target with { Name = newName.Trim() };
            list[index] = renamed;
            SaveList(list);
            FileLog.Write($"[AutomationBrowserRegistry] Rename: id={target.Id}, newName={renamed.Name}");
            return renamed;
        }
    }

    /// <summary>Remove a browser's entry from the registry (the on-disk directory is deleted by
    /// <see cref="AutomationBrowserService.Remove"/>, which also stops the process first). THROWS if the
    /// browser is not found.</summary>
    public static AutomationBrowser RemoveEntry(string idOrName)
    {
        lock (WriteLock)
        {
            var list = Load().ToList();
            var index = IndexOfOrThrow(list, idOrName);
            var removed = list[index];
            list.RemoveAt(index);
            SaveList(list);
            FileLog.Write($"[AutomationBrowserRegistry] RemoveEntry: id={removed.Id}");
            return removed;
        }
    }

    /// <summary>Mark that the human has signed this browser in, now. Returns the updated entry.</summary>
    public static AutomationBrowser MarkSignedIn(string idOrName)
    {
        lock (WriteLock)
        {
            var list = Load().ToList();
            var index = IndexOfOrThrow(list, idOrName);
            var updated = list[index] with { LastSignedInUtc = DateTime.UtcNow };
            list[index] = updated;
            SaveList(list);
            FileLog.Write($"[AutomationBrowserRegistry] MarkSignedIn: id={updated.Id}");
            return updated;
        }
    }

    /// <summary>
    /// The <c>BU_NAME</c> / <c>BU_CDP_URL</c> pair the harness attaches with. Pure (no process contact),
    /// so it is unit-testable: <c>BU_NAME</c> is the id and <c>BU_CDP_URL</c> is loopback + the fixed port.
    /// </summary>
    public static AttachInfo AttachInfoFor(AutomationBrowser browser)
    {
        if (browser is null) throw new ArgumentNullException(nameof(browser));
        return new AttachInfo(browser.Id, $"http://127.0.0.1:{browser.Port}");
    }

    // --- internals ---

    private static int IndexOfOrThrow(List<AutomationBrowser> list, string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
            throw new ArgumentException("A browser id or name is required.", nameof(idOrName));

        var index = list.FindIndex(b => string.Equals(b.Id, idOrName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            index = list.FindIndex(b => string.Equals(b.Name, idOrName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            throw new KeyNotFoundException($"No automation browser named \"{idOrName}\". Run 'browser list' to see the browsers on this machine.");
        return index;
    }

    private static void SaveList(List<AutomationBrowser> list)
    {
        Directory.CreateDirectory(RootDirectory());
        var path = RegistryPath();
        var json = JsonSerializer.Serialize(list, JsonOptions);

        // Atomic replace: write a temp file then move over the target so a crash mid-write can never
        // leave a half-written registry.json (which Load would then throw on).
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// First free debug port at or above <see cref="PortRangeStart"/> that is (a) not already claimed by
    /// a browser in <paramref name="existing"/> and (b) currently bindable per <paramref name="isPortFree"/>.
    /// Exposed internally so allocation can be unit-tested with a fake probe. THROWS when the range is exhausted.
    /// </summary>
    internal static int AllocatePort(IReadOnlyList<AutomationBrowser> existing, Func<int, bool> isPortFree)
    {
        var claimed = existing.Select(b => b.Port).ToHashSet();
        for (var p = PortRangeStart; p <= PortRangeEnd; p++)
        {
            if (claimed.Contains(p)) continue;
            if (!isPortFree(p)) continue;
            return p;
        }
        throw new InvalidOperationException(
            $"No free automation-browser port in range {PortRangeStart}..{PortRangeEnd}. Remove an unused browser to free one.");
    }

    /// <summary>
    /// Turn a human name into a stable, unique, filesystem- and BU_NAME-safe slug: lowercase, spaces and
    /// punctuation collapsed to single hyphens, edges trimmed. Collisions with an existing id get a
    /// numeric suffix ("-2", "-3", ...). Exposed internally for unit tests.
    /// </summary>
    internal static string UniqueSlug(string name, IReadOnlyList<AutomationBrowser> existing)
    {
        var basis = Slugify(name);
        if (basis.Length == 0) basis = "browser";

        var taken = existing.Select(b => b.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(basis)) return basis;

        for (var n = 2; ; n++)
        {
            var candidate = $"{basis}-{n}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    internal static string Slugify(string name)
    {
        var sb = new StringBuilder(name.Length);
        var lastWasHyphen = false;
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                sb.Append('-');
                lastWasHyphen = true;
            }
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>Real port probe: true when 127.0.0.1:<paramref name="port"/> can be bound right now
    /// (nobody is listening). Chrome binds the debug port on loopback, so loopback is the right scope.</summary>
    private static bool IsPortFreeToBind(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

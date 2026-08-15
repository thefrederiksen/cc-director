using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// WHICH SESSION ADDED WHICH WORD to a tenant's dictation glossary. Append-only, one line of JSON per term,
/// beside that tenant's own <c>dictionary.yaml</c> (issue #2484).
///
/// WHY IT EXISTS. The owner ruled that an agent may add a word with NO confirmation step, because being
/// asked every time is worse than the occasional bad entry. Remove the confirmation and nothing catches a
/// bad entry AT WRITE TIME, so the only way a bad one is ever cleaned up is if the person can see where it
/// came from: one stray word is a shrug, and "some agent, some time last week, added forty" is a sweep. That
/// is the trade the ruling asks for and this file is the half that makes it payable.
///
/// WHY IT IS A SIDECAR AND NOT A FIELD IN THE GLOSSARY. The Cockpit dictionary editor saves the WHOLE
/// document (<c>PUT /ingest/dictionary</c>), so provenance stored inside <c>dictionary.yaml</c> would be
/// erased by the next hand-edit - it would be a record that vanished exactly when someone started curating,
/// which is the moment it is wanted. A separate file survives every glossary write, and it survives the term
/// itself being deleted, so the trail outlives what it describes. It also keeps the glossary schema
/// untouched, which matters because the desktop, the phone and the Cockpit all read that schema.
///
/// A FAILURE TO WRITE IT FAILS THE ADDITION. This file originally said the opposite - that losing the note
/// was a smaller harm than losing the word - and that was wrong. The owner traded away the confirmation step
/// FOR this record, so a word added with nothing saying who added it cannot be traced and cannot be swept,
/// which is the state the whole grant was justified against. <see cref="TenantGlossaryWriter.MutateAndRecord"/>
/// therefore writes this INSIDE the glossary's per-tenant lock and BEFORE the glossary itself, so a throw
/// here leaves nothing added and nothing recorded rather than a word with no provenance.
/// </summary>
public static class GlossaryAdditionLog
{
    /// <summary>The file name that sits beside a tenant's <c>dictionary.yaml</c>.</summary>
    internal const string FileName = "dictionary-additions.jsonl";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>This tenant's addition trail - always the directory its glossary lives in, so a per-tenant
    /// glossary carries a per-tenant trail and one account's additions are never written into another's.</summary>
    public static string PathFor(TenantId tenant)
        => Path.Combine(Path.GetDirectoryName(TenantGlossary.PathFor(tenant))!, FileName);

    /// <summary>
    /// Note that <paramref name="sessionId"/> added <paramref name="terms"/> to this tenant's glossary.
    /// Call it with the terms that were ACTUALLY new - a term already present was not added, and recording
    /// it would put the reader onto a session that changed nothing.
    /// </summary>
    /// <param name="tenant">The tenant whose glossary was written. Required and valid.</param>
    /// <param name="sessionId">The calling session, from the credential the auth gate resolved.</param>
    /// <param name="directorId">The Director that session belongs to; may be empty.</param>
    /// <param name="terms">The terms that were newly added. An empty list writes nothing.</param>
    /// <param name="nowUtc">The time to stamp, so a test does not depend on the clock.</param>
    public static void Record(
        TenantId tenant,
        string sessionId,
        string directorId,
        IReadOnlyList<string> terms,
        DateTime nowUtc)
    {
        if (terms.Count == 0) return;

        var path = PathFor(tenant);
        var builder = new StringBuilder();
        foreach (var term in terms)
            builder.Append(JsonSerializer.Serialize(new GlossaryAddition(
                AddedAtUtc: nowUtc,
                Term: term,
                SessionId: sessionId,
                DirectorId: directorId))).Append('\n');

        // NO CATCH HERE, AND THAT IS THE POINT. This used to swallow its own failure and let the addition
        // report success, on the reasoning that losing the note was a smaller harm than losing the word.
        // That reasoning was wrong (second review finding on #2484): the owner traded away the confirmation
        // step FOR this record, so a word added with no trace of who added it is un-sweepable, and an add
        // that returns success having silently lost the guarantee is worse than an add that fails. The
        // caller writes this INSIDE the glossary lock and BEFORE the glossary itself, so a throw here leaves
        // nothing added and nothing recorded.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, builder.ToString());
        FileLog.Write($"[GlossaryAdditionLog] Record: tenant={tenant.Value}, session={sessionId}, terms={terms.Count}");
    }

    /// <summary>
    /// Read this tenant's addition trail, oldest first. An unreadable or malformed line is skipped rather
    /// than throwing: this is a trail, and one corrupt line must not hide the rest of it.
    /// </summary>
    public static IReadOnlyList<GlossaryAddition> Read(TenantId tenant)
    {
        var path = PathFor(tenant);
        if (!File.Exists(path)) return Array.Empty<GlossaryAddition>();

        var entries = new List<GlossaryAddition>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<GlossaryAddition>(line, Json) is { } entry)
                    entries.Add(entry);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[GlossaryAdditionLog] skipping unreadable line: {ex.Message}");
            }
        }
        return entries;
    }
}

/// <summary>One word, and who put it there.</summary>
/// <param name="AddedAtUtc">When the term was added.</param>
/// <param name="Term">The term as it went into the glossary.</param>
/// <param name="SessionId">The session whose key made the call.</param>
/// <param name="DirectorId">The Director that session belongs to; empty when unknown.</param>
public sealed record GlossaryAddition(
    DateTime AddedAtUtc,
    string Term,
    string SessionId,
    string DirectorId);

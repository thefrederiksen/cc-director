using System;
using System.IO;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// devthrottle_internal#1176: the Director's user-editable display name travels in the stream Hello and is
/// stored on the registry entry, so the cockpit can finally tell three Directors on one machine apart.
/// These tests pin the three registry-side rules the feature depends on:
///
///  1. A reported name is STORED and SERVED (ListDirectors carries it to GET /directors).
///  2. The empty-field merge guard applies to it: a Hello with a blank name - an older Director build, or
///     an instance that was never named - is "no statement", never an instruction to erase a stored name.
///     Without this, one re-connect from a pre-name build would blank every name in the fleet.
///  3. The name is CLIENT-WRITTEN and therefore sanitized at the single point every stream registration
///     passes through: control characters stripped (a name must never carry escape sequences into a log
///     line or the cockpit), length clamped rather than rejected.
///
/// Driven at the registry directly, like <see cref="DirectorRegistryTenantKeyTests"/> - no HTTP, no tunnel -
/// so the result depends on nothing but the registration calls made here.
/// </summary>
public sealed class DirectorDisplayNameTests : IDisposable
{
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-ddn-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;

    public DirectorDisplayNameTests()
    {
        Directory.CreateDirectory(_instancesDir);
        // Constructed but never Start()ed: no file watcher, no sweeper - only these registrations exist.
        _registry = new DirectorRegistry(_instancesDir);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    private DirectorDtoLike Register(string displayName)
    {
        var dto = _registry.RegisterFromStream(
            "dir-1", "SOREN_NORTH", "soren", "1.9.1", pid: 1234, startedAt: DateTime.UtcNow,
            TenantId.Local, displayName);
        return new DirectorDtoLike(dto.DirectorId, dto.DisplayName);
    }

    private string ServedDisplayName()
    {
        var served = _registry.ListDirectors(TenantId.Local).Single(d => d.DirectorId == "dir-1");
        return served.DisplayName;
    }

    private readonly record struct DirectorDtoLike(string DirectorId, string DisplayName);

    [Fact]
    public void A_reported_name_is_stored_and_served()
    {
        Register("SOREN_NORTH_SLOT_2");
        Assert.Equal("SOREN_NORTH_SLOT_2", ServedDisplayName());
    }

    [Fact]
    public void A_blank_name_does_not_erase_a_stored_one()
    {
        // Positive control first: the name is really there before the old build says Hello. Without it,
        // "still named" would also hold if the name had never been stored at all.
        Register("SOREN_NORTH_SLOT_2");
        Assert.Equal("SOREN_NORTH_SLOT_2", ServedDisplayName());

        // An older Director build re-registers with no name field (deserializes to ""). No statement.
        Register("");
        Assert.Equal("SOREN_NORTH_SLOT_2", ServedDisplayName());
    }

    [Fact]
    public void A_rename_replaces_the_stored_name_on_the_next_hello()
    {
        Register("OLD_NAME");
        Register("NEW_NAME");
        Assert.Equal("NEW_NAME", ServedDisplayName());
    }

    [Fact]
    public void A_never_named_director_serves_an_empty_name()
    {
        Register("");
        Assert.Equal("", ServedDisplayName());
    }

    [Fact]
    public void Control_characters_are_stripped_from_a_client_written_name()
    {
        // A hostile or corrupted client must not be able to push escape sequences or newlines through the
        // registry into the cockpit or a log line.
        Register("evil\x1b[31mname\r\nline");
        Assert.Equal("evil[31mnameline", ServedDisplayName());
    }

    [Fact]
    public void An_overlong_name_is_clamped_not_rejected()
    {
        var longName = new string('x', DirectorRegistry.MaxDisplayNameLength + 25);
        Register(longName);
        Assert.Equal(new string('x', DirectorRegistry.MaxDisplayNameLength), ServedDisplayName());
    }

    [Fact]
    public void Whitespace_is_trimmed_and_a_whitespace_only_name_is_no_statement()
    {
        Register("  padded name  ");
        Assert.Equal("padded name", ServedDisplayName());

        // Whitespace-only is the same "no statement" as blank: the trimmed-away padding must not erase.
        Register("   ");
        Assert.Equal("padded name", ServedDisplayName());
    }
}

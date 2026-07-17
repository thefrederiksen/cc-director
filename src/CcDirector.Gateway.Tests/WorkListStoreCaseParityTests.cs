using CcDirector.Gateway;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the store's work-list-name case-insensitivity is EXACTLY <see cref="StringComparer.OrdinalIgnoreCase"/>
/// - the behaviour of the legacy <c>Dictionary(OrdinalIgnoreCase)</c> - across the full Unicode range. This is
/// the exact-parity guard for the migration onto the EF backbone, and it pins the case a stored
/// <c>ToUpperInvariant</c> fold column would have got WRONG: U+017F (LATIN SMALL LETTER LONG S) is DISTINCT
/// from 'S' and 's' under OrdinalIgnoreCase (ToUpperInvariant folds the long s onto 'S'), so a "Sx" list and
/// the long-s variant both exist, matching the old store. ASCII and accented-Latin case variants collide.
///
/// The store enforces uniqueness in code via OrdinalIgnoreCase, so this test also guards against a future
/// change back to a database fold column silently reintroducing the divergence. Source is kept ASCII by
/// building the characters from code points.
/// </summary>
public sealed class WorkListStoreCaseParityTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();

    private WorkListStore NewStore() => new(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));

    public void Dispose() => _h.Dispose();

    private static readonly string[] Samples =
    {
        "a", "A", "b", "S", "s",
        ((char)0x017f).ToString(),          // LATIN SMALL LETTER LONG S (ToUpperInvariant -> 'S')
        ((char)0x00e9).ToString(),          // LATIN SMALL LETTER E WITH ACUTE
        ((char)0x00c9).ToString(),          // LATIN CAPITAL LETTER E WITH ACUTE
        char.ConvertFromUtf32(0x10D50),     // astral code point
        char.ConvertFromUtf32(0x10D70),     // astral code point (a case pair under full culture mapping)
    };

    [Fact]
    public void StoreNameCollision_ExactlyMatchesOrdinalIgnoreCase_ForEveryPair()
    {
        var store = NewStore();
        var pair = 0;
        for (var i = 0; i < Samples.Length; i++)
        {
            for (var j = 0; j < Samples.Length; j++)
            {
                if (i == j) continue;

                var prefix = "L" + pair++ + "_";     // unique per pair, ASCII and case-stable
                var na = prefix + Samples[i];
                var nb = prefix + Samples[j];

                Assert.True(store.Create(na));        // first with this prefix always succeeds
                var storeCollides = !store.Create(nb); // second collides (returns false) iff it maps to na
                var ordinalIgnoreCaseEqual = StringComparer.OrdinalIgnoreCase.Equals(na, nb);

                Assert.True(storeCollides == ordinalIgnoreCaseEqual,
                    $"store collision != OrdinalIgnoreCase for [{Describe(Samples[i])}] vs [{Describe(Samples[j])}]: " +
                    $"storeCollides={storeCollides}, OrdinalIgnoreCase-equal={ordinalIgnoreCaseEqual}");
            }
        }
    }

    [Fact]
    public void LongS_StaysDistinctFromAsciiS_ButAsciiSCaseVariantsCollide()
    {
        var store = NewStore();
        var longS = ((char)0x017f).ToString(); // U+017F

        Assert.True(store.Create("S" + "x"));    // an ASCII 'S' name
        Assert.True(store.Create(longS + "x"));  // the long-s variant: OrdinalIgnoreCase keeps it DISTINCT
        Assert.False(store.Create("s" + "x"));   // ASCII 's' DOES collide with 'S'
        Assert.Equal(2, store.ListAll().Count);  // exactly two rows: {Sx | its long-s twin}, never merged
    }

    [Fact]
    public void AsciiAndAccentedLatin_CaseVariants_Collide()
    {
        var store = NewStore();
        Assert.True(store.Create("backlog"));
        Assert.False(store.Create("BACKLOG"));   // ASCII case variant collides

        var lower = "caf" + (char)0x00e9;        // cafe + lowercase acute-e
        var upper = "CAF" + (char)0x00c9;        // CAFE + uppercase acute-E
        Assert.True(store.Create(lower));
        Assert.False(store.Create(upper));       // accented-Latin case variant collides
    }

    /// <summary>Render a string as its code points (U+XXXX ...) so a failure message is readable and ASCII.</summary>
    private static string Describe(string s)
        => string.Join(" ", s.EnumerateRunes().Select(r => $"U+{r.Value:X4}"));
}

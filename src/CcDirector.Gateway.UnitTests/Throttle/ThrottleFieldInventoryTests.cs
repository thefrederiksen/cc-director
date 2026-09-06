using System.Collections;
using System.Reflection;
using System.Text.Json;
using CcDirector.Gateway.Throttle;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Throttle;

/// <summary>
/// THE FIELD INVENTORY OF THE SHARED ANSWER (final inspection finding F-08). Every field the library serves
/// under <c>throttle</c> is written down once, in <c>tools/throttle-conformance/contract/field-inventory.json</c>,
/// with the real consumers that read it. This test holds the DTO to that file: the set of JSON paths the
/// serializer would emit for <see cref="ThrottleFigureDto"/> must be exactly the inventory's set. A field added
/// to the DTO without being inventoried is a red here; so is an inventoried field the DTO no longer has. The
/// browser and the report each hold themselves to the same file from their side, so a field can no longer
/// exist on the wire without a named reader and a test through that reader's real boundary.
/// </summary>
public sealed class ThrottleFieldInventoryTests
{
    private static string ContractDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tools", "throttle-conformance", "contract");
            if (File.Exists(Path.Combine(candidate, "field-inventory.json"))) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("tools/throttle-conformance/contract/field-inventory.json was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Every JSON path the serializer emits for a type, in the inventory's spelling: camelCase, a
    /// dot per nesting, and "[]" for a list. A list of scalars is one path ending in "[]".</summary>
    private static void Paths(Type type, string prefix, SortedSet<string> into)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            var path = prefix.Length == 0 ? name : prefix + "." + name;
            var t = p.PropertyType;
            var underlying = Nullable.GetUnderlyingType(t) ?? t;
            if (underlying != typeof(string) && typeof(IEnumerable).IsAssignableFrom(underlying))
            {
                var item = underlying.IsGenericType ? underlying.GetGenericArguments()[0] : typeof(object);
                if (IsScalar(item)) into.Add(path + "[]");
                else Paths(item, path + "[]", into);
            }
            else if (IsScalar(underlying))
            {
                into.Add(path);
            }
            else
            {
                Paths(underlying, path, into);
            }
        }
    }

    private static bool IsScalar(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(DateTime) || t == typeof(decimal);

    [Fact]
    public void TheDtoServesExactlyTheInventoriedFields_AndEveryFieldNamesAtLeastOneRealReader()
    {
        var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractDir(), "field-inventory.json")))
            .RootElement.GetProperty("fields");
        var inventoried = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var field in inventory.EnumerateObject())
        {
            inventoried.Add(field.Name);
            var readers = field.Value.EnumerateArray().Select(r => r.GetString()).ToArray();
            Assert.NotEmpty(readers);
            Assert.All(readers, r => Assert.Contains(r, new[] { "browser", "report" }));
        }

        var served = new SortedSet<string>(StringComparer.Ordinal);
        Paths(typeof(ThrottleFigureDto), "", served);

        Assert.True(served.SetEquals(inventoried),
            "the DTO and the inventory disagree. Served but not inventoried: [" +
            string.Join(", ", served.Except(inventoried)) + "]. Inventoried but not served: [" +
            string.Join(", ", inventoried.Except(served)) + "]. Add the field to field-inventory.json with its " +
            "readers (run make_fixtures.py), and exercise it through each reader's contract test.");

        // The headline the rings print is read by BOTH consumers, and the inventory says so.
        foreach (var path in new[] { "headline.voice.percent", "headline.phone.percent", "headline.hasData", "headline.denominator" })
        {
            var readers = inventory.GetProperty(path).EnumerateArray().Select(r => r.GetString()).ToArray();
            Assert.Contains("browser", readers);
            Assert.Contains("report", readers);
        }
    }

    [Fact]
    public void TheContractFixturesMatchTheirManifest_SoASharedFixtureCannotDriftUnnoticed()
    {
        var dir = ContractDir();
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"))).RootElement;
        var fixtures = manifest.GetProperty("fixtures");
        Assert.True(fixtures.EnumerateObject().Count() >= 6);
        foreach (var entry in fixtures.EnumerateObject())
        {
            // Digested with line endings normalised to LF: git writes CRLF into a fresh Windows checkout, and a
            // raw digest would be a digest of the checkout rather than of the fixture.
            var text = File.ReadAllText(Path.Combine(dir, entry.Name)).Replace("\r\n", "\n");
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            Assert.Equal(entry.Value.GetString(), digest);
            // Every fixture's wire object is the served shape, and the rendered ones carry the headline the
            // library computes: the fixture's percentages ARE ThrottleDefinition.Headline's rounding.
            var fixture = JsonDocument.Parse(text).RootElement;
            var outcome = fixture.GetProperty("expected").GetProperty("outcome").GetString();
            if (outcome == "rendered" && entry.Name.StartsWith("the-headline-is-rendered", StringComparison.Ordinal))
            {
                var expected = fixture.GetProperty("expected").GetProperty("rendered");
                var h = ThrottleDefinition.Headline(1786, 1015, 771, new[]
                {
                    new ThrottleBucketDto { Modality = "voice", Surface = "desktop", Turns = 835 },
                    new ThrottleBucketDto { Modality = "voice", Surface = "phone", Turns = 180 },
                    new ThrottleBucketDto { Modality = "typed", Surface = "desktop", Turns = 696 },
                    new ThrottleBucketDto { Modality = "typed", Surface = "phone", Turns = 68 },
                    new ThrottleBucketDto { Modality = "typed", Surface = "unknown", Turns = 7 },
                });
                Assert.Equal(h.Voice.Percent, expected.GetProperty("voicePercent").GetInt32());
                Assert.Equal(h.Phone.Percent, expected.GetProperty("phonePercent").GetInt32());
                Assert.Equal(h.Denominator, expected.GetProperty("denominator").GetInt64());
            }
        }
        var inventoryText = File.ReadAllText(Path.Combine(dir, "field-inventory.json")).Replace("\r\n", "\n");
        var inventoryDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(inventoryText))).ToLowerInvariant();
        Assert.Equal(manifest.GetProperty("inventory").GetString(), inventoryDigest);
    }
}

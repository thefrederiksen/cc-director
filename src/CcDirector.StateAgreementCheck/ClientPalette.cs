using System.Text.RegularExpressions;

namespace CcDirector.StateAgreementCheck;

/// <summary>
/// The WEB/MOBILE client's palette, READ OUT OF THE SHIPPING TYPESCRIPT rather than re-typed here.
///
/// WHY IT IS PARSED AND NOT COPIED. This is the whole point of the rendered-hex half of the check.
/// A copy of the table in C# would be a fourteenth private palette that agrees with itself and proves
/// nothing - the same shape as every defect in this mission. The only honest way for a C# check to
/// assert what the phone and the Cockpit actually paint is to read the table they actually ship.
/// StatusPaletteTests says so in its own comment - "a C# test cannot assert the TypeScript, so the
/// spec's table is what keeps them honest" - and that sentence IS the gap this class closes: it makes
/// the two tables checkable by a machine instead of by a human remembering.
///
/// The near-miss that makes this load-bearing: before Phase 4 the desktop rail's red was #EF4444 and
/// this table's was #F14C4C, and its yellow was #EAB308 against #F59E0B. BOTH surfaces folded to the
/// string "red" and agreed perfectly - and then painted different pixels. A check that compares the
/// fold's ANSWER reports ZERO while two screens visibly disagree. Law 7 is "every device shows the
/// same thing, always", and the thing the owner sees is a pixel, not a string.
/// </summary>
public static class ClientPalette
{
    /// <summary>The shipping client table: packages/client-core/src/sessions/ordering.ts.</summary>
    public const string RelativePath = "packages/client-core/src/sessions/ordering.ts";

    /// <summary>
    /// Parse the <c>COLORS</c> map out of ordering.ts: fold-colour name -> hex, upper-cased.
    ///
    /// Fails LOUDLY rather than returning a partial table. A palette parser that silently yields an
    /// empty map would make every hex comparison below vacuously pass - a check that cannot fail is
    /// worse than no check, and this repository has shipped that exact thing (a suite green for
    /// fourteen months over a state nothing emitted).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Read(string repoRoot)
    {
        var path = Path.Combine(repoRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"The client palette was not found at '{path}'. The rendered-hex check cannot run without " +
                "the table the phone and the Cockpit actually ship - it must never be assumed or copied.", path);

        var source = File.ReadAllText(path);

        var block = Regex.Match(source, @"const\s+COLORS\s*:\s*Record<string,\s*string>\s*=\s*\{(.*?)\n\};",
            RegexOptions.Singleline);
        if (!block.Success)
            throw new InvalidOperationException(
                $"Could not find the 'const COLORS: Record<string, string> = {{ ... }}' table in {RelativePath}. " +
                "It was renamed or restructured - this check must be updated to read it, NOT to hard-code a copy of it.");

        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(block.Groups[1].Value,
                     @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:\s*""(#[0-9A-Fa-f]{6})""\s*,", RegexOptions.Multiline))
            table[m.Groups[1].Value] = m.Groups[2].Value.ToUpperInvariant();

        if (table.Count == 0)
            throw new InvalidOperationException(
                $"The COLORS table in {RelativePath} parsed to ZERO entries. Refusing to report agreement from an " +
                "empty palette - that is a check that cannot fail.");

        return table;
    }
}

namespace CcDirector.Gateway.Rules;

/// <summary>
/// The one place a .NET identifier becomes the lower_snake_case name a rule is stored and spoken about
/// with. Primitive names, parameter names, runtime input names and extract kinds ALL go through this, so
/// there is no second list of names anywhere to drift from the code: <c>IsPathInside</c> IS
/// <c>is_path_inside</c>, and the method signature is the whole contract (Architect ruling A2).
/// </summary>
public static class RuleWireNames
{
    /// <summary>
    /// The wire name for a .NET identifier: <c>IsPathInside</c> -> <c>is_path_inside</c>,
    /// <c>screenText</c> -> <c>screen_text</c>. An underscore is inserted before each upper-case letter
    /// that follows a lower-case letter or a digit, and before the last upper-case letter of a run that
    /// is followed by a lower-case letter, then the whole thing is lower-cased.
    /// </summary>
    /// <exception cref="ArgumentException">The identifier is null, empty or whitespace.</exception>
    public static string ToWireName(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("identifier is required", nameof(identifier));

        var text = identifier.Trim();
        var sb = new System.Text.StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsUpper(c) && i > 0)
            {
                var previous = text[i - 1];
                var nextIsLower = i + 1 < text.Length && char.IsLower(text[i + 1]);
                if (!char.IsUpper(previous) || nextIsLower)
                    sb.Append('_');
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}

namespace CcDirector.Gateway.Rules;

// TEMPORARY - a deliberately BAD input, to prove the guard can actually see a rules type reaching the
// typing seam. Deleted immediately after the red is recorded.
internal static class ZzTemporaryTypistProbe
{
    public static string SeamName() => typeof(Api.DirectorCommandRouter).Name;
}

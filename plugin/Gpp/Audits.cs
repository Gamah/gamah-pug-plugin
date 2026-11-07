namespace Gpp;

// How a command line reads for the audit, with no engine in it: what two spellings of the same command
// have in common. See Audit.cs for the logging itself.
internal static class Audits
{
    internal const int RepeatWindowMs = 400;

    // ".kick 4", "!kick 4" and "css_kick 4" are one command typed once. Lowercased because a command is
    // not case sensitive and two spellings of it are not two events.
    internal static string Normalise(string what)
    {
        string text = what.Trim();
        if (text.Length > 0 && (text[0] == '.' || text[0] == '!' || text[0] == '/')) text = text[1..];
        if (text.StartsWith("css_", StringComparison.OrdinalIgnoreCase)) text = text[4..];
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }
}

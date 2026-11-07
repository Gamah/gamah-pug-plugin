namespace Gpp;

// How ".kick <target> [reason]" reads, with no engine in it.
//
// The target is one token: a roster number from .players or a name. Everything after it is the reason,
// kept as typed. Which means a name with a space in it has to be the number instead - the alternative is
// guessing where a name ends, and a .kick that guesses wrong kicks the wrong person.
internal static class Kicks
{
    internal static (string Needle, string Why) Split(string arg)
    {
        var parts = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => ("", ""),
            1 => (parts[0], ""),
            _ => (parts[0], parts[1].Trim()),
        };
    }
}

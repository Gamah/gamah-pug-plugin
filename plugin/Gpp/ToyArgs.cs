namespace Gpp;

// How a warmup toy's argument is read: "[target] [amount] [amount2]".
//
// The target is a roster number from .players, "me", or a name. The amounts are whatever the toy wants -
// seconds for .fent and .drug, a count of slaps and the gap between them for .slap - and both are
// optional.
//
// The one rule that makes it predictable: a lone number is a target, never an amount. ".slap 5" is
// player 5, not five slaps at yourself. .players is what people read a number off, so that is the
// reading somebody typing one expects, and an amount on yourself is spelled out as ".slap me 5".
//
// Which means the amounts are taken off the end, and taking them stops before it would leave no target
// at all: ".slap 5 2" is two slaps at player 5, and ".slap 5 2 3" is two slaps three tenths apart at
// player 5. Names are joined back up, because people have spaces in them.
internal static class ToyArgs
{
    internal record Parsed(string Needle, int? Amount, int? Amount2);

    internal static Parsed Split(string arg)
    {
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var amounts = new List<int>();

        // Up to two, off the end, and never the last token standing - that one is the target.
        while (amounts.Count < 2 && parts.Count > 1 && int.TryParse(parts[^1], out int amount))
        {
            amounts.Insert(0, amount);
            parts.RemoveAt(parts.Count - 1);
        }

        return new Parsed(
            string.Join(' ', parts),
            amounts.Count > 0 ? amounts[0] : null,
            amounts.Count > 1 ? amounts[1] : null);
    }

    internal static bool MeansYourself(string needle) =>
        needle.Length == 0 || needle is "me" or "self" or "myself";
}

using CounterStrikeSharp.API;

namespace Gpp;

// The draft ends, the teams are on their sides, and the match phase takes over.
//
// The server stays open: nothing here kicks a latecomer or drags a player back to their drafted side once
// the match is live. The draft decides who starts where, not who may be here.
public partial class Gpp
{
    private void HandOffToMatch()
    {
        string mapLabel = _votedMap?.Label ?? Server.MapName;
        _votedMap = null;

        if (!Config.AutoStartMatch)
        {
            Announce("auto_start_match is off - teams are set, start the match yourself with !pug match.");
            return;
        }

        StartMatch(mapLabel);
    }

    private string TeamName(ulong captain) => MatchRules.TeamName(Name(captain));
}

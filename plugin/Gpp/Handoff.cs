using CounterStrikeSharp.API;

namespace Gpp;

// What used to be the handoff to MatchZy. The draft ends, the teams are on their sides, and the match
// phase takes over in this plugin instead of being asked for over console commands.
//
// What has gone with MatchZy, deliberately: no match config is written, so nothing kicks a latecomer or
// drags a player back to their drafted side once the match is live - an open server was the whole reason
// for dropping it. And no map veto: the veto was MatchZy's, it read its pool from a match config, and
// nothing here replaces it, so draft_then_veto is no longer a flow. The vote does the map.
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

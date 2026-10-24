using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;

namespace Gpp;

// Who wants the armband. Anyone types !captain to put their hand up, and the two captains are drawn from
// the volunteers rather than from the room. Fewer than two volunteers and the rest of the draw is at
// random, as it always was, so an empty pool costs nothing.
//
// The pool is open for the length of the map vote - !pug start runs one 45-second window that is both,
// and the vote panel carries both asks - so the sign-up costs no clock of its own. Somebody who does not
// want to risk being the last vote in types !captain first and votes after. !pug draft, which has no
// vote to ride along with, still opens the standalone window below.
//
// It is a window rather than a standing list because wanting to captain is a per-pug mood: the person who
// volunteered at midnight is not necessarily up for it at three.
public partial class Gpp
{
    private readonly HashSet<ulong> _volunteers = [];

    // The volunteers who were not drawn, kept past the window's close. The window itself is a mood and
    // closes for good, but an armband that has to be handed on - a captain who never came back from the
    // map change - should go to somebody who asked for it rather than to whoever happens to be standing
    // there. Cleared when the next window opens and when the draft ends.
    private readonly HashSet<ulong> _spareVolunteers = [];

    private int _optInSecondsLeft;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _optInTimer;
    private Action? _optInThen;

    internal bool CaptainOptInOpen => _optInTimer != null;

    // Either window: the standalone one, or a map vote carrying the pool.
    internal bool CaptainPoolOpen => CaptainOptInOpen || _votePoolOpen;

    internal int VolunteerCount() => _volunteers.Count(steamId => PlayerBySteamId(steamId) != null);

    // Whether this player has their hand up, which is the one thing the panels ask.
    internal bool InCaptainPool(ulong steamId) => _volunteers.Contains(steamId);

    // The volunteers who are still here, shuffled, so three hands up is not an argument about which two.
    private List<ulong> Standing() => _volunteers
        .Where(steamId => PlayerBySteamId(steamId) != null)
        .OrderBy(_ => _rng.Next())
        .ToList();

    // Opens the window and runs `then` when it closes. Returns false when there is no window to open -
    // captains already pinned, the grace period turned off, or an admin in a hurry - and the caller gets
    // on with it itself.
    private bool OpenCaptainOptIn(Action then, bool force)
    {
        if (force || Config.CaptainOptInSeconds <= 0) return false;
        if (_captain1 != 0 && _captain2 != 0) return false; // !pug captains has already said who
        if (CaptainOptInOpen) return false;

        _volunteers.Clear();
        _spareVolunteers.Clear();
        _optInThen = then;
        _optInSecondsLeft = Config.CaptainOptInSeconds;
        _optInTimer = AddTimer(1.0f, OptInTick, TimerFlags.REPEAT);

        Prompt($"Picking captains - {_optInSecondsLeft}s to put your hand up.", "!captain", Everybody);
        OptInPanel();
        return true;
    }

    internal void CloseCaptainOptIn(bool draw)
    {
        _optInTimer?.Kill();
        _optInTimer = null;
        _optInSecondsLeft = 0;
        var then = _optInThen;
        _optInThen = null;
        Clear();

        if (!draw)
        {
            _volunteers.Clear();
            _spareVolunteers.Clear();
            return;
        }

        var standing = Standing();
        _volunteers.Clear();

        if (standing.Count == 0)
        {
            Announce("Nobody volunteered - captains drawn at random.");
        }
        else if (standing.Count == 1)
        {
            _captain1 = standing[0];
            Announce($"{Colorize("{green}")}{Name(_captain1)}{Colorize("{default}")} volunteered - "
                + "the other captain is drawn at random.");
        }
        else
        {
            _captain1 = standing[0];
            _captain2 = standing[1];
            foreach (ulong spare in standing.Skip(2)) _spareVolunteers.Add(spare);
            Announce($"Captains: {Colorize("{green}")}{Name(_captain1)}{Colorize("{default}")} and "
                + $"{Colorize("{green}")}{Name(_captain2)}{Colorize("{default}")}"
                + (standing.Count > 2 ? $" (drawn from {standing.Count} volunteers)." : "."));
        }

        // A frame later, for the same reason every phase change is: this runs from a timer.
        if (then != null) AfterTick(then);
    }

    private void OptInTick()
    {
        // Two hands up is not an answer from the room - it was closing the window the moment a second
        // person typed !captain, before anybody else had read it. Everybody having answered is, so that
        // still ends it early; with two players connected those are the same thing.
        int standing = VolunteerCount();
        if (--_optInSecondsLeft <= 0 || standing >= AnsweringPlayers().Count)
        {
            AfterTick(() => CloseCaptainOptIn(draw: true));
            return;
        }
        OptInPanel();
    }

    private void OptInPanel()
    {
        if (!Config.Hud) return;
        _repaint = OptInPanel;
        WorldOptInPanel();
    }

    [ConsoleCommand("css_captain", "Put your hand up to captain the next pug")]
    public void OnCaptainCommand(CCSPlayerController? player, CommandInfo command) =>
        AfterTick(() => Volunteer(player == null || !player.IsValid ? null : PlayerBySteamId(Id(player))));

    internal void Volunteer(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid) return;
        if (!CaptainPoolOpen)
        {
            player.PrintToChat(Prefix() + "Nothing starting right now - !captain is asked for when a pug begins.");
            return;
        }

        if (_volunteers.Remove(Id(player)))
        {
            player.PrintToChat(Prefix() + "Taken your hand down - you will not be captain.");
            return;
        }

        _volunteers.Add(Id(player));
        player.PrintToChat(Prefix() + "You are up for captain.");
    }
}

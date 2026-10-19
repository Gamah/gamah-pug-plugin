using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;

namespace Gpp;

// Who wants the armband. !pug start opens a grace period - thirty seconds by default - where anyone can
// type !captain to put their hand up, and the two captains are drawn from the volunteers rather than from
// the room. Fewer than two volunteers and the rest of the draw is at random, as it always was, so an
// empty opt-in costs nothing but the wait.
//
// It is a window rather than a standing list because wanting to captain is a per-pug mood: the person who
// volunteered at midnight is not necessarily up for it at three.
public partial class Gpp
{
    private readonly HashSet<ulong> _volunteers = [];

    private int _optInSecondsLeft;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _optInTimer;
    private Action? _optInThen;

    internal bool CaptainOptInOpen => _optInTimer != null;

    internal int VolunteerCount() => _volunteers.Count(steamId => PlayerBySteamId(steamId) != null);

    // Opens the window and runs `then` when it closes. Returns false when there is no window to open -
    // captains already pinned, the grace period turned off, or an admin in a hurry - and the caller gets
    // on with it itself.
    private bool OpenCaptainOptIn(Action then, bool force)
    {
        if (force || Config.CaptainOptInSeconds <= 0) return false;
        if (_captain1 != 0 && _captain2 != 0) return false; // !pug captains has already said who
        if (CaptainOptInOpen) return false;

        _volunteers.Clear();
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
            return;
        }

        // Only volunteers who are still here. Two of them, drawn, so three people putting their hands up
        // is not an argument about which two.
        var standing = _volunteers.Where(steamId => PlayerBySteamId(steamId) != null)
            .OrderBy(_ => _rng.Next())
            .ToList();
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
            Announce($"Captains: {Colorize("{green}")}{Name(_captain1)}{Colorize("{default}")} and "
                + $"{Colorize("{green}")}{Name(_captain2)}{Colorize("{default}")}"
                + (standing.Count > 2 ? $" (drawn from {standing.Count} volunteers)." : "."));
        }

        // A frame later, for the same reason every phase change is: this runs from a timer.
        if (then != null) AfterTick(then);
    }

    private void OptInTick()
    {
        if (--_optInSecondsLeft <= 0)
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
        if (WorldStyle) { WorldOptInPanel(); return; }

        var names = _volunteers.Where(steamId => PlayerBySteamId(steamId) != null).Select(Name).ToList();
        var body = names.Count == 0
            ? ["no takers - captains drawn at random"]
            : Columns(names);

        foreach (var player in HumanPlayers())
        {
            bool mine = _volunteers.Contains(Id(player));
            // The command sits on the state line rather than in the headline: by the second pug nobody
            // is reading the headline, they are looking for the clock and the count.
            Paint(player, TwoLine(
                mine ? "YOU'RE UP" : "PICKING CAPTAINS",
                mine ? null : "!captain",
                $"{_optInSecondsLeft}s",
                $"{names.Count} in"), body);
        }
    }

    [ConsoleCommand("css_captain", "Put your hand up to captain the next pug")]
    public void OnCaptainCommand(CCSPlayerController? player, CommandInfo command) =>
        AfterTick(() => Volunteer(player == null || !player.IsValid ? null : PlayerBySteamId(Id(player))));

    internal void Volunteer(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid) return;
        if (!CaptainOptInOpen)
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

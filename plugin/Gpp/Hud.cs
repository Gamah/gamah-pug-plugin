using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

// What the room needs to do next has to be on screen, not in chat: nobody opens chat, and the engine's own
// warmup text sits over anything subtle. Measured on a connected client, 2026-10-15:
//
//   PrintToChat            read by nobody in time, and buried by the next line
//   PrintToCenterHtml      the warmup HUD draws straight over it - invisible for most of a pug
//   PrintToCenterAlert     shows, but larger and in the way
//   PrintToCenter          shows over warmup, holds a 1Hz redraw without flicker  <- this one
//
// PrintToCenter is small, single-weight text with no markup, bottom-anchored, and it will draw about four
// lines of about eighty characters - more than that and the top lines are cut off. Every panel below is
// built to that budget, so the countdown and the list are always the part that survives.
public partial class Gpp
{
    // The layout itself lives in HudLayout, which has no CounterStrikeSharp in it so that Gpp.Tests can
    // run every panel's worst case through the real code rather than an approximation of it.
    private const int HudLines = HudLayout.PanelLines;

    internal static string Alert(params string?[] parts) => HudLayout.Alert(parts);

    // The panel font is proportional, so widths come from HudMetrics, which holds advances measured
    // through the renderer rather than a character count. The alert box is drawn in a larger face that
    // has not been measured, so it keeps a conservative character cap - its lines are short now that the
    // state is stacked down four of them.
    // Seconds the panel has been up, which is what pages the pick list.
    private int _hudTicks;

    // Whatever is on screen right now, so it can be drawn again between phase ticks. A panel sets this
    // when it paints and Clear() takes it down; the repaint timer below is what makes a long line
    // actually move, rather than it stepping once a second when the phase clock happens to tick.
    private Action? _repaint;

    private CounterStrikeSharp.API.Modules.Timers.Timer? _hudTimer;

    private void StartHud()
    {
        if (!Config.Hud) return;
        _hudTicks = 0;
        _hudTimer?.Kill();
        _hudTimer = AddTimer(1.0f, HudTick, TimerFlags.REPEAT);
        HudTick();
    }

    private void StopHud()
    {
        _hudTimer?.Kill();
        _hudTimer = null;
        Clear();
    }

    private void Clear()
    {
        _repaint = null;
        _keepCentred = false;
        ClearWorldPanels();
        foreach (var player in HumanPlayers())
        {
            player.PrintToCenter(" ");
            player.PrintToCenterAlert(" ");
        }
    }

    private void HudTick()
    {
        // The alert line scrolls off this, and it is the only thing left that needs a tick.
        HudLayout.BumpPaintTick();
        _hudTicks++;
        if (_phase == PugPhase.Idle || (_phase == PugPhase.Draft && _pickIndex >= _pickOrder.Count))
        {
            StopHud();
            return;
        }
        PaintPhase();
    }

    // Just the drawing: no clock, no paging, so the repaint timer can call it as often as it likes.
    private void PaintPhase()
    {
        _repaint = PaintPhase;

        switch (_phase)
        {
            // The captains are knifing for first pick, so the boards come down - a board is a solid
            // object in front of the face and this is the one part of the pug that is aim. Two lines of
            // centre-screen alert stay, because nothing on screen at all leaves the nine people watching
            // with no idea what is being decided.
            case PugPhase.CaptainKnife: WorldHide(); KnifeAlert(); break;
            case PugPhase.MapVote: WorldVotePanel(); break;
            case PugPhase.Draft: WorldDraftPanel(); break;
        }
    }

    // Two lines over the captain duel: who is in it for the pair fighting it, and who to watch for
    // everybody else. The boards are down for this, so this is all there is.
    private void KnifeAlert()
    {
        foreach (var player in HumanPlayers())
        {
            bool duelling = Id(player) == _captain1 || Id(player) == _captain2;
            ulong other = Id(player) == _captain1 ? _captain2 : _captain1;
            player.PrintToCenterAlert(duelling
                ? Alert("KNIFE FOR FIRST PICK", $"vs {Name(other)} - first blood")
                : Alert("CAPTAINS ARE KNIFING", $"{Name(_captain1)} vs {Name(_captain2)}"));
        }
    }

    // No truncation of its own any more: a line too long for the panel scrolls (see Scroll).
    private static string Join(List<string> names) => string.Join(", ", names);

    // ".ready in chat" is exactly the instruction a room full of people not reading chat will sit on
    // forever, so the ready phase gets a panel of its own naming who is still holding it up.
    private CounterStrikeSharp.API.Modules.Timers.Timer? _readyHudTimer;
    private int _readyHudSecondsLeft;
    private string _readyHudMap = "";

    private void StartReadyHud(string mapLabel)
    {
        if (!Config.Hud) return;
        _readyHudMap = mapLabel;
        _readyHudSecondsLeft = 300;
        _readyHudTimer?.Kill();
        _readyHudTimer = AddTimer(1.0f, ReadyHudTick, TimerFlags.REPEAT);
        ReadyHudTick();
    }

    private void StopReadyHud()
    {
        _readyHudTimer?.Kill();
        _readyHudTimer = null;
        // The ready roster is only here to drive this panel once the gate has stopped sweeping it.
        _roster.Clear();
        Clear();
    }

    private void ReadyHudTick()
    {
        if (--_readyHudSecondsLeft <= 0)
        {
            // Five minutes of a panel nobody has acted on is a panel nobody is reading. The phase stays
            // open - .ready still starts the match - but the overlay comes down rather than turning into
            // a repeating nag.
            AfterTick(() =>
            {
                StopReadyHud();
                if (_matchPhase == MatchPhase.Ready) Prompt("Still waiting on readies.", ".ready", Everybody);
            });
            return;
        }
        ReadyHudPaint();
    }

    private void ReadyHudPaint()
    {
        _repaint = ReadyHudPaint;

        WorldReadyPanel();
    }

    // The match's own panel, which only goes up when the room is being asked for something: the knife
    // round, the side choice after it, and a pause. A live round deliberately draws nothing - the overlay
    // sits where the engine puts its own round text, and a pug does not need it narrated.
    private CounterStrikeSharp.API.Modules.Timers.Timer? _matchHudTimer;

    private void StartMatchHud()
    {
        if (!Config.Hud) return;
        if (_matchHudTimer != null) return;
        _matchHudTimer = AddTimer(1.0f, MatchHudTick, TimerFlags.REPEAT);
        MatchHudTick();
    }

    private void StopMatchHud()
    {
        _matchHudTimer?.Kill();
        _matchHudTimer = null;
        Clear();
    }

    private void MatchHudTick()
    {
        bool wanted = _matchPhase == MatchPhase.Knife
            || _matchPhase == MatchPhase.SideChoice
            || (_matchPhase == MatchPhase.Live && _paused);
        if (!wanted)
        {
            AfterTick(StopMatchHud);
            return;
        }
        MatchHudPaint();
    }

    private void MatchHudPaint()
    {
        _repaint = MatchHudPaint;

        // Side choice gets the panel; the knife round does not, and a live pause draws no board on
        // purpose - a board in front of ten people's faces when the round is about to resume is the one
        // place this overlay must not be. Both of those get the alert line instead.
        if (_matchPhase == MatchPhase.SideChoice) { WorldSidePanel(); return; }

        WorldHide();
        if (_matchPhase == MatchPhase.Knife)
        {
            foreach (var player in HumanPlayers())
            {
                player.PrintToCenterAlert(Alert("KNIFE ROUND", "WINNER PICKS THE SIDE"));
            }
            return;
        }

        foreach (var player in HumanPlayers())
        {
            player.PrintToCenterAlert(Alert(PauseHasLanded ? "PAUSED" : "PAUSE AT END OF ROUND",
                PauseLine()));
        }
    }

    // The alert line, which is not the centre panel: it is the one channel that reaches a viewer with no
    // pawn of their own, and the captain knife and a live pause are drawn with nothing else.
}

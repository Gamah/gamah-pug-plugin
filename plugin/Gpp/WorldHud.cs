using System.Drawing;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

// The overlay as world text: one point_worldtext per viewer, transmitted only to them.
//
// Why this replaces PrintToCenter at all is in WorldLayout - four lines of eighty characters, measured
// glyph widths, a scroller and a 10Hz redraw to beat the fade were all properties of that one message,
// not of CS2. A point_worldtext is as big as it likes, keeps its text until it is told otherwise, and
// updates for the price of one networked string.
//
// What this file owns is the part that can crash a server: entity lifecycle, who may see whose panel,
// and how the thing stays in front of a moving player.
public partial class Gpp
{
    // Which of a viewer's three boards a piece of text is for. The middle one is what the room is being
    // asked for; the two beside it are the teams, which fill as the draft runs - a name leaves the pool
    // in the middle and appears under a captain on one side.
    private enum Board { Main = 0, Left = 1, Right = 2 }

    // One text entity per board, so the three can change independently: a pick changes one team panel
    // and the middle one, and leaves the other team's panel alone.
    private sealed class Slot
    {
        internal CPointWorldText? Text;
        internal string Sent = "";
    }

    // One per viewer: three boards and the point_orient they all hang from. Three entities parented to
    // one orient rather than three orients, because the orient is the expensive part - it is what has to
    // be aimed - and all three boards share a frame of reference.
    private sealed class ViewerPanel
    {
        internal readonly Slot[] Boards = [new(), new(), new()];
        internal CPointOrient? Orient;
        internal float AimedAt = float.NaN;    // the eye yaw the boards were last squared up to
        internal int ParentedTo;               // pawn index they were parented to, 0 for none

        internal IEnumerable<Slot> Live => Boards.Where(board => board.Text != null && board.Text.IsValid);
    }

    private readonly Dictionary<int, ViewerPanel> _panels = [];

    // How the panel keeps up with the view, best first. Which one is in use is decided once at load by
    // Probe() and logged, because "the panel is in the wrong place" and "the panel is lagging" are
    // different bugs and the log has to say which mechanism was drawing.
    //
    // Orient:   point_orient parented to the pawn, aimed down its eyeline, with the panel parented to
    //           the orient. Position and rotation then ride the client's own prediction of its pawn, so
    //           there is nothing to interpolate and nothing to send per tick.
    // Pawn:     panel parented to the pawn. Translation is predicted; the facing is not, so the panel is
    //           re-squared only when the eyes have turned past AimTolerance.
    // Teleport: moved every tick, which is what the prototype did. Always works, always trails by about
    //           a tenth of a second.
    private enum Follow { Orient, Attachment, Pawn, Teleport }

    private Follow _follow = Follow.Teleport;

    // Degrees the view may turn before a Pawn-tier panel is re-squared. A panel that is re-teleported
    // every tick throws away the prediction that parenting just bought.
    private const float AimTolerance = 1.5f;

    // What the panel is made of, all measured on a client 2026-10-19 and all explained in WorldPanel.cs:
    // FontSize stays put and WorldUnitsPerPx does the sizing, because raising FontSize grows the glyphs
    // without moving the lines apart.
    private const float BaseFontSize = 64f;
    private const float AssumedVerticalFov = 59f;
    private const float LineHeightFactor = 1.2f;
    private const int ReferenceLines = 12;
    private const float PanelDistance = 70f;

    // The two team boards: out to each side with a gap, toed in to face the viewer. Not flush with the
    // middle one - three panels touching read as one wide panel, and the whole point of the side boards
    // is that a glance left or right is a different question from the one in front of you.
    //
    // All three are tunable live with !pug sides, because the only way to judge them is to stand in
    // front of them, and a redeploy per guess is the slow version of that.
    // Tightened on 2026-10-19 after looking at them: 62 units apart with narrow boards read as three
    // separate things on separate walls. Close enough to touch is fine; touching is not a requirement.
    private float _sideGap = 40f;        // units to each side of the middle board's centre
    private float _sideToeIn = 22f;      // degrees each side board turns inward to face the viewer
    private float _sideBack = 0f;        // units further away than the middle board, if they want it
    // Dropped from 75 on 2026-10-19: with three boards across, type that size left the side boards at the
    // edge of the view and nothing room to breathe. Everything is one size, so this is the one knob.
    private const float PanelScreenPercent = 62f;
    private const float PanelUpOffset = 9f;

    private static float PanelScale()
    {
        double half = AssumedVerticalFov * Math.PI / 360.0;
        double worldHeight = 2.0 * PanelDistance * Math.Tan(half) * (PanelScreenPercent / 100.0);
        return (float)(worldHeight / (ReferenceLines * BaseFontSize * LineHeightFactor));
    }

    // Decides the follow tier once, at load.
    //
    // Viewmodel parenting - what every screen-menu plugin used to do - is not an option and cannot be
    // made one: Valve removed CCSPlayer_ViewModelServices and CCSGOViewModel from the server schema in
    // the 2025-08-03 patch, CounterStrikeSharp deleted the generated types to match (commit af3bb52,
    // first in 1.0.329), and there is no field left to point at. Plugins that hardcoded the offset
    // broke; CS2MenuManager deleted its screen menus the next day.
    //
    // So the good tier is point_orient, which is in 1.0.376's schema. The probe is the fallback the
    // owner asked for: GetSchemaOffset answers 0 for a field that is not there, silently, and
    // entity.Handle + 0 is the vtable - reads come back as plausible garbage and writes segfault. So
    // nothing is touched before its offset has been seen to be non-zero.
    // [SOURCE: CounterStrikeSharp 1.0.376/1.0.377 generated schema and src/core/cs2_sdk/schema.cpp,
    // read 2026-10-19 - the 0-on-missing return is an implementation detail, not a documented contract.]
    private void ProbeFollow()
    {
        if (Schema.GetClassSize("CPointOrient") > 0
            && Schema.GetSchemaOffset("CPointOrient", "m_nGoalDirection") > 0
            && Schema.GetSchemaOffset("CPointOrient", "m_hTarget") > 0
            && Schema.GetSchemaOffset("CPointOrient", "m_bActive") > 0)
        {
            _follow = Follow.Orient;
        }
        else if (Schema.GetClassSize("CBaseEntity") > 0)
        {
            _follow = Follow.Pawn;
        }
        else
        {
            _follow = Follow.Teleport;
        }

        // An explicit hud_anchor overrides the probe - that is what makes it possible to stand in front
        // of all four of them in one session and say which one looks right. It cannot pick a tier the
        // schema will not support: asking for one that failed the probe is logged and ignored.
        if (!string.IsNullOrWhiteSpace(Config.HudAnchor))
        {
            var wanted = Config.HudAnchor.Trim().ToLowerInvariant() switch
            {
                "orient" => Follow.Orient,
                "attachment" => Follow.Attachment,
                "pawn" => Follow.Pawn,
                "teleport" => Follow.Teleport,
                _ => (Follow?)null,
            };
            if (wanted == null)
            {
                Logger.LogWarning("hud_anchor \"{Anchor}\" is not a thing - using {Tier}.", Config.HudAnchor, _follow);
            }
            else if ((wanted == Follow.Orient || wanted == Follow.Attachment) && _follow != Follow.Orient)
            {
                Logger.LogWarning("hud_anchor \"{Anchor}\" needs point_orient, which this build does not have - using {Tier}.",
                    Config.HudAnchor, _follow);
            }
            else
            {
                _follow = wanted.Value;
            }
        }

        Logger.LogInformation("World panel follow tier: {Tier}. point_orient size {Size}, m_nGoalDirection at {Offset}.",
            _follow, Schema.GetClassSize("CPointOrient"), Schema.GetSchemaOffset("CPointOrient", "m_nGoalDirection"));
    }

    // Everything below assumes a panel belongs to exactly one player. Without this, ten people in a pug
    // read ten overlapping boards hanging in the room - which is the whole reason the centre panel was
    // per-player too.
    private void WorldPanelTransmit(CCheckTransmitInfoList infos)
    {
        if (_panels.Count == 0) return;

        foreach (var (info, receiver) in infos)
        {
            if (receiver == null) continue;
            foreach (var (slot, panel) in _panels)
            {
                if (slot == receiver.Slot) continue;
                foreach (var board in panel.Live) info.TransmitEntities.Remove(board.Text!);
                if (panel.Orient != null && panel.Orient.IsValid) info.TransmitEntities.Remove(panel.Orient);
            }
        }
    }

    // Put these boards in front of this player: the middle one, and the two team boards beside it. A
    // null side means that board is not wanted now - during the vote there are no teams yet - and it is
    // taken down rather than left showing the last thing it said.
    //
    // Text is only pushed when it has changed, which is what makes a ticking clock cheap: one networked
    // string a second, for the one board that moved.
    //
    // Setting MessageText alone re-flows the lines correctly - measured on a client 2026-10-19 with
    // !pug paneltick, stage B. Setting MessageText *and* FontSize together does not: the glyphs are
    // re-rendered without the lines being re-spaced and the panel comes out overlapped. So the size is
    // set once, at spawn, and never again.
    internal void ShowWorldPanel(CCSPlayerController player, string main, string? left = null, string? right = null)
    {
        if (!player.IsValid || player.IsBot || player.IsHLTV) return;

        if (!_panels.TryGetValue(player.Slot, out var panel))
        {
            panel = new ViewerPanel();
            _panels[player.Slot] = panel;
        }

        Draw(player, panel, Board.Main, main);
        Draw(player, panel, Board.Left, left);
        Draw(player, panel, Board.Right, right);

        KeepUp(player, panel);
    }

    private void Draw(CCSPlayerController player, ViewerPanel panel, Board which, string? text)
    {
        var slot = panel.Boards[(int)which];

        if (string.IsNullOrEmpty(text))
        {
            if (slot.Text != null && slot.Text.IsValid) slot.Text.Remove();
            slot.Text = null;
            slot.Sent = "";
            return;
        }

        if (slot.Text == null || !slot.Text.IsValid)
        {
            if (SpawnBoard(player, panel, which, text)) slot.Sent = text;
            return;
        }

        if (slot.Sent == text) return;
        slot.Text.MessageText = text;
        Utilities.SetStateChanged(slot.Text, "CPointWorldText", "m_messageText");
        slot.Sent = text;
    }

    private bool SpawnBoard(CCSPlayerController player, ViewerPanel panel, Board which, string text)
    {
        var entity = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
        // CreateEntityByName does not return null for a dead factory - it hands back a wrapper over a
        // zero handle - so the handle is what gets checked.
        // [SOURCE: CounterStrikeSharp Utilities.CreateEntityByName, read 2026-10-19]
        if (entity == null || entity.Handle == IntPtr.Zero || !entity.IsValid)
        {
            Logger.LogError("point_worldtext would not spawn for {Name} - the world panel cannot draw.", player.PlayerName);
            return false;
        }

        entity.MessageText = text;
        entity.Enabled = true;
        entity.FontSize = BaseFontSize;
        // Arial Bold because every client has it. A font name the client cannot resolve draws nothing at
        // all, so this is not a knob to guess at.
        entity.FontName = "Arial Bold";
        entity.Color = Color.FromArgb(255, 255, 255, 255);
        entity.Fullbright = true;
        entity.WorldUnitsPerPx = PanelScale();
        entity.DrawBackground = true;
        entity.BackgroundBorderWidth = 0.25f;
        entity.BackgroundBorderHeight = 0.25f;
        entity.DepthOffset = 0f;
        // The side boards are justified towards the middle one so the gap between the three stays the
        // gap you set, whatever length the names on them happen to be.
        entity.JustifyHorizontal = which switch
        {
            Board.Left => PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_RIGHT,
            Board.Right => PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_LEFT,
            _ => PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER,
        };
        entity.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_CENTER;
        // The text is drawn in the entity's own plane, so without a roll of 90 it lies flat like a rug.
        // Auto-reorient does not help, because a flat panel's up axis is the wrong one.
        entity.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;
        entity.DispatchSpawn();

        panel.Boards[(int)which].Text = entity;

        Hang(player, panel, which);
        return true;
    }

    // Makes sure this viewer has an orient to hang from, then hangs one board on it.
    //
    // Order matters and is what a 45-degree panel was: SetParent freezes the entity's offset from its
    // parent at the moment it is called, so teleporting first and parenting second freezes the board
    // against whatever rotation the parent happened to have in that frame. A point_orient spawned in the
    // same frame has not been aimed at its target yet, so that rotation is arbitrary - and the board
    // comes out skewed by it, consistently, for as long as it lives.
    //
    // So: parent first, then teleport, with the position and the facing both read off the orient rather
    // than off the eyes. The orient is what the boards hang from; the eyes are only what the orient is
    // following.
    // [SOURCE: shape taken from darkerz7/CS2-GameHUD HUD.cs GetPositionOrient, read 2026-10-19]
    private void Hang(CCSPlayerController player, ViewerPanel panel, Board which)
    {
        var pawn = player.PlayerPawn.Value;
        var slot = panel.Boards[(int)which];
        if (slot.Text == null || !slot.Text.IsValid || pawn == null || !pawn.IsValid || pawn.AbsOrigin == null) return;

        if (_follow == Follow.Teleport)
        {
            Square(slot, which, pawn.AbsOrigin, pawn.EyeAngles, EyeHeight(pawn));
            panel.AimedAt = pawn.EyeAngles.Y;
            return;
        }

        if (_follow == Follow.Orient || _follow == Follow.Attachment)
        {
            var orient = EnsureOrient(player, panel, pawn);
            if (orient != null)
            {
                // Next frame, by which time the orient has been turned to face down the eyeline. Doing
                // this in the same frame is exactly the bug described above.
                Server.NextFrame(() =>
                {
                    if (slot.Text == null || !slot.Text.IsValid || !orient.IsValid) return;
                    slot.Text.AcceptInput("SetParent", orient, null, "!activator");
                    SquareToOrient(slot, which, orient);
                });
                return;
            }
            // The factory went away under us: carry on at the next tier down rather than drawing nothing.
            Logger.LogWarning("point_orient would not spawn - the world panel is falling back to pawn parenting.");
            _follow = Follow.Pawn;
        }

        slot.Text.AcceptInput("SetParent", pawn, null, "!activator");
        panel.ParentedTo = (int)pawn.Index;
        Square(slot, which, pawn.AbsOrigin, pawn.EyeAngles, EyeHeight(pawn));
        panel.AimedAt = pawn.EyeAngles.Y;
    }

    // One orient per viewer, shared by all three boards: it is the expensive half - the thing that has
    // to be aimed - and the boards only differ by a fixed offset within its frame.
    private CPointOrient? EnsureOrient(CCSPlayerController player, ViewerPanel panel, CCSPlayerPawn pawn)
    {
        if (panel.Orient != null && panel.Orient.IsValid && panel.ParentedTo == (int)pawn.Index) return panel.Orient;

        ClearOrient(panel);
        var orient = Utilities.CreateEntityByName<CPointOrient>("point_orient");
        if (orient == null || orient.Handle == IntPtr.Zero || !orient.IsValid) return null;

        orient.Active = true;
        // Aimed down the pawn's eyeline, which is what carries the boards' facing without the server
        // sending an angle every tick.
        orient.GoalDirection = PointOrientGoalDirectionType_t.eEyesForward;
        // ePreserveUpAxis holds the orient's up axis at the sky, which keeps the boards upright through
        // a pitch without Square() having to take the pitch back out of the roll.
        orient.Constraint = Config.HudOrientConstraint.Trim().ToLowerInvariant() == "preserveupaxis"
            ? PointOrientConstraint_t.ePreserveUpAxis
            : PointOrientConstraint_t.eNone;
        // A rate-limited orient trails a fast flick, which reads as the boards swinging out and catching
        // up. 0 leaves whatever the engine defaults to.
        if (Config.HudOrientTurnRate > 0f) orient.MaxTurnRate = Config.HudOrientTurnRate;
        orient.DispatchSpawn();
        orient.Teleport(new Vector(pawn.AbsOrigin!.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + EyeHeight(pawn)), null, null);

        // The whole difference between the two orient tiers: what the orient itself hangs from. The
        // pawn's origin is at its feet and its rotation is the body yaw; a look attachment on the model
        // is a head bone, which tracks where somebody is actually looking. Whether this server's player
        // models carry the attachment is not knowable from here - a name the model does not have makes
        // SetParentAttachmentMaintainOffset do nothing, and it stays parented at the origin.
        if (_follow == Follow.Attachment && !string.IsNullOrWhiteSpace(Config.HudAnchorAttachment))
        {
            orient.AcceptInput("SetParentAttachmentMaintainOffset", pawn, null, Config.HudAnchorAttachment);
        }
        else
        {
            orient.AcceptInput("SetParent", pawn, null, "!activator");
        }
        orient.AcceptInput("SetTarget", pawn, null, "!activator");

        panel.Orient = orient;
        panel.ParentedTo = (int)pawn.Index;
        return orient;
    }

    // Hangs one board in the orient's own frame. Teleporting a parented entity re-freezes its offset, so
    // this doubles as the correction when the freeze was taken at a bad moment.
    private void SquareToOrient(Slot slot, Board which, CPointOrient orient)
    {
        if (orient.AbsOrigin == null || orient.AbsRotation == null) return;
        Square(slot, which, orient.AbsOrigin, orient.AbsRotation, 0f);
    }

    // The one place a board's position and facing are worked out, from whatever frame it is hanging in.
    //
    // The roll is 90 minus the pitch rather than a flat 90: 90 is what stands the text plane up when the
    // view is level, and the pitch has to come back out of it or the board tilts as soon as somebody
    // looks up. The side boards add a lateral offset and a yaw of their own, which is what toes them in
    // to face the viewer rather than leaving them edge-on at the corner of the eye.
    private void Square(Slot slot, Board which, Vector origin, QAngle angles, float eyeHeight)
    {
        if (slot.Text == null || !slot.Text.IsValid) return;

        float sideways = which switch { Board.Left => -_sideGap, Board.Right => _sideGap, _ => 0f };
        float toeIn = which switch { Board.Left => _sideToeIn, Board.Right => -_sideToeIn, _ => 0f };
        float away = PanelDistance + (which == Board.Main ? 0f : _sideBack);

        double yaw = angles.Y * Math.PI / 180.0;
        double pitch = angles.X * Math.PI / 180.0;
        double sy = Math.Sin(yaw), cy = Math.Cos(yaw), sp = Math.Sin(pitch), cp = Math.Cos(pitch);

        var forward = new Vector((float)(cp * cy), (float)(cp * sy), (float)-sp);
        var up = new Vector((float)(sp * cy), (float)(sp * sy), (float)cp);
        // Flat on the ground and square to the view: what the side boards are offset along.
        var right = new Vector((float)sy, (float)-cy, 0);

        var at = new Vector(
            origin.X + (forward.X * away) + (up.X * PanelUpOffset) + (right.X * sideways),
            origin.Y + (forward.Y * away) + (up.Y * PanelUpOffset) + (right.Y * sideways),
            origin.Z + eyeHeight + (forward.Z * away) + (up.Z * PanelUpOffset) + (right.Z * sideways));

        slot.Text.Teleport(at, new QAngle(0, angles.Y + 270f + toeIn, 90f - angles.X), new Vector(0, 0, 0));
    }

    // Called on every text update. Parented boards usually need nothing; this is what notices a respawn
    // (the pawn they were parented to is gone) and, on the pawn tier, that the view has turned far
    // enough to be worth re-squaring.
    private void KeepUp(CCSPlayerController player, ViewerPanel panel)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null) return;
        if (!panel.Live.Any()) return;

        if (_follow != Follow.Teleport && panel.ParentedTo != (int)pawn.Index)
        {
            // New pawn, which means a respawn or a round restart: the old parent is dead and the boards
            // are floating where that pawn used to be.
            Rehang(player, panel);
            return;
        }

        if (_follow == Follow.Orient || _follow == Follow.Attachment)
        {
            // Parented and riding the orient, so there is normally nothing to do. The check is for the
            // one case that cannot be seen from here: a freeze taken while the orient was still turning,
            // which leaves a board permanently off to one side. One comparison a second, and a single
            // teleport to put it right, rather than an angle on the wire every tick.
            if (panel.Orient == null || !panel.Orient.IsValid || panel.Orient.AbsRotation == null) return;

            for (int which = 0; which < panel.Boards.Length; which++)
            {
                var slot = panel.Boards[which];
                if (slot.Text == null || !slot.Text.IsValid || slot.Text.AbsRotation == null) continue;

                float toeIn = (Board)which switch { Board.Left => _sideToeIn, Board.Right => -_sideToeIn, _ => 0f };
                float want = panel.Orient.AbsRotation.Y + 270f + toeIn;
                if (Math.Abs(Delta(want, slot.Text.AbsRotation.Y)) > AimTolerance)
                {
                    SquareToOrient(slot, (Board)which, panel.Orient);
                }
            }
            return;
        }

        var eyes = pawn.EyeAngles;
        if (_follow == Follow.Pawn && !float.IsNaN(panel.AimedAt) && Math.Abs(Delta(eyes.Y, panel.AimedAt)) <= AimTolerance)
        {
            return;
        }

        for (int which = 0; which < panel.Boards.Length; which++)
        {
            Square(panel.Boards[which], (Board)which, pawn.AbsOrigin, eyes, EyeHeight(pawn));
        }
        panel.AimedAt = eyes.Y;
    }

    // Takes every board down and puts it back up where it belongs - after a respawn, or when the anchor
    // or the side geometry changes under them. Re-parenting in place would mean unpicking the frozen
    // offsets, which is the state that is hardest to see and easiest to get wrong.
    private void Rehang(CCSPlayerController player, ViewerPanel panel)
    {
        var texts = panel.Boards.Select(board => board.Sent).ToArray();
        for (int which = 0; which < panel.Boards.Length; which++)
        {
            var slot = panel.Boards[which];
            if (slot.Text != null && slot.Text.IsValid) slot.Text.Remove();
            slot.Text = null;
            slot.Sent = "";
        }
        ClearOrient(panel);

        for (int which = 0; which < texts.Length; which++)
        {
            if (texts[which].Length == 0) continue;
            if (SpawnBoard(player, panel, (Board)which, texts[which])) panel.Boards[which].Sent = texts[which];
        }
    }

    // The Teleport tier only: every tick, for the players who have a panel up.
    private void WorldPanelTick()
    {
        if (_follow != Follow.Teleport || _panels.Count == 0) return;
        foreach (var (slot, panel) in _panels)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid) continue;
            KeepUp(player, panel);
        }
    }

    private static float EyeHeight(CCSPlayerPawn pawn) => pawn.ViewOffset.Z == 0 ? 64f : pawn.ViewOffset.Z;

    private static float Delta(float now, float then)
    {
        if (float.IsNaN(then)) return 360f;
        float delta = (now - then) % 360f;
        if (delta > 180f) delta -= 360f;
        if (delta < -180f) delta += 360f;
        return delta;
    }

    private static void ClearOrient(ViewerPanel panel)
    {
        if (panel.Orient != null && panel.Orient.IsValid) panel.Orient.Remove();
        panel.Orient = null;
        panel.ParentedTo = 0;
    }

    // A leaked point_worldtext stays in the world until the map does, so every way a panel can end has
    // to come through here: phase end, disconnect, map change, plugin unload.
    internal void HideWorldPanel(int slot)
    {
        if (!_panels.TryGetValue(slot, out var panel)) return;
        foreach (var board in panel.Boards)
        {
            if (board.Text != null && board.Text.IsValid) board.Text.Remove();
            board.Text = null;
            board.Sent = "";
        }
        ClearOrient(panel);
        _panels.Remove(slot);
    }

    internal void ClearWorldPanels()
    {
        foreach (int slot in _panels.Keys.ToList()) HideWorldPanel(slot);
        _panels.Clear();
    }

    // Belt and braces for the one failure that cannot be cleaned up later: a panel left behind by a
    // crash, a hot reload, or a bug in the bookkeeping above. Runs on map start, when nothing of ours
    // can legitimately be alive yet.
    internal void SweepStrayPanels()
    {
        _panels.Clear();
        foreach (var stray in Utilities.FindAllEntitiesByDesignerName<CPointWorldText>("point_worldtext").ToList())
        {
            if (stray.IsValid) stray.Remove();
        }
        foreach (var stray in Utilities.FindAllEntitiesByDesignerName<CPointOrient>("point_orient").ToList())
        {
            if (stray.IsValid) stray.Remove();
        }
    }

    // Puts every viewer's boards back up with whatever the geometry or the anchor is now. The text each
    // board was showing is kept, so a tuning command does not blank the room mid-draft.
    private int Rehang()
    {
        int rehung = 0;
        foreach (var (slot, panel) in _panels)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid) continue;
            Rehang(player, panel);
            rehung++;
        }
        return rehung;
    }

    // !pug sides [gap] [toe-in] [back] - where the two team boards sit, tuned by standing in front of
    // them. Nothing here is knowable from a schema: it is whether a glance to the left lands on a team.
    internal string SetSides(string gap, string toeIn, string back)
    {
        if (float.TryParse(gap, out float parsedGap)) _sideGap = Math.Clamp(parsedGap, 0f, 400f);
        if (float.TryParse(toeIn, out float parsedToe)) _sideToeIn = Math.Clamp(parsedToe, -90f, 90f);
        if (float.TryParse(back, out float parsedBack)) _sideBack = Math.Clamp(parsedBack, -60f, 400f);

        int rehung = Rehang();
        return $"Side boards: gap {_sideGap:F0}, toe-in {_sideToeIn:F0} degrees, {_sideBack:F0} further back"
            + $" - {rehung} viewer(s) rehung.";
    }

    // What the schema probe chose at load, so the gallery can walk through the mounts and put the real
    // one back afterwards rather than leaving the server on whatever it showed last.
    private Follow? _probedFollow;

    // Used by the gallery: switch mount without saying anything in chat, and without the guards that
    // exist for a human typing it - a step that cannot work on this build is still worth showing, as
    // itself, so the difference is visible.
    internal void UseAnchor(string mode)
    {
        _probedFollow ??= _follow;
        var wanted = mode switch
        {
            "orient" => Follow.Orient,
            "attachment" => Follow.Attachment,
            "pawn" => Follow.Pawn,
            _ => Follow.Teleport,
        };
        if (wanted == _follow) return;
        if ((wanted == Follow.Orient || wanted == Follow.Attachment) && Schema.GetClassSize("CPointOrient") <= 0) return;

        _follow = wanted;
        Rehang();
    }

    internal void RestoreProbedAnchor()
    {
        if (_probedFollow == null || _probedFollow == _follow) return;
        _follow = _probedFollow.Value;
        _probedFollow = null;
        Rehang();
    }

    // One line per mount, so the panel itself says what is being looked at rather than the owner having
    // to hold four descriptions in their head while turning round.
    private string MountNote(string mode) => mode switch
    {
        "orient" => "point_orient on the pawn: position and facing both predicted by your own client.",
        "attachment" => $"the same, hung from the model's \"{Config.HudAnchorAttachment}\" attachment."
            + " If this looks identical to orient, the model has no such attachment.",
        "pawn" => "parented to the player model: the body yaw, which snaps and lags behind your aim.",
        _ => "no parent at all - moved by the server every tick, so it trails about a tenth of a second.",
    };

    // !pug anchor [orient|attachment|pawn|teleport] [attachment name]
    //
    // Config is read at plugin load, so comparing four anchors through Gpp.json is four reloads and a
    // lost sense of what the last one looked like. This switches live: every panel up is torn down and
    // hung again from the new anchor, which is exactly what happens at a phase change anyway.
    internal string SetAnchor(string mode, string attachment)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return $"Anchor is {_follow}"
                + (_follow == Follow.Attachment ? $" on \"{Config.HudAnchorAttachment}\"" : "")
                + $". point_orient {(Schema.GetClassSize("CPointOrient") > 0 ? "is" : "is NOT")} in this build's schema.";
        }

        var wanted = mode.Trim().ToLowerInvariant() switch
        {
            "orient" => Follow.Orient,
            "attachment" or "attach" => Follow.Attachment,
            "pawn" or "model" => Follow.Pawn,
            "teleport" or "follow" => Follow.Teleport,
            _ => (Follow?)null,
        };
        if (wanted == null) return "Anchor is one of: orient, attachment, pawn, teleport.";

        if ((wanted == Follow.Orient || wanted == Follow.Attachment) && Schema.GetClassSize("CPointOrient") <= 0)
        {
            return "point_orient is not in this build's schema - orient and attachment cannot work here.";
        }

        if (!string.IsNullOrWhiteSpace(attachment)) Config.HudAnchorAttachment = attachment;
        _follow = wanted.Value;

        // Torn down rather than re-parented: an entity's offset from its parent is frozen when it is
        // parented, so moving a live panel between anchors means unpicking exactly the state that is
        // hardest to see. A fresh panel is one spawn per viewer and no doubt about what it is hanging
        // from.
        int rehung = Rehang();

        return $"Anchor is now {_follow}"
            + (_follow == Follow.Attachment ? $" on \"{Config.HudAnchorAttachment}\"" : "")
            + $", {rehung} viewer(s) rehung.";
    }
}

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

        // What CheckTransmit needs, kept as a plain array rather than rebuilt by LINQ: that hook runs
        // once per viewer per frame, so on a ten-man it is a hundred passes a frame and every
        // allocation in it is paid a hundred times.
        internal CEntityInstance[] Entities = [];

        internal void Rebuild()
        {
            var live = new List<CEntityInstance>(4);
            foreach (var board in Boards)
            {
                if (board.Text != null && board.Text.IsValid) live.Add(board.Text);
            }
            if (Orient != null && Orient.IsValid) live.Add(Orient);
            Entities = [.. live];
        }
    }

    private readonly Dictionary<int, ViewerPanel> _panels = [];

    // How the boards keep up with the view.
    //
    // Orient:   a point_orient parented to the pawn and aimed down its eyeline, with the boards
    //           parented to that. Position and rotation then ride the client's own prediction of its
    //           pawn, so there is nothing to interpolate and nothing to send per tick.
    // Teleport: moved by the server every tick. Always works, always trails about a tenth of a second
    //           behind the camera, and only ever used if point_orient leaves the schema the way the
    //           viewmodel did.
    //
    // Two other mounts were built and compared on a 4K client, 2026-10-19, and both are gone: parenting
    // the orient to a look attachment on the player model was indistinguishable from the plain orient,
    // which means these models do not carry the attachment, and parenting the boards straight to the
    // pawn swings them about on every turn because the pawn's rotation is the body yaw.
    private enum Follow { Orient, Teleport }

    private Follow _follow = Follow.Teleport;

    // Degrees the view may turn before a Pawn-tier panel is re-squared. A panel that is re-teleported
    // every tick throws away the prediction that parenting just bought.
    private const float AimTolerance = 1.5f;

    // What a board is made of, measured on a client 2026-10-19: FontSize stays put and WorldUnitsPerPx
    // does the sizing, because raising FontSize grows the glyphs without moving the lines apart.
    //
    // The sizes, the distance and where the side boards sit live in WorldGeometry, which has no engine
    // in it - so the tests can check the one thing that has gone wrong repeatedly, which is three boards
    // that do not fit across the view and end up on top of each other.
    private const float BaseFontSize = WorldGeometry.BaseFontSize;
    private const float PanelDistance = WorldGeometry.Distance;
    private const float PanelUpOffset = 9f;

    private static float PanelScale() => WorldGeometry.Scale();

    // Centre to centre between the middle board and a side board. !pug sides overrides it, for when the
    // derived number still looks wrong in front of a real client; !pug sides auto hands it back.
    private float SideGap => _sideGapOverride
        ?? (Config.HudSideGap > 0f ? Config.HudSideGap : WorldGeometry.SideGap());

    private float? _sideGapOverride;
    private float _sideToeIn = 22f;      // degrees each side board turns inward to face the viewer
    private float _sideBack = 0f;        // units further away than the middle board, if they want it

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
        _follow = Schema.GetClassSize("CPointOrient") > 0
            && Schema.GetSchemaOffset("CPointOrient", "m_nGoalDirection") > 0
            && Schema.GetSchemaOffset("CPointOrient", "m_hTarget") > 0
            && Schema.GetSchemaOffset("CPointOrient", "m_bActive") > 0
                ? Follow.Orient
                : Follow.Teleport;

        Logger.LogInformation("World panel follow tier: {Tier}. point_orient size {Size}, m_nGoalDirection at {Offset}.",
            _follow, Schema.GetClassSize("CPointOrient"), Schema.GetSchemaOffset("CPointOrient", "m_nGoalDirection"));
    }

    // Everything below assumes a panel belongs to exactly one player. Without this, ten people in a pug
    // read ten overlapping boards hanging in the room - which is the whole reason the centre panel was
    // per-player too.
    private void WorldPanelTransmit(CCheckTransmitInfoList infos)
    {
        if (Retired || _panels.Count < 2) return;

        foreach (var (info, receiver) in infos)
        {
            if (receiver == null) continue;
            foreach (var (slot, panel) in _panels)
            {
                if (slot == receiver.Slot) continue;
                foreach (var entity in panel.Entities) info.TransmitEntities.Remove(entity);
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

        // Nothing to hang a board on. A spectator's camera is driven client-side and the observer pawn
        // does not reliably carry a position the server can read, so rather than spawn boards at the map
        // origin where nobody is looking, this falls back to the centre-screen panel - which needs no
        // pawn at all. Better the old overlay than no overlay for the people waiting to be picked.
        if (ViewPawn(player) == null)
        {
            HideWorldPanel(player.Slot);
            player.PrintToCenter(CentreFallback(main));
            ReportNoPawn(player);
            return;
        }

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
            panel.Rebuild();
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
        // Centred, all three. The rule at the bottom of each board is what fixes its width now, so
        // justification only decides where the words sit inside a box that is already the right size -
        // and the probe measured CENTER as the one that puts that box on the crosshair.
        entity.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER;
        entity.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_CENTER;
        // The text is drawn in the entity's own plane, so without a roll of 90 it lies flat like a rug.
        // Auto-reorient does not help, because a flat panel's up axis is the wrong one.
        entity.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;
        entity.DispatchSpawn();

        panel.Boards[(int)which].Text = entity;

        Hang(player, panel, which);
        panel.Rebuild();
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
        var pawn = ViewPawn(player);
        var slot = panel.Boards[(int)which];
        if (slot.Text == null || !slot.Text.IsValid || pawn == null || pawn.AbsOrigin == null) return;

        if (_follow == Follow.Teleport)
        {
            Square(slot, which, pawn.AbsOrigin, Eyes(pawn), EyeHeight(pawn));
            panel.AimedAt = Eyes(pawn).Y;
            return;
        }

        if (_follow == Follow.Orient)
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
            // The factory went away under us: fall back to moving the boards by hand rather than
            // drawing nothing at all.
            Logger.LogWarning("point_orient would not spawn - the world panel is moving the boards itself.");
            _follow = Follow.Teleport;
        }

        Square(slot, which, pawn.AbsOrigin, Eyes(pawn), EyeHeight(pawn));
        panel.AimedAt = Eyes(pawn).Y;
    }

    // One orient per viewer, shared by all three boards: it is the expensive half - the thing that has
    // to be aimed - and the boards only differ by a fixed offset within its frame.
    private CPointOrient? EnsureOrient(CCSPlayerController player, ViewerPanel panel, CBasePlayerPawn pawn)
    {
        if (panel.Orient != null && panel.Orient.IsValid && panel.ParentedTo == (int)pawn.Index) return panel.Orient;

        ClearOrient(panel);
        var orient = Utilities.CreateEntityByName<CPointOrient>("point_orient");
        if (orient == null || orient.Handle == IntPtr.Zero || !orient.IsValid) return null;

        orient.Active = true;
        // Aimed down the pawn's eyeline, which is what carries the boards' facing without the server
        // sending an angle every tick.
        orient.GoalDirection = PointOrientGoalDirectionType_t.eEyesForward;
        // Unconstrained: Square() takes the pitch back out of the roll itself, and ePreserveUpAxis would
        // be a second thing doing the same job in a different frame.
        orient.Constraint = PointOrientConstraint_t.eNone;
        orient.DispatchSpawn();
        orient.Teleport(new Vector(pawn.AbsOrigin!.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + EyeHeight(pawn)), null, null);

        // The whole difference between the two orient tiers: what the orient itself hangs from. The
        // pawn's origin is at its feet and its rotation is the body yaw; a look attachment on the model
        // is a head bone, which tracks where somebody is actually looking. Whether this server's player
        // models carry the attachment is not knowable from here - a name the model does not have makes
        // SetParentAttachmentMaintainOffset do nothing, and it stays parented at the origin.
        orient.AcceptInput("SetParent", pawn, null, "!activator");
        orient.AcceptInput("SetTarget", pawn, null, "!activator");

        panel.Orient = orient;
        panel.ParentedTo = (int)pawn.Index;
        panel.Rebuild();
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

        float sideways = which switch { Board.Left => -SideGap, Board.Right => SideGap, _ => 0f };
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
        var pawn = ViewPawn(player);
        if (pawn == null || pawn.AbsOrigin == null) return;
        if (!panel.Live.Any()) return;

        if (_follow != Follow.Teleport && panel.ParentedTo != (int)pawn.Index)
        {
            // New pawn, which means a respawn or a round restart: the old parent is dead and the boards
            // are floating where that pawn used to be.
            Rehang(player, panel);
            return;
        }

        if (_follow == Follow.Orient)
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

        var eyes = Eyes(pawn);
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
        if (Retired || _follow != Follow.Teleport || _panels.Count == 0) return;
        foreach (var (slot, panel) in _panels)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid) continue;
            KeepUp(player, panel);
        }
    }

    // The first few lines of a board, for the centre panel: it holds four lines of about eighty
    // characters, so the title and what follows it is what survives the trip.
    private static string CentreFallback(string panel)
    {
        var lines = panel.Split('\n')
            .Where(line => line.Length > 0 && line != WorldLayout.Filler && !line.StartsWith("---"))
            .Take(HudLayout.PanelLines)
            .ToList();
        return string.Join('\n', lines);
    }

    // Logged once per player per minute, because the alternative is a line a tick. This is the state
    // that cannot be seen from a screenshot: whether the server can find a camera for somebody at all.
    private readonly Dictionary<int, DateTime> _toldAboutPawn = [];

    private void ReportNoPawn(CCSPlayerController player)
    {
        var now = DateTime.UtcNow;
        if (_toldAboutPawn.TryGetValue(player.Slot, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
        _toldAboutPawn[player.Slot] = now;

        var pawn = player.Pawn.Value;
        var observer = player.ObserverPawn.Value;
        Logger.LogInformation(
            "No view pawn for {Name} (team {Team}): PlayerPawn {Player}, Pawn {Base} at {Origin}, ObserverPawn {Observer}, watching {Target}. Centre panel instead.",
            player.PlayerName, player.Team,
            player.PlayerPawn.Value?.IsValid == true ? player.PlayerPawn.Value.Health.ToString() : "none",
            pawn?.IsValid == true ? pawn.DesignerName : "none",
            pawn?.AbsOrigin == null ? "nowhere" : $"{pawn.AbsOrigin.X:F0},{pawn.AbsOrigin.Y:F0},{pawn.AbsOrigin.Z:F0}",
            observer?.IsValid == true ? observer.DesignerName : "none",
            pawn?.ObserverServices?.ObserverTarget?.Value?.DesignerName ?? "nobody");
    }

    private static float EyeHeight(CBasePlayerPawn pawn) => pawn.ViewOffset.Z == 0 ? 64f : pawn.ViewOffset.Z;

    // Whether the server can find a camera for this player at all. False means a spectator, or somebody
    // who has not spawned yet, and there is nowhere to hang a board for them.
    internal bool HasCamera(CCSPlayerController player) => ViewPawn(player) != null;

    // The pawn this player is looking through, which is their own or nothing.
    //
    // Following whoever a spectator was watching was tried and is worse than useless: the boards hang on
    // that player and go wherever they go, so the spectator sees nothing while the server believes it
    // has drawn them a panel - which is also why the alert that was meant to cover this case never
    // fired. Somebody with no pawn of their own gets told so, in a box that needs no pawn.
    private static CBasePlayerPawn? ViewPawn(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        return pawn != null && pawn.IsValid && pawn.Health > 0 && pawn.AbsOrigin != null ? pawn : null;
    }

    // The view angle, which is the one field that is on every pawn. EyeAngles is only on CCSPlayerPawn,
    // so reading it was the other half of why spectators got nothing.
    private static QAngle Eyes(CBasePlayerPawn pawn) => pawn.V_angle;

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
        if (float.TryParse(gap, out float parsedGap)) _sideGapOverride = Math.Clamp(parsedGap, 0f, 400f);
        else if (gap == "auto") _sideGapOverride = null;
        if (float.TryParse(toeIn, out float parsedToe)) _sideToeIn = Math.Clamp(parsedToe, -90f, 90f);
        if (float.TryParse(back, out float parsedBack)) _sideBack = Math.Clamp(parsedBack, -60f, 400f);

        int rehung = Rehang();
        return $"Side boards: gap {SideGap:F0}{(_sideGapOverride == null ? " (derived)" : "")},"
            + $" toe-in {_sideToeIn:F0} degrees, {_sideBack:F0} further back"
            + $" - {rehung} viewer(s) rehung.";
    }

    // What the schema probe chose at load, so the gallery can walk through the mounts and put the real
    // one back afterwards rather than leaving the server on whatever it showed last.
    private Follow? _probedFollow;

}

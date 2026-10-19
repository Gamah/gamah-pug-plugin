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
    // One per viewer. The panel entity, the point_orient it hangs off when parenting is available, and
    // the text last sent - so a 1Hz tick that changes nothing costs nothing.
    private sealed class ViewerPanel
    {
        internal CPointWorldText? Text;
        internal CPointOrient? Orient;
        internal string Sent = "";
        internal float AimedAt = float.NaN;    // the eye yaw the panel was last squared up to
        internal int ParentedTo;               // pawn index the panel was parented to, 0 for none
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
    private enum Follow { Orient, Pawn, Teleport }

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
    private const float PanelScreenPercent = 75f;
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
                if (panel.Text != null && panel.Text.IsValid) info.TransmitEntities.Remove(panel.Text);
                if (panel.Orient != null && panel.Orient.IsValid) info.TransmitEntities.Remove(panel.Orient);
            }
        }
    }

    // Put this text in front of this player. The text is only pushed when it has changed, which is what
    // makes a ticking clock cheap: one networked string a second for the one line that moved.
    //
    // Setting MessageText alone re-flows the lines correctly - measured on a client 2026-10-19 with
    // !pug paneltick, stage B. Setting MessageText *and* FontSize together does not: the glyphs are
    // re-rendered without the lines being re-spaced and the panel comes out overlapped. So the size is
    // set once, at spawn, and never again.
    internal void ShowWorldPanel(CCSPlayerController player, string text)
    {
        if (!player.IsValid || player.IsBot || player.IsHLTV) return;

        if (!_panels.TryGetValue(player.Slot, out var panel))
        {
            panel = new ViewerPanel();
            _panels[player.Slot] = panel;
        }

        if (panel.Text == null || !panel.Text.IsValid)
        {
            if (!SpawnPanel(player, panel, text)) return;
            panel.Sent = text;
            return;
        }

        if (panel.Sent != text)
        {
            panel.Text.MessageText = text;
            Utilities.SetStateChanged(panel.Text, "CPointWorldText", "m_messageText");
            panel.Sent = text;
        }

        KeepUp(player, panel);
    }

    private bool SpawnPanel(CCSPlayerController player, ViewerPanel panel, string text)
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
        entity.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER;
        entity.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_CENTER;
        // The text is drawn in the entity's own plane, so without a roll of 90 it lies flat like a rug.
        // Auto-reorient does not help, because a flat panel's up axis is the wrong one.
        entity.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;
        entity.DispatchSpawn();

        panel.Text = entity;
        panel.Orient = null;
        panel.ParentedTo = 0;
        panel.AimedAt = float.NaN;

        Place(player, panel);
        return true;
    }

    // Puts the panel where it belongs and attaches it, by whichever tier the probe chose. Teleport
    // first, parent second: SetParent freezes whatever local offset the entity has at the time.
    private void Place(CCSPlayerController player, ViewerPanel panel)
    {
        var pawn = player.PlayerPawn.Value;
        if (panel.Text == null || !panel.Text.IsValid || pawn == null || !pawn.IsValid || pawn.AbsOrigin == null) return;

        var eyes = pawn.EyeAngles;
        panel.Text.Teleport(InFront(pawn, eyes), new QAngle(0, eyes.Y + 270f, 90f), new Vector(0, 0, 0));
        panel.AimedAt = eyes.Y;

        if (_follow == Follow.Teleport) return;

        if (_follow == Follow.Orient)
        {
            var orient = Utilities.CreateEntityByName<CPointOrient>("point_orient");
            if (orient != null && orient.Handle != IntPtr.Zero && orient.IsValid)
            {
                orient.Active = true;
                // Aimed down the pawn's eyeline, which is what carries the panel's facing without the
                // server sending an angle every tick.
                orient.GoalDirection = PointOrientGoalDirectionType_t.eEyesForward;
                orient.DispatchSpawn();
                orient.Teleport(new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + EyeHeight(pawn)),
                    null, null);
                orient.AcceptInput("SetParent", pawn, null, "!activator");
                orient.AcceptInput("SetTarget", pawn, null, "!activator");
                panel.Orient = orient;
                panel.Text.AcceptInput("SetParent", orient, null, "!activator");
                panel.ParentedTo = (int)pawn.Index;
                return;
            }
            // The factory went away under us: carry on at the next tier down rather than drawing nothing.
            Logger.LogWarning("point_orient would not spawn - the world panel is falling back to pawn parenting.");
            _follow = Follow.Pawn;
        }

        panel.Text.AcceptInput("SetParent", pawn, null, "!activator");
        panel.ParentedTo = (int)pawn.Index;
    }

    // Called on every text update. A parented panel usually needs nothing; this is what notices a
    // respawn (the pawn it was parented to is gone) and, on the pawn tier, that the view has turned far
    // enough to be worth re-squaring.
    private void KeepUp(CCSPlayerController player, ViewerPanel panel)
    {
        var pawn = player.PlayerPawn.Value;
        if (panel.Text == null || !panel.Text.IsValid || pawn == null || !pawn.IsValid || pawn.AbsOrigin == null) return;

        if (_follow != Follow.Teleport && panel.ParentedTo != (int)pawn.Index)
        {
            // New pawn, which means a respawn or a round restart: the old parent is dead and the panel
            // is floating where that pawn used to be.
            ClearOrient(panel);
            Place(player, panel);
            return;
        }

        if (_follow == Follow.Orient) return;

        var eyes = pawn.EyeAngles;
        if (_follow == Follow.Pawn)
        {
            float turned = Math.Abs(Delta(eyes.Y, panel.AimedAt));
            if (float.IsNaN(panel.AimedAt) || turned > AimTolerance)
            {
                panel.Text.AcceptInput("ClearParent", null, null, "!activator");
                panel.Text.Teleport(InFront(pawn, eyes), new QAngle(0, eyes.Y + 270f, 90f), new Vector(0, 0, 0));
                panel.Text.AcceptInput("SetParent", pawn, null, "!activator");
                panel.AimedAt = eyes.Y;
            }
            return;
        }

        panel.Text.Teleport(InFront(pawn, eyes), new QAngle(0, eyes.Y + 270f, 90f), new Vector(0, 0, 0));
        panel.AimedAt = eyes.Y;
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

    // Where the panel hangs: in front of the eyes, lifted above the crosshair along the camera's own up
    // axis so the offset stays above the crosshair when they are looking up or down rather than above
    // the world.
    private static Vector InFront(CCSPlayerPawn pawn, QAngle eyes)
    {
        double yaw = eyes.Y * Math.PI / 180.0;
        double pitch = eyes.X * Math.PI / 180.0;

        var forward = new Vector(
            (float)(Math.Cos(pitch) * Math.Cos(yaw)),
            (float)(Math.Cos(pitch) * Math.Sin(yaw)),
            (float)-Math.Sin(pitch));
        var right = new Vector((float)-Math.Sin(yaw), (float)Math.Cos(yaw), 0);
        var up = new Vector(
            (forward.Y * right.Z) - (forward.Z * right.Y),
            (forward.Z * right.X) - (forward.X * right.Z),
            (forward.X * right.Y) - (forward.Y * right.X));

        return new Vector(
            pawn.AbsOrigin!.X + (forward.X * PanelDistance) + (up.X * PanelUpOffset),
            pawn.AbsOrigin.Y + (forward.Y * PanelDistance) + (up.Y * PanelUpOffset),
            pawn.AbsOrigin.Z + EyeHeight(pawn) + (forward.Z * PanelDistance) + (up.Z * PanelUpOffset));
    }

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
        if (panel.Text != null && panel.Text.IsValid) panel.Text.Remove();
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
}

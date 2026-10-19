using System.Drawing;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace Gpp;

// A prototype of the overlay drawn as world text instead of centre-screen text.
//
// Everything the centre panel forced on us - four lines, about eighty characters, a measured glyph
// table, a line budget that depends on the viewer's resolution, a scroller, and a redraw loop to beat
// the fade - is a property of PrintToCenter, not of CS2. A point_worldtext is a stock entity the server
// spawns: as many lines as you like, a font size you pick, colour per panel, and it stays put until it
// is deleted. Nothing is downloaded, so no workshop addon and no MultiAddonManager.
//
// What this does NOT do yet, deliberately, because it is a prototype to be looked at rather than used:
//   - one entity per viewer with transmit filtering, so ten people do not read each other's panels
//   - re-parenting across death, respawn and round restart
//   - any input: chat stays the way a captain picks
//
// [SOURCE: CPointWorldText fields read from CounterStrikeSharp 1.0.376's generated schema, 2026-10-19 -
// MessageText, FontName, FontSize, Color, WorldUnitsPerPx, DrawBackground, Justify*, ReorientMode.]
public partial class Gpp
{
    private CPointWorldText? _panelEntity;
    private CCSPlayerController? _panelViewer;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _panelTimer;

    // Follow the player's view, or stand still in the world once placed.
    //
    // Following always trails, and not because of the update rate: a point_worldtext is a networked
    // entity, so the client interpolates its position over about a tenth of a second however often the
    // server moves it. The only way to beat that is to parent it to something the client moves with the
    // camera itself - the viewmodel - which CounterStrikeSharp 1.0.376 does not expose in its generated
    // schema (there is no CCSPlayer_ViewModelServices class), so that needs raw offsets.
    //
    // Standing still has no lag by construction, because nothing moves. A draft list you look at is
    // arguably the better object anyway: a board on the wall rather than something stuck to your face.
    // [SOURCE: schema checked against CounterStrikeSharp 1.0.376, 2026-10-19]
    private bool _panelFollows = true;
    private bool _panelPlaced;

    // How far in front of the eye the panel floats, how much of the screen it should fill, and how far
    // above the crosshair it sits so the gun is not in the way. All three are command arguments,
    // because the only way to judge any of them is to stand in front of one.
    private float _panelDistance = 70f;
    private float _panelScreenPercent = 75f;
    private float _panelUpOffset = 9f;

    // The background box sizes itself from the border values rather than from the text block, so it
    // framed the lower half of the panel and ran wider at the bottom - which read as a second panel
    // behind the first. Off by default until that is understood; the text is drawn fullbright, so it
    // holds up on a bright map without it. !pug panel bg turns it on to experiment with.
    private bool _panelBackground = true;

    // The size underneath is world units - FontSize multiplied by WorldUnitsPerPx - so "half the
    // screen" is not something the entity understands. It can be worked out, because the panel sits at
    // a known distance in front of the eye: a block of height H at distance D fills
    // 2*atan(H/2D) of the view, so H = 2*D*tan(fraction*vFov/2).
    //
    // The assumptions, which is where this gets approximate: 90 degrees horizontal FOV on 16:9, giving
    // about 59 vertical, and a line of text about 1.2 times its font size tall. The server cannot read
    // a player's aspect ratio or their FOV, so anybody on 4:3 stretched sees a different panel. Hence a
    // percentage knob to taste rather than a promise.
    private const float AssumedVerticalFov = 59f;
    private const float LineHeightFactor = 1.2f;

    // FontSize is left alone and the panel is scaled with WorldUnitsPerPx instead.
    //
    // Raising FontSize makes the glyphs bigger without moving the lines apart, so past a certain size
    // the text grows into itself - which is what "narrow and overlapped" was, and why it got worse
    // every time I enlarged it. It also explains the background box: that is sized in the same world
    // units, so it framed a block of a different size than the glyphs were drawn at.
    //
    // WorldUnitsPerPx is how much world each text pixel occupies, so it scales the whole rendered
    // block - spacing, background and all - uniformly. 64 is a plain readable size to render at.
    private const float BaseFontSize = 64f;

    // One text size, always, whatever is on the panel.
    //
    // Scaling to fit meant a long list was drawn smaller than a short one - the text changed size
    // between the map vote and the draft, which reads as the panel moving rather than the content
    // changing. So the scale is solved once for a reference block and then left alone: a panel with
    // more lines is taller, not smaller.
    //
    // Twelve lines filling the wanted share of the screen, so a line is about a twelfth of it. A list
    // longer than that runs off the top, which is a layout problem - columns, or paging - rather than
    // something to solve by shrinking the type until nobody can read it.
    private const int ReferenceLines = 12;

    private float FixedScale()
    {
        double half = AssumedVerticalFov * Math.PI / 360.0;
        double worldHeight = 2.0 * _panelDistance * Math.Tan(half) * (_panelScreenPercent / 100.0);
        double pixelsTall = ReferenceLines * BaseFontSize * LineHeightFactor;
        return (float)(worldHeight / pixelsTall);
    }

    internal void WorldPanelDemo(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid)
        {
            Logger.LogInformation("!pug panel has to be run by a player - it draws in front of their face.");
            return;
        }

        StopWorldPanel();
        _panelViewer = player;
        Announce("World text panel: map select, then the draft, 10s each. !pug panel stop ends it.");

        var maps = Config.MapPool.Take(12).Select((map, i) => $"{i + 1,2}  {map.Label}").ToList();
        string mapPanel = "PICK A MAP\n\n" + Columned(maps, 3) + "\n\ntype !1 - !12 in chat";

        // Eighteen, which is a twenty-man pug once the two captains are out of the pool - the case
        // that decides whether one column can work at all.
        var names = new[]
        {
            "Ada", "Baz", "Cosmo", "Fitzwilliam", "Rouchard", "Slingshot", "Enforcer",
            "☆Supreme☆Toxicity☆", "Madzie", "Kittana", "Tani Rani", "giant panda!",
            "SeventeenMidgets", "BadWolfKnight", "peterwrinkle", "da iceman", "Breezy", "Tea.",
        };
        // One name per line: padding columns with spaces only lines up in a monospace font, and this is
        // not one. Real columns would want each column as its own entity, positioned.
        string draftPanel = "YOUR PICK\n\n"
            + string.Join("\n", names.Select((name, i) => $"{i + 1,2}  {name}"))
            + "\n\n30s   18 left   type !1 - !18";

        Spawn(mapPanel);
        AddTimer(10.0f, () => { if (_panelEntity != null) SetText(draftPanel); });
        AddTimer(20.0f, StopWorldPanel);
    }

    // Padded columns, the same idea as the centre panel's but without a width budget to fight: the
    // panel is as wide as it needs to be.
    private static string Columned(List<string> cells, int perRow)
    {
        int width = cells.Max(cell => cell.Length) + 4;
        var lines = new List<string>();
        for (int start = 0; start < cells.Count; start += perRow)
        {
            lines.Add(string.Concat(cells.Skip(start).Take(perRow).Select(cell => cell.PadRight(width))).TrimEnd());
        }
        return string.Join("\n", lines);
    }

    private void Spawn(string text)
    {
        var entity = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
        if (entity == null || !entity.IsValid)
        {
            Announce("Could not create a point_worldtext - see the log.");
            Logger.LogError("CreateEntityByName(\"point_worldtext\") returned nothing.");
            return;
        }

        entity.MessageText = text;
        entity.Enabled = true;
        entity.FontSize = BaseFontSize;
        // Arial Bold because every client has it; the HUD's own Stratum2 is not addressable by name.
        entity.FontName = "Arial Bold";
        entity.Color = Color.FromArgb(255, 255, 255, 255);
        entity.Fullbright = true;                 // not lit by the map, so it reads the same everywhere
        entity.WorldUnitsPerPx = FixedScale();
        entity.DrawBackground = _panelBackground;
        entity.BackgroundBorderWidth = 0.25f;
        entity.BackgroundBorderHeight = 0.25f;
        entity.DepthOffset = 0f;
        entity.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER;
        entity.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_CENTER;
        // Left alone, and the angles are worked out in Follow() instead. Reorienting around the up axis
        // sounds like what is wanted and is not: the text is drawn in the entity's own plane, so without
        // a roll it lies flat like a rug and is only legible from directly above it.
        entity.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;

        entity.DispatchSpawn();
        _panelEntity = entity;

        Follow();
        // Every tick rather than on a timer: it does not beat the client's interpolation, but it is the
        // most the server can do, and it keeps the comparison between the two modes honest.
        if (_panelFollows) RegisterListener<Listeners.OnTick>(Follow);
    }

    // A new panel is a new entity. Changing MessageText and FontSize on a live one re-rendered the
    // text without re-flowing it, so the second panel came out with its lines on top of each other:
    // the line spacing appears to be worked out once, when the entity spawns.
    private void SetText(string text)
    {
        if (_panelEntity != null && _panelEntity.IsValid) _panelEntity.Remove();
        _panelEntity = null;
        RemoveListener<Listeners.OnTick>(Follow);
        _panelPlaced = false;
        Spawn(text);
    }

    // Keeps the panel in front of the viewer's eyes. Teleporting it rather than parenting it to the
    // viewmodel: fewer moving parts for a prototype, and it is obvious when it is wrong.
    private void Follow()
    {
        if (_panelEntity == null || !_panelEntity.IsValid || _panelViewer == null || !_panelViewer.IsValid)
        {
            return;
        }
        // Placed once and then left: the panel is somewhere in the room rather than on your face.
        if (!_panelFollows && _panelPlaced) return;
        _panelPlaced = true;

        var pawn = _panelViewer.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null) return;

        var eyes = pawn.EyeAngles;
        double yaw = eyes.Y * Math.PI / 180.0;
        double pitch = eyes.X * Math.PI / 180.0;

        // Source angles: X is pitch (down positive), Y is yaw.
        var forward = new Vector(
            (float)(Math.Cos(pitch) * Math.Cos(yaw)),
            (float)(Math.Cos(pitch) * Math.Sin(yaw)),
            (float)-Math.Sin(pitch));

        // Camera-relative up, so the offset keeps the panel above the crosshair rather than above the
        // world when they are looking up or down. up = right x forward, with right flat on the ground.
        var right = new Vector((float)-Math.Sin(yaw), (float)Math.Cos(yaw), 0);
        // forward x right, not right x forward: the other way round pointed the offset at the floor.
        var up = new Vector(
            (forward.Y * right.Z) - (forward.Z * right.Y),
            (forward.Z * right.X) - (forward.X * right.Z),
            (forward.X * right.Y) - (forward.Y * right.X));

        float eyeHeight = pawn.ViewOffset.Z == 0 ? 64f : pawn.ViewOffset.Z;
        var at = new Vector(
            pawn.AbsOrigin.X + (forward.X * _panelDistance) + (up.X * _panelUpOffset),
            pawn.AbsOrigin.Y + (forward.Y * _panelDistance) + (up.Y * _panelUpOffset),
            pawn.AbsOrigin.Z + eyeHeight + (forward.Z * _panelDistance) + (up.Z * _panelUpOffset));

        // Standing upright and turned to face them. Roll 90 is what stands the text plane up; the yaw
        // is a quarter turn behind theirs so its face points back down their eyeline.
        var facing = new QAngle(0, eyes.Y + 270f, 90f);
        _panelEntity.Teleport(at, facing, new Vector(0, 0, 0));
    }

    internal void StopWorldPanel()
    {
        _panelTimer?.Kill();
        _panelTimer = null;
        RemoveListener<Listeners.OnTick>(Follow);
        _panelPlaced = false;

        if (_panelEntity != null && _panelEntity.IsValid) _panelEntity.Remove();
        _panelEntity = null;
        _panelViewer = null;
    }
}

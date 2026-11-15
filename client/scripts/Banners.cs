using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;

namespace IH.Client;

/// <summary>
/// Screen-space city plates and readable, individually clickable unit markers.
/// </summary>
public partial class CityBanners : Control
{
    private readonly GameScreen _screen;
    private readonly List<(Rect2 rect, int city)> _hit = new();
    private readonly List<(Vector2 center, float radius, int id)> _unitHit = new();
    private readonly Dictionary<Vector2I, List<Rect2>> _occupied = new();
    private int _hoverUnit = -1;
    private string _signature = "";

    public CityBanners(GameScreen screen)
    {
        _screen = screen;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>Id of the city whose plate is under the point, or -1.</summary>
    public int CityAt(Vector2 at)
    {
        for (int i = _hit.Count - 1; i >= 0; i--)
            if (_hit[i].rect.HasPoint(at)) return _hit[i].city;
        return -1;
    }

    public int UnitAt(Vector2 at)
    {
        for (int i = _unitHit.Count - 1; i >= 0; i--)
        {
            var hit = _unitHit[i];
            if (at.DistanceSquaredTo(hit.center) <= hit.radius * hit.radius) return hit.id;
        }
        return -1;
    }

    public override void _Process(double delta)
    {
        // redraw only when the camera or the world has changed, so an idle game costs nothing
        var map = _screen.Map;
        string sig = $"{map.Cam.X:0.00}|{map.Cam.Y:0.00}|{map.Zoom:0.000}|{map.Yaw:0.000}|{_screen.W.Revision}|{map.UnitProjectionRevision}|{Size.X}|{Size.Y}";
        int hover = UnitAt(GetLocalMousePosition());
        if (sig == _signature && hover == _hoverUnit) return;
        _signature = sig;
        _hoverUnit = hover;
        QueueRedraw();
    }

    public override void _Draw()
    {
        _hit.Clear();
        _unitHit.Clear();
        _occupied.Clear();
        var w = _screen.W;
        var map = _screen.Map;
        int lod = map.Lod;
        int fs = lod >= 2 ? 14 : lod == 1 ? 12 : 10;
        var font = Ui.Bold;
        float h = fs + 10;
        foreach (var c in w.Cities.Values.OrderBy(c => c.Owner == w.MyId).ThenBy(c => c.Id))
        {
            if (w.Vis[c.Tile] == 0 || !map.Project(c.Tile, lod >= 2 ? 1.75f : 1.2f, out var anchor)) continue;
            if (anchor.X < -200 || anchor.Y < -80 || anchor.X > Size.X + 200 || anchor.Y > Size.Y + 80) continue;
            var color = map.OwnerColor(c.Owner);
            bool mine = c.Owner == w.MyId;
            string pop = c.Pop.ToString();
            int garrison = w.UnitsAt(c.Tile).Count(u => u.Def.Str > 0);
            bool faith = c.Religion != 0 && lod >= 1;
            float nameW = font.GetStringSize(c.Name, HorizontalAlignment.Left, -1, fs).X;
            float popW = Math.Max(h, font.GetStringSize(pop, HorizontalAlignment.Left, -1, fs).X + 12);
            float star = c.Capital ? fs + 2 : 0, rel = faith ? fs + 1 : 0;
            string guard = garrison > 0 && lod >= 1 ? garrison.ToString() : "";
            float guardW = guard.Length > 0 ? fs + 3 + font.GetStringSize(guard, HorizontalAlignment.Left, -1, fs - 2).X : 0;
            float width = popW + 7 + star + nameW + (rel > 0 ? rel + 4 : 0) + (guardW > 0 ? guardW + 5 : 0) + 10;
            var rect = new Rect2(anchor.X - width / 2, anchor.Y - h, width, h);
            if (w.Vis[c.Tile] == 1) color = color.Darkened(0.3f);

            // the plate: dark pill with the owner's colour as a border and as the population cap
            var bg = Ui.Box(c.Disorder ? Ui.Col(0x3A141A, 0.95f) : Ui.Col(0x0B101C, 0.9f), c.Disorder ? Ui.Bad : color, (int)(h / 2), 0, 0, mine ? 2 : 1);
            DrawStyleBox(bg, rect);
            var cap = Ui.Box(color, null, (int)(h / 2), 0, 0);
            cap.CornerRadiusTopRight = cap.CornerRadiusBottomRight = 3;
            DrawStyleBox(cap, new Rect2(rect.Position, new Vector2(popW, h)));
            float baseline = rect.Position.Y + (h - font.GetHeight(fs)) / 2 + font.GetAscent(fs);
            var popSize = font.GetStringSize(pop, HorizontalAlignment.Left, -1, fs);
            DrawString(font, new Vector2(rect.Position.X + (popW - popSize.X) / 2, baseline), pop, HorizontalAlignment.Left, -1, fs,
                color.Luminance > 0.55f ? new Color(0.06f, 0.08f, 0.12f) : Colors.White);
            float x = rect.Position.X + popW + 7;
            if (c.Capital) { Icon.Paint(this, Ico.Star, new Rect2(x, rect.Position.Y + (h - fs) / 2, fs, fs), Ui.Gold); x += star; }
            DrawString(font, new Vector2(x, baseline), c.Name, HorizontalAlignment.Left, -1, fs, c.Disorder ? Ui.Col(0xFFB3AC) : Ui.Text);
            x += nameW;
            if (faith) { x += 4; Icon.Paint(this, Ico.Faith, new Rect2(x, rect.Position.Y + (h - fs + 2) / 2, fs - 2, fs - 2), Ui.Faith); x += rel; }
            if (guardW > 0)
            {
                x += 5;
                Icon.Paint(this, Ico.Shield, new Rect2(x, rect.Position.Y + (h - fs + 2) / 2, fs - 2, fs - 2), Ui.Dim);
                DrawString(font, new Vector2(x + fs + 1, baseline), guard, HorizontalAlignment.Left, -1, fs - 2, Ui.Dim);
            }
            _hit.Add((rect, c.Id));
            Reserve(rect.Grow(2));

            // own cities: growth and production under the plate
            if (!mine || c.Detail is not { } d || lod == 0) continue;
            float bx = rect.Position.X + 8, bw = width - 16, by = rect.End.Y + 2;
            Reserve(new Rect2(bx, by, bw, 7).Grow(2));
            DrawRect(new Rect2(bx, by, bw, 3), Ui.Col(0x0B101C, 0.85f));
            DrawRect(new Rect2(bx, by, bw * Mathf.Clamp(d.Food / (float)Math.Max(1, d.FoodNeed), 0, 1), 3), d.FoodRate < 0 ? Ui.Bad : Ui.Food);
            by += 4;
            string item = d.Queue.Length > 0 ? d.Queue[0] : "";
            int cost = item.Length > 0 && item != Rules.Wealth ? Rules.ItemCost(item) : 0;
            DrawRect(new Rect2(bx, by, bw, 3), Ui.Col(0x0B101C, 0.85f));
            if (cost > 0) DrawRect(new Rect2(bx, by, bw * Mathf.Clamp(d.Prod / (float)cost, 0, 1), 3), Ui.Prod);
            if (lod < 2) continue;
            string what = d.DisorderTurns > 0 ? $"беспорядки: {d.DisorderTurns} х." : item.Length == 0 ? "простаивает"
                : item == Rules.Wealth ? "богатство" : $"{Rules.ItemName(item)} · {(d.ProdRate > 0 ? Math.Max(1, (cost - d.Prod + d.ProdRate - 1) / d.ProdRate) + " х." : "∞")}";
            var plain = ThemeDB.FallbackFont;
            float ww = plain.GetStringSize(what, HorizontalAlignment.Left, -1, 11).X;
            var tag = new Rect2(anchor.X - ww / 2 - 6, by + 5, ww + 12, 16);
            Reserve(tag.Grow(2));
            DrawStyleBox(Ui.Box(Ui.Col(0x0B101C, 0.78f), null, 8, 0, 0), tag);
            DrawString(plain, new Vector2(tag.Position.X + 6, tag.Position.Y + 12), what, HorizontalAlignment.Left, -1, 11,
                d.DisorderTurns > 0 || item.Length == 0 ? Ui.Col(0xFF9C92) : Ui.Col(0xC8D0E0));
        }
        DrawUnits(map);
    }

    private void DrawUnits(MapView map)
    {
        var w = _screen.W;
        var formations = new List<(int tile, List<(UnitDto unit, Vector2 head)> members)>();
        var standing = new Dictionary<int, List<(UnitDto unit, Vector2 head)>>();
        foreach (var unit in w.Units.Values.OrderBy(u => u.Id))
        {
            if (!map.ProjectUnit(unit.Id, out var head, out bool moving)) continue;
            if (!new Rect2(Vector2.Zero, Size).Grow(80).HasPoint(head)) continue;
            if (moving)
            {
                formations.Add((unit.Tile, new() { (unit, head) }));
                continue;
            }
            if (!standing.TryGetValue(unit.Tile, out var members))
            {
                members = new();
                standing.Add(unit.Tile, members);
                formations.Add((unit.Tile, members));
            }
            members.Add((unit, head));
        }

        float radius = map.Lod switch { 0 => 9f, 1 => 11f, _ => 13f };
        float spacing = 2f * radius + 3f;
        foreach (var formation in formations.OrderByDescending(g => g.members.Any(x => x.unit.Id == map.SelectedUnit))
                     .ThenByDescending(g => g.members[0].unit.Owner == w.MyId).ThenBy(g => g.tile))
        {
            var members = formation.members;
            float top = members.Min(x => x.head.Y);
            float middle = members.Average(x => x.head.X);
            var positions = members.Select(x => x.head - Vector2.Down * (radius + 3f)).ToArray();
            if (members.Count == 2)
            {
                float dx = Mathf.Max(spacing / 2f, Mathf.Abs(positions[0].X - positions[1].X) / 2f);
                positions[0] = new Vector2(middle - dx, top - radius - 3f);
                positions[1] = new Vector2(middle + dx, top - radius - 3f);
            }
            else if (members.Count == 3)
            {
                float dx = Mathf.Max(spacing / 2f, Mathf.Abs(positions[1].X - positions[2].X) / 2f);
                float y = Mathf.Min(members[1].head.Y, members[2].head.Y) - radius - 3f;
                positions[0] = new Vector2(middle, Mathf.Min(positions[0].Y, y - spacing * 0.87f));
                positions[1] = new Vector2(middle - dx, y);
                positions[2] = new Vector2(middle + dx, y);
            }

            // Move the formation as a group so collision avoidance preserves its shape.
            if (!FindBadgeOffset(positions, radius, out var offset)) continue;
            for (int i = 0; i < members.Count; i++)
            {
                var (unit, head) = members[i];
                var at = positions[i] + offset;
                Reserve(BadgeRect(at, radius));
                DrawUnitBadge(unit, head, at, radius, unit.Id == map.SelectedUnit, map.OwnerColor(unit.Owner));
            }
        }

        int hovered = UnitAt(GetLocalMousePosition());
        var hover = _unitHit.Find(x => x.id == hovered);
        if (hovered < 0 || !w.Units.TryGetValue(hovered, out var data)) return;
        string label = data.Hp < 100 ? $"{data.Def.Name} · {data.Hp}%" : data.Def.Name;
        const int fs = 12;
        var font = ThemeDB.FallbackFont;
        float width = font.GetStringSize(label, HorizontalAlignment.Left, -1, fs).X + 14;
        var pos = new Vector2(Mathf.Clamp(hover.center.X - width / 2f, 0, Mathf.Max(0f, Size.X - width)),
            Mathf.Max(0f, hover.center.Y - radius - 26f));
        DrawStyleBox(Ui.Box(Ui.Col(0x1C2928, 0.97f), null, 7, 0, 0), new Rect2(pos, new Vector2(width, 21)));
        DrawString(font, pos + new Vector2(7, 15), label, HorizontalAlignment.Left, -1, fs, Colors.White);
    }

    private void DrawUnitBadge(UnitDto unit, Vector2 head, Vector2 at, float r, bool selected, Color team)
    {
        var stem = head - at;
        if (stem.Length() > r + 4)
            DrawLine(at + stem.Normalized() * r, head, Ui.Col(0x1C2928, 0.48f), 1f, true);
        DrawCircle(at + new Vector2(0, 1.5f), r + 1.5f, Ui.Col(0x14211E, 0.43f), antialiased: true);
        DrawCircle(at, r, selected ? Ui.Gold : team.Lightened(0.12f), antialiased: true);
        DrawCircle(at, r - (selected ? 2.8f : 2f), Ui.Col(0x253936), antialiased: true);
        float side = r * 1.17f;
        var ink = unit.Owner == _screen.W.MyId && (unit.Moves == 0 || unit.Fortified || unit.Work != 0)
            ? Ui.Col(0xB5BCB1) : Ui.Col(0xFFFAE9);
        Icon.Paint(this, GameScreen.UnitIcon(unit.Def), new Rect2(at - Vector2.One * side / 2, Vector2.One * side), ink);
        if (unit.Hp < 100)
            DrawArc(at, r - 0.75f, Mathf.Pi * 0.2f, Mathf.Pi * (0.2f + 0.6f * (1 - unit.Hp / 100f)),
                12, Ui.Bad, 2, true);
        _unitHit.Add((at, r, unit.Id));
    }

    private static Rect2 BadgeRect(Vector2 point, float radius) =>
        new(point - Vector2.One * (radius + 1), Vector2.One * (radius + 1) * 2);

    private bool FindBadgeOffset(Vector2[] positions, float radius, out Vector2 result)
    {
        float step = radius * 1.25f;
        var viewport = new Rect2(Vector2.Zero, Size);
        for (int ring = 0; ring <= 5; ring++)
        {
            int points = ring == 0 ? 1 : ring * 8;
            for (int p = 0; p < points; p++)
            {
                float angle = Mathf.Tau * p / points - Mathf.Pi / 2;
                var shift = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (ring * step);
                if (!positions.All(at => viewport.Encloses(BadgeRect(at + shift, radius)) && !Blocked(BadgeRect(at + shift, radius)))) continue;
                result = shift;
                return true;
            }
        }
        result = Vector2.Zero;
        return false;
    }

    private const int CellSize = 40;

    private static IEnumerable<Vector2I> CellsFor(Rect2 rect)
    {
        int left = Mathf.FloorToInt(rect.Position.X / CellSize);
        int right = Mathf.FloorToInt(rect.End.X / CellSize);
        int top = Mathf.FloorToInt(rect.Position.Y / CellSize);
        int bottom = Mathf.FloorToInt(rect.End.Y / CellSize);
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
                yield return new Vector2I(x, y);
    }

    private void Reserve(Rect2 rect)
    {
        foreach (var cell in CellsFor(rect))
        {
            if (!_occupied.TryGetValue(cell, out var entries)) _occupied[cell] = entries = new();
            entries.Add(rect);
        }
    }

    private bool Blocked(Rect2 rect)
    {
        foreach (var cell in CellsFor(rect))
            if (_occupied.TryGetValue(cell, out var entries) && entries.Any(other => other.Intersects(rect))) return true;
        return false;
    }
}

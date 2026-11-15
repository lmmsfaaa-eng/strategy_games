namespace IH.Shared;

/// <summary>Pointy-top hex torus in "odd-r" offset coordinates. Tile index = y * W + x.</summary>
public sealed class HexGrid
{
    public readonly int W, H;
    public readonly int[][] Nb;

    private static readonly (int dx, int dy)[] EvenRow = { (1, 0), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1) };
    private static readonly (int dx, int dy)[] OddRow = { (1, 0), (1, -1), (0, -1), (-1, 0), (0, 1), (1, 1) };

    public HexGrid(int w, int h)
    {
        W = w; H = h;
        Nb = new int[w * h][];
        var tmp = new List<int>(6);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                tmp.Clear();
                foreach (var (dx, dy) in (y & 1) == 0 ? EvenRow : OddRow)
                {
                    int neighbor = WrapIndex(x + dx, y + dy);
                    if (neighbor != y * w + x && !tmp.Contains(neighbor)) tmp.Add(neighbor);
                }
                Nb[y * w + x] = tmp.ToArray();
            }
    }

    public int Count => W * H;
    public int X(int idx) => idx % W;
    public int Y(int idx) => idx / W;
    public bool Valid(int idx) => idx >= 0 && idx < W * H;

    /// <summary>Offset coordinates in any repeat of the map. Odd-height legacy maps have a half-hex shear.</summary>
    public int WrapIndex(int x, int y)
    {
        int row = Mod(y, H);
        int periods = (y - row) / H;
        int column = x - (y >> 1) + periods * (H >> 1) + (row >> 1);
        return row * W + Mod(column, W);
    }

    private static int Mod(int value, int period)
    {
        int remainder = value % period;
        return remainder < 0 ? remainder + period : remainder;
    }

    public int Distance(int a, int b)
    {
        int ay = Y(a), by = Y(b), dr = ay - by;
        int dx2 = 2 * (X(a) - X(b)) + (ay & 1) - (by & 1);
        int best = ImageDistance(dx2, dr);
        int shear = H & 1;
        if (Math.Abs(dr - H) < best) best = Math.Min(best, ImageDistance(dx2 - shear, dr - H));
        if (Math.Abs(dr + H) < best) best = Math.Min(best, ImageDistance(dx2 + shear, dr + H));
        return best;
    }

    private int ImageDistance(int dx2, int dr)
    {
        // For each vertical image, the shortest horizontal image minimizes |dq + dr/2|.
        int horizontal2 = Mod(dx2 + W, W * 2) - W;
        int dq = (horizontal2 - dr) / 2;
        return (Math.Abs(dq) + Math.Abs(dq + dr) + Math.Abs(dr)) / 2;
    }

    /// <summary>All tiles within <paramref name="radius"/> of the center (center included).</summary>
    public List<int> Range(int center, int radius)
    {
        if (radius <= 0) return new List<int> { center };
        if (1L + 3L * radius * (radius + 1L) >= Count)
            return Enumerable.Range(0, Count).Where(tile => Distance(center, tile) <= radius).ToList();

        int cy = Y(center), cq = X(center) - (cy >> 1);
        var res = new List<int>(1 + 3 * radius * (radius + 1));
        for (int dr = -radius; dr <= radius; dr++)
        {
            int row = cy + dr;
            for (int dq = Math.Max(-radius, -dr - radius); dq <= Math.Min(radius, -dr + radius); dq++)
                res.Add(WrapIndex(cq + dq + (row >> 1), row));
        }
        res.Sort();
        for (int i = res.Count - 1; i > 0; i--)
            if (res[i] == res[i - 1]) res.RemoveAt(i);
        return res;
    }

    /// <summary>A* path. cost(from,to) returns a negative value for impassable steps. Result excludes the start tile.</summary>
    public List<int>? FindPath(int from, int to, Func<int, int, int> cost, int maxNodes = 20000)
    {
        if (from == to) return new List<int>();
        var open = new PriorityQueue<int, int>();
        var g = new Dictionary<int, int> { [from] = 0 };
        var prev = new Dictionary<int, int>();
        open.Enqueue(from, 0);
        int expanded = 0;
        while (open.Count > 0)
        {
            open.TryDequeue(out int cur, out int pri);
            int gc = g[cur];
            if (pri > gc + Distance(cur, to)) continue; // stale entry
            if (cur == to)
            {
                var path = new List<int>();
                for (int t = to; t != from; t = prev[t]) path.Add(t);
                path.Reverse();
                return path;
            }
            if (++expanded > maxNodes) return null;
            foreach (int n in Nb[cur])
            {
                int c = cost(cur, n);
                if (c < 0) continue;
                int ng = gc + c;
                if (g.TryGetValue(n, out int old) && old <= ng) continue;
                g[n] = ng; prev[n] = cur;
                open.Enqueue(n, ng + Distance(n, to));
            }
        }
        return null;
    }
}

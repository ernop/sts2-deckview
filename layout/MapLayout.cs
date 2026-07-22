using System;
using System.Collections.Generic;
using System.Linq;

namespace DeckView.Layout;

// PURE, game-independent map-layout core. No Godot, no sts2 — just graph -> lane-per-node.
// This is where the "flatten the minimap" logic lives so it can be exhaustively unit-tested
// offline (see layout/Program.cs) without launching the game.
//
// Model: a layered graph. Row = map floor/depth (the game truth, kept as the X axis and NEVER
// changed). Col = the game's original lane, used ONLY to seed the vertical order we preserve.
// The layout assigns each node a display LANE (Y). We never touch edges, so connectivity — the
// set of moves a vanilla player has — is untouchable by construction. The one hazard is overlap
// (two nodes at the same spot could read as one room and hide a choice), so the hard invariant
// is: within a row, lanes are distinct AND in the same order as col.

public readonly struct LNode
{
    public readonly int Id;   // dense 0..N-1
    public readonly int Row;  // layer / floor
    public readonly int Col;  // original lane (seed for within-row order)
    public LNode(int id, int row, int col) { Id = id; Row = row; Col = col; }
}

public sealed class LGraph
{
    public readonly LNode[] Nodes;
    public readonly (int From, int To)[] Edges; // parent (lower row) -> child (higher row), by Id
    public readonly int[][] NeighborsOf;        // id -> parent+child ids
    public readonly int[][] RowsOrdered;        // compact row index -> node ids sorted by (Col, Id)
    public readonly int[] RowOf;                // id -> compact row index
    public readonly int RowCount;

    public LGraph(IReadOnlyList<LNode> nodes, IReadOnlyList<(int From, int To)> edges)
    {
        Nodes = nodes.OrderBy(n => n.Id).ToArray();
        for (int i = 0; i < Nodes.Length; i++)
            if (Nodes[i].Id != i)
                throw new ArgumentException("LGraph node Ids must be dense 0..N-1.");
        Edges = edges.ToArray();

        foreach ((int f, int t) in Edges)
        {
            if (f < 0 || f >= Nodes.Length || t < 0 || t >= Nodes.Length)
                throw new ArgumentException($"Edge references a missing node: ({f}->{t}).");
            if (Nodes[f].Row >= Nodes[t].Row)
                throw new ArgumentException($"Edge must go to a higher row: ({f}->{t}).");
        }

        var neigh = new List<int>[Nodes.Length];
        for (int i = 0; i < neigh.Length; i++) neigh[i] = new List<int>();
        foreach ((int f, int t) in Edges) { neigh[f].Add(t); neigh[t].Add(f); }
        NeighborsOf = neigh.Select(l => l.Distinct().ToArray()).ToArray();

        int[] rowValues = Nodes.Select(n => n.Row).Distinct().OrderBy(r => r).ToArray();
        RowCount = rowValues.Length;
        var rowIndex = new Dictionary<int, int>();
        for (int i = 0; i < rowValues.Length; i++) rowIndex[rowValues[i]] = i;
        RowOf = Nodes.Select(n => rowIndex[n.Row]).ToArray();
        RowsOrdered = new int[RowCount][];
        for (int r = 0; r < RowCount; r++)
            RowsOrdered[r] = Nodes.Where(n => rowIndex[n.Row] == r)
                                  .OrderBy(n => n.Col).ThenBy(n => n.Id)
                                  .Select(n => n.Id).ToArray();
    }

    public int[] BaselineLanes() => Nodes.Select(n => n.Col).ToArray();
}

public static class MapLayout
{
    // Assign a display lane to every node. Legal-by-construction (see the invariant checker). Every
    // step preserves each row's column order, so the crossing count is invariant (== baseline) —
    // meaning we can minimize LANES and edge length freely without ever adding a crossing.
    public static int[] AssignLanes(LGraph g)
    {
        // Candidate 1 — alignment-first: start from the game's columns, straighten, and merge any
        // cleanly-clearable lanes. Tends to keep the familiar shape; may leave lanes uncleared.
        int[] aligned = g.BaselineLanes();
        Normalize(g, aligned);
        for (int i = 0; i < 64; i++)
        {
            HillClimb(g, aligned, int.MaxValue);
            if (!TryMergeLane(g, aligned)) break;
        }
        Compact(aligned);

        // Candidate 2 — compactness-first: pack every floor's rooms into the fewest lanes (= the
        // widest floor), then straighten WITHIN that lane budget so it can't re-expand.
        int[] packed = MinPack(g);
        HillClimb(g, packed, packed.Max());
        Compact(packed);

        // Pick fewer lanes (the goal: clear whole rows); tie-break on straightness. Crossings are
        // equal either way, so this never trades away readability of connections.
        int la = LayoutMetrics.LanesUsed(aligned), lp = LayoutMetrics.LanesUsed(packed);
        if (lp != la) return lp < la ? packed : aligned;
        return LayoutMetrics.VerticalEdgeLength(g, packed) <= LayoutMetrics.VerticalEdgeLength(g, aligned)
            ? packed : aligned;
    }

    // Pack each floor's rooms into lanes 0..k-1 by column order — the fewest lanes possible
    // (= the widest floor). Legal by construction (distinct + col-ordered per row).
    private static int[] MinPack(LGraph g)
    {
        var lane = new int[g.Nodes.Length];
        foreach (int[] row in g.RowsOrdered)
            for (int i = 0; i < row.Length; i++) lane[row[i]] = i;
        return lane;
    }

    // Merge two vertically-adjacent lanes (a, a+1) when NO row uses both. It's a monotonic relabel
    // (everything above a shifts down one), so per-row order is preserved (no new crossing) and no
    // edge lengthens (a boundary-crossing edge only shortens). It removes one lane — this is what
    // "completely clear out a row" needs. Returns true if a merge happened.
    private static bool TryMergeLane(LGraph g, int[] lane)
    {
        int min = lane.Min(), max = lane.Max();
        for (int a = min; a < max; a++)
        {
            bool conflict = false;
            foreach (int[] row in g.RowsOrdered)
            {
                bool hasA = false, hasB = false;
                foreach (int id in row)
                {
                    if (lane[id] == a) hasA = true;
                    else if (lane[id] == a + 1) hasB = true;
                }
                if (hasA && hasB) { conflict = true; break; }
            }
            if (conflict) continue;
            for (int id = 0; id < lane.Length; id++)
                if (lane[id] > a) lane[id]--;
            return true;
        }
        return false;
    }

    // Make each row strictly increasing by col order (guarantees the hard invariant as a start).
    private static void Normalize(LGraph g, int[] lane)
    {
        foreach (int[] row in g.RowsOrdered)
            for (int i = 1; i < row.Length; i++)
                if (lane[row[i]] <= lane[row[i - 1]])
                    lane[row[i]] = lane[row[i - 1]] + 1;
    }

    // Shorten edges by shifting rigid same-lane runs / single nodes ±1, never leaving [0, laneCap]
    // (the cap keeps the compact candidate from re-expanding; pass int.MaxValue for unbounded).
    private static void HillClimb(LGraph g, int[] lane, int laneCap)
    {
        // Bounded by monotonic decrease of integer edge length; the cap is just a safety net.
        for (int guard = 0; guard < 100_000; guard++)
        {
            bool improved = false;

            // Candidate moves, biggest rigid runs first, then single nodes. A same-lane run is a
            // connected set of nodes all currently sharing a lane; shifting it keeps it flat.
            var candidates = new List<int[]>();
            candidates.AddRange(SameLaneRuns(g, lane).Where(s => s.Length >= 2)
                                                     .OrderByDescending(s => s.Length));
            candidates.AddRange(g.Nodes.Select(n => new[] { n.Id }));

            foreach (int[] set in candidates)
            {
                foreach (int delta in new[] { 1, -1 })
                {
                    bool inBounds = set.All(id => lane[id] + delta >= 0 && lane[id] + delta <= laneCap);
                    if (inBounds && IsLegalShift(g, lane, set, delta) && EdgeLengthDelta(g, lane, set, delta) < 0)
                    {
                        foreach (int id in set) lane[id] += delta;
                        improved = true;
                        break;
                    }
                }
                if (improved) break; // re-evaluate runs from scratch after any change
            }
            if (!improved) return;
        }
    }

    // Connected components over edges whose two endpoints currently share a lane.
    private static List<int[]> SameLaneRuns(LGraph g, int[] lane)
    {
        int n = g.Nodes.Length;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        foreach ((int f, int t) in g.Edges)
            if (lane[f] == lane[t]) Union(f, t);

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!groups.TryGetValue(r, out List<int>? list)) groups[r] = list = new List<int>();
            list.Add(i);
        }
        return groups.Values.Select(l => l.ToArray()).ToList();
    }

    // Would shifting every node in `set` by `delta` keep the layout legal? (No overlap and no
    // col-order inversion within any affected row, checked against the non-moving nodes.)
    private static bool IsLegalShift(LGraph g, int[] lane, int[] set, int delta)
    {
        var moving = new HashSet<int>(set);
        foreach (int u in set)
        {
            int newU = lane[u] + delta;
            int r = g.RowOf[u];
            foreach (int v in g.RowsOrdered[r])
            {
                if (moving.Contains(v)) continue; // moves together -> relative order preserved
                if (newU == lane[v]) return false; // overlap
                if (Math.Sign(g.Nodes[u].Col - g.Nodes[v].Col) != Math.Sign(newU - lane[v]))
                    return false;                  // would pass v -> order inversion
            }
        }
        return true;
    }

    // Change in total vertical edge length if `set` shifts by `delta`. Edges fully inside the set
    // don't change; only boundary edges do.
    private static int EdgeLengthDelta(LGraph g, int[] lane, int[] set, int delta)
    {
        var moving = new HashSet<int>(set);
        int d = 0;
        foreach ((int a, int b) in g.Edges)
        {
            bool am = moving.Contains(a), bm = moving.Contains(b);
            if (am == bm) continue;
            int la = lane[a] + (am ? delta : 0);
            int lb = lane[b] + (bm ? delta : 0);
            d += Math.Abs(la - lb) - Math.Abs(lane[a] - lane[b]);
        }
        return d;
    }

    // Remove globally-unused lanes by remapping used lane values to consecutive ranks. Monotonic,
    // so it preserves all orders and never lengthens an edge (rank gaps <= value gaps).
    private static void Compact(int[] lane)
    {
        int[] used = lane.Distinct().OrderBy(v => v).ToArray();
        var rank = new Dictionary<int, int>();
        for (int i = 0; i < used.Length; i++) rank[used[i]] = i;
        for (int i = 0; i < lane.Length; i++) lane[i] = rank[lane[i]];
    }
}

// Hard safety checks. A layout is legal iff every node is placed and, within each row, lanes are
// distinct and in the same order as col. That's exactly "no two rooms overlap" + "we never
// reorder / imply a move that isn't real". Returns the list of violations (empty == legal).
public static class LayoutInvariants
{
    public static List<string> Check(LGraph g, int[] lane)
    {
        var violations = new List<string>();
        if (lane.Length != g.Nodes.Length)
            violations.Add($"lane count {lane.Length} != node count {g.Nodes.Length}");

        for (int r = 0; r < g.RowCount; r++)
        {
            int[] row = g.RowsOrdered[r];
            for (int i = 1; i < row.Length; i++)
            {
                int a = row[i - 1], b = row[i]; // ordered by col ascending
                if (lane[a] == lane[b])
                    violations.Add($"overlap in row {r}: nodes {a},{b} both at lane {lane[a]}");
                else if (lane[a] > lane[b])
                    violations.Add($"col-order inverted in row {r}: node {a}(col {g.Nodes[a].Col}) " +
                                   $"at lane {lane[a]} above node {b}(col {g.Nodes[b].Col}) at lane {lane[b]}");
            }
        }
        return violations;
    }

    public static bool IsLegal(LGraph g, int[] lane) => Check(g, lane).Count == 0;
}

public static class LayoutMetrics
{
    // Total vertical edge length — our zigzag proxy (a straight sub-path costs 0).
    public static int VerticalEdgeLength(LGraph g, int[] lane) =>
        g.Edges.Sum(e => Math.Abs(lane[e.From] - lane[e.To]));

    // Distinct lanes in use — the drawing's height.
    public static int LanesUsed(int[] lane) => lane.Distinct().Count();

    // Edge crossings between edges that span the same pair of rows (readability, report-only).
    public static int Crossings(LGraph g, int[] lane)
    {
        int c = 0;
        for (int i = 0; i < g.Edges.Length; i++)
            for (int j = i + 1; j < g.Edges.Length; j++)
            {
                var (a, b) = g.Edges[i];
                var (x, y) = g.Edges[j];
                if (g.RowOf[a] != g.RowOf[x] || g.RowOf[b] != g.RowOf[y]) continue;
                if (Math.Sign(lane[a] - lane[x]) * Math.Sign(lane[b] - lane[y]) < 0) c++;
            }
        return c;
    }
}

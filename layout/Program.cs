using System;
using System.Collections.Generic;
using System.Linq;
using DeckView.Layout;

// Offline test harness for the pure MapLayout algorithm. No game, no Godot — run with:
//   dotnet run --project layout/layouttest.csproj
// Exit code 0 = every hard gate passed (never illegal, never worse than baseline, checker works).

internal static class Runner
{
    private static int _failures;

    private static void Fail(string msg) { _failures++; Console.WriteLine($"  FAIL: {msg}"); }
    private static void Expect(bool cond, string msg) { if (!cond) Fail(msg); }

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "viz") { CompressionAnalysis(); return 0; }

        Console.WriteLine("=== DeckView map-layout tests ===\n");
        CuratedCases();
        SafetyNetTest();
        PropertyTests(seedCount: 500);

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL GATES PASSED" : $"{_failures} FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }

    // ---- viz tool: render a captured level and compare compression options -----------------

    // ASCII render of a lane assignment: text-rows = lanes (top to bottom), text-columns = map
    // floors (left to right). Each cell shows the node's ORIGINAL column digit (so you can trace
    // how the game's columns spread across lanes), or '.' if empty.
    private static string RenderGrid(LGraph g, int[] lane)
    {
        int maxRow = g.Nodes.Max(n => n.Row);
        int minL = lane.Min(), maxL = lane.Max();
        var at = new Dictionary<(int row, int lane), int>(); // -> col digit
        foreach (LNode n in g.Nodes) at[(n.Row, lane[n.Id])] = n.Col;

        var sb = new System.Text.StringBuilder();
        sb.Append("      ").Append(string.Join("", Enumerable.Range(0, maxRow + 1).Select(r => (r % 10).ToString()))).Append("   (floors)\n");
        for (int L = minL; L <= maxL; L++)
        {
            sb.Append($"lane{L,2} ");
            for (int r = 0; r <= maxRow; r++)
                sb.Append(at.TryGetValue((r, L), out int col) ? (char)('0' + col % 10) : '.');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // Maximum compression: pack each floor's nodes into lanes 0..k-1 by column order. Uses the
    // fewest lanes possible (= the widest floor) but ignores alignment, so it usually has many
    // crossings. Shows the compression ceiling / the crossings cost.
    private static int[] MinPack(LGraph g)
    {
        var lane = new int[g.Nodes.Length];
        foreach (int[] row in g.RowsOrdered)
            for (int i = 0; i < row.Length; i++) lane[row[i]] = i;
        return lane;
    }

    private static void CompressionAnalysis()
    {
        // The latest captured level (Act 2 — Hive), from the in-game MAPDUMP.
        const string nodes =
            "0,3 1,2 1,6 2,1 2,3 2,6 3,0 3,1 3,2 3,3 3,6 4,0 4,2 4,3 4,6 5,1 5,3 5,6 6,0 6,2 6,4 6,6 " +
            "7,0 7,2 7,3 7,4 7,5 8,0 8,1 8,3 8,5 9,0 9,2 9,3 9,4 9,6 10,0 10,1 10,3 10,5 10,6 " +
            "11,0 11,1 11,3 11,5 11,6 12,0 12,4 12,6 13,0 13,5 13,6 14,0 14,6 15,3";
        const string edges =
            "0,3->1,2 0,3->1,6 1,2->2,3 1,2->2,1 1,6->2,6 2,1->3,0 2,1->3,1 2,3->3,3 2,3->3,2 2,6->3,6 " +
            "3,0->4,0 3,1->4,2 3,2->4,2 3,3->4,3 3,3->4,2 3,6->4,6 4,0->5,1 4,2->5,1 4,2->5,3 4,3->5,3 " +
            "4,6->5,6 5,1->6,2 5,1->6,0 5,3->6,4 5,3->6,2 5,6->6,6 6,0->7,0 6,2->7,2 6,2->7,3 6,4->7,3 " +
            "6,4->7,4 6,6->7,5 7,0->8,0 7,2->8,1 7,3->8,3 7,4->8,3 7,4->8,5 7,5->8,5 8,0->9,0 8,1->9,0 " +
            "8,3->9,4 8,3->9,3 8,3->9,2 8,5->9,6 9,0->10,0 9,2->10,1 9,3->10,3 9,4->10,5 9,6->10,6 " +
            "10,0->11,1 10,0->11,0 10,1->11,1 10,3->11,3 10,5->11,5 10,6->11,6 10,6->11,5 11,0->12,0 " +
            "11,1->12,0 11,3->12,4 11,5->12,6 11,6->12,6 12,0->13,0 12,4->13,5 12,6->13,5 12,6->13,6 " +
            "13,0->14,0 13,5->14,6 13,6->14,6 14,0->15,3 14,6->15,3";
        LGraph g = FromDump(nodes, edges);

        void Show(string name, int[] l) =>
            Console.WriteLine($"\n## {name}\n   lanes={LayoutMetrics.LanesUsed(l)}  edgeLen={LayoutMetrics.VerticalEdgeLength(g, l)}  " +
                              $"crossings={LayoutMetrics.Crossings(g, l)}  legal={LayoutInvariants.IsLegal(g, l)}\n" + RenderGrid(g, l));

        Console.WriteLine("=== COMPRESSION ANALYSIS — Act 2 Hive ===");
        Show("A. baseline (raw game columns)", g.BaselineLanes());
        Show("B. our algorithm (flatten + lane-merge)", MapLayout.AssignLanes(g));
        Show("C. min-pack (max compression, ignores crossings)", MinPack(g));

        // Which adjacent lane pairs could be cleanly merged (no floor uses both)?
        int[] algo = MapLayout.AssignLanes(g);
        Console.WriteLine("\n## clean lane-merges available on (B):");
        int min = algo.Min(), max = algo.Max(), found = 0;
        for (int a = min; a < max; a++)
        {
            bool conflict = g.RowsOrdered.Any(row => row.Any(id => algo[id] == a) && row.Any(id => algo[id] == a + 1));
            if (!conflict) { Console.WriteLine($"   lanes {a}+{a + 1}: MERGEABLE"); found++; }
        }
        if (found == 0) Console.WriteLine("   none — every adjacent lane pair shares a floor, so 6 lanes would overlap two rooms.");
    }

    // ---- fluent graph builder --------------------------------------------------------------
    private sealed class B
    {
        private readonly Dictionary<(int, int), int> _ids = new();
        private readonly List<LNode> _nodes = new();
        private readonly HashSet<(int, int, int, int)> _seen = new();
        private readonly List<(int, int)> _edges = new();

        private int N(int row, int col)
        {
            if (!_ids.TryGetValue((row, col), out int id))
            {
                id = _nodes.Count;
                _ids[(row, col)] = id;
                _nodes.Add(new LNode(id, row, col));
            }
            return id;
        }

        public B E(int r1, int c1, int r2, int c2)
        {
            if (_seen.Add((r1, c1, r2, c2))) _edges.Add((N(r1, c1), N(r2, c2)));
            else { N(r1, c1); N(r2, c2); }
            return this;
        }

        public B Chain(int col, int rowFrom, int rowTo)
        {
            for (int r = rowFrom; r < rowTo; r++) E(r, col, r + 1, col);
            return this;
        }

        public B Node(int row, int col) { N(row, col); return this; }

        public LGraph G() => new(_nodes, _edges);
    }

    // Build a graph from a captured in-game MAPDUMP (nodes "row,col[,type] ..."; edges
    // "row,col->row,col ...").
    private static LGraph FromDump(string nodes, string edges)
    {
        var b = new B();
        foreach (string tok in nodes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] p = tok.Split(',');
            b.Node(int.Parse(p[0]), int.Parse(p[1]));
        }
        foreach (string tok in edges.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] ft = tok.Split("->");
            string[] f = ft[0].Split(','), t = ft[1].Split(',');
            b.E(int.Parse(f[0]), int.Parse(f[1]), int.Parse(t[0]), int.Parse(t[1]));
        }
        return b.G();
    }

    // ---- curated cases ---------------------------------------------------------------------
    private static void CuratedCases()
    {
        Console.WriteLine("-- curated cases --");

        // 1. A straight column: nothing to do, must stay flat at one lane.
        Run("straight-column", new B().Chain(0, 0, 5).G(), (g, baseLen, len, baseLn, ln) =>
        {
            Expect(len == 0, $"expected 0 edge length, got {len}");
            Expect(ln == 1, $"expected 1 lane, got {ln}");
        });

        // 2. Top run that should sink one lane (your campfire example, distilled): a lane-0 run
        //    rows 1..7, both ends pulled downward, lane 1 empty beneath it.
        var drop = new B().Chain(0, 1, 7)       // the run at col 0
            .E(0, 1, 1, 0)                        // left end pulled down (parent at col 1)
            .E(7, 0, 8, 2)                        // right end pulled down (child at col 2)
            .G();
        Run("top-run-drops", drop, (g, baseLen, len, baseLn, ln) =>
            Expect(len < baseLen || ln < baseLn, $"expected the run to sink/compact ({len}/{ln} vs {baseLen}/{baseLn})"));

        // 3. A pure zigzag path should straighten to a single lane.
        var zig = new B().E(0, 0, 1, 1).E(1, 1, 2, 0).E(2, 0, 3, 1).E(3, 1, 4, 0).G();
        Run("zigzag-straightens", zig, (g, baseLen, len, baseLn, ln) =>
            Expect(len < baseLen || ln < baseLn, $"expected straighten/compact ({len}/{ln} vs {baseLen}/{baseLn})"));

        // 4. Dense grid — every cell filled, no empty lane to move into: must be left legal and
        //    no worse (guaranteed), not "improved" into an overlap.
        var dense = new B();
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
                for (int cc = Math.Max(0, c - 1); cc <= Math.Min(3, c + 1); cc++)
                    dense.E(r, c, r + 1, cc);
        Run("dense-grid-stays-legal", dense.G(), (g, baseLen, len, baseLn, ln) =>
            Expect(ln == baseLn, $"dense grid height should be unchanged ({ln} vs {baseLn})"));

        // 5. Trap: two nodes in a row both pulled onto the same lane — must stay distinct.
        var trap = new B().E(0, 0, 1, 0).E(0, 0, 1, 1).E(1, 1, 2, 0).E(1, 0, 2, 0).G();
        Run("trap-no-overlap", trap, (g, baseLen, len, baseLn, ln) => { /* legality asserted in Run */ });

        // 6. A real captured in-game level (Act 1, 7 columns). Baseline uses all 7 lanes; the
        //    lane-merge pass should clear at least one lane while staying legal.
        const string realNodes =
            "0,3 1,0 1,2 1,4 1,6 2,0 2,2 2,4 2,5 2,6 3,0 3,2 3,4 3,6 4,0 4,3 4,5 5,1 5,2 5,4 " +
            "6,1 6,3 6,4 6,5 7,0 7,2 7,3 7,4 7,5 8,0 8,1 8,2 8,4 8,5 9,0 9,2 9,5 10,1 10,2 10,4 10,6 " +
            "11,0 11,1 11,2 11,4 11,6 12,0 12,2 12,4 12,6 13,0 13,1 13,3 13,4 13,6 14,0 14,1 14,4 14,6 15,0 15,5 16,3";
        const string realEdges =
            "0,3->1,0 0,3->1,2 0,3->1,4 0,3->1,6 1,0->2,0 1,2->2,2 1,4->2,5 1,4->2,4 1,6->2,6 " +
            "2,0->3,0 2,2->3,2 2,4->3,4 2,5->3,4 2,6->3,6 3,0->4,0 3,2->4,3 3,4->4,3 3,6->4,5 " +
            "4,0->5,1 4,3->5,4 4,3->5,2 4,5->5,4 5,1->6,1 5,2->6,3 5,2->6,1 5,4->6,3 5,4->6,5 5,4->6,4 " +
            "6,1->7,2 6,1->7,0 6,3->7,2 6,3->7,3 6,4->7,4 6,5->7,5 7,0->8,0 7,2->8,2 7,2->8,1 7,3->8,2 " +
            "7,4->8,4 7,5->8,5 8,0->9,0 8,1->9,0 8,2->9,2 8,4->9,5 8,5->9,5 9,0->10,1 9,2->10,1 9,2->10,2 " +
            "9,5->10,6 9,5->10,4 10,1->11,1 10,1->11,0 10,2->11,2 10,2->11,1 10,4->11,4 10,6->11,6 " +
            "11,0->12,0 11,1->12,2 11,2->12,2 11,4->12,4 11,6->12,6 12,0->13,0 12,0->13,1 12,2->13,1 " +
            "12,2->13,3 12,4->13,4 12,6->13,6 13,0->14,0 13,1->14,1 13,3->14,4 13,4->14,4 13,6->14,6 " +
            "14,0->15,0 14,1->15,0 14,4->15,5 14,6->15,5 15,0->16,3 15,5->16,3";
        // Legality + never-worse are asserted in Run; lane count is reported. (This particular
        // level's crossing-free minimum happens to be 7 — no adjacent lanes are conflict-free.)
        Run("real-act1-level", FromDump(realNodes, realEdges), (g, baseLen, len, baseLn, ln) => { });
    }

    // A case runner: asserts legality (hard) + the never-worse guarantees (hard), prints metrics,
    // then runs the case-specific expectation.
    private static void Run(string name, LGraph g, Action<LGraph, int, int, int, int> expect)
    {
        int[] baseline = g.BaselineLanes();
        int baseLen = LayoutMetrics.VerticalEdgeLength(g, baseline);
        int baseLanes = LayoutMetrics.LanesUsed(baseline);
        int baseCross = LayoutMetrics.Crossings(g, baseline);

        int[] lane = MapLayout.AssignLanes(g);
        List<string> violations = LayoutInvariants.Check(g, lane);
        int len = LayoutMetrics.VerticalEdgeLength(g, lane);
        int lanes = LayoutMetrics.LanesUsed(lane);
        int cross = LayoutMetrics.Crossings(g, lane);

        Console.WriteLine($"  {name,-26} edgeLen {baseLen}->{len}  lanes {baseLanes}->{lanes}  " +
                          $"crossings {baseCross}->{cross}  {(violations.Count == 0 ? "legal" : "ILLEGAL")}");
        foreach (string v in violations) Fail($"{name}: {v}");
        // Hard gates: legal, never MORE lanes, never MORE crossings. Edge length may rise a little
        // when we trade it for clearing a lane — reported, not gated.
        Expect(lanes <= baseLanes, $"{name}: lanes regressed ({lanes} > {baseLanes})");
        Expect(cross <= baseCross, $"{name}: crossings regressed ({cross} > {baseCross})");
        expect(g, baseLen, len, baseLanes, lanes);
    }

    // ---- prove the invariant checker actually catches illegal layouts ----------------------
    private static void SafetyNetTest()
    {
        Console.WriteLine("-- safety-net (checker must reject illegal layouts) --");
        var g = new B().E(0, 0, 1, 0).E(0, 0, 1, 1).E(0, 1, 1, 1).G();

        int[] allZero = new int[g.Nodes.Length];               // forces overlaps
        Expect(!LayoutInvariants.IsLegal(g, allZero), "checker failed to flag an all-same-lane overlap");

        int[] inverted = g.BaselineLanes();                     // reverse a row's order
        int[] row1 = g.RowsOrdered[1];
        if (row1.Length >= 2) { int t = inverted[row1[0]]; inverted[row1[0]] = inverted[row1[1]]; inverted[row1[1]] = t; }
        Expect(!LayoutInvariants.IsLegal(g, inverted), "checker failed to flag a col-order inversion");
        Console.WriteLine("  checker rejects overlap + inversion: ok");
    }

    // ---- property tests over hundreds of random STS-like maps ------------------------------
    private static void PropertyTests(int seedCount)
    {
        Console.WriteLine($"-- property tests ({seedCount} random maps) --");
        int illegal = 0, regressed = 0, improved = 0;
        long baseLenSum = 0, lenSum = 0, baseLaneSum = 0, laneSum = 0;

        for (int seed = 0; seed < seedCount; seed++)
        {
            LGraph g = RandomStsMap(new Random(seed));
            int[] baseline = g.BaselineLanes();
            int baseLen = LayoutMetrics.VerticalEdgeLength(g, baseline);
            int baseLanes = LayoutMetrics.LanesUsed(baseline);

            int baseCross = LayoutMetrics.Crossings(g, baseline);
            int[] lane = MapLayout.AssignLanes(g);
            if (!LayoutInvariants.IsLegal(g, lane)) { illegal++; if (illegal <= 3) foreach (var v in LayoutInvariants.Check(g, lane)) Console.WriteLine($"    seed {seed}: {v}"); }
            int len = LayoutMetrics.VerticalEdgeLength(g, lane);
            int lanes = LayoutMetrics.LanesUsed(lane);

            if (lanes > baseLanes || LayoutMetrics.Crossings(g, lane) > baseCross) regressed++;
            if (lanes < baseLanes || len < baseLen) improved++;
            baseLenSum += baseLen; lenSum += len; baseLaneSum += baseLanes; laneSum += lanes;
        }

        Console.WriteLine($"  illegal: {illegal}   regressed: {regressed}   improved: {improved}/{seedCount}");
        Console.WriteLine($"  total edge length {baseLenSum} -> {lenSum}  ({100.0 * (baseLenSum - lenSum) / Math.Max(1, baseLenSum):F1}% shorter)");
        Console.WriteLine($"  total lanes used  {baseLaneSum} -> {laneSum}  ({100.0 * (baseLaneSum - laneSum) / Math.Max(1, baseLaneSum):F1}% fewer)");
        Expect(illegal == 0, $"{illegal} random maps produced an ILLEGAL layout");
        Expect(regressed == 0, $"{regressed} random maps regressed vs baseline");
    }

    // STS-like generator: a handful of paths that walk up the 7-wide grid, each step moving to an
    // adjacent column. Union of paths = nodes; steps = edges. Mirrors the real map's adjacency.
    private static LGraph RandomStsMap(Random rng)
    {
        const int width = 7;
        int rows = rng.Next(10, 18);
        int paths = rng.Next(4, 8);
        var b = new B();
        for (int p = 0; p < paths; p++)
        {
            int col = rng.Next(0, width);
            for (int r = 0; r < rows; r++)
            {
                int next = Math.Clamp(col + rng.Next(-1, 2), 0, width - 1);
                b.E(r, col, r + 1, next);
                col = next;
            }
        }
        return b.G();
    }
}

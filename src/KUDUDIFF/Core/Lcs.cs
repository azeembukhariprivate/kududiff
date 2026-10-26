using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Kududiff.Core
{
    public enum DiffAlgorithm
    {
        /// <summary>Myers' method: the fewest added and removed lines. What Git uses by default.</summary>
        Standard,
        /// <summary>Patience: lines that appear once on each side anchor the comparison, which keeps moved and reordered code readable.</summary>
        Patience
    }

    /// <summary>
    /// Finds the lines (or words) two sequences have in common, in order. Items are numbers (equal items, equal numbers),
    /// so comparing lines or words is the same work. The result is the matched pairs of positions, in order.
    /// </summary>
    public static class Lcs
    {
        /// <summary>How long one comparison may take before the rest of a region is simply shown as changed.</summary>
        public static TimeSpan Deadline = TimeSpan.FromSeconds(8);

        public static List<(int A, int B)> Match(int[] a, int[] b, DiffAlgorithm algorithm)
        {
            var result = new List<(int, int)>();
            var watch = Stopwatch.StartNew();
            if (algorithm == DiffAlgorithm.Patience) Patience(a, 0, a.Length, b, 0, b.Length, result, watch);
            else Myers(a, 0, a.Length, b, 0, b.Length, result, watch);
            return result;
        }

        // ------------------------------------------------------------------ Myers

        private static void Myers(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi, List<(int, int)> result, Stopwatch watch)
        {
            // Common start and end first: cheap, and most files share most of their lines.
            while (aLo < aHi && bLo < bHi && a[aLo] == b[bLo]) result.Add((aLo++, bLo++));
            var tail = 0;
            while (aLo < aHi - tail && bLo < bHi - tail && a[aHi - 1 - tail] == b[bHi - 1 - tail]) tail++;
            aHi -= tail;
            bHi -= tail;

            if (aLo < aHi && bLo < bHi)
            {
                var split = Bisect(a, aLo, aHi, b, bLo, bHi, watch);
                if (split.HasValue)
                {
                    Myers(a, aLo, split.Value.X, b, bLo, split.Value.Y, result, watch);
                    Myers(a, split.Value.X, aHi, b, split.Value.Y, bHi, result, watch);
                }
                // No split: nothing in common (or out of time), so the region is one change.
            }

            for (var i = 0; i < tail; i++) result.Add((aHi + i, bHi + i));
        }

        /// <summary>
        /// Myers' middle snake (as in "An O(ND) Difference Algorithm and Its Variations", searching from both ends at
        /// once): returns where the shortest edit path crosses the middle, so each half can be solved on its own.
        /// </summary>
        private static (int X, int Y)? Bisect(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi, Stopwatch watch)
        {
            int n = aHi - aLo, m = bHi - bLo;
            var maxD = (n + m + 1) / 2;
            var offset = maxD;
            var length = 2 * maxD + 2;
            var v1 = new int[length];
            var v2 = new int[length];
            for (var i = 0; i < length; i++) v1[i] = v2[i] = -1;
            v1[offset + 1] = 0;
            v2[offset + 1] = 0;
            var delta = n - m;
            var front = (delta & 1) != 0;
            int k1Start = 0, k1End = 0, k2Start = 0, k2End = 0;

            for (var d = 0; d < maxD; d++)
            {
                if ((d & 63) == 0 && watch.Elapsed > Deadline) return null;

                for (var k1 = -d + k1Start; k1 <= d - k1End; k1 += 2)
                {
                    var k1Offset = offset + k1;
                    int x1 = k1 == -d || (k1 != d && v1[k1Offset - 1] < v1[k1Offset + 1]) ? v1[k1Offset + 1] : v1[k1Offset - 1] + 1;
                    var y1 = x1 - k1;
                    while (x1 < n && y1 < m && a[aLo + x1] == b[bLo + y1]) { x1++; y1++; }
                    v1[k1Offset] = x1;
                    if (x1 > n) k1End += 2;
                    else if (y1 > m) k1Start += 2;
                    else if (front)
                    {
                        var k2Offset = offset + delta - k1;
                        if (k2Offset >= 0 && k2Offset < length && v2[k2Offset] != -1 && x1 >= n - v2[k2Offset])
                            return Split(aLo, aHi, bLo, bHi, x1, y1);
                    }
                }

                for (var k2 = -d + k2Start; k2 <= d - k2End; k2 += 2)
                {
                    var k2Offset = offset + k2;
                    int x2 = k2 == -d || (k2 != d && v2[k2Offset - 1] < v2[k2Offset + 1]) ? v2[k2Offset + 1] : v2[k2Offset - 1] + 1;
                    var y2 = x2 - k2;
                    while (x2 < n && y2 < m && a[aHi - 1 - x2] == b[bHi - 1 - y2]) { x2++; y2++; }
                    v2[k2Offset] = x2;
                    if (x2 > n) k2End += 2;
                    else if (y2 > m) k2Start += 2;
                    else if (!front)
                    {
                        var k1Offset = offset + delta - k2;
                        if (k1Offset >= 0 && k1Offset < length && v1[k1Offset] != -1)
                        {
                            var x1 = v1[k1Offset];
                            var y1 = offset + x1 - k1Offset;
                            if (x1 >= n - x2) return Split(aLo, aHi, bLo, bHi, x1, y1);
                        }
                    }
                }
            }
            return null;
        }

        private static (int X, int Y)? Split(int aLo, int aHi, int bLo, int bHi, int x, int y)
        {
            // A split at either end would make no progress; treat the region as one change instead.
            if ((x == 0 && y == 0) || (x == aHi - aLo && y == bHi - bLo)) return null;
            return (aLo + x, bLo + y);
        }

        // ------------------------------------------------------------------ patience

        private static void Patience(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi, List<(int, int)> result, Stopwatch watch)
        {
            while (aLo < aHi && bLo < bHi && a[aLo] == b[bLo]) result.Add((aLo++, bLo++));
            var tail = 0;
            while (aLo < aHi - tail && bLo < bHi - tail && a[aHi - 1 - tail] == b[bHi - 1 - tail]) tail++;
            aHi -= tail;
            bHi -= tail;

            if (aLo < aHi && bLo < bHi)
            {
                var anchors = UniqueAnchors(a, aLo, aHi, b, bLo, bHi);
                if (anchors.Count == 0)
                {
                    Myers(a, aLo, aHi, b, bLo, bHi, result, watch);
                }
                else
                {
                    int pa = aLo, pb = bLo;
                    foreach (var (x, y) in anchors)
                    {
                        Patience(a, pa, x, b, pb, y, result, watch);
                        result.Add((x, y));
                        pa = x + 1;
                        pb = y + 1;
                    }
                    Patience(a, pa, aHi, b, pb, bHi, result, watch);
                }
            }

            for (var i = 0; i < tail; i++) result.Add((aHi + i, bHi + i));
        }

        /// <summary>
        /// Items that appear exactly once on each side, kept in an order that rises on both sides (the longest such
        /// chain, found by patience sorting).
        /// </summary>
        private static List<(int, int)> UniqueAnchors(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi)
        {
            var countA = new Dictionary<int, int>();
            var posA = new Dictionary<int, int>();
            for (var i = aLo; i < aHi; i++)
            {
                countA.TryGetValue(a[i], out var c);
                countA[a[i]] = c + 1;
                posA[a[i]] = i;
            }
            var countB = new Dictionary<int, int>();
            var posB = new Dictionary<int, int>();
            for (var j = bLo; j < bHi; j++)
            {
                countB.TryGetValue(b[j], out var c);
                countB[b[j]] = c + 1;
                posB[b[j]] = j;
            }

            var pairs = new List<(int A, int B)>();
            for (var i = aLo; i < aHi; i++)
                if (countA[a[i]] == 1 && countB.TryGetValue(a[i], out var cb) && cb == 1)
                    pairs.Add((i, posB[a[i]]));
            if (pairs.Count == 0) return new List<(int, int)>();

            // Longest increasing run of B positions (pairs are already in A order).
            var tops = new List<int>();          // index into pairs of the smallest tail for each length
            var previous = new int[pairs.Count];
            for (var p = 0; p < pairs.Count; p++)
            {
                int lo = 0, hi = tops.Count;
                while (lo < hi)
                {
                    var mid = (lo + hi) / 2;
                    if (pairs[tops[mid]].B < pairs[p].B) lo = mid + 1;
                    else hi = mid;
                }
                previous[p] = lo > 0 ? tops[lo - 1] : -1;
                if (lo == tops.Count) tops.Add(p);
                else tops[lo] = p;
            }
            var chain = new List<(int, int)>();
            for (var p = tops[tops.Count - 1]; p >= 0; p = previous[p]) chain.Add(pairs[p]);
            chain.Reverse();
            return chain;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;

namespace UnityNetworkViewer.Core.Layout
{
    /// <summary>
    /// Fruchterman-Reingold 系の力学レイアウト。前回位置を初期値として温かく再開する。
    /// 大きなグラフでは一様グリッドで斥力計算を近傍に限定する。
    /// </summary>
    public sealed class ForceDirectedLayout : ILayoutStrategy
    {
        public string Name => "Force-directed";
        public int Iterations = 300;
        public int GridThreshold = 600;

        public LayoutResult Compute(LayoutInput input, CancellationToken cancellation)
        {
            int n = input.NodeIds.Count;
            var result = new LayoutResult { Positions = new Vec2[n] };
            if (n == 0) return result;

            var pos = result.Positions;
            var rng = new Random(input.Seed);
            float k = (input.NodeWidth + input.HorizontalGap) * 0.9f; // 理想エッジ長

            // 初期位置: 前回位置 → 無ければ放射レイアウト結果
            Vec2[] seed = null;
            if (input.Previous.Count < n)
                seed = new RadialLayout().Compute(input, cancellation).Positions;
            for (int i = 0; i < n; i++)
            {
                if (input.Previous.TryGetValue(i, out var p)) pos[i] = p;
                else pos[i] = seed[i] + new Vec2((float)rng.NextDouble() * 4f, (float)rng.NextDouble() * 4f);
            }
            LayoutUtil.ApplyPinned(input, pos);

            var disp = new Vec2[n];
            float temperature = k * 3f;
            float cooling = temperature / (Iterations + 1);
            var pinned = new bool[n];
            foreach (var kv in input.Pinned) if (kv.Key >= 0 && kv.Key < n) pinned[kv.Key] = true;

            var grid = n > GridThreshold ? new Dictionary<long, int>(n) : null;
            var next = n > GridThreshold ? new int[n] : null;
            for (int iter = 0; iter < Iterations; iter++)
            {
                if ((iter & 15) == 0) cancellation.ThrowIfCancellationRequested();
                for (int i = 0; i < n; i++) disp[i] = default;

                if (n <= GridThreshold) RepulseAll(pos, disp, k);
                else RepulseGrid(pos, disp, k, grid, next);

                foreach (var e in input.Edges)
                {
                    var d = pos[e.Source] - pos[e.Target];
                    float dist = Math.Max(d.Length, 0.01f);
                    float weight = EdgeKindInfo.IsStructural(e.Kind) ? 1.5f : 1f;
                    float force = dist * dist / k * weight;
                    var f = d * (force / dist);
                    disp[e.Source] -= f;
                    disp[e.Target] += f;
                }

                // 中心への弱い引力（非連結成分が飛び散らないように）
                for (int i = 0; i < n; i++)
                {
                    disp[i] -= pos[i] * 0.01f;
                }

                for (int i = 0; i < n; i++)
                {
                    if (pinned[i]) continue;
                    float len = Math.Max(disp[i].Length, 0.01f);
                    float step = Math.Min(len, temperature);
                    pos[i] += disp[i] * (step / len);
                }
                temperature = Math.Max(temperature - cooling, k * 0.02f);
            }
            return result;
        }

        static void RepulseAll(Vec2[] pos, Vec2[] disp, float k)
        {
            int n = pos.Length;
            float k2 = k * k;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    var d = pos[i] - pos[j];
                    float dist2 = Math.Max(d.X * d.X + d.Y * d.Y, 0.01f);
                    var f = d * (k2 / dist2);
                    disp[i] += f;
                    disp[j] -= f;
                }
            }
        }

        static void RepulseGrid(Vec2[] pos, Vec2[] disp, float k, Dictionary<long, int> grid, int[] next)
        {
            int n = pos.Length;
            float cell = k * 2f;
            float k2 = k * k;
            grid.Clear();
            for (int i = 0; i < n; i++)
            {
                long key = Key((int)Math.Floor(pos[i].X / cell), (int)Math.Floor(pos[i].Y / cell));
                next[i] = grid.TryGetValue(key, out int head) ? head : -1;
                grid[key] = i;
            }
            for (int i = 0; i < n; i++)
            {
                int cx = (int)Math.Floor(pos[i].X / cell), cy = (int)Math.Floor(pos[i].Y / cell);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (!grid.TryGetValue(Key(cx + dx, cy + dy), out int head)) continue;
                        for (int j = head; j >= 0; j = next[j])
                        {
                            if (j == i) continue;
                            var d = pos[i] - pos[j];
                            float dist2 = Math.Max(d.X * d.X + d.Y * d.Y, 0.01f);
                            disp[i] += d * (k2 / dist2);
                        }
                    }
            }
        }

        static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
    }
}

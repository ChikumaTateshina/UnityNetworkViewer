using System;
using System.Collections.Generic;
using System.Threading;

namespace UnityNetworkViewer.Core.Layout
{
    /// <summary>選択ノードを中心に、ホップ数ごとの同心円へ配置する。角度は BFS 木の葉数で配分する。</summary>
    public sealed class RadialLayout : ILayoutStrategy
    {
        public string Name => "Radial";
        public float RingSpacing = 0f; // 0 の場合はノードサイズから算出

        public LayoutResult Compute(LayoutInput input, CancellationToken cancellation)
        {
            int n = input.NodeIds.Count;
            var result = new LayoutResult { Positions = new Vec2[n] };
            if (n == 0) return result;

            var adj = LayoutUtil.BuildAdjacency(input, false);
            var depth = new int[n];
            var parent = new int[n];
            for (int i = 0; i < n; i++) { depth[i] = -1; parent[i] = -1; }

            var roots = new List<int>();
            foreach (int r in input.Roots) if (r >= 0 && r < n && depth[r] < 0) { roots.Add(r); depth[r] = 0; }
            if (roots.Count == 0)
            {
                // ルートが無ければ次数最大のノードを中心にする
                int best = 0;
                for (int i = 1; i < n; i++) if (adj[i].Count > adj[best].Count) best = i;
                roots.Add(best);
                depth[best] = 0;
            }

            var children = new List<int>[n];
            for (int i = 0; i < n; i++) children[i] = new List<int>();
            var queue = new Queue<int>(roots);
            var orderVisited = new List<int>(roots);
            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                foreach (int w in adj[v])
                {
                    if (depth[w] >= 0) continue;
                    depth[w] = depth[v] + 1;
                    parent[w] = v;
                    children[v].Add(w);
                    queue.Enqueue(w);
                    orderVisited.Add(w);
                }
            }
            cancellation.ThrowIfCancellationRequested();

            // 未到達ノード（別成分）は最外周の外にまとめる
            int maxDepth = 0;
            foreach (int d in depth) maxDepth = Math.Max(maxDepth, d);
            var unreached = new List<int>();
            for (int i = 0; i < n; i++) if (depth[i] < 0) unreached.Add(i);

            // 葉数（重み）を後順で計算
            var weight = new float[n];
            for (int k = orderVisited.Count - 1; k >= 0; k--)
            {
                int v = orderVisited[k];
                float w = 0f;
                foreach (int c in children[v]) w += weight[c];
                weight[v] = Math.Max(1f, w);
            }

            // リング間隔: 周長に対しノードが重ならないよう、各リングのノード数から決める
            var ringCount = new int[maxDepth + 2];
            for (int i = 0; i < n; i++) if (depth[i] >= 0) ringCount[depth[i]]++;
            float baseSpacing = RingSpacing > 0 ? RingSpacing : input.NodeWidth + input.VerticalGap;
            var radius = new float[maxDepth + 2];
            float prev = 0f;
            for (int d = 1; d <= maxDepth + 1; d++)
            {
                int count = d <= maxDepth ? ringCount[d] : unreached.Count;
                float needed = count * (input.NodeWidth * 0.75f + input.HorizontalGap) / (2f * (float)Math.PI);
                radius[d] = Math.Max(prev + baseSpacing, needed);
                prev = radius[d];
            }

            // ルートの配置（複数ルートは中心付近の小円上）
            float totalRootWeight = 0f;
            foreach (int r in roots) totalRootWeight += weight[r];
            var angleStart = new float[n];
            var angleEnd = new float[n];
            float angle = 0f;
            float rootRadius = roots.Count > 1 ? Math.Max(input.NodeWidth * 0.6f, roots.Count * input.NodeWidth / (2f * (float)Math.PI)) : 0f;
            foreach (int r in roots)
            {
                float span = (float)(2 * Math.PI) * weight[r] / totalRootWeight;
                float mid = angle + span * 0.5f;
                result.Positions[r] = new Vec2((float)Math.Cos(mid) * rootRadius, (float)Math.Sin(mid) * rootRadius);
                angleStart[r] = angle;
                angleEnd[r] = angle + span;
                angle += span;
            }

            // Parents precede children in BFS order; avoid recursion on deep hierarchies.
            foreach (int v in orderVisited)
            {
                cancellation.ThrowIfCancellationRequested();
                float a0 = angleStart[v], a1 = angleEnd[v];
                float total = 0f;
                foreach (int c in children[v]) total += weight[c];
                float a = a0;
                foreach (int c in children[v])
                {
                    float span = (a1 - a0) * weight[c] / Math.Max(total, 1e-6f);
                    float mid = a + span * 0.5f;
                    float rr = radius[Math.Min(depth[c], radius.Length - 1)];
                    result.Positions[c] = new Vec2((float)Math.Cos(mid) * rr, (float)Math.Sin(mid) * rr);
                    angleStart[c] = a;
                    angleEnd[c] = a + span;
                    a += span;
                }
            }

            if (unreached.Count > 0)
            {
                float rr = radius[maxDepth + 1];
                for (int k = 0; k < unreached.Count; k++)
                {
                    float a = (float)(2 * Math.PI) * k / unreached.Count;
                    result.Positions[unreached[k]] = new Vec2((float)Math.Cos(a) * rr, (float)Math.Sin(a) * rr);
                }
            }

            LayoutUtil.ApplyPinned(input, result.Positions);
            return result;
        }
    }
}

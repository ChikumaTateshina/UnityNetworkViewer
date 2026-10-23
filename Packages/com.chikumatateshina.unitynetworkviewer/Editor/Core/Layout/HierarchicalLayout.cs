using System;
using System.Collections.Generic;
using System.Threading;

namespace UnityNetworkViewer.Core.Layout
{
    /// <summary>
    /// 親子・依存方向に沿った階層レイアウト（簡易 Sugiyama）。
    /// 循環は強連結成分（SCC）にまとめてから層を割り当てる。
    /// </summary>
    public sealed class HierarchicalLayout : ILayoutStrategy
    {
        public string Name => "Hierarchical";
        public int MaxNodesPerRow = 24;
        public int OrderingSweeps = 6;

        public LayoutResult Compute(LayoutInput input, CancellationToken cancellation)
        {
            int n = input.NodeIds.Count;
            var result = new LayoutResult { Positions = new Vec2[n] };
            if (n == 0) return result;

            var adj = LayoutUtil.BuildAdjacency(input, true);
            int[] scc = Tarjan(adj, out int sccCount);
            cancellation.ThrowIfCancellationRequested();

            // 縮約 DAG
            var dagOut = new HashSet<int>[sccCount];
            var dagInCount = new int[sccCount];
            for (int i = 0; i < sccCount; i++) dagOut[i] = new HashSet<int>();
            for (int v = 0; v < n; v++)
                foreach (int w in adj[v])
                {
                    int a = scc[v], b = scc[w];
                    if (a != b && dagOut[a].Add(b)) dagInCount[b]++;
                }

            // 最長路による層割当（トポロジカル順）
            var layerOf = new int[sccCount];
            var queue = new Queue<int>();
            var inRemaining = (int[])dagInCount.Clone();
            for (int c = 0; c < sccCount; c++) if (inRemaining[c] == 0) queue.Enqueue(c);
            var topo = new List<int>(sccCount);
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                topo.Add(c);
                foreach (int d in dagOut[c])
                {
                    layerOf[d] = Math.Max(layerOf[d], layerOf[c] + 1);
                    if (--inRemaining[d] == 0) queue.Enqueue(d);
                }
            }

            // 選択ノードを含む成分を上へ寄せるため、ルートの層より上にある祖先は層を詰める
            CompactLayers(layerOf, dagOut, topo);

            // 連結成分ごとに配置
            var undirected = LayoutUtil.BuildAdjacency(input, false);
            int[] comp = LayoutUtil.ConnectedComponents(undirected, out int compCount);
            var compOrder = OrderComponents(input, comp, compCount);

            float cellW = input.NodeWidth + input.HorizontalGap;
            float cellH = input.NodeHeight + input.VerticalGap;
            float offsetX = 0f;

            foreach (int ci in compOrder)
            {
                cancellation.ThrowIfCancellationRequested();
                // 成分内のノードを層ごとに分ける
                var layers = new SortedDictionary<int, List<int>>();
                for (int v = 0; v < n; v++)
                {
                    if (comp[v] != ci) continue;
                    int layer = layerOf[scc[v]];
                    if (!layers.TryGetValue(layer, out var list)) layers[layer] = list = new List<int>();
                    list.Add(v);
                }
                var layerList = new List<List<int>>(layers.Values);
                // 同じ SCC のノードは隣接させる
                foreach (var l in layerList) l.Sort((a, b) => scc[a] != scc[b] ? scc[a].CompareTo(scc[b]) : a.CompareTo(b));

                OrderByBarycenter(layerList, adj, input, cancellation);

                float maxWidth = 0f;
                float y = 0f;
                foreach (var layer in layerList)
                {
                    int rows = (layer.Count + MaxNodesPerRow - 1) / MaxNodesPerRow;
                    for (int r = 0; r < rows; r++)
                    {
                        int start = r * MaxNodesPerRow;
                        int count = Math.Min(MaxNodesPerRow, layer.Count - start);
                        float rowWidth = count * cellW;
                        maxWidth = Math.Max(maxWidth, rowWidth);
                        for (int k = 0; k < count; k++)
                        {
                            int v = layer[start + k];
                            result.Positions[v] = new Vec2(offsetX + k * cellW - rowWidth * 0.5f + cellW * 0.5f, y);
                        }
                        y += cellH;
                    }
                }
                // 成分を中心揃えした分だけ右へずらす
                for (int v = 0; v < n; v++)
                    if (comp[v] == ci) result.Positions[v].X += maxWidth * 0.5f;
                offsetX += maxWidth + cellW;
            }

            LayoutUtil.ApplyPinned(input, result.Positions);
            return result;
        }

        static void CompactLayers(int[] layerOf, HashSet<int>[] dagOut, List<int> topo)
        {
            // 逆トポロジカル順に、子の最小層-1 まで親を下げる（ソースが不必要に上に浮くのを防ぐ）
            for (int i = topo.Count - 1; i >= 0; i--)
            {
                int c = topo[i];
                if (dagOut[c].Count == 0) continue;
                int minChild = int.MaxValue;
                foreach (int d in dagOut[c]) minChild = Math.Min(minChild, layerOf[d]);
                if (minChild != int.MaxValue && minChild - 1 > layerOf[c]) layerOf[c] = minChild - 1;
            }
        }

        static List<int> OrderComponents(LayoutInput input, int[] comp, int compCount)
        {
            var sizes = new int[compCount];
            var hasRoot = new bool[compCount];
            foreach (int c in comp) sizes[c]++;
            foreach (int r in input.Roots) if (r >= 0 && r < comp.Length) hasRoot[comp[r]] = true;
            var order = new List<int>();
            for (int i = 0; i < compCount; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                if (hasRoot[a] != hasRoot[b]) return hasRoot[a] ? -1 : 1;
                return sizes[b].CompareTo(sizes[a]);
            });
            return order;
        }

        void OrderByBarycenter(List<List<int>> layers, List<int>[] adj, LayoutInput input, CancellationToken ct)
        {
            int n = adj.Length;
            var pos = new float[n];
            var preds = new List<int>[n];
            for (int i = 0; i < n; i++) preds[i] = new List<int>();
            for (int v = 0; v < n; v++) foreach (int w in adj[v]) preds[w].Add(v);

            void Refresh()
            {
                foreach (var l in layers)
                    for (int i = 0; i < l.Count; i++) pos[l[i]] = i;
            }
            Refresh();

            for (int sweep = 0; sweep < OrderingSweeps; sweep++)
            {
                ct.ThrowIfCancellationRequested();
                bool down = sweep % 2 == 0;
                int start = down ? 1 : layers.Count - 2;
                int end = down ? layers.Count : -1;
                int step = down ? 1 : -1;
                for (int li = start; li != end; li += step)
                {
                    var layer = layers[li];
                    var bary = new Dictionary<int, float>(layer.Count);
                    foreach (int v in layer)
                    {
                        var neigh = down ? preds[v] : adj[v];
                        float sum = 0f;
                        int cnt = 0;
                        foreach (int w in neigh) { sum += pos[w]; cnt++; }
                        bary[v] = cnt > 0 ? sum / cnt : pos[v];
                    }
                    layer.Sort((a, b) =>
                    {
                        int c = bary[a].CompareTo(bary[b]);
                        return c != 0 ? c : pos[a].CompareTo(pos[b]);
                    });
                    for (int i = 0; i < layer.Count; i++) pos[layer[i]] = i;
                }
            }
        }

        /// <summary>反復版 Tarjan SCC。深い参照連鎖でもスタックオーバーフローしない。</summary>
        public static int[] Tarjan(List<int>[] adj, out int count)
        {
            int n = adj.Length;
            var index = new int[n];
            var low = new int[n];
            var onStack = new bool[n];
            var comp = new int[n];
            for (int i = 0; i < n; i++) { index[i] = -1; comp[i] = -1; }
            var stack = new Stack<int>();
            var callStack = new Stack<(int v, int edge)>();
            int idx = 0;
            count = 0;

            for (int s = 0; s < n; s++)
            {
                if (index[s] >= 0) continue;
                callStack.Push((s, 0));
                index[s] = low[s] = idx++;
                stack.Push(s);
                onStack[s] = true;

                while (callStack.Count > 0)
                {
                    var (v, ei) = callStack.Pop();
                    if (ei < adj[v].Count)
                    {
                        callStack.Push((v, ei + 1));
                        int w = adj[v][ei];
                        if (index[w] < 0)
                        {
                            index[w] = low[w] = idx++;
                            stack.Push(w);
                            onStack[w] = true;
                            callStack.Push((w, 0));
                        }
                        else if (onStack[w])
                        {
                            low[v] = Math.Min(low[v], index[w]);
                        }
                    }
                    else
                    {
                        if (low[v] == index[v])
                        {
                            int w;
                            do
                            {
                                w = stack.Pop();
                                onStack[w] = false;
                                comp[w] = count;
                            } while (w != v);
                            count++;
                        }
                        if (callStack.Count > 0)
                        {
                            int parent = callStack.Peek().v;
                            low[parent] = Math.Min(low[parent], low[v]);
                        }
                    }
                }
            }
            return comp;
        }
    }
}

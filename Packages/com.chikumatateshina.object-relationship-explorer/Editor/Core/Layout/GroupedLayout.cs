using System;
using System.Collections.Generic;
using System.Threading;

namespace ObjectRelationshipExplorer.Core.Layout
{
    /// <summary>
    /// Scene / Prefab / 種類などのグループごとにコンテナを作り、内部は階層レイアウトで配置する。
    /// グループ間エッジはコンテナを跨いで描画される。
    /// </summary>
    public sealed class GroupedLayout : ILayoutStrategy
    {
        public string Name => "Grouped";
        public float Padding = 24f;
        public float HeaderHeight = 28f;
        public float GroupGap = 60f;

        public LayoutResult Compute(LayoutInput input, CancellationToken cancellation)
        {
            int n = input.NodeIds.Count;
            var result = new LayoutResult { Positions = new Vec2[n] };
            if (n == 0) return result;

            var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var order = new List<string>();
            for (int i = 0; i < n; i++)
            {
                string key = input.GroupKeys.TryGetValue(i, out var g) && !string.IsNullOrEmpty(g) ? g : "(other)";
                if (!groups.TryGetValue(key, out var list))
                {
                    groups[key] = list = new List<int>();
                    order.Add(key);
                }
                list.Add(i);
            }

            // ルートを含むグループを先頭、それ以外は大きい順
            var rootSet = new HashSet<int>(input.Roots);
            order.Sort((a, b) =>
            {
                bool ra = groups[a].Exists(rootSet.Contains), rb = groups[b].Exists(rootSet.Contains);
                if (ra != rb) return ra ? -1 : 1;
                int c = groups[b].Count.CompareTo(groups[a].Count);
                return c != 0 ? c : string.CompareOrdinal(a, b);
            });

            var inner = new HierarchicalLayout { MaxNodesPerRow = 8 };
            var boxes = new List<(string key, List<int> members, Vec2[] local, float w, float h)>();
            float totalArea = 0f;
            foreach (var key in order)
            {
                cancellation.ThrowIfCancellationRequested();
                var members = groups[key];
                var sub = new LayoutInput
                {
                    NodeWidth = input.NodeWidth,
                    NodeHeight = input.NodeHeight,
                    HorizontalGap = input.HorizontalGap * 0.6f,
                    VerticalGap = input.VerticalGap * 0.6f,
                };
                var map = new Dictionary<int, int>();
                foreach (int m in members) map[m] = sub.AddNode(input.NodeIds[m]);
                foreach (var e in input.Edges)
                    if (map.TryGetValue(e.Source, out int s) && map.TryGetValue(e.Target, out int t))
                        sub.Edges.Add(new LayoutEdge { Source = s, Target = t, Kind = e.Kind });
                foreach (int r in input.Roots) if (map.TryGetValue(r, out int rr)) sub.Roots.Add(rr);

                var local = inner.Compute(sub, cancellation).Positions;
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                foreach (var p in local)
                {
                    minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                    minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                }
                var normalized = new Vec2[local.Length];
                for (int i = 0; i < local.Length; i++)
                    normalized[i] = new Vec2(local[i].X - minX + input.NodeWidth * 0.5f + Padding,
                                             local[i].Y - minY + input.NodeHeight * 0.5f + Padding + HeaderHeight);
                float w = maxX - minX + input.NodeWidth + Padding * 2f;
                float h = maxY - minY + input.NodeHeight + Padding * 2f + HeaderHeight;
                totalArea += w * h;
                boxes.Add((key, members, normalized, w, h));
            }

            // 行詰め（シェルフパッキング）
            float rowLimit = Math.Max((float)Math.Sqrt(totalArea) * 1.6f, input.NodeWidth * 4f);
            float x = 0f, y = 0f, rowHeight = 0f;
            foreach (var box in boxes)
            {
                if (x > 0f && x + box.w > rowLimit)
                {
                    x = 0f;
                    y += rowHeight + GroupGap;
                    rowHeight = 0f;
                }
                for (int i = 0; i < box.members.Count; i++)
                    result.Positions[box.members[i]] = new Vec2(x + box.local[i].X, y + box.local[i].Y);
                result.Groups.Add(new GroupRect
                {
                    Key = box.key,
                    Label = input.GroupLabels.TryGetValue(box.key, out var label) ? label : box.key,
                    X = x,
                    Y = y,
                    Width = box.w,
                    Height = box.h,
                });
                x += box.w + GroupGap;
                rowHeight = Math.Max(rowHeight, box.h);
            }

            LayoutUtil.ApplyPinned(input, result.Positions);
            return result;
        }
    }
}

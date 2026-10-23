using System;
using System.Collections.Generic;
using System.Threading;

namespace ObjectRelationshipExplorer.Core.Layout
{
    public struct Vec2
    {
        public float X;
        public float Y;

        public Vec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);
        public float Length => (float)Math.Sqrt(X * X + Y * Y);
        public override string ToString() => $"({X:0.0}, {Y:0.0})";
    }

    public struct LayoutEdge
    {
        public int Source;
        public int Target;
        public EdgeKind Kind;
    }

    public struct GroupRect
    {
        public string Key;
        public string Label;
        public float X, Y, Width, Height;
    }

    /// <summary>レイアウト計算の入力。Unity Object を参照しない純粋なスナップショット。</summary>
    public sealed class LayoutInput
    {
        public readonly List<string> NodeIds = new List<string>();
        public readonly List<LayoutEdge> Edges = new List<LayoutEdge>();
        /// <summary>中心・起点となるノード（選択ノード）。</summary>
        public readonly List<int> Roots = new List<int>();
        /// <summary>Grouped レイアウトのグループキーとラベル。</summary>
        public readonly Dictionary<int, string> GroupKeys = new Dictionary<int, string>();
        public readonly Dictionary<string, string> GroupLabels = new Dictionary<string, string>();
        /// <summary>固定（ピン留め）されたノードの位置。</summary>
        public readonly Dictionary<int, Vec2> Pinned = new Dictionary<int, Vec2>();
        /// <summary>前回位置（Force-directed の初期値に使用）。</summary>
        public readonly Dictionary<int, Vec2> Previous = new Dictionary<int, Vec2>();
        public float NodeWidth = 160f;
        public float NodeHeight = 36f;
        public float HorizontalGap = 40f;
        public float VerticalGap = 70f;
        public int Seed = 12345;

        readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal);

        public int AddNode(string id)
        {
            if (_index.TryGetValue(id, out int i)) return i;
            i = NodeIds.Count;
            NodeIds.Add(id);
            _index.Add(id, i);
            return i;
        }

        public int IndexOf(string id) => id != null && _index.TryGetValue(id, out int i) ? i : -1;

        public void AddEdge(string source, string target, EdgeKind kind)
        {
            int s = IndexOf(source), t = IndexOf(target);
            if (s < 0 || t < 0 || s == t) return;
            Edges.Add(new LayoutEdge { Source = s, Target = t, Kind = kind });
        }
    }

    public sealed class LayoutResult
    {
        public Vec2[] Positions;
        public readonly List<GroupRect> Groups = new List<GroupRect>();
    }

    public interface ILayoutStrategy
    {
        string Name { get; }
        LayoutResult Compute(LayoutInput input, CancellationToken cancellation);
    }

    public enum LayoutKind
    {
        Hierarchical,
        Radial,
        ForceDirected,
        Grouped,
    }

    public static class LayoutFactory
    {
        public static ILayoutStrategy Create(LayoutKind kind)
        {
            switch (kind)
            {
                case LayoutKind.Radial: return new RadialLayout();
                case LayoutKind.ForceDirected: return new ForceDirectedLayout();
                case LayoutKind.Grouped: return new GroupedLayout();
                default: return new HierarchicalLayout();
            }
        }
    }

    internal static class LayoutUtil
    {
        public static List<int>[] BuildAdjacency(LayoutInput input, bool directed, bool reverse = false)
        {
            int n = input.NodeIds.Count;
            var adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>();
            foreach (var e in input.Edges)
            {
                int s = reverse ? e.Target : e.Source;
                int t = reverse ? e.Source : e.Target;
                adj[s].Add(t);
                if (!directed) adj[t].Add(s);
            }
            return adj;
        }

        /// <summary>無向グラフの連結成分。成分番号の配列を返す。</summary>
        public static int[] ConnectedComponents(List<int>[] undirected, out int count)
        {
            int n = undirected.Length;
            var comp = new int[n];
            for (int i = 0; i < n; i++) comp[i] = -1;
            count = 0;
            var stack = new Stack<int>();
            for (int i = 0; i < n; i++)
            {
                if (comp[i] >= 0) continue;
                comp[i] = count;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    int v = stack.Pop();
                    foreach (int w in undirected[v])
                    {
                        if (comp[w] >= 0) continue;
                        comp[w] = count;
                        stack.Push(w);
                    }
                }
                count++;
            }
            return comp;
        }

        public static void ApplyPinned(LayoutInput input, Vec2[] positions)
        {
            foreach (var kv in input.Pinned)
                if (kv.Key >= 0 && kv.Key < positions.Length) positions[kv.Key] = kv.Value;
        }
    }
}

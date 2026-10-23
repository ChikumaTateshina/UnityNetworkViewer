using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnityNetworkViewer.Core
{
    public sealed class QueryOptions
    {
        public RelationDirection Direction = RelationDirection.Both;
        public int MaxDepth = 1;
        /// <summary>null の場合は全種類。</summary>
        public HashSet<EdgeKind> EdgeKinds;
        /// <summary>null の場合は全確度。</summary>
        public HashSet<EvidenceLevel> Evidence;
        /// <summary>探索で到達を許可するノード種類。null の場合は全種類。ルートは常に含む。</summary>
        public HashSet<NodeKind> NodeKinds;
        /// <summary>false の場合、ルートと異なる Scene のノードへは進まない。</summary>
        public bool CrossSceneBoundaries = true;
        public int MaxNodes = 300;

        public QueryOptions Clone()
        {
            var c = (QueryOptions)MemberwiseClone();
            if (EdgeKinds != null) c.EdgeKinds = new HashSet<EdgeKind>(EdgeKinds);
            if (Evidence != null) c.Evidence = new HashSet<EvidenceLevel>(Evidence);
            if (NodeKinds != null) c.NodeKinds = new HashSet<NodeKind>(NodeKinds);
            return c;
        }

        public bool AllowsEdge(GraphEdge e)
        {
            if (EdgeKinds != null && !EdgeKinds.Contains(e.Kind)) return false;
            if (Evidence != null && !Evidence.Contains(e.Evidence)) return false;
            return true;
        }
    }

    public enum EdgeRole
    {
        /// <summary>ルートから順方向（参照先側）に辿ったエッジ。</summary>
        Outgoing,
        /// <summary>ルートから逆方向（参照元側）に辿ったエッジ。</summary>
        Incoming,
        /// <summary>結果ノード間に存在するが探索経路ではないエッジ。</summary>
        Induced,
    }

    public sealed class SubgraphResult
    {
        public readonly List<string> Roots = new List<string>();
        /// <summary>ノードID → ルートからのホップ数（ルート = 0）。</summary>
        public readonly Dictionary<string, int> NodeDepth = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly Dictionary<string, EdgeRole> EdgeRoles = new Dictionary<string, EdgeRole>(StringComparer.Ordinal);
        /// <summary>予算超過で展開しきれなかったノード → 省略された隣接数。</summary>
        public readonly Dictionary<string, int> TruncatedNeighbors = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>ストアに存在しなかったルート。</summary>
        public readonly List<string> MissingRoots = new List<string>();
        public bool Truncated;

        public bool ContainsNode(string id) => id != null && NodeDepth.ContainsKey(id);

        public int GetDepth(string id) => id != null && NodeDepth.TryGetValue(id, out int d) ? d : -1;
    }

    /// <summary>
    /// GraphStore に対する読み取りクエリ。選択ハイライトは毎回プロジェクトを再スキャンせず、この索引だけを使う。
    /// </summary>
    public sealed class GraphQueryService
    {
        readonly GraphStore _store;

        public GraphQueryService(GraphStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public GraphStore Store => _store;

        /// <summary>幅優先探索で部分グラフを求める。循環があっても各ノードは一度だけ訪問する。</summary>
        public SubgraphResult QuerySubgraph(IEnumerable<string> roots, QueryOptions options)
        {
            options = options ?? new QueryOptions();
            var result = new SubgraphResult();
            var queue = new Queue<string>();
            var rootScenes = new HashSet<string>(StringComparer.Ordinal);

            foreach (var root in roots)
            {
                if (root == null || result.NodeDepth.ContainsKey(root)) continue;
                var node = _store.GetNode(root);
                if (node == null)
                {
                    result.MissingRoots.Add(root);
                    continue;
                }
                result.Roots.Add(root);
                result.NodeDepth[root] = 0;
                queue.Enqueue(root);
                if (!string.IsNullOrEmpty(node.SceneGuid)) rootScenes.Add(node.SceneGuid);
            }

            int maxNodes = options.MaxNodes <= 0 ? int.MaxValue : Math.Max(options.MaxNodes, result.Roots.Count);
            bool forward = options.Direction != RelationDirection.Incoming;
            bool backward = options.Direction != RelationDirection.Outgoing;

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                int depth = result.NodeDepth[current];
                if (depth >= options.MaxDepth) continue;

                if (forward) Expand(current, depth, true, options, result, queue, rootScenes, maxNodes);
                if (backward) Expand(current, depth, false, options, result, queue, rootScenes, maxNodes);
            }

            AddInducedEdges(result, options);
            return result;
        }

        void Expand(string current, int depth, bool forward, QueryOptions options, SubgraphResult result,
            Queue<string> queue, HashSet<string> rootScenes, int maxNodes)
        {
            var edgeIds = forward ? _store.GetOutEdgeIds(current) : _store.GetInEdgeIds(current);
            int skipped = 0;
            foreach (var edgeId in edgeIds)
            {
                var e = _store.GetEdge(edgeId);
                if (e == null || !options.AllowsEdge(e)) continue;
                string next = forward ? e.TargetId : e.SourceId;
                var node = _store.GetNode(next);
                if (node == null) continue;
                if (!AllowsNode(node, options, rootScenes)) continue;

                if (result.NodeDepth.ContainsKey(next))
                {
                    if (!result.EdgeRoles.ContainsKey(edgeId))
                        result.EdgeRoles[edgeId] = forward ? EdgeRole.Outgoing : EdgeRole.Incoming;
                    continue;
                }
                if (result.NodeDepth.Count >= maxNodes)
                {
                    skipped++;
                    continue;
                }
                result.NodeDepth[next] = depth + 1;
                result.EdgeRoles[edgeId] = forward ? EdgeRole.Outgoing : EdgeRole.Incoming;
                queue.Enqueue(next);
            }
            if (skipped > 0)
            {
                result.Truncated = true;
                result.TruncatedNeighbors.TryGetValue(current, out int prev);
                result.TruncatedNeighbors[current] = prev + skipped;
            }
        }

        static bool AllowsNode(GraphNode node, QueryOptions options, HashSet<string> rootScenes)
        {
            if (options.NodeKinds != null && !options.NodeKinds.Contains(node.Kind)) return false;
            if (!options.CrossSceneBoundaries && rootScenes.Count > 0 && !string.IsNullOrEmpty(node.SceneGuid)
                && !rootScenes.Contains(node.SceneGuid)) return false;
            return true;
        }

        void AddInducedEdges(SubgraphResult result, QueryOptions options)
        {
            foreach (var id in result.NodeDepth.Keys)
            {
                foreach (var edgeId in _store.GetOutEdgeIds(id))
                {
                    if (result.EdgeRoles.ContainsKey(edgeId)) continue;
                    var e = _store.GetEdge(edgeId);
                    if (e == null || !options.AllowsEdge(e)) continue;
                    if (result.NodeDepth.ContainsKey(e.TargetId)) result.EdgeRoles[edgeId] = EdgeRole.Induced;
                }
            }
        }

        /// <summary>
        /// 複数ルートの関連集合。Union は和集合、Intersection は全ルートから到達可能なノード（共通参照先/参照元）のみ。
        /// </summary>
        public SubgraphResult QueryMulti(IList<string> roots, QueryOptions options, SelectionSetMode mode)
        {
            if (mode == SelectionSetMode.Union || roots.Count <= 1) return QuerySubgraph(roots, options);

            var perRoot = new List<SubgraphResult>();
            var unlimited = options.Clone();
            unlimited.MaxNodes = Math.Max(options.MaxNodes * 20, 5000);
            foreach (var r in roots) perRoot.Add(QuerySubgraph(new[] { r }, unlimited));

            var result = new SubgraphResult();
            foreach (var pr in perRoot)
            {
                result.MissingRoots.AddRange(pr.MissingRoots);
                result.Truncated |= pr.Truncated;
            }
            var validRoots = new List<SubgraphResult>();
            foreach (var pr in perRoot) if (pr.Roots.Count > 0) validRoots.Add(pr);
            if (validRoots.Count == 0) return result;

            foreach (var pr in validRoots)
            {
                result.Roots.Add(pr.Roots[0]);
                result.NodeDepth[pr.Roots[0]] = 0;
            }

            foreach (var kv in validRoots[0].NodeDepth)
            {
                if (result.NodeDepth.ContainsKey(kv.Key)) continue;
                int maxDepth = kv.Value;
                bool inAll = true;
                for (int i = 1; i < validRoots.Count; i++)
                {
                    if (!validRoots[i].NodeDepth.TryGetValue(kv.Key, out int d)) { inAll = false; break; }
                    if (d > maxDepth) maxDepth = d;
                }
                if (!inAll) continue;
                if (result.NodeDepth.Count >= options.MaxNodes) { result.Truncated = true; break; }
                result.NodeDepth[kv.Key] = maxDepth;
            }

            foreach (var pr in validRoots)
            {
                foreach (var kv in pr.EdgeRoles)
                {
                    var e = _store.GetEdge(kv.Key);
                    if (e == null) continue;
                    if (result.NodeDepth.ContainsKey(e.SourceId) && result.NodeDepth.ContainsKey(e.TargetId)
                        && !result.EdgeRoles.ContainsKey(kv.Key))
                        result.EdgeRoles[kv.Key] = kv.Value;
                }
            }
            return result;
        }

        /// <summary>from から to への最短経路（エッジIDの列）。見つからなければ null。</summary>
        public List<string> FindPath(string from, string to, QueryOptions options, int maxVisited = 200000)
        {
            if (from == null || to == null || !_store.ContainsNode(from) || !_store.ContainsNode(to)) return null;
            if (from == to) return new List<string>();
            options = options ?? new QueryOptions();
            bool forward = options.Direction != RelationDirection.Incoming;
            bool backward = options.Direction != RelationDirection.Outgoing;
            var prevEdge = new Dictionary<string, string>(StringComparer.Ordinal) { [from] = null };
            var queue = new Queue<string>();
            queue.Enqueue(from);
            while (queue.Count > 0 && prevEdge.Count < maxVisited)
            {
                string cur = queue.Dequeue();
                if (forward && Visit(cur, true)) break;
                if (backward && Visit(cur, false)) break;
            }
            if (!prevEdge.ContainsKey(to)) return null;

            var path = new List<string>();
            string node = to;
            while (node != from)
            {
                string edgeId = prevEdge[node];
                path.Add(edgeId);
                var e = _store.GetEdge(edgeId);
                node = e.OtherEnd(node);
            }
            path.Reverse();
            return path;

            bool Visit(string cur, bool fwd)
            {
                foreach (var edgeId in fwd ? _store.GetOutEdgeIds(cur) : _store.GetInEdgeIds(cur))
                {
                    var e = _store.GetEdge(edgeId);
                    if (e == null || !options.AllowsEdge(e)) continue;
                    string next = fwd ? e.TargetId : e.SourceId;
                    if (prevEdge.ContainsKey(next)) continue;
                    var n = _store.GetNode(next);
                    if (n == null || (options.NodeKinds != null && !options.NodeKinds.Contains(n.Kind) && next != to)) continue;
                    prevEdge[next] = edgeId;
                    if (next == to) return true;
                    queue.Enqueue(next);
                }
                return false;
            }
        }

        public List<GraphEdge> GetEdges(string nodeId, RelationDirection direction, QueryOptions filter = null)
        {
            var list = new List<GraphEdge>();
            if (direction != RelationDirection.Incoming)
                foreach (var e in _store.GetOutEdges(nodeId))
                    if (filter == null || filter.AllowsEdge(e)) list.Add(e);
            if (direction != RelationDirection.Outgoing)
                foreach (var e in _store.GetInEdges(nodeId))
                    if (filter == null || filter.AllowsEdge(e)) list.Add(e);
            return list;
        }

        /// <summary>ParentChild / OwnsComponent で親を辿ったパス表示（"Root/Child/Component"）。</summary>
        public string GetStructuralPath(string nodeId, int maxDepth = 64)
        {
            var parts = new List<string>();
            string cur = nodeId;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (cur != null && maxDepth-- > 0 && visited.Add(cur))
            {
                var n = _store.GetNode(cur);
                if (n == null) break;
                parts.Add(n.Name ?? n.Id);
                string parent = null;
                foreach (var e in _store.GetInEdges(cur))
                {
                    if (e.Kind == EdgeKind.ParentChild || e.Kind == EdgeKind.OwnsComponent) { parent = e.SourceId; break; }
                }
                cur = parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        // ------------------------------------------------------------------ Search

        /// <summary>
        /// 検索。空白区切りのトークンを AND 結合する。
        /// 書式: 自由語（名前/型/パス部分一致）, kind:Component, type:Animator, scene:Main, prefab:Door,
        /// evidence:Inferred, provider:udon, method:SetActive, flag:Missing, edge:UnityEventCall, path:Assets/Foo, id:...
        /// 先頭に "-" で否定。
        /// </summary>
        public List<GraphNode> Search(string query, int limit = 200)
        {
            var results = new List<GraphNode>();
            var terms = SearchTerm.Parse(query);
            if (terms.Count == 0) return results;

            foreach (var node in _store.Nodes)
            {
                bool ok = true;
                foreach (var t in terms)
                {
                    if (Matches(node, t) == t.Negate) { ok = false; break; }
                }
                if (!ok) continue;
                results.Add(node);
                if (results.Count >= limit) break;
            }
            results.Sort((a, b) =>
            {
                int c = ScoreKind(a.Kind).CompareTo(ScoreKind(b.Kind));
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return results;
        }

        static int ScoreKind(NodeKind k)
        {
            switch (k)
            {
                case NodeKind.GameObject: return 0;
                case NodeKind.PrefabAsset: return 1;
                case NodeKind.Component: return 2;
                case NodeKind.Scene: return 3;
                default: return 4;
            }
        }

        bool Matches(GraphNode node, SearchTerm t)
        {
            switch (t.Key)
            {
                case "":
                    return StringUtil.ContainsIgnoreCase(node.Name, t.Value)
                           || StringUtil.ContainsIgnoreCase(node.TypeName, t.Value)
                           || StringUtil.ContainsIgnoreCase(node.HierarchyPath, t.Value);
                case "name":
                    return StringUtil.ContainsIgnoreCase(node.Name, t.Value);
                case "kind":
                    return string.Equals(node.Kind.ToString(), t.Value, StringComparison.OrdinalIgnoreCase)
                           || StringUtil.ContainsIgnoreCase(node.Kind.ToString(), t.Value);
                case "type":
                case "component":
                    return StringUtil.ContainsIgnoreCase(node.TypeName, t.Value);
                case "path":
                    return StringUtil.ContainsIgnoreCase(node.AssetPath, t.Value) || StringUtil.ContainsIgnoreCase(node.HierarchyPath, t.Value);
                case "id":
                    return StringUtil.ContainsIgnoreCase(node.Id, t.Value);
                case "flag":
                    return Enum.TryParse(t.Value, true, out NodeFlags f) && f != NodeFlags.None && node.HasFlag(f);
                case "scene":
                {
                    if (string.IsNullOrEmpty(node.SceneGuid)) return false;
                    if (node.SceneGuid.Equals(t.Value, StringComparison.OrdinalIgnoreCase)) return true;
                    var scene = _store.GetNode(NodeIds.Scene(node.SceneGuid));
                    return scene != null && (StringUtil.ContainsIgnoreCase(scene.Name, t.Value) || StringUtil.ContainsIgnoreCase(scene.AssetPath, t.Value));
                }
                case "prefab":
                {
                    if (node.Kind == NodeKind.PrefabAsset && StringUtil.ContainsIgnoreCase(node.Name, t.Value)) return true;
                    foreach (var e in _store.GetOutEdges(node.Id))
                    {
                        if (e.Kind != EdgeKind.PrefabSource && e.Kind != EdgeKind.PrefabVariant) continue;
                        var target = _store.GetNode(e.TargetId);
                        if (target == null) continue;
                        if (StringUtil.ContainsIgnoreCase(target.Name, t.Value) || StringUtil.ContainsIgnoreCase(target.AssetPath, t.Value)) return true;
                        var asset = string.IsNullOrEmpty(target.AssetGuid) ? null : _store.GetNode(NodeIds.Asset(target.AssetGuid));
                        if (asset != null && StringUtil.ContainsIgnoreCase(asset.Name, t.Value)) return true;
                    }
                    return false;
                }
                case "evidence":
                {
                    if (!Enum.TryParse(t.Value, true, out EvidenceLevel ev)) return false;
                    foreach (var e in _store.GetOutEdges(node.Id)) if (e.Evidence == ev) return true;
                    foreach (var e in _store.GetInEdges(node.Id)) if (e.Evidence == ev) return true;
                    return false;
                }
                case "edge":
                {
                    if (!Enum.TryParse(t.Value, true, out EdgeKind ek)) return false;
                    foreach (var e in _store.GetOutEdges(node.Id)) if (e.Kind == ek) return true;
                    foreach (var e in _store.GetInEdges(node.Id)) if (e.Kind == ek) return true;
                    return false;
                }
                case "provider":
                    return StringUtil.ContainsIgnoreCase(node.ProviderId, t.Value);
                case "method":
                {
                    if (node.Kind == NodeKind.UdonMethod || node.Kind == NodeKind.UdonEvent)
                        if (StringUtil.ContainsIgnoreCase(node.Name, t.Value)) return true;
                    foreach (var e in _store.GetOutEdges(node.Id))
                        if ((e.Kind == EdgeKind.MethodControl || e.Kind == EdgeKind.UnityEventCall || e.Kind == EdgeKind.EventDispatch || e.Kind == EdgeKind.MethodCall)
                            && StringUtil.ContainsIgnoreCase(e.MemberName, t.Value)) return true;
                    return false;
                }
                default:
                    return false;
            }
        }

        public sealed class SearchTerm
        {
            public string Key;
            public string Value;
            public bool Negate;

            public static List<SearchTerm> Parse(string query)
            {
                var list = new List<SearchTerm>();
                if (string.IsNullOrWhiteSpace(query)) return list;
                foreach (var raw in Tokenize(query))
                {
                    string token = raw;
                    bool negate = token.Length > 1 && token[0] == '-';
                    if (negate) token = token.Substring(1);
                    string key = "", value = token;
                    int colon = token.IndexOf(':');
                    if (colon > 0)
                    {
                        string k = token.Substring(0, colon).ToLower(CultureInfo.InvariantCulture);
                        if (IsKnownKey(k))
                        {
                            key = k;
                            value = token.Substring(colon + 1);
                        }
                    }
                    if (value.Length == 0) continue;
                    list.Add(new SearchTerm { Key = key, Value = value, Negate = negate });
                }
                return list;
            }

            static bool IsKnownKey(string k)
            {
                switch (k)
                {
                    case "name": case "kind": case "type": case "component": case "path": case "id": case "flag":
                    case "scene": case "prefab": case "evidence": case "edge": case "provider": case "method":
                        return true;
                    default:
                        return false;
                }
            }

            static IEnumerable<string> Tokenize(string q)
            {
                int i = 0;
                while (i < q.Length)
                {
                    while (i < q.Length && char.IsWhiteSpace(q[i])) i++;
                    if (i >= q.Length) yield break;
                    var sb = new System.Text.StringBuilder();
                    bool quoted = false;
                    while (i < q.Length && (quoted || !char.IsWhiteSpace(q[i])))
                    {
                        if (q[i] == '"') quoted = !quoted;
                        else sb.Append(q[i]);
                        i++;
                    }
                    if (sb.Length > 0) yield return sb.ToString();
                }
            }
        }
    }
}

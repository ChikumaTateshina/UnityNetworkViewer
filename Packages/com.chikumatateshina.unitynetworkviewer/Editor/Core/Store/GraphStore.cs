using System;
using System.Collections.Generic;

namespace UnityNetworkViewer.Core
{
    /// <summary>owner（Provider × 解析単位）ごとのメタデータ。キャッシュの妥当性検証に用いる。</summary>
    public sealed class OwnerMetadata
    {
        public string ProviderId;
        public int ProviderVersion;
        public string TargetKey;
        /// <summary>解析時の対象アセット依存ハッシュ（未ロードScene/Asset）。空ならキャッシュ不可。</summary>
        public string DependencyHash;
        public long AnalyzedAtUtcTicks;
        /// <summary>false の場合（ロード済み Scene の session ID を含む等）、ディスクキャッシュへ保存しない。</summary>
        public bool Persistent;

        public OwnerMetadata Clone() => (OwnerMetadata)MemberwiseClone();
    }

    public sealed class OwnerRecord
    {
        public readonly string Key;
        public OwnerMetadata Metadata;
        public readonly List<GraphNode> Nodes = new List<GraphNode>();
        public readonly List<string> EdgeIds = new List<string>();
        public readonly List<AnalysisDiagnostic> Diagnostics = new List<AnalysisDiagnostic>();
        public readonly HashSet<string> DependencyKeys = new HashSet<string>(StringComparer.Ordinal);

        public OwnerRecord(string key)
        {
            Key = key;
        }
    }

    public sealed class GraphChangedEventArgs : EventArgs
    {
        public long Revision;
        public readonly HashSet<string> ChangedOwners = new HashSet<string>(StringComparer.Ordinal);
        public bool Cleared;
    }

    /// <summary>
    /// 型付き有向マルチグラフの索引。forward/reverse adjacency と二次索引を持つ。
    /// メインスレッドからのみ変更すること（読み取りスナップショットは <see cref="CreateSnapshot"/> を使う）。
    /// </summary>
    public sealed class GraphStore
    {
        readonly Dictionary<string, GraphNode> _nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        // nodeId → 提供元（ownerKey, ノード）の小さな列。大半のノードは提供元が 1〜2 件のため Dictionary を持たない
        readonly Dictionary<string, Contributions> _contributions = new Dictionary<string, Contributions>(StringComparer.Ordinal);
        readonly Dictionary<string, GraphEdge> _edges = new Dictionary<string, GraphEdge>(StringComparer.Ordinal);
        readonly Dictionary<string, string> _edgeOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, HashSet<string>> _out = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        readonly Dictionary<string, HashSet<string>> _in = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        readonly Dictionary<string, OwnerRecord> _owners = new Dictionary<string, OwnerRecord>(StringComparer.Ordinal);

        readonly Dictionary<NodeKind, HashSet<string>> _byKind = new Dictionary<NodeKind, HashSet<string>>();
        readonly Dictionary<string, HashSet<string>> _byScene = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        readonly Dictionary<string, HashSet<string>> _byAsset = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        readonly Dictionary<string, HashSet<string>> _byProvider = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        readonly Dictionary<string, HashSet<string>> _dependencyToOwners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // targetKey → owner、および基底キー（'#' より前。Scene のチャンクをまとめて引く）→ owner
        readonly Dictionary<string, HashSet<string>> _ownersByTarget = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        readonly Dictionary<string, HashSet<string>> _ownersByBaseTarget = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        sealed class Contributions
        {
            public readonly List<KeyValuePair<string, GraphNode>> Items = new List<KeyValuePair<string, GraphNode>>(1);

            public int Count => Items.Count;

            public void Set(string owner, GraphNode node)
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    if (!string.Equals(Items[i].Key, owner, StringComparison.Ordinal)) continue;
                    Items[i] = new KeyValuePair<string, GraphNode>(owner, node);
                    return;
                }
                Items.Add(new KeyValuePair<string, GraphNode>(owner, node));
            }

            public void Remove(string owner)
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    if (!string.Equals(Items[i].Key, owner, StringComparison.Ordinal)) continue;
                    Items.RemoveAt(i);
                    return;
                }
            }
        }

        static readonly HashSet<string> EmptySet = new HashSet<string>();

        int _batchDepth;
        GraphChangedEventArgs _pendingChange;

        public long Revision { get; private set; }
        /// <summary>永続化対象（キャッシュ保存される owner）が変化するたびに増える。保存の要否判定に使う。</summary>
        public long PersistentRevision { get; private set; }
        public int NodeCount => _nodes.Count;
        public int EdgeCount => _edges.Count;
        public int OwnerCount => _owners.Count;

        public event Action<GraphChangedEventArgs> Changed;

        public IEnumerable<GraphNode> Nodes => _nodes.Values;
        public IEnumerable<GraphEdge> Edges => _edges.Values;
        public IEnumerable<OwnerRecord> Owners => _owners.Values;
        public IEnumerable<string> SceneGuids => _byScene.Keys;
        public IEnumerable<string> ProviderIds => _byProvider.Keys;

        public GraphNode GetNode(string id)
        {
            if (id == null) return null;
            _nodes.TryGetValue(id, out var n);
            return n;
        }

        public bool ContainsNode(string id) => id != null && _nodes.ContainsKey(id);

        public GraphEdge GetEdge(string id)
        {
            if (id == null) return null;
            _edges.TryGetValue(id, out var e);
            return e;
        }

        public IReadOnlyCollection<string> GetOutEdgeIds(string nodeId)
            => nodeId != null && _out.TryGetValue(nodeId, out var s) ? s : (IReadOnlyCollection<string>)EmptySet;

        public IReadOnlyCollection<string> GetInEdgeIds(string nodeId)
            => nodeId != null && _in.TryGetValue(nodeId, out var s) ? s : (IReadOnlyCollection<string>)EmptySet;

        public IEnumerable<GraphEdge> GetOutEdges(string nodeId)
        {
            foreach (var id in GetOutEdgeIds(nodeId))
                if (_edges.TryGetValue(id, out var e)) yield return e;
        }

        public IEnumerable<GraphEdge> GetInEdges(string nodeId)
        {
            foreach (var id in GetInEdgeIds(nodeId))
                if (_edges.TryGetValue(id, out var e)) yield return e;
        }

        public int GetDegree(string nodeId) => GetOutEdgeIds(nodeId).Count + GetInEdgeIds(nodeId).Count;

        public IReadOnlyCollection<string> GetNodesOfKind(NodeKind kind)
            => _byKind.TryGetValue(kind, out var s) ? s : (IReadOnlyCollection<string>)EmptySet;

        public IReadOnlyCollection<string> GetNodesInScene(string sceneGuid)
            => sceneGuid != null && _byScene.TryGetValue(sceneGuid, out var s) ? s : (IReadOnlyCollection<string>)EmptySet;

        public IReadOnlyCollection<string> GetNodesOfAsset(string assetGuid)
            => assetGuid != null && _byAsset.TryGetValue(assetGuid, out var s) ? s : (IReadOnlyCollection<string>)EmptySet;

        public IReadOnlyCollection<string> GetNodesOfProvider(string providerId)
            => providerId != null && _byProvider.TryGetValue(providerId, out var s) ? s : (IReadOnlyCollection<string>)EmptySet;

        public IReadOnlyCollection<string> GetOwnersDependingOn(string dependencyKey)
            => dependencyKey != null && _dependencyToOwners.TryGetValue(dependencyKey, out var s) ? s : (IReadOnlyCollection<string>)EmptySet;

        public OwnerRecord GetOwner(string ownerKey)
        {
            if (ownerKey == null) return null;
            _owners.TryGetValue(ownerKey, out var r);
            return r;
        }

        public bool HasOwner(string ownerKey) => ownerKey != null && _owners.ContainsKey(ownerKey);

        public string GetEdgeOwner(string edgeId)
        {
            if (edgeId == null) return null;
            _edgeOwner.TryGetValue(edgeId, out var o);
            return o;
        }

        public IEnumerable<string> GetNodeOwners(string nodeId)
        {
            if (nodeId != null && _contributions.TryGetValue(nodeId, out var c))
                foreach (var kv in c.Items) yield return kv.Key;
        }

        public IEnumerable<AnalysisDiagnostic> AllDiagnostics()
        {
            foreach (var o in _owners.Values)
                foreach (var d in o.Diagnostics)
                    yield return d;
        }

        public void BeginBatch()
        {
            _batchDepth++;
        }

        public void EndBatch()
        {
            if (_batchDepth == 0) throw new InvalidOperationException("EndBatch without BeginBatch");
            _batchDepth--;
            if (_batchDepth == 0 && _pendingChange != null)
            {
                var change = _pendingChange;
                _pendingChange = null;
                Changed?.Invoke(change);
            }
        }

        void NotifyChanged(string ownerKey, bool cleared = false)
        {
            if (_pendingChange == null) _pendingChange = new GraphChangedEventArgs();
            _pendingChange.Revision = Revision;
            _pendingChange.Cleared |= cleared;
            if (ownerKey != null) _pendingChange.ChangedOwners.Add(ownerKey);
            if (_batchDepth == 0)
            {
                var change = _pendingChange;
                _pendingChange = null;
                Changed?.Invoke(change);
            }
        }

        public void Clear()
        {
            _nodes.Clear();
            _contributions.Clear();
            _edges.Clear();
            _edgeOwner.Clear();
            _out.Clear();
            _in.Clear();
            _owners.Clear();
            _byKind.Clear();
            _byScene.Clear();
            _byAsset.Clear();
            _byProvider.Clear();
            _dependencyToOwners.Clear();
            _ownersByTarget.Clear();
            _ownersByBaseTarget.Clear();
            Revision++;
            PersistentRevision++;
            NotifyChanged(null, true);
        }

        /// <summary>
        /// owner の既存ノード/エッジ/診断を削除し、結果で置き換える。途中で例外が起きても
        /// 半端な状態を公開しないよう、検証は変更前に行う。
        /// </summary>
        public void ReplaceOwner(string ownerKey, AnalysisResult result, OwnerMetadata metadata)
        {
            if (string.IsNullOrEmpty(ownerKey)) throw new ArgumentException("ownerKey is required");
            if (result == null) throw new ArgumentNullException(nameof(result));
            foreach (var n in result.Nodes)
                if (string.IsNullOrEmpty(n.Id)) throw new ArgumentException("node without id in result for " + ownerKey);
            foreach (var e in result.Edges)
                if (string.IsNullOrEmpty(e.Id) || string.IsNullOrEmpty(e.SourceId) || string.IsNullOrEmpty(e.TargetId))
                    throw new ArgumentException("invalid edge in result for " + ownerKey);

            Revision++;
            RemoveOwnerInternal(ownerKey);

            var record = new OwnerRecord(ownerKey)
            {
                Metadata = metadata?.Clone() ?? new OwnerMetadata { ProviderId = OwnerKeys.GetProviderId(ownerKey), TargetKey = OwnerKeys.GetTargetKey(ownerKey) },
            };
            _owners.Add(ownerKey, record);
            IndexOwner(ownerKey);
            if (record.Metadata.Persistent) PersistentRevision++;

            foreach (var node in result.Nodes)
            {
                node.LastSeenRevision = Revision;
                record.Nodes.Add(node);
                AddContribution(ownerKey, node);
            }

            foreach (var edge in result.Edges)
            {
                EnsureEndpoint(ownerKey, record, edge.SourceId);
                EnsureEndpoint(ownerKey, record, edge.TargetId);

                string id = edge.Id;
                if (_edges.ContainsKey(id))
                {
                    // 他 owner と ID が衝突した場合（同一オブジェクトを別解析単位が報告）は owner で区別する。
                    id = edge.Id + "@" + StableHash.Compute(ownerKey);
                    if (_edges.ContainsKey(id)) continue;
                    edge.Id = id;
                }
                edge.AnalysisRevision = Revision;
                _edges.Add(id, edge);
                _edgeOwner.Add(id, ownerKey);
                record.EdgeIds.Add(id);
                GetOrCreate(_out, edge.SourceId).Add(id);
                GetOrCreate(_in, edge.TargetId).Add(id);
            }

            foreach (var d in result.Diagnostics)
            {
                d.OwnerKey = ownerKey;
                record.Diagnostics.Add(d);
            }

            foreach (var dep in result.DependencyKeys)
            {
                record.DependencyKeys.Add(dep);
                GetOrCreate(_dependencyToOwners, dep).Add(ownerKey);
            }

            NotifyChanged(ownerKey);
        }

        public bool RemoveOwner(string ownerKey)
        {
            if (ownerKey == null || !_owners.ContainsKey(ownerKey)) return false;
            Revision++;
            RemoveOwnerInternal(ownerKey);
            NotifyChanged(ownerKey);
            return true;
        }

        /// <summary>指定した解析単位（targetKey）を持つ owner をすべて返す。</summary>
        public List<string> FindOwnersByTarget(string targetKey)
        {
            return targetKey != null && _ownersByTarget.TryGetValue(targetKey, out var set) ? new List<string>(set) : new List<string>();
        }

        /// <summary>基底キー（例: "scene:{guid}"）と、その下位キー（"scene:{guid}#…"）を持つ owner をすべて返す。</summary>
        public List<string> FindOwnersByBaseTarget(string baseKey)
        {
            return baseKey != null && _ownersByBaseTarget.TryGetValue(baseKey, out var set) ? new List<string>(set) : new List<string>();
        }

        public bool HasOwnersForTarget(string targetKey) => targetKey != null && _ownersByTarget.ContainsKey(targetKey);

        public bool HasOwnersForBaseTarget(string baseKey) => baseKey != null && _ownersByBaseTarget.ContainsKey(baseKey);

        /// <summary>targetKey の '#' より前（Scene のチャンクキーから Scene キーを得る）。</summary>
        public static string GetBaseTargetKey(string targetKey)
        {
            if (targetKey == null) return null;
            int i = targetKey.IndexOf('#');
            return i < 0 ? targetKey : targetKey.Substring(0, i);
        }

        void IndexOwner(string ownerKey)
        {
            string target = OwnerKeys.GetTargetKey(ownerKey);
            GetOrCreate(_ownersByTarget, target).Add(ownerKey);
            GetOrCreate(_ownersByBaseTarget, GetBaseTargetKey(target)).Add(ownerKey);
        }

        void UnindexOwner(string ownerKey)
        {
            string target = OwnerKeys.GetTargetKey(ownerKey);
            Remove(_ownersByTarget, target, ownerKey);
            Remove(_ownersByBaseTarget, GetBaseTargetKey(target), ownerKey);
        }

        void EnsureEndpoint(string ownerKey, OwnerRecord record, string nodeId)
        {
            if (_nodes.ContainsKey(nodeId)) return;
            // Provider が端点ノードを提供しなかった場合でも、エッジが孤立しないよう仮ノードを作る。
            var placeholder = new GraphNode
            {
                Id = nodeId,
                Kind = NodeIds.IsScene(nodeId) ? NodeKind.Scene : NodeKind.UnresolvedReference,
                Name = nodeId,
                Flags = NodeFlags.Placeholder,
                ProviderId = OwnerKeys.GetProviderId(ownerKey),
                SceneGuid = NodeIds.TryGetSceneGuid(nodeId),
                AssetGuid = NodeIds.TryGetAssetGuid(nodeId),
                LastSeenRevision = Revision,
            };
            record.Nodes.Add(placeholder);
            AddContribution(ownerKey, placeholder);
        }

        void AddContribution(string ownerKey, GraphNode node)
        {
            if (!_contributions.TryGetValue(node.Id, out var contribs))
            {
                contribs = new Contributions();
                _contributions.Add(node.Id, contribs);
            }
            contribs.Set(ownerKey, node);
            RecomputeEffective(node.Id, contribs);
        }

        void RecomputeEffective(string nodeId, Contributions contribs)
        {
            GraphNode best = null;
            foreach (var kv in contribs.Items)
            {
                var n = kv.Value;
                if (best == null) { best = n; continue; }
                bool bestPh = best.IsPlaceholder, nPh = n.IsPlaceholder;
                if (bestPh && !nPh) best = n;
                else if (bestPh == nPh && n.LastSeenRevision > best.LastSeenRevision) best = n;
            }

            if (_nodes.TryGetValue(nodeId, out var current))
            {
                if (ReferenceEquals(current, best)) return;
                Unindex(current);
            }
            if (best == null)
            {
                _nodes.Remove(nodeId);
                return;
            }
            _nodes[nodeId] = best;
            Index(best);
        }

        void RemoveOwnerInternal(string ownerKey)
        {
            if (!_owners.TryGetValue(ownerKey, out var record)) return;
            _owners.Remove(ownerKey);
            UnindexOwner(ownerKey);
            if (record.Metadata != null && record.Metadata.Persistent) PersistentRevision++;

            foreach (var edgeId in record.EdgeIds)
            {
                if (!_edges.TryGetValue(edgeId, out var edge)) continue;
                _edges.Remove(edgeId);
                _edgeOwner.Remove(edgeId);
                if (_out.TryGetValue(edge.SourceId, out var o)) { o.Remove(edgeId); if (o.Count == 0) _out.Remove(edge.SourceId); }
                if (_in.TryGetValue(edge.TargetId, out var i)) { i.Remove(edgeId); if (i.Count == 0) _in.Remove(edge.TargetId); }
            }

            foreach (var node in record.Nodes)
            {
                if (!_contributions.TryGetValue(node.Id, out var contribs)) continue;
                contribs.Remove(ownerKey);
                if (contribs.Count == 0)
                {
                    if (HasIncidentEdges(node.Id))
                    {
                        // 他 owner のエッジがまだ参照している場合は、そのエッジの owner に仮ノードとして引き継ぐ。
                        string heir = FindIncidentEdgeOwner(node.Id);
                        var ph = node.Clone();
                        ph.Flags |= NodeFlags.Placeholder;
                        contribs.Set(heir, ph);
                        if (_owners.TryGetValue(heir, out var heirRecord)) heirRecord.Nodes.Add(ph);
                        RecomputeEffective(node.Id, contribs);
                    }
                    else
                    {
                        _contributions.Remove(node.Id);
                        RecomputeEffective(node.Id, contribs);
                    }
                }
                else
                {
                    RecomputeEffective(node.Id, contribs);
                }
            }

            foreach (var dep in record.DependencyKeys)
            {
                if (_dependencyToOwners.TryGetValue(dep, out var set))
                {
                    set.Remove(ownerKey);
                    if (set.Count == 0) _dependencyToOwners.Remove(dep);
                }
            }
        }

        bool HasIncidentEdges(string nodeId)
            => (_out.TryGetValue(nodeId, out var o) && o.Count > 0) || (_in.TryGetValue(nodeId, out var i) && i.Count > 0);

        string FindIncidentEdgeOwner(string nodeId)
        {
            if (_out.TryGetValue(nodeId, out var o))
                foreach (var e in o) if (_edgeOwner.TryGetValue(e, out var owner)) return owner;
            if (_in.TryGetValue(nodeId, out var i))
                foreach (var e in i) if (_edgeOwner.TryGetValue(e, out var owner)) return owner;
            return null;
        }

        void Index(GraphNode n)
        {
            GetOrCreate(_byKind, n.Kind).Add(n.Id);
            if (!string.IsNullOrEmpty(n.SceneGuid)) GetOrCreate(_byScene, n.SceneGuid).Add(n.Id);
            if (!string.IsNullOrEmpty(n.AssetGuid)) GetOrCreate(_byAsset, n.AssetGuid).Add(n.Id);
            if (!string.IsNullOrEmpty(n.ProviderId)) GetOrCreate(_byProvider, n.ProviderId).Add(n.Id);
        }

        void Unindex(GraphNode n)
        {
            Remove(_byKind, n.Kind, n.Id);
            if (!string.IsNullOrEmpty(n.SceneGuid)) Remove(_byScene, n.SceneGuid, n.Id);
            if (!string.IsNullOrEmpty(n.AssetGuid)) Remove(_byAsset, n.AssetGuid, n.Id);
            if (!string.IsNullOrEmpty(n.ProviderId)) Remove(_byProvider, n.ProviderId, n.Id);
        }

        static HashSet<string> GetOrCreate<TKey>(Dictionary<TKey, HashSet<string>> dict, TKey key)
        {
            if (!dict.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                dict.Add(key, set);
            }
            return set;
        }

        static void Remove<TKey>(Dictionary<TKey, HashSet<string>> dict, TKey key, string id)
        {
            if (dict.TryGetValue(key, out var set))
            {
                set.Remove(id);
                if (set.Count == 0) dict.Remove(key);
            }
        }

        /// <summary>ワーカースレッドでのレイアウト等に使う、読み取り専用の浅いスナップショット。</summary>
        public GraphSnapshot CreateSnapshot(IEnumerable<string> nodeIds)
        {
            var snap = new GraphSnapshot();
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in nodeIds)
            {
                var n = GetNode(id);
                if (n == null || !set.Add(id)) continue;
                snap.Nodes.Add(n.Clone());
            }
            foreach (var id in set)
            {
                foreach (var e in GetOutEdges(id))
                    if (set.Contains(e.TargetId)) snap.Edges.Add(e.Clone());
            }
            snap.Revision = Revision;
            return snap;
        }
    }

    public sealed class GraphSnapshot
    {
        public readonly List<GraphNode> Nodes = new List<GraphNode>();
        public readonly List<GraphEdge> Edges = new List<GraphEdge>();
        public long Revision;
    }
}

using System;
using System.Collections.Generic;

namespace ObjectRelationshipExplorer.Core
{
    public sealed class GraphNode
    {
        public string Id;
        public NodeKind Kind;
        public string Name;
        /// <summary>型名（Component の型、Asset の型など）。</summary>
        public string TypeName;
        public string SceneGuid;
        public string AssetGuid;
        public string AssetPath;
        public long LocalFileId;
        public NodeFlags Flags;
        /// <summary>Hierarchy 上のパス（表示・検索用。ID には使わない）。</summary>
        public string HierarchyPath;
        /// <summary>このノードを提供した Provider 群のうち、代表のもの。</summary>
        public string ProviderId;
        public long LastSeenRevision;

        public bool IsPlaceholder => (Flags & NodeFlags.Placeholder) != 0;
        public bool IsUnloaded => (Flags & NodeFlags.Unloaded) != 0;
        public bool IsTransient => (Flags & NodeFlags.Transient) != 0;

        public bool HasFlag(NodeFlags flag) => (Flags & flag) != 0;

        public GraphNode Clone()
        {
            return (GraphNode)MemberwiseClone();
        }

        public override string ToString() => $"{Kind}:{Name} ({Id})";
    }

    public sealed class GraphEdge
    {
        public string Id;
        public string SourceId;
        public string TargetId;
        public EdgeKind Kind;
        public EvidenceLevel Evidence;
        public string ProviderId;

        // ---- Provenance（根拠・出典） ----
        /// <summary>SerializedProperty.propertyPath 等。</summary>
        public string PropertyPath;
        /// <summary>メソッド名・変数名・操作API名など。</summary>
        public string MemberName;
        public string SourceFile;
        public int SourceLine;
        public int SourceColumn;
        /// <summary>根拠の Asset / Scene GUID。</summary>
        public string ProvenanceAssetGuid;
        public string ProvenanceSceneGuid;
        /// <summary>推定理由・診断など人が読む説明。</summary>
        public string Explanation;
        /// <summary>";" 区切りの付加属性（prefabOverride, arrayElement, mayEdge 等）。</summary>
        public string Qualifiers;
        public long AnalysisRevision;
        public long AnalyzedAtUtcTicks;

        public bool HasQualifier(string qualifier)
        {
            if (string.IsNullOrEmpty(Qualifiers) || string.IsNullOrEmpty(qualifier)) return false;
            int start = 0;
            while (start <= Qualifiers.Length)
            {
                int end = Qualifiers.IndexOf(';', start);
                if (end < 0) end = Qualifiers.Length;
                int len = end - start;
                if (len == qualifier.Length && string.CompareOrdinal(Qualifiers, start, qualifier, 0, len) == 0) return true;
                if (len > qualifier.Length && Qualifiers[start + qualifier.Length] == '=' &&
                    string.CompareOrdinal(Qualifiers, start, qualifier, 0, qualifier.Length) == 0) return true;
                start = end + 1;
            }
            return false;
        }

        public void AddQualifier(string qualifier)
        {
            if (string.IsNullOrEmpty(qualifier) || HasQualifier(qualifier)) return;
            Qualifiers = string.IsNullOrEmpty(Qualifiers) ? qualifier : Qualifiers + ";" + qualifier;
        }

        public string OtherEnd(string nodeId) => nodeId == SourceId ? TargetId : SourceId;

        public GraphEdge Clone()
        {
            return (GraphEdge)MemberwiseClone();
        }

        public override string ToString() => $"{SourceId} -[{Kind}/{Evidence}]-> {TargetId}";
    }

    public sealed class AnalysisDiagnostic
    {
        public DiagnosticSeverity Severity;
        /// <summary>機械判別用コード（MissingReference, MissingScript, ProviderError 等）。</summary>
        public string Code;
        public string Message;
        public string NodeId;
        public string ProviderId;
        public string OwnerKey;
        public string AssetPath;
        public string PropertyPath;
        public int SourceLine;

        public AnalysisDiagnostic Clone() => (AnalysisDiagnostic)MemberwiseClone();

        public override string ToString() => $"[{Severity}] {Code}: {Message}";
    }

    public static class DiagnosticCodes
    {
        public const string MissingReference = "MissingReference";
        public const string MissingScript = "MissingScript";
        public const string MissingPrefab = "MissingPrefab";
        public const string ProviderError = "ProviderError";
        public const string Unsupported = "Unsupported";
        public const string BinarySerialization = "BinarySerialization";
        public const string Truncated = "Truncated";
        public const string UnresolvedTarget = "UnresolvedTarget";
        public const string ValueMismatch = "ValueMismatch";
        public const string Cycle = "Cycle";
        public const string Orphan = "Orphan";
        public const string ParseLimit = "ParseLimit";
    }

    /// <summary>
    /// Provider の解析結果。Provider は GraphStore を直接変更せず、これを返す。
    /// Scheduler が owner 単位で原子的に差し替える。
    /// </summary>
    public sealed class AnalysisResult
    {
        public readonly List<GraphNode> Nodes = new List<GraphNode>();
        public readonly List<GraphEdge> Edges = new List<GraphEdge>();
        public readonly List<AnalysisDiagnostic> Diagnostics = new List<AnalysisDiagnostic>();
        /// <summary>この結果が依存する Asset GUID 等。変更時に再解析対象になる。</summary>
        public readonly HashSet<string> DependencyKeys = new HashSet<string>(StringComparer.Ordinal);

        readonly Dictionary<string, int> _nodeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, int> _edgeOccurrence = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly HashSet<string> _usedEdgeIds = new HashSet<string>(StringComparer.Ordinal);

        public string ProviderId;

        public AnalysisResult() { }

        public AnalysisResult(string providerId)
        {
            ProviderId = providerId;
        }

        public bool ContainsNode(string id) => id != null && _nodeIndex.ContainsKey(id);

        public GraphNode GetNode(string id)
        {
            return id != null && _nodeIndex.TryGetValue(id, out int index) ? Nodes[index] : null;
        }

        /// <summary>
        /// ノードを追加する。同一IDが既にある場合、Placeholder より実体を優先して統合する。
        /// </summary>
        public GraphNode AddNode(GraphNode node)
        {
            if (node == null || string.IsNullOrEmpty(node.Id)) throw new ArgumentException("node id is required");
            if (node.ProviderId == null) node.ProviderId = ProviderId;
            if (_nodeIndex.TryGetValue(node.Id, out int index))
            {
                GraphNode existing = Nodes[index];
                if (existing.IsPlaceholder && !node.IsPlaceholder)
                {
                    Nodes[index] = node;
                    return node;
                }
                if (!node.IsPlaceholder)
                {
                    existing.Flags |= node.Flags & ~NodeFlags.Placeholder;
                }
                MergeMissingFields(existing, node);
                return existing;
            }
            _nodeIndex.Add(node.Id, Nodes.Count);
            Nodes.Add(node);
            return node;
        }

        static void MergeMissingFields(GraphNode into, GraphNode from)
        {
            if (string.IsNullOrEmpty(into.Name)) into.Name = from.Name;
            if (string.IsNullOrEmpty(into.TypeName)) into.TypeName = from.TypeName;
            if (string.IsNullOrEmpty(into.SceneGuid)) into.SceneGuid = from.SceneGuid;
            if (string.IsNullOrEmpty(into.AssetGuid)) into.AssetGuid = from.AssetGuid;
            if (string.IsNullOrEmpty(into.AssetPath)) into.AssetPath = from.AssetPath;
            if (string.IsNullOrEmpty(into.HierarchyPath)) into.HierarchyPath = from.HierarchyPath;
            if (into.LocalFileId == 0) into.LocalFileId = from.LocalFileId;
        }

        /// <summary>
        /// エッジを追加する。ID未指定の場合は決定的に生成し、同一キーが複数回現れた場合は
        /// occurrence を加えて多重エッジとして保持する（同種参照を潰さない）。
        /// </summary>
        public GraphEdge AddEdge(GraphEdge edge)
        {
            if (edge == null) throw new ArgumentNullException(nameof(edge));
            if (string.IsNullOrEmpty(edge.SourceId) || string.IsNullOrEmpty(edge.TargetId))
                throw new ArgumentException("edge endpoints are required");
            if (edge.ProviderId == null) edge.ProviderId = ProviderId;
            string baseId = string.IsNullOrEmpty(edge.Id) ? EdgeIds.Create(edge) : edge.Id;
            _edgeOccurrence.TryGetValue(baseId, out int occurrence);
            string id = baseId;
            while (_usedEdgeIds.Contains(id))
            {
                occurrence++;
                id = baseId + "#" + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            _edgeOccurrence[baseId] = occurrence;
            _usedEdgeIds.Add(id);
            edge.Id = id;
            Edges.Add(edge);
            return edge;
        }

        public GraphEdge AddEdge(string sourceId, string targetId, EdgeKind kind, EvidenceLevel evidence,
            string propertyPath = null, string memberName = null)
        {
            return AddEdge(new GraphEdge
            {
                SourceId = sourceId,
                TargetId = targetId,
                Kind = kind,
                Evidence = evidence,
                PropertyPath = propertyPath,
                MemberName = memberName,
            });
        }

        public AnalysisDiagnostic AddDiagnostic(DiagnosticSeverity severity, string code, string message,
            string nodeId = null, string assetPath = null, string propertyPath = null, int line = 0)
        {
            var d = new AnalysisDiagnostic
            {
                Severity = severity,
                Code = code,
                Message = message,
                NodeId = nodeId,
                ProviderId = ProviderId,
                AssetPath = assetPath,
                PropertyPath = propertyPath,
                SourceLine = line,
            };
            Diagnostics.Add(d);
            return d;
        }

        /// <summary>別の結果を取り込む（同一 owner に複数 Provider の部分結果をまとめる場合）。</summary>
        public void Merge(AnalysisResult other)
        {
            if (other == null) return;
            foreach (var n in other.Nodes) AddNode(n);
            foreach (var e in other.Edges) AddEdge(e);
            Diagnostics.AddRange(other.Diagnostics);
            foreach (var k in other.DependencyKeys) DependencyKeys.Add(k);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityNetworkViewer.Core
{
    public sealed class CachedOwner
    {
        public string Key;
        public OwnerMetadata Metadata;
        public AnalysisResult Result;
    }

    /// <summary>
    /// 保存用の owner の浅いコピー。メインスレッドで作成し、書き出しはワーカースレッドで行える
    /// （コミット済みのノード/エッジは以後変更されないため、参照の配列だけを複製する）。
    /// </summary>
    public sealed class OwnerSnapshot
    {
        public string Key;
        public OwnerMetadata Metadata;
        public GraphNode[] Nodes;
        public GraphEdge[] Edges;
        public AnalysisDiagnostic[] Diagnostics;
        public string[] DependencyKeys;
    }

    public sealed class GraphCacheHeader
    {
        public int SchemaVersion;
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);

        public string Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
    }

    public sealed class GraphCacheFormatException : Exception
    {
        public GraphCacheFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// 索引キャッシュのバイナリ形式。owner 単位で保存し、読み込み時に owner ごとに妥当性を検証できる。
    /// 破損・版不一致の場合は <see cref="GraphCacheFormatException"/> を投げ、呼び出し側が再構築する。
    /// </summary>
    public static class GraphCacheSerializer
    {
        const uint Magic = 0x3145524F; // "ORE1"
        public const int FormatVersion = 2;

        public static List<OwnerSnapshot> CreateSnapshots(GraphStore store, Func<OwnerRecord, bool> filter)
        {
            var list = new List<OwnerSnapshot>();
            foreach (var o in store.Owners)
            {
                if (filter != null && !filter(o)) continue;
                var edges = new List<GraphEdge>(o.EdgeIds.Count);
                foreach (var id in o.EdgeIds)
                {
                    var e = store.GetEdge(id);
                    if (e != null) edges.Add(e);
                }
                var deps = new string[o.DependencyKeys.Count];
                o.DependencyKeys.CopyTo(deps);
                list.Add(new OwnerSnapshot
                {
                    Key = o.Key,
                    Metadata = (o.Metadata ?? new OwnerMetadata()).Clone(),
                    Nodes = o.Nodes.ToArray(),
                    Edges = edges.ToArray(),
                    Diagnostics = o.Diagnostics.ToArray(),
                    DependencyKeys = deps,
                });
            }
            return list;
        }

        public static void Write(Stream stream, GraphCacheHeader header, GraphStore store, Func<OwnerRecord, bool> filter)
        {
            Write(stream, header, CreateSnapshots(store, filter));
        }

        public static void Write(Stream stream, GraphCacheHeader header, IReadOnlyList<OwnerSnapshot> owners)
        {
            using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                w.Write(Magic);
                w.Write(FormatVersion);
                w.Write(header.SchemaVersion);
                w.Write(header.Values.Count);
                foreach (var kv in header.Values)
                {
                    WriteString(w, kv.Key);
                    WriteString(w, kv.Value);
                }

                w.Write(owners.Count);
                foreach (var o in owners)
                {
                    WriteString(w, o.Key);
                    var m = o.Metadata ?? new OwnerMetadata();
                    WriteString(w, m.ProviderId);
                    w.Write(m.ProviderVersion);
                    WriteString(w, m.TargetKey);
                    WriteString(w, m.DependencyHash);
                    w.Write(m.AnalyzedAtUtcTicks);
                    w.Write(m.Persistent);

                    w.Write(o.Nodes.Length);
                    foreach (var n in o.Nodes) WriteNode(w, n);

                    w.Write(o.Edges.Length);
                    foreach (var e in o.Edges) WriteEdge(w, e);

                    w.Write(o.Diagnostics.Length);
                    foreach (var d in o.Diagnostics) WriteDiagnostic(w, d);

                    w.Write(o.DependencyKeys.Length);
                    foreach (var k in o.DependencyKeys) WriteString(w, k);
                }
                w.Write(Magic);
            }
        }

        public static GraphCacheHeader ReadHeader(Stream stream)
        {
            using (var r = new BinaryReader(stream, Encoding.UTF8, true))
            {
                return ReadHeaderInternal(r);
            }
        }

        static GraphCacheHeader ReadHeaderInternal(BinaryReader r)
        {
            if (r.ReadUInt32() != Magic) throw new GraphCacheFormatException("invalid magic");
            int format = r.ReadInt32();
            if (format != FormatVersion) throw new GraphCacheFormatException("format version mismatch: " + format);
            var header = new GraphCacheHeader { SchemaVersion = r.ReadInt32() };
            int count = r.ReadInt32();
            if (count < 0 || count > 10000) throw new GraphCacheFormatException("invalid header");
            for (int i = 0; i < count; i++) header.Values[ReadString(r) ?? ""] = ReadString(r);
            return header;
        }

        public static List<CachedOwner> Read(Stream stream, out GraphCacheHeader header)
        {
            try
            {
                using (var r = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    header = ReadHeaderInternal(r);
                    int ownerCount = r.ReadInt32();
                    if (ownerCount < 0) throw new GraphCacheFormatException("invalid owner count");
                    var list = new List<CachedOwner>(ownerCount);
                    for (int i = 0; i < ownerCount; i++)
                    {
                        var owner = new CachedOwner { Key = ReadString(r) };
                        owner.Metadata = new OwnerMetadata
                        {
                            ProviderId = ReadString(r),
                            ProviderVersion = r.ReadInt32(),
                            TargetKey = ReadString(r),
                            DependencyHash = ReadString(r),
                            AnalyzedAtUtcTicks = r.ReadInt64(),
                            Persistent = r.ReadBoolean(),
                        };
                        var result = new AnalysisResult(owner.Metadata.ProviderId);
                        int nodes = ReadCount(r);
                        for (int k = 0; k < nodes; k++) result.AddNode(ReadNode(r));
                        int edges = ReadCount(r);
                        for (int k = 0; k < edges; k++) result.AddEdge(ReadEdge(r));
                        int diags = ReadCount(r);
                        for (int k = 0; k < diags; k++) result.Diagnostics.Add(ReadDiagnostic(r));
                        int deps = ReadCount(r);
                        for (int k = 0; k < deps; k++) result.DependencyKeys.Add(ReadString(r));
                        owner.Result = result;
                        list.Add(owner);
                    }
                    if (r.ReadUInt32() != Magic) throw new GraphCacheFormatException("truncated cache");
                    return list;
                }
            }
            catch (EndOfStreamException)
            {
                throw new GraphCacheFormatException("unexpected end of cache");
            }
            catch (IOException ex)
            {
                throw new GraphCacheFormatException("cache read error: " + ex.Message);
            }
            catch (ArgumentException ex)
            {
                throw new GraphCacheFormatException("corrupted cache: " + ex.Message);
            }
        }

        static int ReadCount(BinaryReader r)
        {
            int c = r.ReadInt32();
            if (c < 0 || c > 50_000_000) throw new GraphCacheFormatException("invalid count");
            return c;
        }

        static void WriteNode(BinaryWriter w, GraphNode n)
        {
            WriteString(w, n.Id);
            w.Write((int)n.Kind);
            WriteString(w, n.Name);
            WriteString(w, n.TypeName);
            WriteString(w, n.SceneGuid);
            WriteString(w, n.AssetGuid);
            WriteString(w, n.AssetPath);
            w.Write(n.LocalFileId);
            w.Write((int)n.Flags);
            WriteString(w, n.HierarchyPath);
            WriteString(w, n.ProviderId);
        }

        static GraphNode ReadNode(BinaryReader r)
        {
            return new GraphNode
            {
                Id = ReadString(r),
                Kind = (NodeKind)r.ReadInt32(),
                Name = ReadString(r),
                TypeName = ReadString(r),
                SceneGuid = ReadString(r),
                AssetGuid = ReadString(r),
                AssetPath = ReadString(r),
                LocalFileId = r.ReadInt64(),
                Flags = (NodeFlags)r.ReadInt32(),
                HierarchyPath = ReadString(r),
                ProviderId = ReadString(r),
            };
        }

        static void WriteEdge(BinaryWriter w, GraphEdge e)
        {
            WriteString(w, e.Id);
            WriteString(w, e.SourceId);
            WriteString(w, e.TargetId);
            w.Write((int)e.Kind);
            w.Write((int)e.Evidence);
            WriteString(w, e.ProviderId);
            WriteString(w, e.PropertyPath);
            WriteString(w, e.MemberName);
            WriteString(w, e.SourceFile);
            w.Write(e.SourceLine);
            w.Write(e.SourceColumn);
            WriteString(w, e.ProvenanceAssetGuid);
            WriteString(w, e.ProvenanceSceneGuid);
            WriteString(w, e.Explanation);
            WriteString(w, e.Qualifiers);
            w.Write(e.AnalyzedAtUtcTicks);
        }

        static GraphEdge ReadEdge(BinaryReader r)
        {
            return new GraphEdge
            {
                Id = ReadString(r),
                SourceId = ReadString(r),
                TargetId = ReadString(r),
                Kind = (EdgeKind)r.ReadInt32(),
                Evidence = (EvidenceLevel)r.ReadInt32(),
                ProviderId = ReadString(r),
                PropertyPath = ReadString(r),
                MemberName = ReadString(r),
                SourceFile = ReadString(r),
                SourceLine = r.ReadInt32(),
                SourceColumn = r.ReadInt32(),
                ProvenanceAssetGuid = ReadString(r),
                ProvenanceSceneGuid = ReadString(r),
                Explanation = ReadString(r),
                Qualifiers = ReadString(r),
                AnalyzedAtUtcTicks = r.ReadInt64(),
            };
        }

        static void WriteDiagnostic(BinaryWriter w, AnalysisDiagnostic d)
        {
            w.Write((int)d.Severity);
            WriteString(w, d.Code);
            WriteString(w, d.Message);
            WriteString(w, d.NodeId);
            WriteString(w, d.ProviderId);
            WriteString(w, d.AssetPath);
            WriteString(w, d.PropertyPath);
            w.Write(d.SourceLine);
        }

        static AnalysisDiagnostic ReadDiagnostic(BinaryReader r)
        {
            return new AnalysisDiagnostic
            {
                Severity = (DiagnosticSeverity)r.ReadInt32(),
                Code = ReadString(r),
                Message = ReadString(r),
                NodeId = ReadString(r),
                ProviderId = ReadString(r),
                AssetPath = ReadString(r),
                PropertyPath = ReadString(r),
                SourceLine = r.ReadInt32(),
            };
        }

        static void WriteString(BinaryWriter w, string s)
        {
            if (s == null)
            {
                w.Write(false);
                return;
            }
            w.Write(true);
            w.Write(s);
        }

        static string ReadString(BinaryReader r)
        {
            return r.ReadBoolean() ? r.ReadString() : null;
        }
    }
}

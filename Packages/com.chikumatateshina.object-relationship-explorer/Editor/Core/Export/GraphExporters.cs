using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace ObjectRelationshipExplorer.Core.Export
{
    /// <summary>
    /// 出力に含める項目。ローカルパスやソース位置は機密になり得るため、出力前に選択できるようにする。
    /// </summary>
    public sealed class ExportOptions
    {
        public bool IncludeAssetPaths = true;
        public bool IncludeHierarchyPaths = true;
        public bool IncludeSourceLocations = true;
        public bool IncludeExplanations = true;
        public bool IncludeDiagnostics = true;
        public string ToolVersion = "";
    }

    public sealed class ExportSet
    {
        public readonly List<GraphNode> Nodes = new List<GraphNode>();
        public readonly List<GraphEdge> Edges = new List<GraphEdge>();
        public readonly List<AnalysisDiagnostic> Diagnostics = new List<AnalysisDiagnostic>();

        /// <summary>ノード集合と、その間のエッジ（両端が集合内）を収集する。</summary>
        public static ExportSet FromNodes(GraphStore store, IEnumerable<string> nodeIds, IEnumerable<string> edgeIds = null)
        {
            var set = new ExportSet();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in nodeIds)
            {
                var n = store.GetNode(id);
                if (n != null && ids.Add(id)) set.Nodes.Add(n);
            }
            var edgeSet = new HashSet<string>(StringComparer.Ordinal);
            if (edgeIds != null)
            {
                foreach (var id in edgeIds)
                {
                    var e = store.GetEdge(id);
                    if (e != null && ids.Contains(e.SourceId) && ids.Contains(e.TargetId) && edgeSet.Add(id)) set.Edges.Add(e);
                }
            }
            else
            {
                foreach (var id in ids)
                    foreach (var e in store.GetOutEdges(id))
                        if (ids.Contains(e.TargetId) && edgeSet.Add(e.Id)) set.Edges.Add(e);
            }
            foreach (var d in store.AllDiagnostics())
                if (d.NodeId != null && ids.Contains(d.NodeId)) set.Diagnostics.Add(d);
            return set;
        }

        public static ExportSet FromStore(GraphStore store)
        {
            var set = new ExportSet();
            set.Nodes.AddRange(store.Nodes);
            set.Edges.AddRange(store.Edges);
            set.Diagnostics.AddRange(store.AllDiagnostics());
            return set;
        }
    }

    public static class GraphJsonExporter
    {
        public static string Export(ExportSet set, ExportOptions options)
        {
            var sw = new StringWriter(CultureInfo.InvariantCulture);
            Write(sw, set, options);
            return sw.ToString();
        }

        public static void Write(TextWriter w, ExportSet set, ExportOptions options)
        {
            options = options ?? new ExportOptions();
            var j = new JsonWriter(w);
            j.BeginObject();
            j.Prop("schemaVersion", NodeIds.SchemaVersion);
            j.Prop("format", "object-relationship-explorer/graph");
            j.Prop("toolVersion", options.ToolVersion);
            j.Prop("exportedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

            j.Key("nodes");
            j.BeginArray();
            foreach (var n in set.Nodes)
            {
                j.BeginObject();
                j.Prop("id", n.Id);
                j.Prop("kind", n.Kind.ToString());
                j.Prop("name", n.Name);
                j.Prop("type", n.TypeName);
                j.Prop("sceneGuid", n.SceneGuid);
                j.Prop("assetGuid", n.AssetGuid);
                if (options.IncludeAssetPaths) j.Prop("assetPath", n.AssetPath);
                if (options.IncludeHierarchyPaths) j.Prop("hierarchyPath", n.HierarchyPath);
                if (n.LocalFileId != 0) j.Prop("localFileId", n.LocalFileId);
                j.Prop("flags", n.Flags.ToString());
                j.Prop("provider", n.ProviderId);
                j.EndObject();
            }
            j.EndArray();

            j.Key("edges");
            j.BeginArray();
            foreach (var e in set.Edges)
            {
                j.BeginObject();
                j.Prop("id", e.Id);
                j.Prop("source", e.SourceId);
                j.Prop("target", e.TargetId);
                j.Prop("kind", e.Kind.ToString());
                j.Prop("evidence", e.Evidence.ToString());
                j.Key("provenance");
                j.BeginObject();
                j.Prop("provider", e.ProviderId);
                j.Prop("propertyPath", e.PropertyPath);
                j.Prop("member", e.MemberName);
                if (options.IncludeSourceLocations)
                {
                    j.Prop("sourceFile", e.SourceFile);
                    if (e.SourceLine > 0) j.Prop("line", e.SourceLine);
                    if (e.SourceColumn > 0) j.Prop("column", e.SourceColumn);
                }
                j.Prop("assetGuid", e.ProvenanceAssetGuid);
                j.Prop("sceneGuid", e.ProvenanceSceneGuid);
                if (options.IncludeExplanations) j.Prop("explanation", e.Explanation);
                j.Prop("qualifiers", e.Qualifiers);
                j.Prop("analysisRevision", e.AnalysisRevision);
                if (e.AnalyzedAtUtcTicks > 0)
                    j.Prop("analyzedAtUtc", new DateTime(e.AnalyzedAtUtcTicks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture));
                j.EndObject();
                j.EndObject();
            }
            j.EndArray();

            if (options.IncludeDiagnostics)
            {
                j.Key("diagnostics");
                j.BeginArray();
                foreach (var d in set.Diagnostics)
                {
                    j.BeginObject();
                    j.Prop("severity", d.Severity.ToString());
                    j.Prop("code", d.Code);
                    j.Prop("message", d.Message);
                    j.Prop("node", d.NodeId);
                    j.Prop("provider", d.ProviderId);
                    if (options.IncludeAssetPaths) j.Prop("assetPath", d.AssetPath);
                    j.Prop("propertyPath", d.PropertyPath);
                    if (options.IncludeSourceLocations && d.SourceLine > 0) j.Prop("line", d.SourceLine);
                    j.EndObject();
                }
                j.EndArray();
            }
            j.EndObject();
            w.Flush();
        }
    }

    public static class GraphMlExporter
    {
        public static string Export(ExportSet set, ExportOptions options)
        {
            var sb = new StringBuilder();
            var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
            using (var sw = new StringWriter(sb, CultureInfo.InvariantCulture))
            using (var x = XmlWriter.Create(sw, settings))
            {
                Write(x, set, options ?? new ExportOptions());
            }
            return sb.ToString();
        }

        static void Write(XmlWriter x, ExportSet set, ExportOptions o)
        {
            const string ns = "http://graphml.graphdrawing.org/xmlns";
            x.WriteStartDocument();
            x.WriteStartElement("graphml", ns);

            void KeyDef(string id, string target, string name)
            {
                x.WriteStartElement("key", ns);
                x.WriteAttributeString("id", id);
                x.WriteAttributeString("for", target);
                x.WriteAttributeString("attr.name", name);
                x.WriteAttributeString("attr.type", "string");
                x.WriteEndElement();
            }

            KeyDef("n_kind", "node", "kind");
            KeyDef("n_name", "node", "name");
            KeyDef("n_type", "node", "type");
            KeyDef("n_scene", "node", "sceneGuid");
            KeyDef("n_asset", "node", "assetGuid");
            KeyDef("n_path", "node", "assetPath");
            KeyDef("n_hpath", "node", "hierarchyPath");
            KeyDef("n_flags", "node", "flags");
            KeyDef("e_kind", "edge", "relationType");
            KeyDef("e_evidence", "edge", "evidence");
            KeyDef("e_provider", "edge", "provider");
            KeyDef("e_prop", "edge", "propertyPath");
            KeyDef("e_member", "edge", "member");
            KeyDef("e_file", "edge", "sourceFile");
            KeyDef("e_line", "edge", "sourceLine");
            KeyDef("e_expl", "edge", "explanation");
            KeyDef("e_qual", "edge", "qualifiers");
            KeyDef("g_schema", "graph", "schemaVersion");

            x.WriteStartElement("graph", ns);
            x.WriteAttributeString("id", "G");
            x.WriteAttributeString("edgedefault", "directed");
            Data("g_schema", NodeIds.SchemaVersion.ToString(CultureInfo.InvariantCulture));

            void Data(string key, string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                x.WriteStartElement("data", ns);
                x.WriteAttributeString("key", key);
                x.WriteString(value);
                x.WriteEndElement();
            }

            foreach (var n in set.Nodes)
            {
                x.WriteStartElement("node", ns);
                x.WriteAttributeString("id", n.Id);
                Data("n_kind", n.Kind.ToString());
                Data("n_name", n.Name);
                Data("n_type", n.TypeName);
                Data("n_scene", n.SceneGuid);
                Data("n_asset", n.AssetGuid);
                if (o.IncludeAssetPaths) Data("n_path", n.AssetPath);
                if (o.IncludeHierarchyPaths) Data("n_hpath", n.HierarchyPath);
                Data("n_flags", n.Flags.ToString());
                x.WriteEndElement();
            }

            foreach (var e in set.Edges)
            {
                x.WriteStartElement("edge", ns);
                x.WriteAttributeString("id", e.Id);
                x.WriteAttributeString("source", e.SourceId);
                x.WriteAttributeString("target", e.TargetId);
                Data("e_kind", e.Kind.ToString());
                Data("e_evidence", e.Evidence.ToString());
                Data("e_provider", e.ProviderId);
                Data("e_prop", e.PropertyPath);
                Data("e_member", e.MemberName);
                if (o.IncludeSourceLocations)
                {
                    Data("e_file", e.SourceFile);
                    if (e.SourceLine > 0) Data("e_line", e.SourceLine.ToString(CultureInfo.InvariantCulture));
                }
                if (o.IncludeExplanations) Data("e_expl", e.Explanation);
                Data("e_qual", e.Qualifiers);
                x.WriteEndElement();
            }

            x.WriteEndElement(); // graph
            x.WriteEndElement(); // graphml
            x.WriteEndDocument();
        }
    }

    /// <summary>依存の無い最小 JSON ライタ。</summary>
    public sealed class JsonWriter
    {
        readonly TextWriter _w;
        readonly Stack<bool> _first = new Stack<bool>();
        bool _afterKey;

        public JsonWriter(TextWriter w)
        {
            _w = w;
        }

        void Separator()
        {
            if (_afterKey) { _afterKey = false; return; }
            if (_first.Count == 0) return;
            if (_first.Peek()) { _first.Pop(); _first.Push(false); }
            else _w.Write(',');
        }

        public void BeginObject() { Separator(); _w.Write('{'); _first.Push(true); }
        public void EndObject() { _first.Pop(); _w.Write('}'); }
        public void BeginArray() { Separator(); _w.Write('['); _first.Push(true); }
        public void EndArray() { _first.Pop(); _w.Write(']'); }

        public void Key(string key)
        {
            Separator();
            WriteString(key);
            _w.Write(':');
            _afterKey = true;
        }

        public void Value(string value)
        {
            Separator();
            if (value == null) _w.Write("null");
            else WriteString(value);
        }

        public void Value(long value)
        {
            Separator();
            _w.Write(value.ToString(CultureInfo.InvariantCulture));
        }

        public void Value(double value)
        {
            Separator();
            _w.Write(value.ToString("R", CultureInfo.InvariantCulture));
        }

        public void Value(bool value)
        {
            Separator();
            _w.Write(value ? "true" : "false");
        }

        /// <summary>null/空文字列のプロパティは省略する。</summary>
        public void Prop(string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            Key(key);
            Value(value);
        }

        public void Prop(string key, long value) { Key(key); Value(value); }
        public void Prop(string key, double value) { Key(key); Value(value); }
        public void Prop(string key, bool value) { Key(key); Value(value); }

        void WriteString(string s)
        {
            _w.Write('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': _w.Write("\\\""); break;
                    case '\\': _w.Write("\\\\"); break;
                    case '\n': _w.Write("\\n"); break;
                    case '\r': _w.Write("\\r"); break;
                    case '\t': _w.Write("\\t"); break;
                    default:
                        if (c < 0x20) _w.Write("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else _w.Write(c);
                        break;
                }
            }
            _w.Write('"');
        }
    }
}

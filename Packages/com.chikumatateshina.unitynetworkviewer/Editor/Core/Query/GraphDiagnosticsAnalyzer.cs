using System;
using System.Collections.Generic;
using UnityNetworkViewer.Core.Layout;

namespace UnityNetworkViewer.Core
{
    public sealed class GraphDiagnosticReport
    {
        public readonly List<AnalysisDiagnostic> Items = new List<AnalysisDiagnostic>();
        public int MissingReferenceCount;
        public int CycleCount;
        public int OrphanCount;
        public int UnresolvedEdgeCount;
    }

    /// <summary>
    /// 索引全体に対する診断: Missing Reference、参照の循環（構造エッジを除く）、孤立ノード、未解決エッジ。
    /// </summary>
    public static class GraphDiagnosticsAnalyzer
    {
        public static GraphDiagnosticReport Analyze(GraphStore store, int maxCycles = 200, int maxOrphans = 500)
        {
            var report = new GraphDiagnosticReport();

            // Missing / Unresolved
            foreach (var e in store.Edges)
            {
                if (e.Evidence != EvidenceLevel.Unresolved) continue;
                report.UnresolvedEdgeCount++;
                var target = store.GetNode(e.TargetId);
                bool missing = target != null && target.HasFlag(NodeFlags.Missing);
                if (missing) report.MissingReferenceCount++;
                var source = store.GetNode(e.SourceId);
                report.Items.Add(new AnalysisDiagnostic
                {
                    Severity = missing ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
                    Code = missing ? DiagnosticCodes.MissingReference : DiagnosticCodes.UnresolvedTarget,
                    Message = $"{Describe(source)} → {(target?.Name ?? e.TargetId)} [{e.Kind}] {e.PropertyPath ?? e.MemberName} {e.Explanation}".Trim(),
                    NodeId = e.SourceId,
                    ProviderId = e.ProviderId,
                    PropertyPath = e.PropertyPath,
                    SourceLine = e.SourceLine,
                    AssetPath = source?.AssetPath,
                });
            }

            // 循環（参照・制御系のみ）
            var ids = new List<string>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var n in store.Nodes)
            {
                index[n.Id] = ids.Count;
                ids.Add(n.Id);
            }
            var adj = new List<int>[ids.Count];
            for (int i = 0; i < adj.Length; i++) adj[i] = new List<int>();
            foreach (var e in store.Edges)
            {
                if (EdgeKindInfo.IsStructural(e.Kind) || e.Kind == EdgeKind.ScriptUsage || e.Kind == EdgeKind.AssetDependency) continue;
                if (e.SourceId == e.TargetId) continue;
                if (index.TryGetValue(e.SourceId, out int s) && index.TryGetValue(e.TargetId, out int t)) adj[s].Add(t);
            }
            int[] comp = HierarchicalLayout.Tarjan(adj, out int compCount);
            var members = new Dictionary<int, List<string>>();
            for (int i = 0; i < comp.Length; i++)
            {
                if (!members.TryGetValue(comp[i], out var list)) members[comp[i]] = list = new List<string>();
                list.Add(ids[i]);
            }
            foreach (var kv in members)
            {
                if (kv.Value.Count < 2) continue;
                report.CycleCount++;
                if (report.CycleCount > maxCycles) continue;
                var names = new List<string>();
                foreach (var id in kv.Value)
                {
                    if (names.Count >= 6) { names.Add("…"); break; }
                    names.Add(store.GetNode(id)?.Name ?? id);
                }
                report.Items.Add(new AnalysisDiagnostic
                {
                    Severity = DiagnosticSeverity.Info,
                    Code = DiagnosticCodes.Cycle,
                    Message = $"循環参照 ({kv.Value.Count} ノード): {string.Join(" ⇄ ", names)}",
                    NodeId = kv.Value[0],
                });
            }

            // 孤立ノード（Scene と Asset を除く）
            foreach (var n in store.Nodes)
            {
                if (n.Kind == NodeKind.Scene || n.Kind == NodeKind.PrefabAsset || n.Kind == NodeKind.ScriptAsset || n.Kind == NodeKind.OtherAsset) continue;
                if (store.GetDegree(n.Id) > 0) continue;
                report.OrphanCount++;
                if (report.OrphanCount > maxOrphans) continue;
                report.Items.Add(new AnalysisDiagnostic
                {
                    Severity = DiagnosticSeverity.Info,
                    Code = DiagnosticCodes.Orphan,
                    Message = $"孤立ノード: {Describe(n)}",
                    NodeId = n.Id,
                    AssetPath = n.AssetPath,
                });
            }
            return report;
        }

        static string Describe(GraphNode n)
        {
            if (n == null) return "?";
            return string.IsNullOrEmpty(n.HierarchyPath) ? n.Name : n.HierarchyPath;
        }
    }
}

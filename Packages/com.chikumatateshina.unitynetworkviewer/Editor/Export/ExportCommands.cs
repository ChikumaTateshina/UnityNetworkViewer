using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using UnityNetworkViewer.Core;
using UnityNetworkViewer.Core.Export;
using UnityNetworkViewer.Editor.UI;
using UnityEditor;
using UnityEngine;

namespace UnityNetworkViewer.Editor
{
    public enum ExportFormat
    {
        Json,
        GraphMl,
        Svg,
    }

    /// <summary>現在の表示状態を SVG として出力する（ノード・エッジ・強調・線種を含む）。</summary>
    public static class SvgExporter
    {
        public static string Export(GraphViewModel vm, ExportOptions options)
        {
            var ids = vm.GetDrawnNodes().Where(vm.Positions.ContainsKey).ToList();
            float w = GraphStyles.NodeWidth, h = GraphStyles.NodeHeight;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var id in ids)
            {
                var p = vm.Positions[id];
                minX = Mathf.Min(minX, p.x - w); minY = Mathf.Min(minY, p.y - h);
                maxX = Mathf.Max(maxX, p.x + w); maxY = Mathf.Max(maxY, p.y + h);
            }
            if (ids.Count == 0) { minX = minY = 0; maxX = maxY = 100; }
            var inv = CultureInfo.InvariantCulture;
            string F(float v) => v.ToString("0.#", inv);

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{F(minX)} {F(minY)} {F(maxX - minX)} {F(maxY - minY)}\" width=\"{F(maxX - minX)}\" height=\"{F(maxY - minY)}\" font-family=\"sans-serif\">");
            sb.AppendLine($"<desc>UnityNetworkViewer export (schema {NodeIds.SchemaVersion}) {DateTime.UtcNow.ToString("o", inv)}</desc>");
            sb.AppendLine("<defs><marker id=\"arrow\" viewBox=\"0 0 10 10\" refX=\"10\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto-start-reverse\"><path d=\"M 0 0 L 10 5 L 0 10 z\" fill=\"context-stroke\"/></marker></defs>");
            sb.AppendLine($"<rect x=\"{F(minX)}\" y=\"{F(minY)}\" width=\"{F(maxX - minX)}\" height=\"{F(maxY - minY)}\" fill=\"{GraphStyles.ToHtml(GraphStyles.Background)}\"/>");

            foreach (var g in vm.Groups)
                sb.AppendLine($"<g><rect x=\"{F(g.X)}\" y=\"{F(g.Y)}\" width=\"{F(g.Width)}\" height=\"{F(g.Height)}\" rx=\"8\" fill=\"none\" stroke=\"#888\" stroke-opacity=\"0.5\"/><text x=\"{F(g.X + 8)}\" y=\"{F(g.Y + 18)}\" font-size=\"13\" fill=\"#ccc\">{X(g.Label)}</text></g>");

            foreach (var eid in vm.GetVisibleEdges())
            {
                var e = vm.Store.GetEdge(eid);
                if (e == null || !vm.Positions.TryGetValue(e.SourceId, out var a) || !vm.Positions.TryGetValue(e.TargetId, out var b)) continue;
                Color color;
                float width = 1.2f;
                if (vm.PathEdges.Contains(eid)) { color = GraphStyles.PathColor; width = 3; }
                else if (vm.Highlight.EdgeRoles.TryGetValue(eid, out var role) && role != EdgeRole.Induced)
                {
                    color = EdgeKindInfo.IsStructural(e.Kind) ? GraphStyles.Structural : EdgeKindInfo.IsPrefabRelation(e.Kind) ? GraphStyles.Prefab : role == EdgeRole.Incoming ? GraphStyles.Incoming : GraphStyles.Outgoing;
                    width = 2.2f;
                }
                else color = GraphStyles.EdgeBase(e.Kind);
                var dash = GraphStyles.DashPattern(e.Evidence);
                string dashAttr = dash == Vector2.zero ? "" : $" stroke-dasharray=\"{F(dash.x)} {F(dash.y)}\"";
                var s = Clip(a, b, w * 0.5f, h * 0.5f);
                var t = Clip(b, a, w * 0.5f, h * 0.5f);
                string title = $"{e.Kind} [{e.Evidence}] {e.PropertyPath ?? e.MemberName}";
                if (options.IncludeSourceLocations && !string.IsNullOrEmpty(e.SourceFile)) title += $" ({e.SourceFile}:{e.SourceLine})";
                if (options.IncludeExplanations && !string.IsNullOrEmpty(e.Explanation)) title += " — " + e.Explanation;
                sb.AppendLine($"<line x1=\"{F(s.x)}\" y1=\"{F(s.y)}\" x2=\"{F(t.x)}\" y2=\"{F(t.y)}\" stroke=\"{GraphStyles.ToHtml(color)}\" stroke-width=\"{F(width)}\"{dashAttr} marker-end=\"url(#arrow)\"><title>{X(title)}</title></line>");
            }

            foreach (var id in ids)
            {
                var n = vm.Store.GetNode(id);
                if (n == null) continue;
                var p = vm.Positions[id];
                bool root = vm.Roots.Contains(id);
                var fill = GraphStyles.NodeFill(n.Kind);
                string stroke = root ? GraphStyles.ToHtml(GraphStyles.Selected) : "#666";
                string sw = root ? "3" : "1";
                string dashAttr = n.IsUnloaded || n.IsPlaceholder ? " stroke-dasharray=\"6 4\"" : "";
                string tooltip = n.Kind + " " + n.Name + (options.IncludeHierarchyPaths && n.HierarchyPath != null ? " / " + n.HierarchyPath : "") + (options.IncludeAssetPaths && n.AssetPath != null ? " / " + n.AssetPath : "");
                sb.AppendLine($"<g><title>{X(tooltip)}</title><rect x=\"{F(p.x - w / 2)}\" y=\"{F(p.y - h / 2)}\" width=\"{F(w)}\" height=\"{F(h)}\" rx=\"5\" fill=\"{GraphStyles.ToHtml(fill)}\" stroke=\"{stroke}\" stroke-width=\"{sw}\"{dashAttr}/>" +
                              $"<text x=\"{F(p.x - w / 2 + 8)}\" y=\"{F(p.y - 2)}\" font-size=\"11\" font-weight=\"bold\" fill=\"#eee\">{X(Trunc(n.Name, 26))}</text>" +
                              $"<text x=\"{F(p.x - w / 2 + 8)}\" y=\"{F(p.y + 12)}\" font-size=\"9\" fill=\"#aaa\">{X(GraphStyles.KindBadge(n.Kind))}</text></g>");
            }
            sb.AppendLine("</svg>");
            return sb.ToString();
        }

        static Vector2 Clip(Vector2 c, Vector2 toward, float hw, float hh)
        {
            var d = toward - c;
            if (d.sqrMagnitude < 1e-4f) return c;
            float tx = Mathf.Abs(d.x) > 1e-4f ? hw / Mathf.Abs(d.x) : float.MaxValue;
            float ty = Mathf.Abs(d.y) > 1e-4f ? hh / Mathf.Abs(d.y) : float.MaxValue;
            return c + d * Mathf.Min(tx, ty, 1f);
        }

        static string Trunc(string s, int n) => s == null || s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        static string X(string s) => SecurityElement.Escape(s ?? "");
    }

    /// <summary>エクスポート前に含める項目を選ぶポップアップ（ローカルパス・ソース断片の機密性に配慮）。</summary>
    public sealed class ExportPopup : PopupWindowContent
    {
        readonly GraphViewModel _vm;
        readonly ExportFormat _format;
        readonly bool _fullIndex;
        readonly ExportOptions _options = new ExportOptions();

        public ExportPopup(GraphViewModel vm, ExportFormat format, bool fullIndex)
        {
            _vm = vm;
            _format = format;
            _fullIndex = fullIndex;
        }

        public override Vector2 GetWindowSize() => new Vector2(300, 190);

        public override void OnGUI(Rect rect)
        {
            GUILayout.Label($"{_format} 出力（{(_fullIndex ? "全索引" : "表示中の部分グラフ")}）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("出力にはローカルパスやソース位置が含まれる場合があります。共有する前に含める項目を確認してください。", MessageType.None);
            _options.IncludeAssetPaths = EditorGUILayout.ToggleLeft("Asset パス", _options.IncludeAssetPaths);
            _options.IncludeHierarchyPaths = EditorGUILayout.ToggleLeft("Hierarchy パス", _options.IncludeHierarchyPaths);
            _options.IncludeSourceLocations = EditorGUILayout.ToggleLeft("ソースファイル / 行", _options.IncludeSourceLocations);
            _options.IncludeExplanations = EditorGUILayout.ToggleLeft("推定理由・説明", _options.IncludeExplanations);
            using (new EditorGUI.DisabledScope(_format == ExportFormat.Svg))
                _options.IncludeDiagnostics = EditorGUILayout.ToggleLeft("診断", _options.IncludeDiagnostics);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("保存…"))
            {
                editorWindow.Close();
                EditorApplication.delayCall += () => Run(_vm, _format, _fullIndex, _options);
            }
        }

        public static void Run(GraphViewModel vm, ExportFormat format, bool fullIndex, ExportOptions options)
        {
            string ext = format == ExportFormat.Json ? "json" : format == ExportFormat.GraphMl ? "graphml" : "svg";
            string path = EditorUtility.SaveFilePanel("Export Graph", "", "unitynetworkviewer." + ext, ext);
            if (string.IsNullOrEmpty(path)) return;
            options.ToolVersion = "0.1.0";
            string content;
            switch (format)
            {
                case ExportFormat.Svg:
                    content = SvgExporter.Export(vm, options);
                    break;
                default:
                {
                    var set = fullIndex ? ExportSet.FromStore(vm.Store) : ExportSet.FromNodes(vm.Store, vm.GetDrawnNodes(), vm.GetVisibleEdges());
                    content = format == ExportFormat.Json ? GraphJsonExporter.Export(set, options) : GraphMlExporter.Export(set, options);
                    break;
                }
            }
            File.WriteAllText(path, content, new UTF8Encoding(false));
            EditorUtility.RevealInFinder(path);
        }
    }
}

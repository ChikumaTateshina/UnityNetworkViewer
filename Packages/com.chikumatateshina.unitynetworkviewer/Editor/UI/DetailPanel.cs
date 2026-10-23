using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityNetworkViewer.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityNetworkViewer.Editor.UI
{
    /// <summary>右パネル: 選択ノードの詳細、参照元/参照先一覧（仮想化 ListView）、エッジの根拠。</summary>
    public sealed class DetailPanel : VisualElement
    {
        GraphViewModel _vm;
        readonly Label _title;
        readonly Label _info;
        readonly TextField _idField;
        readonly VisualElement _buttons;
        readonly ListView _list;
        readonly Label _edgeDetail;
        readonly Button _openSource;
        readonly List<Row> _rows = new List<Row>();
        string _shownNode;
        long _shownRevision = -1;
        string _shownEdge;
        double _shownAt;
        int _outPage, _inPage;

        /// <summary>方向ごとに一覧へ出す関係の上限（参照が非常に多い Asset でも固まらないように）。</summary>
        const int MaxRowsPerDirection = 300;

        public Action<string> FocusNodeRequested;
        public Action<string> NavigateRequested;
        public Action<string> SelectInUnityRequested;
        public Action<string> ExpandRequested;

        sealed class Row
        {
            public bool Header;
            public string Text;
            public GraphEdge Edge;
            public bool Outgoing;
        }

        public DetailPanel()
        {
            style.flexGrow = 1;
            style.paddingLeft = 6;
            style.paddingRight = 6;
            style.paddingTop = 4;

            _title = new Label("（ノード未選択）");
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            _title.style.fontSize = 13;
            _title.style.whiteSpace = WhiteSpace.Normal;
            Add(_title);

            _info = new Label { enableRichText = true };
            _info.style.whiteSpace = WhiteSpace.Normal;
            _info.style.marginTop = 2;
            Add(_info);

            _idField = new TextField("ID") { isReadOnly = true };
            _idField.style.marginTop = 2;
            Add(_idField);

            _buttons = new VisualElement();
            _buttons.style.flexDirection = FlexDirection.Row;
            _buttons.style.flexWrap = Wrap.Wrap;
            _buttons.style.marginTop = 4;
            Add(_buttons);

            var listHeader = new Label("関係一覧（クリックで根拠 / ダブルクリックで相手ノードへ）");
            listHeader.style.marginTop = 6;
            listHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            Add(listHeader);

            _list = new ListView
            {
                itemsSource = _rows,
                fixedItemHeight = 20,
                makeItem = () =>
                {
                    var l = new Label { enableRichText = true };
                    l.style.unityTextAlign = TextAnchor.MiddleLeft;
                    l.style.overflow = Overflow.Hidden;
                    l.style.whiteSpace = WhiteSpace.NoWrap;
                    l.style.paddingLeft = 2;
                    return l;
                },
                bindItem = (e, i) =>
                {
                    var l = (Label)e;
                    var r = _rows[i];
                    l.text = r.Text;
                    l.style.unityFontStyleAndWeight = r.Header ? FontStyle.Bold : FontStyle.Normal;
                    l.style.backgroundColor = r.Header ? new Color(0, 0, 0, 0.15f) : Color.clear;
                    l.tooltip = r.Edge != null ? (r.Edge.PropertyPath ?? r.Edge.MemberName ?? "") : "";
                },
                selectionType = SelectionType.Single,
            };
            _list.style.flexGrow = 1;
            _list.style.minHeight = 120;
            _list.selectionChanged += OnRowSelected;
            _list.itemsChosen += OnRowChosen;
            Add(_list);

            var edgeHeader = new Label("エッジの根拠");
            edgeHeader.style.marginTop = 4;
            edgeHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            Add(edgeHeader);
            _edgeDetail = new Label { enableRichText = true };
            _edgeDetail.style.whiteSpace = WhiteSpace.Normal;
            _edgeDetail.style.minHeight = 60;
            Add(_edgeDetail);
            _openSource = new Button(OpenEdgeSource) { text = "ソースを開く" };
            _openSource.style.display = DisplayStyle.None;
            Add(_openSource);
        }

        public void Bind(GraphViewModel vm)
        {
            _vm = vm;
            Refresh(true);
        }

        public void Refresh(bool force = false)
        {
            if (_vm == null) return;
            string id = _vm.GraphSelectedNodeId;
            double now = EditorApplication.timeSinceStartup;
            // 索引の更新による作り直しは 1 秒に 1 回まで（バックグラウンド索引中に毎回作り直さない）
            bool storeChanged = _vm.Store.Revision != _shownRevision && now - _shownAt > 1.0;
            bool nodeChanged = force || id != _shownNode || storeChanged;
            if (nodeChanged)
            {
                _outPage = _inPage = 0;
                _shownNode = id;
                _shownRevision = _vm.Store.Revision;
                _shownAt = now;
                ShowNode(id);
            }
            if (force || nodeChanged || _shownEdge != _vm.SelectedEdgeId) ShowEdge(_vm.Store.GetEdge(_vm.SelectedEdgeId));
        }

        void ShowNode(string id)
        {
            _buttons.Clear();
            _rows.Clear();
            var node = _vm.Store.GetNode(id);
            if (node == null)
            {
                _title.text = id == null ? "（ノード未選択）" : "索引にないノード";
                _info.text = id == null ? "Hierarchy / Scene View / Project で選択するか、グラフ上のノードをクリックしてください。" : (_vm.Notification ?? "");
                _idField.value = id ?? "";
                _list.Rebuild();
                return;
            }

            _title.text = node.Name;
            var lines = new List<string>
            {
                $"<b>種類</b>: {node.Kind}" + (string.IsNullOrEmpty(node.TypeName) ? "" : $"  <b>型</b>: {node.TypeName}"),
            };
            if (!string.IsNullOrEmpty(node.HierarchyPath)) lines.Add($"<b>Hierarchy</b>: {Esc(node.HierarchyPath)}");
            if (!string.IsNullOrEmpty(node.AssetPath)) lines.Add($"<b>Asset</b>: {Esc(node.AssetPath)}");
            if (!string.IsNullOrEmpty(node.SceneGuid))
            {
                var scene = _vm.Store.GetNode(NodeIds.Scene(node.SceneGuid));
                bool loaded = ObjectResolver.IsSceneLoaded(AssetDatabase.GUIDToAssetPath(node.SceneGuid));
                lines.Add($"<b>Scene</b>: {Esc(scene?.Name ?? node.SceneGuid)} ({(loaded ? "ロード済み" : "未ロード")})");
            }
            if (node.Flags != NodeFlags.None) lines.Add($"<b>属性</b>: {node.Flags}");
            lines.Add($"<b>Provider</b>: {node.ProviderId}  <b>入力</b>: {_vm.Store.GetInEdgeIds(id).Count}  <b>出力</b>: {_vm.Store.GetOutEdgeIds(id).Count}");
            _info.text = string.Join("\n", lines);
            _idField.value = node.Id;

            AddButton("Unityで選択", () => SelectInUnityRequested?.Invoke(id));
            AddButton("移動 / 開く", () => NavigateRequested?.Invoke(id));
            AddButton("Ping", () => NodeNavigator.Ping(node));
            AddButton("起点にする", () => FocusNodeRequested?.Invoke(id));
            AddButton("隣接を展開", () => ExpandRequested?.Invoke(id));
            if (node.Kind == NodeKind.PrefabAsset) AddButton("Prefab Mode", () => NodeNavigator.OpenPrefabMode(node));
            if (node.Kind == NodeKind.UdonMethod || node.Kind == NodeKind.UdonVariable || node.Kind == NodeKind.ScriptAsset)
                AddButton("ソース", () => NodeNavigator.TryOpenSource(node, _vm.Store));

            if (_outPage > 0) AddButton("前の参照先", () => ChangePage(true, -1));
            if ((_outPage + 1L) * MaxRowsPerDirection < _vm.Store.GetOutEdgeIds(id).Count)
                AddButton("次の参照先", () => ChangePage(true, 1));
            if (_inPage > 0) AddButton("前の参照元", () => ChangePage(false, -1));
            if ((_inPage + 1L) * MaxRowsPerDirection < _vm.Store.GetInEdgeIds(id).Count)
                AddButton("次の参照元", () => ChangePage(false, 1));
            AddEdgeRows(id, true);
            AddEdgeRows(id, false);
            _list.Rebuild();
        }

        void ChangePage(bool outgoing, int delta)
        {
            if (_shownRevision != _vm.Store.Revision) { Refresh(true); return; }
            if (outgoing) _outPage = Math.Max(0, _outPage + delta);
            else _inPage = Math.Max(0, _inPage + delta);
            ShowNode(_shownNode);
        }

        void AddEdgeRows(string id, bool outgoing)
        {
            // 件数は全件数えるが、行は上限まで（数千〜数万の参照を持つ Asset で固まらないように）
            int total = outgoing ? _vm.Store.GetOutEdgeIds(id).Count : _vm.Store.GetInEdgeIds(id).Count;
            var edges = new List<GraphEdge>(Math.Min(total, MaxRowsPerDirection));
            int offset = (outgoing ? _outPage : _inPage) * MaxRowsPerDirection;
            int skipped = 0;
            foreach (var e in outgoing ? _vm.Store.GetOutEdges(id) : _vm.Store.GetInEdges(id))
            {
                if (skipped++ < offset) continue;
                if (edges.Count >= MaxRowsPerDirection) break;
                edges.Add(e);
            }
            _rows.Add(new Row { Header = true, Text = (outgoing ? "▶ 参照先 / 子 (Outgoing) " : "◀ 参照元 / 親 (Incoming) ") + total });
            if (total > edges.Count)
                _rows.Add(new Row { Header = true, Text = $"<color=#999>（{offset + 1}〜{offset + edges.Count} 件を表示。「前の参照」「次の参照」でページを切り替え）</color>" });
            foreach (var group in edges.GroupBy(e => e.Kind).OrderBy(g => g.Key))
            {
                foreach (var e in group.OrderBy(x => x.PropertyPath ?? x.MemberName ?? ""))
                {
                    var other = _vm.Store.GetNode(outgoing ? e.TargetId : e.SourceId);
                    string ev = e.Evidence == EvidenceLevel.Confirmed ? "" : $" <color=#f0a040>[{GraphStyles.EvidenceLabel(e.Evidence)}]</color>";
                    string detail = e.PropertyPath ?? e.MemberName;
                    if (e.Kind == EdgeKind.MethodControl || e.Kind == EdgeKind.UnityEventCall || e.Kind == EdgeKind.EventDispatch) detail = e.MemberName ?? e.PropertyPath;
                    string hidden = _vm.EdgePassesFilter(e) ? "" : " <color=#888>(フィルタ外)</color>";
                    _rows.Add(new Row
                    {
                        Edge = e,
                        Outgoing = outgoing,
                        Text = $"<color={GraphStyles.ToHtml(outgoing ? GraphStyles.Outgoing : GraphStyles.Incoming)}>{GraphStyles.EdgeAbbrev(e.Kind)}</color>{ev} {Esc(other?.Name ?? "?")}" +
                               (string.IsNullOrEmpty(detail) ? "" : $" <color=#999>· {Esc(detail)}</color>") + hidden,
                    });
                }
            }
        }

        void AddButton(string text, Action action)
        {
            var b = new Button(action) { text = text };
            b.style.marginRight = 2;
            b.style.marginBottom = 2;
            _buttons.Add(b);
        }

        void OnRowSelected(IEnumerable<object> items)
        {
            var row = items.FirstOrDefault() as Row;
            if (row?.Edge == null) return;
            _vm.SelectEdge(row.Edge.Id);
        }

        void OnRowChosen(IEnumerable<object> items)
        {
            var row = items.FirstOrDefault() as Row;
            if (row?.Edge == null) return;
            FocusNodeRequested?.Invoke(row.Outgoing ? row.Edge.TargetId : row.Edge.SourceId);
        }

        void ShowEdge(GraphEdge e)
        {
            _shownEdge = e?.Id;
            if (e == null)
            {
                _edgeDetail.text = "<color=#999>エッジを選択すると、関係の方向・propertyPath・ソース位置・推定理由・解析時刻を表示します。</color>";
                _openSource.style.display = DisplayStyle.None;
                return;
            }
            var src = _vm.Store.GetNode(e.SourceId);
            var dst = _vm.Store.GetNode(e.TargetId);
            var lines = new List<string>
            {
                $"<b>{e.Kind}</b>  確度: <b>{e.Evidence}</b> ({GraphStyles.EvidenceLabel(e.Evidence)})",
                $"<b>方向</b>: {Esc(src?.Name ?? e.SourceId)} → {Esc(dst?.Name ?? e.TargetId)}",
                $"<b>Provider</b>: {e.ProviderId}",
            };
            if (!string.IsNullOrEmpty(e.PropertyPath)) lines.Add($"<b>propertyPath</b>: {Esc(e.PropertyPath)}");
            if (!string.IsNullOrEmpty(e.MemberName)) lines.Add($"<b>Member</b>: {Esc(e.MemberName)}");
            if (!string.IsNullOrEmpty(e.SourceFile)) lines.Add($"<b>ソース</b>: {Esc(e.SourceFile)}{(e.SourceLine > 0 ? ":" + e.SourceLine : "")}");
            if (!string.IsNullOrEmpty(e.Explanation)) lines.Add($"<b>説明</b>: {Esc(e.Explanation)}");
            if (!string.IsNullOrEmpty(e.Qualifiers)) lines.Add($"<b>属性</b>: {Esc(e.Qualifiers)}");
            string owner = _vm.Store.GetEdgeOwner(e.Id);
            lines.Add($"<b>解析</b>: rev {e.AnalysisRevision}" +
                      (e.AnalyzedAtUtcTicks > 0 ? " / " + new DateTime(e.AnalyzedAtUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "") +
                      (owner != null ? " / " + owner : ""));
            if (e.Evidence == EvidenceLevel.Confirmed && (e.Kind == EdgeKind.UnityEventCall || e.Kind == EdgeKind.SerializedReference || e.Kind == EdgeKind.UdonVariableReference))
                lines.Add("<color=#999>※ Confirmed は参照が存在することを示し、実行されることは保証しません。</color>");
            _edgeDetail.text = string.Join("\n", lines);
            _openSource.style.display = !string.IsNullOrEmpty(e.SourceFile) ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void OpenEdgeSource()
        {
            var e = _vm?.Store.GetEdge(_vm.SelectedEdgeId);
            if (e != null) NodeNavigator.OpenSourceFile(e.SourceFile, e.SourceLine);
        }

        static string Esc(string s) => string.IsNullOrEmpty(s) ? s : s.Replace("<", "<​");
    }
}

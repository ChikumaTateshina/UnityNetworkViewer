using System.Collections.Generic;
using System.Linq;
using UnityNetworkViewer.Core;
using UnityNetworkViewer.Core.Layout;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityNetworkViewer.Editor.UI
{
    /// <summary>
    /// UnityNetworkViewer のメインウィンドウ。
    /// 上: ツールバー（検索・再解析・レイアウト・出力） / 左: フィルタ / 中央: グラフ / 右: 詳細 / 下: 診断・ログ。
    /// </summary>
    public sealed class UnityNetworkViewerWindow : EditorWindow
    {
        [SerializeField] ViewSnapshot _persisted;
        [SerializeField] Vector2 _pan;
        [SerializeField] float _zoom = 1f;

        UnvService _service;
        GraphViewModel _vm;
        SelectionBridge _bridge;
        GraphCanvas _canvas;
        DetailPanel _detail;
        FilterPanel _filters;
        BottomPanel _bottom;
        Label _status;
        ToolbarSearchField _search;
        ListView _searchResults;
        readonly List<GraphNode> _searchItems = new List<GraphNode>();
        ToolbarButton _cancelButton;
        double _nextStatusUpdate;
        bool _uiBuilt;

        [MenuItem("Window/Analysis/UnityNetworkViewer", priority = 2100)]
        [MenuItem("Tools/UnityNetworkViewer/Open Window")]
        public static void Open()
        {
            var w = GetWindow<UnityNetworkViewerWindow>();
            w.titleContent = new GUIContent("Relationship Explorer", EditorGUIUtility.IconContent("d_UnityEditor.HierarchyWindow").image);
            w.minSize = new Vector2(720, 420);
            w.Show();
        }

        void OnEnable()
        {
            _service = UnvService.Acquire();
            _vm = new GraphViewModel(_service);
            _bridge = new SelectionBridge();
            _bridge.UnitySelectionChanged += OnUnitySelection;
            _vm.Changed += OnViewChanged;
            _vm.FocusRequested += OnFocusRequested;
            _service.Scheduler.StatusChanged += OnSchedulerStatus;
            _service.LogAdded += OnLogAdded;
            if (_persisted != null && _persisted.state != null) _vm.ApplySnapshot(_persisted);
            SceneViewOverlay.Attach(_vm);
            EditorApplication.update += OnEditorUpdate;
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            if (_vm != null)
            {
                _persisted = _vm.CreateSnapshot("window");
                SceneViewOverlay.Detach(_vm);
                _vm.Changed -= OnViewChanged;
                _vm.FocusRequested -= OnFocusRequested;
                _vm.Dispose();
                _vm = null;
            }
            if (_canvas != null)
            {
                _pan = _canvas.Pan;
                _zoom = _canvas.Zoom;
            }
            if (_bridge != null)
            {
                _bridge.UnitySelectionChanged -= OnUnitySelection;
                _bridge.Dispose();
                _bridge = null;
            }
            if (_service != null)
            {
                _service.Scheduler.StatusChanged -= OnSchedulerStatus;
                _service.LogAdded -= OnLogAdded;
                _service = null;
                UnvService.Release();
            }
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.flexDirection = FlexDirection.Column;

            root.Add(BuildToolbar());

            var main = new TwoPaneSplitView(0, 240, TwoPaneSplitViewOrientation.Horizontal);
            main.style.flexGrow = 1;
            _filters = new FilterPanel();
            main.Add(_filters);

            var rightSplit = new TwoPaneSplitView(1, 320, TwoPaneSplitViewOrientation.Horizontal);
            var centerSplit = new TwoPaneSplitView(1, 150, TwoPaneSplitViewOrientation.Vertical);
            _canvas = new GraphCanvas();
            centerSplit.Add(_canvas);
            _bottom = new BottomPanel();
            centerSplit.Add(_bottom);
            rightSplit.Add(centerSplit);
            _detail = new DetailPanel();
            var detailScroll = new VisualElement();
            detailScroll.style.flexGrow = 1;
            detailScroll.Add(_detail);
            rightSplit.Add(detailScroll);
            main.Add(rightSplit);
            root.Add(main);

            _searchResults = BuildSearchResults();
            root.Add(_searchResults);

            _canvas.Pan = _pan;
            _canvas.Zoom = Mathf.Clamp(_zoom <= 0 ? 1f : _zoom, GraphCanvas.MinZoom, GraphCanvas.MaxZoom);
            WireCanvas();
            WirePanels();

            _filters.Bind(_vm);
            _canvas.Bind(_vm);
            _detail.Bind(_vm);
            _bottom.Bind(_vm);
            ApplyOverlayVisibility();
            _uiBuilt = true;

            if (_vm.Visible.Count == 0)
            {
                var sel = SelectionBridge.ResolveCurrent();
                if (sel.Ids.Count > 0) _vm.OnUnitySelection(sel.Ids);
            }
            UpdateStatus();
        }

        // ------------------------------------------------------------------ Toolbar

        Toolbar BuildToolbar()
        {
            var bar = new Toolbar();
            bar.Add(new ToolbarButton(() => _service.RebuildAll()) { text = "Rebuild All", tooltip = "索引とキャッシュを破棄してプロジェクト全体を再解析します" });
            bar.Add(new ToolbarButton(() => _service.IndexLoadedScenes()) { text = "Loaded Scenes", tooltip = "ロード済み Scene を再解析します" });
            bar.Add(new ToolbarButton(() => _service.EnqueueProjectDiscovery(null, AnalysisPriority.Low)) { text = "Index Project", tooltip = "プロジェクト全体の Asset / Scene を（再）索引します" });
            _cancelButton = new ToolbarButton(() => _service.Scheduler.Cancel()) { text = "Cancel" };
            _cancelButton.SetEnabled(false);
            bar.Add(_cancelButton);
            bar.Add(new ToolbarSpacer());

            _search = new ToolbarSearchField { tooltip = "名前・型で検索。kind: type: scene: prefab: evidence: method: flag: edge: path: が使えます（例: type:Animator scene:Main）" };
            _search.style.flexGrow = 1;
            _search.style.minWidth = 160;
            _search.RegisterValueChangedCallback(e => RunSearch(e.newValue));
            _search.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    if (_searchItems.Count > 0) ShowSearchResultsInGraph();
                    e.StopPropagation();
                }
                else if (e.keyCode == KeyCode.Escape)
                {
                    HideSearch();
                }
            }, TrickleDown.TrickleDown);
            bar.Add(_search);

            var layout = new ToolbarMenu { text = "Layout" };
            foreach (LayoutKind k in System.Enum.GetValues(typeof(LayoutKind)))
            {
                var kind = k;
                layout.menu.AppendAction(kind.ToString(), _ =>
                {
                    _vm.State.layout = kind;
                    _filters.SyncFromState();
                    _vm.RequestLayout(false);
                    _vm.OnStateChanged(false);
                }, _ => _vm != null && _vm.State.layout == kind ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            }
            layout.menu.AppendSeparator();
            layout.menu.AppendAction("再レイアウト（ピン留め以外）", _ => { _vm.RequestLayout(false); });
            layout.menu.AppendAction("ピン留めをすべて解除", _ => { _vm.Pinned.Clear(); _vm.RequestLayout(false); });
            bar.Add(layout);

            var view = new ToolbarMenu { text = "View" };
            view.menu.AppendAction("全体を表示 (A)", _ => _canvas.FrameAll());
            view.menu.AppendAction("選択を表示 (F)", _ => _canvas.FrameNodes(_vm.Roots));
            view.menu.AppendAction("表示をクリア", _ => _vm.ClearView());
            view.menu.AppendSeparator();
            view.menu.AppendAction("凡例", _ => { _vm.State.showLegend = !_vm.State.showLegend; ApplyOverlayVisibility(); },
                _ => _vm != null && _vm.State.showLegend ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            view.menu.AppendAction("ミニマップ", _ => { _vm.State.showMiniMap = !_vm.State.showMiniMap; ApplyOverlayVisibility(); },
                _ => _vm != null && _vm.State.showMiniMap ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            view.menu.AppendAction("Scene View オーバーレイ", _ =>
            {
                var s = UnvSettings.instance;
                s.sceneViewOverlay = !s.sceneViewOverlay;
                s.SaveSettings();
                SceneViewOverlay.Repaint();
            }, _ => UnvSettings.instance.sceneViewOverlay ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            bar.Add(view);

            var export = new ToolbarMenu { text = "Export" };
            export.menu.AppendAction("JSON（表示中）", a => ShowExport(ExportFormat.Json, false));
            export.menu.AppendAction("JSON（全索引）", a => ShowExport(ExportFormat.Json, true));
            export.menu.AppendAction("GraphML（表示中）", a => ShowExport(ExportFormat.GraphMl, false));
            export.menu.AppendAction("GraphML（全索引）", a => ShowExport(ExportFormat.GraphMl, true));
            export.menu.AppendAction("SVG（現在の表示）", a => ShowExport(ExportFormat.Svg, false));
            bar.Add(export);

            _status = new Label();
            _status.style.unityTextAlign = TextAnchor.MiddleRight;
            _status.style.marginLeft = 6;
            _status.style.marginRight = 4;
            _status.style.minWidth = 200;
            bar.Add(_status);
            return bar;
        }

        void ShowExport(ExportFormat format, bool full)
        {
            var rect = new Rect(position.width - 320, 20, 1, 1);
            UnityEditor.PopupWindow.Show(rect, new ExportPopup(_vm, format, full));
        }

        void ApplyOverlayVisibility()
        {
            if (_canvas == null) return;
            _canvas.Legend.style.display = _vm.State.showLegend ? DisplayStyle.Flex : DisplayStyle.None;
            _canvas.MiniMap.style.display = _vm.State.showMiniMap ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ------------------------------------------------------------------ Search

        ListView BuildSearchResults()
        {
            var list = new ListView
            {
                itemsSource = _searchItems,
                fixedItemHeight = 20,
                makeItem = () =>
                {
                    var l = new Label { enableRichText = true };
                    l.style.unityTextAlign = TextAnchor.MiddleLeft;
                    l.style.paddingLeft = 4;
                    return l;
                },
                bindItem = (e, i) =>
                {
                    var n = _searchItems[i];
                    ((Label)e).text = $"<b>{Esc(n.Name)}</b> <color=#999>{GraphStyles.KindBadge(n.Kind)} · {Esc(n.HierarchyPath ?? n.AssetPath ?? n.TypeName)}</color>";
                },
            };
            list.style.position = Position.Absolute;
            list.style.top = 21;
            list.style.left = 280;
            list.style.right = 260;
            list.style.maxHeight = 320;
            list.style.height = 320;
            list.style.backgroundColor = GraphStyles.Dark ? new Color(0.18f, 0.18f, 0.19f) : new Color(0.93f, 0.93f, 0.93f);
            list.style.borderBottomWidth = list.style.borderLeftWidth = list.style.borderRightWidth = 1;
            list.style.borderBottomColor = list.style.borderLeftColor = list.style.borderRightColor = new Color(0, 0, 0, 0.5f);
            list.style.display = DisplayStyle.None;
            list.itemsChosen += items =>
            {
                var n = items.FirstOrDefault() as GraphNode;
                if (n == null) return;
                HideSearch();
                _vm.FocusNode(n.Id);
                if (_vm.State.syncGraphToUnity) _bridge.PushToUnity(new[] { n.Id }, true);
            };
            list.selectionChanged += items =>
            {
                var n = items.FirstOrDefault() as GraphNode;
                if (n == null) return;
                // シングルクリックでも移動できるように
                HideSearch();
                _vm.FocusNode(n.Id);
            };
            return list;
        }

        void RunSearch(string text)
        {
            _searchItems.Clear();
            if (!string.IsNullOrWhiteSpace(text) && _vm != null) _searchItems.AddRange(_vm.Query.Search(text, 200));
            _searchResults.Rebuild();
            _searchResults.ClearSelection();
            _searchResults.style.display = _searchItems.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _searchResults.style.height = Mathf.Min(320, _searchItems.Count * 20 + 4);
        }

        void ShowSearchResultsInGraph()
        {
            var ids = _searchItems.Select(n => n.Id).Take(_vm.State.maxNodes).ToList();
            HideSearch();
            _vm.ShowNodes(ids, false, false);
            _vm.Notification = $"検索結果 {ids.Count} 件を表示";
        }

        void HideSearch()
        {
            _searchResults.style.display = DisplayStyle.None;
        }

        // ------------------------------------------------------------------ Wiring

        void WireCanvas()
        {
            _canvas.NodeClicked += (id, additive) =>
            {
                _vm.SelectFromGraph(id, additive);
                if (_vm.State.syncGraphToUnity) _bridge.PushToUnity(_vm.Roots, false);
            };
            _canvas.NodeDoubleClicked += id => NodeNavigator.Navigate(_vm.Store.GetNode(id), _vm.Store, _bridge, true);
            _canvas.EdgeClicked += id => _vm.SelectEdge(id);
            _canvas.BackgroundClicked += () => { _vm.SelectEdge(null); HideSearch(); };
            _canvas.NodeContextMenu += ShowNodeMenu;
            _canvas.BackgroundContextMenu += _ =>
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("全体を表示"), false, _canvas.FrameAll);
                menu.AddItem(new GUIContent("再レイアウト"), false, () => _vm.RequestLayout(false));
                menu.AddItem(new GUIContent("経路表示をクリア"), false, () => { _vm.PathEdges.Clear(); _vm.OnStateChanged(false); });
                menu.AddItem(new GUIContent("表示をクリア"), false, _vm.ClearView);
                menu.ShowAsContext();
            };
            _canvas.KeyCommand += key =>
            {
                if ((key == KeyCode.Delete || key == KeyCode.Backspace) && _vm.GraphSelectedNodeId != null) _vm.RemoveFromView(_vm.GraphSelectedNodeId);
                else if (key == KeyCode.Escape) { _vm.PathEdges.Clear(); _vm.SelectFromGraph(null, false); }
            };
        }

        void WirePanels()
        {
            _detail.FocusNodeRequested = id =>
            {
                if (!_vm.Store.ContainsNode(id)) return;
                _vm.FocusNode(id);
                if (_vm.State.syncGraphToUnity) _bridge.PushToUnity(new[] { id }, false);
            };
            _detail.NavigateRequested = id => NodeNavigator.Navigate(_vm.Store.GetNode(id), _vm.Store, _bridge, true);
            _detail.SelectInUnityRequested = id => _bridge.PushToUnity(new[] { id }, true);
            _detail.ExpandRequested = id => _vm.ExpandNode(id);
            _bottom.FocusNodeRequested = id =>
            {
                if (_vm.Store.ContainsNode(id)) _vm.FocusNode(id);
            };
        }

        void ShowNodeMenu(string id, Vector2 pos)
        {
            var node = _vm.Store.GetNode(id);
            if (node == null) return;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Unity で選択"), false, () => _bridge.PushToUnity(new[] { id }, true));
            menu.AddItem(new GUIContent("移動 / 開く (ダブルクリック)"), false, () => NodeNavigator.Navigate(node, _vm.Store, _bridge, true));
            menu.AddItem(new GUIContent("Ping"), false, () => NodeNavigator.Ping(node));
            if (node.Kind == NodeKind.PrefabAsset) menu.AddItem(new GUIContent("Prefab Mode で開く"), false, () => NodeNavigator.OpenPrefabMode(node));
            if (node.IsUnloaded || (node.SceneGuid != null && !ObjectResolver.IsSceneLoaded(ObjectResolver.GetScenePath(node))))
                menu.AddItem(new GUIContent("Scene を開く…"), false, () => NodeNavigator.OpenSceneWithConfirmation(node));
            if (node.Kind == NodeKind.UdonMethod || node.Kind == NodeKind.UdonVariable || node.Kind == NodeKind.UdonEvent || node.Kind == NodeKind.ScriptAsset)
                menu.AddItem(new GUIContent("ソースを開く"), false, () => NodeNavigator.TryOpenSource(node, _vm.Store));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("このノードを起点にする"), false, () => _vm.FocusNode(id));
            menu.AddItem(new GUIContent("隣接ノードを展開"), false, () => _vm.ExpandNode(id));
            menu.AddItem(new GUIContent(_vm.Collapsed.Contains(id) ? "子を展開（折り畳み解除）" : "子を折り畳む"), false, () => _vm.ToggleCollapse(id));
            menu.AddItem(new GUIContent(_vm.Pinned.Contains(id) ? "ピン留め解除" : "ピン留め"), false, () => _vm.TogglePin(id));
            string from = _vm.GraphSelectedNodeId;
            if (from != null && from != id)
            {
                var fromNode = _vm.Store.GetNode(from);
                menu.AddItem(new GUIContent($"経路を表示: {Short(fromNode?.Name)} → {Short(node.Name)}"), false, () => _vm.ShowPath(from, id));
            }
            menu.AddItem(new GUIContent("表示から外す"), false, () => _vm.RemoveFromView(id));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("ID をコピー"), false, () => EditorGUIUtility.systemCopyBuffer = id);
            menu.ShowAsContext();
        }

        static string Short(string s) => s == null ? "?" : s.Length > 20 ? s.Substring(0, 19) + "…" : s.Replace("/", "／");

        // ------------------------------------------------------------------ Events

        void OnUnitySelection(ResolvedSelection sel)
        {
            _vm?.OnUnitySelection(sel.Ids);
        }

        void OnViewChanged()
        {
            if (!_uiBuilt) return;
            _canvas.Refresh();
            _detail.Refresh();
            _bottom.Rebuild();
            UpdateBanner();
            SceneViewOverlay.Repaint();
        }

        void OnFocusRequested(Vector2 world)
        {
            if (_canvas != null && !_canvas.IsInteracting) _canvas.CenterOn(world);
        }

        void OnSchedulerStatus(SchedulerStatus status)
        {
            UpdateStatus();
            UpdateBanner();
        }

        void OnLogAdded(string line)
        {
            _bottom?.OnLogAdded();
        }

        void OnEditorUpdate()
        {
            if (!_uiBuilt) return;
            if (EditorApplication.timeSinceStartup > _nextStatusUpdate)
            {
                _nextStatusUpdate = EditorApplication.timeSinceStartup + 0.5;
                UpdateStatus();
            }
        }

        void UpdateStatus()
        {
            if (_status == null || _service == null) return;
            var st = _service.Scheduler.Status;
            string queue = st.Running ? $" | 解析中 {st.Completed}/{st.Total}" : "";
            string layout = _vm != null && _vm.LayoutInProgress ? " | レイアウト計算中" : "";
            int drawn = _vm?.GetDrawnNodes().Count ?? 0;
            _status.text = $"ノード {_service.Store.NodeCount:N0} / エッジ {_service.Store.EdgeCount:N0} | 表示 {drawn}{queue}{layout}";
            _cancelButton?.SetEnabled(st.Running);
        }

        void UpdateBanner()
        {
            if (_canvas == null || _vm == null) return;
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(_vm.Notification)) parts.Add(_vm.Notification);
            if (_vm.ViewTruncated) parts.Add($"表示上限 {_vm.State.maxNodes} ノードを超えたため一部を省略（+{_vm.ViewTruncatedCount}）。ノードの右クリック →「隣接ノードを展開」で段階表示できます");
            if (!_vm.State.followSelection) parts.Add("Follow selection: OFF（表示固定中）");
            var st = _service.Scheduler.Status;
            if (st.Running) parts.Add($"索引中… {st.Completed}/{st.Total} {System.IO.Path.GetFileName(st.CurrentTarget ?? "")}");
            if (_vm.Visible.Count == 0 && !st.Running) parts.Add("Hierarchy / Scene View / Project でオブジェクトを選択するか、検索してください");
            _canvas.SetBanner(string.Join("\n", parts));
        }

        static string Esc(string s) => string.IsNullOrEmpty(s) ? s : s.Replace("<", "<​");
    }
}

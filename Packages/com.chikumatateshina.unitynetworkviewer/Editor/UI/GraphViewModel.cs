using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityNetworkViewer.Core;
using UnityNetworkViewer.Core.Layout;
using UnityEditor;
using UnityEngine;

namespace UnityNetworkViewer.Editor.UI
{
    public enum NewSelectionBehavior
    {
        /// <summary>選択の関連部分グラフで表示を置き換える。</summary>
        ReplaceView,
        /// <summary>現在の表示に関連部分グラフを追加する。</summary>
        AddToView,
        /// <summary>表示を維持し、表示外なら通知のみ。</summary>
        KeepViewAndNotify,
    }

    /// <summary>表示状態の変化の種類。受け手は必要な更新だけを行う。</summary>
    [Flags]
    public enum ViewChange
    {
        None = 0,
        /// <summary>ノード位置のみ（ドラッグ・レイアウト適用）。</summary>
        Positions = 1,
        /// <summary>選択・ハイライト・経路など強調表示。</summary>
        Selection = 2,
        /// <summary>表示ノード集合・フィルタ。</summary>
        Structure = 4,
        /// <summary>索引（ストア）の内容。</summary>
        Store = 8,
        All = Positions | Selection | Structure | Store,
    }

    public enum GroupByMode
    {
        Scene,
        Prefab,
        NodeKind,
    }

    /// <summary>保存可能なビュー設定（ウィンドウの永続化・保存ビューで共通）。</summary>
    [Serializable]
    public sealed class ViewState
    {
        public RelationDirection direction = RelationDirection.Both;
        [Range(0, 5)] public int depth = 1;
        public int maxNodes = 300;
        public List<EdgeKind> disabledEdgeKinds = new List<EdgeKind> { EdgeKind.ScriptUsage };
        public List<NodeKind> disabledNodeKinds = new List<NodeKind>();
        public List<EvidenceLevel> disabledEvidence = new List<EvidenceLevel>();
        public SelectionSetMode setMode = SelectionSetMode.Union;
        public bool followSelection = true;
        public bool focusSelection = true;
        public bool includeComponents;
        public bool crossScene = true;
        public bool hideUnrelated;
        public bool syncGraphToUnity = true;
        public NewSelectionBehavior newSelection = NewSelectionBehavior.ReplaceView;
        public LayoutKind layout = LayoutKind.Hierarchical;
        public GroupByMode groupBy = GroupByMode.Scene;
        public bool showLegend = true;
        public bool showMiniMap = true;

        public ViewState Clone() => JsonUtility.FromJson<ViewState>(JsonUtility.ToJson(this));
    }

    [Serializable]
    public sealed class ViewSnapshot
    {
        public string name;
        public ViewState state = new ViewState();
        public List<string> roots = new List<string>();
        public List<string> visible = new List<string>();
        public List<string> pinnedIds = new List<string>();
        public List<Vector2> pinnedPositions = new List<Vector2>();
        public List<string> collapsed = new List<string>();
    }

    /// <summary>
    /// 表示状態。表示ノード集合（Visible）とハイライト（Highlight）は別物:
    /// Visible は描画対象、Highlight は選択からの関連（Query 結果）で強調度を決める。
    /// </summary>
    public sealed class GraphViewModel : IDisposable
    {
        readonly UnvService _service;
        public GraphStore Store => _service.Store;
        public GraphQueryService Query => _service.Query;
        public UnvService Service => _service;

        public ViewState State = new ViewState();
        public readonly List<string> Roots = new List<string>();
        public readonly HashSet<string> Visible = new HashSet<string>(StringComparer.Ordinal);
        public readonly Dictionary<string, Vector2> Positions = new Dictionary<string, Vector2>(StringComparer.Ordinal);
        public readonly HashSet<string> Pinned = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> Collapsed = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> HiddenByCollapse = new HashSet<string>(StringComparer.Ordinal);
        public readonly Dictionary<string, int> CollapsedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly HashSet<string> PathEdges = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<GroupRect> Groups = new List<GroupRect>();
        public SubgraphResult Highlight = new SubgraphResult();
        public string GraphSelectedNodeId;
        public string SelectedEdgeId;
        public string Notification;
        public bool ViewTruncated;
        public int ViewTruncatedCount;
        public long ViewRevision { get; private set; }
        /// <summary>ノード位置が変わるたびに増える（描画の再生成判定用）。</summary>
        public long PositionsRevision { get; private set; }
        /// <summary>ハイライト（選択からの関連）が変わるたびに増える。</summary>
        public long HighlightRevision { get; private set; }
        public bool LayoutInProgress => _layoutTask != null;

        public event Action<ViewChange> Changed;
        public event Action LayoutApplied;
        /// <summary>自動パン要求（ワールド座標の中心）。</summary>
        public event Action<Vector2> FocusRequested;

        readonly HashSet<string> _priorityRequested = new HashSet<string>(StringComparer.Ordinal);
        List<string> _visibleEdgesCache;
        long _visibleEdgesRevision = -1;
        long _visibleEdgesStoreRevision = -1;
        List<string> _drawnCache;
        long _drawnRevision = -1;
        long _drawnStoreRevision = -1;
        readonly HashSet<string> _pendingOwners = new HashSet<string>(StringComparer.Ordinal);
        bool _pendingCleared;
        Task<LayoutResult> _layoutTask;
        CancellationTokenSource _layoutCts;
        List<string> _layoutIds;
        bool _layoutPending;
        bool _focusAfterLayout;
        bool _storeDirty;
        double _storeDirtyAt;

        public GraphViewModel(UnvService service)
        {
            _service = service;
            Store.Changed += OnStoreChanged;
            EditorApplication.update += Update;
        }

        public void Dispose()
        {
            Store.Changed -= OnStoreChanged;
            EditorApplication.update -= Update;
            _layoutCts?.Cancel();
        }

        void Bump(bool structure) => Bump(structure ? ViewChange.Structure | ViewChange.Selection : ViewChange.Selection);

        void Bump(ViewChange change)
        {
            ViewRevision++;
            if ((change & ViewChange.Structure) != 0)
            {
                _visibleEdgesRevision = -1;
                _drawnRevision = -1;
            }
            Changed?.Invoke(change);
        }

        // ------------------------------------------------------------------ Query options

        public QueryOptions BuildOptions(int depth, int maxNodes)
        {
            var o = new QueryOptions
            {
                Direction = State.direction,
                MaxDepth = depth,
                MaxNodes = maxNodes,
                CrossSceneBoundaries = State.crossScene,
            };
            if (State.disabledEdgeKinds.Count > 0) o.EdgeKinds = new HashSet<EdgeKind>(EdgeKindInfo.All.Where(k => !State.disabledEdgeKinds.Contains(k)));
            if (State.disabledEvidence.Count > 0) o.Evidence = new HashSet<EvidenceLevel>(((EvidenceLevel[])Enum.GetValues(typeof(EvidenceLevel))).Where(k => !State.disabledEvidence.Contains(k)));
            if (State.disabledNodeKinds.Count > 0) o.NodeKinds = new HashSet<NodeKind>(EdgeKindInfo.AllNodeKinds.Where(k => !State.disabledNodeKinds.Contains(k)));
            return o;
        }

        public bool EdgePassesFilter(GraphEdge e)
        {
            if (State.disabledEdgeKinds.Contains(e.Kind)) return false;
            if (State.disabledEvidence.Contains(e.Evidence)) return false;
            return true;
        }

        public bool NodePassesFilter(GraphNode n) => n != null && !State.disabledNodeKinds.Contains(n.Kind);

        // ------------------------------------------------------------------ Selection

        /// <summary>Unity 側の選択変更（Hierarchy / Scene View / Project）。</summary>
        public void OnUnitySelection(IList<string> ids)
        {
            if (!State.followSelection) return;
            SetRoots(ids, fromGraph: false);
        }

        /// <summary>Graph 上でのノード選択（Unity への反映は呼び出し側が行う）。</summary>
        public void SelectFromGraph(string id, bool additive)
        {
            GraphSelectedNodeId = id;
            SelectedEdgeId = null;
            var roots = additive ? new List<string>(Roots) : new List<string>();
            if (id != null)
            {
                if (additive && roots.Contains(id)) roots.Remove(id);
                else roots.Add(id);
            }
            Roots.Clear();
            Roots.AddRange(roots);
            RecomputeHighlight();
            Bump(false);
        }

        public void SelectEdge(string edgeId)
        {
            SelectedEdgeId = edgeId;
            Bump(false);
        }

        void SetRoots(IList<string> ids, bool fromGraph)
        {
            var expanded = new List<string>();
            foreach (var id in ids)
            {
                if (!expanded.Contains(id)) expanded.Add(id);
                if (State.includeComponents)
                {
                    foreach (var e in Store.GetOutEdges(id))
                        if (e.Kind == EdgeKind.OwnsComponent && !expanded.Contains(e.TargetId)) expanded.Add(e.TargetId);
                }
            }
            Roots.Clear();
            Roots.AddRange(expanded);
            GraphSelectedNodeId = expanded.Count > 0 ? expanded[0] : null;
            SelectedEdgeId = null;
            Notification = DescribeMissing(ids);

            RecomputeHighlight();
            bool visibleChanged = false;
            if (Roots.Count > 0)
            {
                bool allVisible = Roots.Where(Store.ContainsNode).All(Visible.Contains);
                switch (State.newSelection)
                {
                    case NewSelectionBehavior.ReplaceView:
                        visibleChanged = RebuildVisible(false);
                        break;
                    case NewSelectionBehavior.AddToView:
                        visibleChanged = RebuildVisible(true);
                        break;
                    case NewSelectionBehavior.KeepViewAndNotify:
                        if (!allVisible && Roots.Any(Store.ContainsNode))
                            Notification = (Notification == null ? "" : Notification + " / ") + "選択ノードは現在の表示に含まれていません（表示維持モード）";
                        break;
                }
            }
            if (visibleChanged) RequestLayout(State.focusSelection);
            else if (State.focusSelection) RequestFocus();
            Bump(visibleChanged);
        }

        string DescribeMissing(IList<string> ids)
        {
            var reasons = new List<string>();
            foreach (var id in ids)
            {
                var node = Store.GetNode(id);
                if (node != null)
                {
                    if (!NodePassesFilter(node)) reasons.Add($"「{node.Name}」はノード種類フィルタ ({node.Kind}) で非表示です");
                    continue;
                }
                bool requested = !_priorityRequested.Contains(id) && _service.RequestPriorityScan(id);
                _priorityRequested.Add(id);
                if (NodeIds.IsSession(id))
                    reasons.Add(requested ? "未保存のオブジェクトを解析中です" : "未保存・一時オブジェクトは索引されていない可能性があります");
                else if (NodeIds.IsSceneObject(id))
                    reasons.Add(_service.Scheduler.IsIdle && !requested ? "このオブジェクトは索引にありません（Hidden/除外の可能性）" : "Scene を解析中です。完了後に反映されます");
                else if (NodeIds.IsAsset(id) || NodeIds.IsAssetObject(id))
                    reasons.Add(requested ? "Asset を優先解析中です" : "この Asset 種別は解析対象外か、まだ索引されていません");
                else
                    reasons.Add("未索引のオブジェクトです");
            }
            return reasons.Count == 0 ? null : string.Join(" / ", reasons.Distinct());
        }

        public void RecomputeHighlight()
        {
            HighlightRevision++;
            if (Roots.Count == 0)
            {
                Highlight = new SubgraphResult();
                return;
            }
            var o = BuildOptions(State.depth, Math.Max(State.maxNodes * 4, 1000));
            Highlight = Query.QueryMulti(Roots, o, State.setMode);
        }

        /// <summary>ルートの関連部分グラフで表示集合を作る。戻り値は表示集合が変化したか。</summary>
        bool RebuildVisible(bool additive)
        {
            var o = BuildOptions(Math.Max(State.depth, 1), Math.Max(State.maxNodes, 10));
            var sub = Query.QueryMulti(Roots, o, State.setMode);
            var next = new HashSet<string>(additive ? Visible : Enumerable.Empty<string>(), StringComparer.Ordinal);
            foreach (var id in sub.NodeDepth.Keys) next.Add(id);
            ViewTruncated = sub.Truncated;
            ViewTruncatedCount = sub.TruncatedNeighbors.Values.Sum();
            if (next.SetEquals(Visible)) return false;
            Visible.Clear();
            Visible.UnionWith(next);
            CleanupAfterVisibleChange();
            return true;
        }

        void CleanupAfterVisibleChange()
        {
            foreach (var id in Positions.Keys.Where(k => !Visible.Contains(k) && !Pinned.Contains(k)).ToList()) Positions.Remove(id);
            Collapsed.RemoveWhere(id => !Visible.Contains(id));
            RecomputeCollapse();
            PathEdges.RemoveWhere(e => Store.GetEdge(e) == null);
        }

        // ------------------------------------------------------------------ View manipulation

        public void ShowNodes(IEnumerable<string> ids, bool additive, bool select)
        {
            if (!additive) Visible.Clear();
            var list = ids.Where(Store.ContainsNode).ToList();
            foreach (var id in list) Visible.Add(id);
            if (select && list.Count > 0)
            {
                Roots.Clear();
                Roots.Add(list[0]);
                GraphSelectedNodeId = list[0];
                RecomputeHighlight();
            }
            CleanupAfterVisibleChange();
            RequestLayout(true);
            Bump(true);
        }

        /// <summary>ノードを起点（ルート）にして関連部分グラフを表示する。</summary>
        public void FocusNode(string id)
        {
            if (!Store.ContainsNode(id)) return;
            Roots.Clear();
            Roots.Add(id);
            GraphSelectedNodeId = id;
            RecomputeHighlight();
            bool changed = RebuildVisible(false);
            RequestLayout(true);
            Bump(changed);
        }

        public void ExpandNode(string id, int limit = 100)
        {
            var o = BuildOptions(1, limit + 1);
            var sub = Query.QuerySubgraph(new[] { id }, o);
            int before = Visible.Count;
            foreach (var n in sub.NodeDepth.Keys) Visible.Add(n);
            Collapsed.Remove(id);
            RecomputeCollapse();
            if (Visible.Count != before)
            {
                RequestLayout(false);
                Bump(true);
            }
            if (sub.Truncated) Notification = $"隣接ノードが多いため {limit} 件までを表示しました";
        }

        public void ToggleCollapse(string id)
        {
            if (!Collapsed.Remove(id)) Collapsed.Add(id);
            RecomputeCollapse();
            RequestLayout(false);
            Bump(true);
        }

        public void RemoveFromView(string id)
        {
            if (!Visible.Remove(id)) return;
            Pinned.Remove(id);
            Positions.Remove(id);
            Roots.Remove(id);
            if (GraphSelectedNodeId == id) GraphSelectedNodeId = null;
            CleanupAfterVisibleChange();
            Bump(true);
        }

        public void ClearView()
        {
            Visible.Clear();
            Positions.Clear();
            Pinned.Clear();
            Collapsed.Clear();
            HiddenByCollapse.Clear();
            PathEdges.Clear();
            Groups.Clear();
            Notification = null;
            Bump(true);
        }

        public void TogglePin(string id)
        {
            if (!Pinned.Remove(id)) Pinned.Add(id);
            PositionsRevision++;
            Changed?.Invoke(ViewChange.Positions);
        }

        public void MoveNode(string id, Vector2 world)
        {
            Positions[id] = world;
            Pinned.Add(id);
            PositionsRevision++;
            Changed?.Invoke(ViewChange.Positions);
        }

        /// <summary>経路探索（FR-004）。見つかった経路のノードを表示に加えて強調する。</summary>
        public bool ShowPath(string from, string to)
        {
            var o = BuildOptions(int.MaxValue, 0);
            o.Direction = RelationDirection.Both;
            var path = Query.FindPath(from, to, o);
            PathEdges.Clear();
            if (path == null)
            {
                Notification = "経路が見つかりません（フィルタ条件で到達できない可能性があります）";
                Bump(false);
                return false;
            }
            foreach (var eid in path)
            {
                var e = Store.GetEdge(eid);
                if (e == null) continue;
                PathEdges.Add(eid);
                Visible.Add(e.SourceId);
                Visible.Add(e.TargetId);
            }
            Notification = $"経路: {path.Count} ホップ";
            RecomputeCollapse();
            RequestLayout(false);
            Bump(true);
            return true;
        }

        void RecomputeCollapse()
        {
            HiddenByCollapse.Clear();
            CollapsedCounts.Clear();
            foreach (var c in Collapsed)
            {
                int count = 0;
                var stack = new Stack<string>();
                stack.Push(c);
                var seen = new HashSet<string>(StringComparer.Ordinal) { c };
                while (stack.Count > 0)
                {
                    var cur = stack.Pop();
                    foreach (var e in Store.GetOutEdges(cur))
                    {
                        if (e.Kind != EdgeKind.ParentChild && e.Kind != EdgeKind.OwnsComponent) continue;
                        if (!seen.Add(e.TargetId)) continue;
                        if (Visible.Contains(e.TargetId) && !Roots.Contains(e.TargetId))
                        {
                            HiddenByCollapse.Add(e.TargetId);
                            count++;
                        }
                        stack.Push(e.TargetId);
                    }
                }
                CollapsedCounts[c] = count;
            }
        }

        // ------------------------------------------------------------------ Derived data

        public bool IsDrawn(string id)
        {
            if (!Visible.Contains(id) || HiddenByCollapse.Contains(id)) return false;
            if (State.hideUnrelated && Roots.Count > 0 && !Highlight.ContainsNode(id)) return false;
            return Store.ContainsNode(id);
        }

        /// <summary>描画対象ノード（キャッシュ。呼び出し側で変更しないこと）。</summary>
        public IReadOnlyList<string> GetDrawnNodes()
        {
            if (_drawnCache != null && _drawnRevision == ViewRevision && _drawnStoreRevision == Store.Revision) return _drawnCache;
            var list = new List<string>();
            foreach (var id in Visible) if (IsDrawn(id)) list.Add(id);
            _drawnCache = list;
            _drawnRevision = ViewRevision;
            _drawnStoreRevision = Store.Revision;
            return list;
        }

        public List<string> GetVisibleEdges()
        {
            if (_visibleEdgesCache != null && _visibleEdgesRevision == ViewRevision && _visibleEdgesStoreRevision == Store.Revision) return _visibleEdgesCache;
            var list = new List<string>();
            foreach (var id in Visible)
            {
                if (!IsDrawn(id)) continue;
                foreach (var e in Store.GetOutEdges(id))
                {
                    if (!IsDrawn(e.TargetId)) continue;
                    if (!EdgePassesFilter(e) && !PathEdges.Contains(e.Id)) continue;
                    list.Add(e.Id);
                }
            }
            _visibleEdgesCache = list;
            _visibleEdgesRevision = ViewRevision;
            _visibleEdgesStoreRevision = Store.Revision;
            return list;
        }

        // ------------------------------------------------------------------ Store change

        void OnStoreChanged(GraphChangedEventArgs args)
        {
            if (!_storeDirty) _storeDirtyAt = EditorApplication.timeSinceStartup;
            _storeDirty = true;
            if (args.Cleared)
            {
                _pendingCleared = true;
                _priorityRequested.Clear();
            }
            foreach (var o in args.ChangedOwners) _pendingOwners.Add(o);
        }

        /// <summary>変化した owner が表示・ハイライト中のノードに関係するか。</summary>
        bool IsStoreChangeRelevant()
        {
            if (_pendingCleared) return true;
            foreach (var r in Roots)
                if (!Highlight.ContainsNode(r) && Store.ContainsNode(r)) return true; // 解析待ちだったルートが索引された
            if (Visible.Count == 0 && Highlight.NodeDepth.Count == 0) return false;
            foreach (var key in _pendingOwners)
            {
                var rec = Store.GetOwner(key);
                if (rec == null) return true; // 削除された owner（内容は不明なので反映する）
                foreach (var n in rec.Nodes)
                    if (Visible.Contains(n.Id) || Highlight.ContainsNode(n.Id)) return true;
                foreach (var eid in rec.EdgeIds)
                {
                    var e = Store.GetEdge(eid);
                    if (e == null) continue;
                    if (Visible.Contains(e.SourceId) || Visible.Contains(e.TargetId) || Highlight.ContainsNode(e.SourceId) || Highlight.ContainsNode(e.TargetId)) return true;
                }
            }
            return false;
        }

        void ApplyStoreChange()
        {
            _storeDirty = false;
            bool relevant = IsStoreChangeRelevant();
            _pendingOwners.Clear();
            _pendingCleared = false;
            if (!relevant)
            {
                // 表示中の部分グラフに関係しない更新（バックグラウンド索引など）は再計算・再描画しない
                Changed?.Invoke(ViewChange.Store);
                return;
            }
            int removed = Visible.RemoveWhere(id => !Store.ContainsNode(id));
            Pinned.RemoveWhere(id => !Store.ContainsNode(id));
            bool hadRoots = Roots.Count > 0;
            bool rootsNowPresent = Roots.Any(Store.ContainsNode);
            bool rootsWereMissing = hadRoots && Roots.Any(id => !Visible.Contains(id));

            RecomputeHighlight();
            bool changed = removed > 0;
            if (hadRoots && rootsNowPresent && rootsWereMissing && State.newSelection != NewSelectionBehavior.KeepViewAndNotify)
            {
                changed |= RebuildVisible(State.newSelection == NewSelectionBehavior.AddToView);
                if (Notification != null && Roots.All(Store.ContainsNode)) Notification = null;
            }
            CleanupAfterVisibleChange();
            if (changed) RequestLayout(State.focusSelection);
            Bump(ViewChange.Structure | ViewChange.Selection | ViewChange.Store);
        }

        // ------------------------------------------------------------------ Layout

        public void RequestLayout(bool focusAfter)
        {
            _layoutPending = true;
            _focusAfterLayout |= focusAfter;
        }

        public void RequestFocus()
        {
            var pts = Roots.Where(r => Positions.ContainsKey(r) && IsDrawn(r)).Select(r => Positions[r]).ToList();
            if (pts.Count == 0) return;
            var c = Vector2.zero;
            foreach (var p in pts) c += p;
            FocusRequested?.Invoke(c / pts.Count);
        }

        void Update()
        {
            // 索引更新は間引いて反映する（バックグラウンド索引中は 1 秒ごと）
            double interval = _service.IsBusy ? 1.0 : 0.15;
            if (_storeDirty && EditorApplication.timeSinceStartup - _storeDirtyAt > interval) ApplyStoreChange();

            if (_layoutTask != null && _layoutTask.IsCompleted)
            {
                var task = _layoutTask;
                var ids = _layoutIds;
                _layoutTask = null;
                if (task.Status == TaskStatus.RanToCompletion && task.Result != null) ApplyLayout(ids, task.Result);
                else if (task.Exception != null) Debug.LogWarning("[UnityNetworkViewer] Layout failed: " + task.Exception.GetBaseException().Message);
            }
            if (_layoutPending && _layoutTask == null) StartLayout();
        }

        void StartLayout()
        {
            _layoutPending = false;
            var ids = new List<string>(GetDrawnNodes());
            var input = new LayoutInput
            {
                NodeWidth = GraphStyles.NodeWidth,
                NodeHeight = GraphStyles.NodeHeight,
            };
            // 安定した結果のため ID 順で投入（ルートは先頭）
            ids.Sort(StringComparer.Ordinal);
            foreach (var r in Roots) if (ids.Remove(r)) ids.Insert(0, r);
            foreach (var id in ids) input.AddNode(id);
            foreach (var r in Roots)
            {
                int i = input.IndexOf(r);
                if (i >= 0) input.Roots.Add(i);
            }
            foreach (var eid in GetVisibleEdges())
            {
                var e = Store.GetEdge(eid);
                if (e != null) input.AddEdge(e.SourceId, e.TargetId, e.Kind);
            }
            for (int i = 0; i < ids.Count; i++)
            {
                if (Pinned.Contains(ids[i]) && Positions.TryGetValue(ids[i], out var p)) input.Pinned[i] = new Vec2(p.x, p.y);
                else if (Positions.TryGetValue(ids[i], out var q)) input.Previous[i] = new Vec2(q.x, q.y);
            }
            if (State.layout == LayoutKind.Grouped)
            {
                for (int i = 0; i < ids.Count; i++)
                {
                    var (key, label) = GroupKey(Store.GetNode(ids[i]));
                    input.GroupKeys[i] = key;
                    input.GroupLabels[key] = label;
                }
            }

            _layoutCts?.Cancel();
            _layoutCts = new CancellationTokenSource();
            var token = _layoutCts.Token;
            var strategy = LayoutFactory.Create(State.layout);
            _layoutIds = ids;
            // 純粋計算のみワーカースレッドで実行（Unity API は呼ばない）
            _layoutTask = Task.Run(() => strategy.Compute(input, token), token);
        }

        void ApplyLayout(List<string> ids, LayoutResult result)
        {
            for (int i = 0; i < ids.Count && i < result.Positions.Length; i++)
            {
                if (Pinned.Contains(ids[i])) continue;
                Positions[ids[i]] = new Vector2(result.Positions[i].X, result.Positions[i].Y);
            }
            Groups.Clear();
            Groups.AddRange(result.Groups);
            PositionsRevision++;
            Changed?.Invoke(ViewChange.Positions);
            LayoutApplied?.Invoke();
            if (_focusAfterLayout)
            {
                _focusAfterLayout = false;
                if (State.focusSelection) RequestFocus();
            }
        }

        (string key, string label) GroupKey(GraphNode n)
        {
            if (n == null) return ("(none)", "(none)");
            if (State.groupBy == GroupByMode.NodeKind) return (n.Kind.ToString(), n.Kind.ToString());
            if (State.groupBy == GroupByMode.Prefab)
            {
                var prefab = FindPrefabAsset(n);
                if (prefab != null) return ("prefab:" + prefab.Id, "Prefab: " + prefab.Name);
            }
            if (!string.IsNullOrEmpty(n.SceneGuid))
            {
                var scene = Store.GetNode(NodeIds.Scene(n.SceneGuid));
                return ("scene:" + n.SceneGuid, "Scene: " + (scene?.Name ?? n.SceneGuid));
            }
            if (!string.IsNullOrEmpty(n.AssetGuid))
            {
                var asset = Store.GetNode(NodeIds.Asset(n.AssetGuid));
                return ("asset:" + n.AssetGuid, "Asset: " + (asset?.Name ?? System.IO.Path.GetFileName(n.AssetPath ?? n.AssetGuid)));
            }
            return ("kind:" + n.Kind, n.Kind.ToString());
        }

        GraphNode FindPrefabAsset(GraphNode n)
        {
            if (n.Kind == NodeKind.PrefabAsset) return n;
            // Instance → 最寄りの Prefab Instance ルート → Source Prefab
            string cur = n.Id;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (cur != null && visited.Add(cur))
            {
                foreach (var e in Store.GetOutEdges(cur))
                {
                    if (e.Kind != EdgeKind.PrefabSource) continue;
                    var t = Store.GetNode(e.TargetId);
                    if (t != null && t.Kind == NodeKind.PrefabAsset) return t;
                }
                string parent = null;
                foreach (var e in Store.GetInEdges(cur))
                    if (e.Kind == EdgeKind.ParentChild || e.Kind == EdgeKind.OwnsComponent) { parent = e.SourceId; break; }
                cur = parent;
            }
            if (!string.IsNullOrEmpty(n.AssetGuid))
            {
                var a = Store.GetNode(NodeIds.Asset(n.AssetGuid));
                if (a != null && a.Kind == NodeKind.PrefabAsset) return a;
            }
            return null;
        }

        // ------------------------------------------------------------------ Snapshots

        public ViewSnapshot CreateSnapshot(string name)
        {
            var s = new ViewSnapshot { name = name, state = State.Clone() };
            s.roots.AddRange(Roots);
            s.visible.AddRange(Visible);
            foreach (var id in Pinned)
            {
                if (!Positions.TryGetValue(id, out var p)) continue;
                s.pinnedIds.Add(id);
                s.pinnedPositions.Add(p);
            }
            s.collapsed.AddRange(Collapsed);
            return s;
        }

        public void ApplySnapshot(ViewSnapshot s, bool restoreState = true)
        {
            if (s == null) return;
            if (restoreState && s.state != null) State = s.state.Clone();
            Roots.Clear();
            Roots.AddRange(s.roots);
            Visible.Clear();
            foreach (var id in s.visible) Visible.Add(id);
            Pinned.Clear();
            for (int i = 0; i < s.pinnedIds.Count && i < s.pinnedPositions.Count; i++)
            {
                Pinned.Add(s.pinnedIds[i]);
                Positions[s.pinnedIds[i]] = s.pinnedPositions[i];
            }
            Collapsed.Clear();
            foreach (var c in s.collapsed) Collapsed.Add(c);
            GraphSelectedNodeId = Roots.FirstOrDefault();
            RecomputeHighlight();
            RecomputeCollapse();
            RequestLayout(true);
            Bump(true);
        }

        /// <summary>フィルタ等の設定変更後の再評価。</summary>
        public void OnStateChanged(bool relayout)
        {
            RecomputeHighlight();
            RecomputeCollapse();
            if (relayout) RequestLayout(false);
            Bump(true);
        }

        /// <summary>現在のルートで表示を作り直す（深さ変更時など）。</summary>
        public void RefreshFromRoots()
        {
            RecomputeHighlight();
            if (Roots.Count > 0 && RebuildVisible(State.newSelection == NewSelectionBehavior.AddToView)) RequestLayout(State.focusSelection);
            else RequestLayout(false);
            Bump(true);
        }
    }
}

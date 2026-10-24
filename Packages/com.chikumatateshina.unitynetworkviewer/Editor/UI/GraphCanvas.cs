using System;
using System.Collections.Generic;
using System.Linq;
using UnityNetworkViewer.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityNetworkViewer.Editor.UI
{
    /// <summary>
    /// グラフ描画キャンバス。ノード/エッジは generateVisualContent で一括描画し、VisualElement はラベル
    /// （可視範囲内のノードのみ・プール再利用）に限定する。パン/ズーム/ドラッグ/クリック/コンテキストメニュー。
    /// 描画内容は「生成時のパン/ズーム」で作り、パン操作中は層の移動（transform）だけで追従する。
    /// 表示範囲が生成範囲（画面の周囲に余白を含む）を外れたとき、内容が変わったとき、ズームが止まったときだけ作り直す。
    /// </summary>
    public sealed class GraphCanvas : VisualElement
    {
        GraphViewModel _vm;
        public Vector2 Pan;
        public float Zoom = 1f;
        public const float MinZoom = 0.08f;
        public const float MaxZoom = 3f;

        readonly VisualElement _contentLayer;
        readonly VisualElement _labelLayer;
        readonly List<Label> _nodeLabels = new List<Label>();
        readonly List<Label> _edgeLabels = new List<Label>();
        readonly List<Label> _groupLabels = new List<Label>();
        readonly Label _banner;
        public readonly MiniMapElement MiniMap;
        public readonly LegendElement Legend;

        // 入力状態
        int _pointerId = -1;
        int _pressButton;
        string _pressNode;
        Vector2 _pressScreen;
        Vector2 _pressPan;
        Vector2 _dragOffset;
        bool _moved;
        bool _dragging;
        bool _panning;
        bool _initialized;

        // 描画用キャッシュ（ViewRevision 単位）
        long _visualRevision = -1;

        // 生成時のパン/ズームと内容のリビジョン
        Vector2 _genPan;
        float _genZoom = 1f;
        long _genViewRevision = -1;
        long _genPositionsRevision = -1;
        Vector2 _genSize;
        bool _gesture;
        IVisualElementScheduledItem _settle;
        double _lastRegenerate;
        /// <summary>これを超えるノード・エッジ数では Painter2D を使わず、頂点を直接作る（角丸・曲線のアンチエイリアスは省く）。</summary>
        const int FastDrawNodeThreshold = 200;
        const int FastDrawEdgeThreshold = 400;
        /// <summary>同時に表示するノードラベルの上限（選択に近いノードを優先）。</summary>
        const int MaxNodeLabels = 300;
        /// <summary>生成範囲の余白（画面サイズに対する割合）。</summary>
        const float GenerateMargin = 0.5f;
        readonly Dictionary<string, EdgeRole> _nodeRole = new Dictionary<string, EdgeRole>(StringComparer.Ordinal);

        public bool IsInteracting => _dragging || _panning;

        public event Action<string, bool> NodeClicked;
        public event Action<string> NodeDoubleClicked;
        public event Action<string> EdgeClicked;
        public event Action BackgroundClicked;
        public event Action<string, Vector2> NodeContextMenu;
        public event Action<Vector2> BackgroundContextMenu;
        public event Action ViewChanged;

        public GraphCanvas()
        {
            name = "graph-canvas";
            style.flexGrow = 1;
            style.overflow = Overflow.Hidden;
            style.backgroundColor = GraphStyles.Background;
            focusable = true;
            pickingMode = PickingMode.Position;

            _contentLayer = CreateLayer("content");
            _contentLayer.generateVisualContent += Draw;
            Add(_contentLayer);
            _labelLayer = CreateLayer("labels");
            Add(_labelLayer);

            _banner = new Label { pickingMode = PickingMode.Ignore };
            _banner.style.position = Position.Absolute;
            _banner.style.left = 8;
            _banner.style.top = 6;
            _banner.style.right = 220;
            _banner.style.color = GraphStyles.Text;
            _banner.style.whiteSpace = WhiteSpace.Normal;
            _banner.style.backgroundColor = new Color(0, 0, 0, 0.35f);
            _banner.style.paddingLeft = 6;
            _banner.style.paddingRight = 6;
            _banner.style.paddingTop = 2;
            _banner.style.paddingBottom = 2;
            _banner.style.borderTopLeftRadius = 3;
            _banner.style.borderTopRightRadius = 3;
            _banner.style.borderBottomLeftRadius = 3;
            _banner.style.borderBottomRightRadius = 3;
            _banner.style.display = DisplayStyle.None;
            Add(_banner);

            MiniMap = new MiniMapElement(this);
            Add(MiniMap);
            Legend = new LegendElement();
            Add(Legend);

            generateVisualContent += DrawBackground;
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCaptureOutEvent>(_ => EndInteraction());
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<KeyDownEvent>(OnKeyDown);
            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        }

        static VisualElement CreateLayer(string name)
        {
            var layer = new VisualElement { name = name, pickingMode = PickingMode.Ignore };
            layer.style.position = Position.Absolute;
            layer.style.left = 0;
            layer.style.top = 0;
            layer.style.right = 0;
            layer.style.bottom = 0;
            layer.style.transformOrigin = new TransformOrigin(0, 0, 0);
            return layer;
        }

        public void Bind(GraphViewModel vm)
        {
            _vm = vm;
            MiniMap.Bind(vm);
            Refresh();
        }

        void OnGeometryChanged(GeometryChangedEvent evt)
        {
            if (!_initialized && contentRect.width > 0)
            {
                _initialized = true;
                if (Pan == Vector2.zero) Pan = contentRect.size * 0.5f;
            }
            Refresh();
        }

        public Vector2 WorldToScreen(Vector2 w) => w * Zoom + Pan;
        public Vector2 ScreenToWorld(Vector2 s) => (s - Pan) / Zoom;

        public Rect WorldViewport
        {
            get
            {
                var size = contentRect.size;
                var min = ScreenToWorld(Vector2.zero);
                var max = ScreenToWorld(size);
                return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            }
        }

        /// <summary>
        /// 表示を更新する。内容・位置・ズームが変わったか、生成範囲を外れたときだけ描画を作り直し、
        /// それ以外（パン）は層の移動だけで済ませる。
        /// </summary>
        public void Refresh()
        {
            MarkDirtyRepaint(); // 背景グリッド（軽量）
            bool contentChanged = _vm == null || _genViewRevision != _vm.ViewRevision || _genPositionsRevision != _vm.PositionsRevision;
            var size = contentRect.size;
            // ウィンドウのサイズ変更中もズームと同様に、止まってから描き直す
            if (!contentChanged && _genSize != Vector2.zero && size != _genSize) BeginGesture();
            bool zoomChanged = !Mathf.Approximately(_genZoom, Zoom);
            bool covered = Covers(GeneratedWorldRect, WorldViewport);
            double now = EditorApplication.timeSinceStartup;
            if (contentChanged) Regenerate();
            else if (_gesture)
            {
                // 操作中は変換だけで追従し、描画済みの範囲を外れたときだけ間隔を空けて描き直す
                if (!covered && now - _lastRegenerate > 0.15) Regenerate();
                else ApplyLayerTransform();
            }
            else if (!covered || zoomChanged || size != _genSize) Regenerate();
            else ApplyLayerTransform();
            MiniMap.OnViewChanged(contentChanged);
        }

        static bool Covers(Rect outer, Rect inner)
            => outer.xMin <= inner.xMin && outer.yMin <= inner.yMin && outer.xMax >= inner.xMax && outer.yMax >= inner.yMax;

        /// <summary>ズーム・サイズ変更などの連続操作の開始（または継続）。止まって 150ms 後に描き直す。</summary>
        void BeginGesture()
        {
            _gesture = true;
            if (_settle == null)
                _settle = schedule.Execute(() =>
                {
                    _gesture = false;
                    Refresh();
                });
            _settle.ExecuteLater(150);
        }

        void Regenerate()
        {
            _lastRegenerate = EditorApplication.timeSinceStartup;
            _genPan = Pan;
            _genZoom = Zoom;
            _genSize = contentRect.size;
            if (_vm != null)
            {
                _genViewRevision = _vm.ViewRevision;
                _genPositionsRevision = _vm.PositionsRevision;
            }
            ApplyLayerTransform();
            _contentLayer.MarkDirtyRepaint();
            SyncLabels();
        }

        /// <summary>生成時のパン/ズームから現在のパン/ズームへの変換を層に設定する。</summary>
        void ApplyLayerTransform()
        {
            float k = _genZoom <= 0 ? 1f : Zoom / _genZoom;
            var t = Pan - _genPan * k;
            var translate = new StyleTranslate(new Translate(t.x, t.y, 0));
            var scale = new StyleScale(new Scale(new Vector3(k, k, 1f)));
            _contentLayer.style.translate = translate;
            _contentLayer.style.scale = scale;
            _labelLayer.style.translate = translate;
            _labelLayer.style.scale = scale;
        }

        Vector2 GenToScreen(Vector2 w) => w * _genZoom + _genPan;

        /// <summary>生成範囲（画面＋余白）のワールド座標。</summary>
        Rect GeneratedWorldRect
        {
            get
            {
                var size = _genSize;
                var margin = size * GenerateMargin;
                var min = (-margin - _genPan) / _genZoom;
                var max = (size + margin - _genPan) / _genZoom;
                return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            }
        }

        public void SetBanner(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                _banner.style.display = DisplayStyle.None;
                return;
            }
            _banner.text = text;
            _banner.style.display = DisplayStyle.Flex;
        }

        public void CenterOn(Vector2 world)
        {
            if (IsInteracting) return;
            Pan = contentRect.size * 0.5f - world * Zoom;
            Refresh();
            ViewChanged?.Invoke();
        }

        public void FrameNodes(IEnumerable<string> ids)
        {
            if (_vm == null) return;
            bool any = false;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var id in ids)
            {
                if (!_vm.Positions.TryGetValue(id, out var p) || !_vm.IsDrawn(id)) continue;
                any = true;
                minX = Mathf.Min(minX, p.x - GraphStyles.NodeWidth);
                maxX = Mathf.Max(maxX, p.x + GraphStyles.NodeWidth);
                minY = Mathf.Min(minY, p.y - GraphStyles.NodeHeight * 2);
                maxY = Mathf.Max(maxY, p.y + GraphStyles.NodeHeight * 2);
            }
            if (!any) return;
            var size = contentRect.size;
            if (size.x < 10 || size.y < 10) return;
            float zx = size.x / Mathf.Max(maxX - minX, 1f);
            float zy = size.y / Mathf.Max(maxY - minY, 1f);
            Zoom = Mathf.Clamp(Mathf.Min(zx, zy) * 0.95f, MinZoom, 1.4f);
            Pan = size * 0.5f - new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f) * Zoom;
            Refresh();
            ViewChanged?.Invoke();
        }

        public void FrameAll()
        {
            if (_vm != null) FrameNodes(_vm.GetDrawnNodes());
        }

        // ------------------------------------------------------------------ Visual state

        void EnsureVisualCache()
        {
            if (_vm == null || _visualRevision == _vm.ViewRevision) return;
            _visualRevision = _vm.ViewRevision;
            _nodeRole.Clear();
            foreach (var kv in _vm.Highlight.EdgeRoles)
            {
                if (kv.Value == EdgeRole.Induced) continue;
                var e = _vm.Store.GetEdge(kv.Key);
                if (e == null) continue;
                // ルートから遠い側の端点に役割を付ける
                string far = kv.Value == EdgeRole.Outgoing ? e.TargetId : e.SourceId;
                if (_nodeRole.ContainsKey(far)) continue;
                _nodeRole[far] = kv.Value;
            }
        }

        float NodeAlpha(string id)
        {
            if (_vm.Roots.Count == 0) return 1f;
            int d = _vm.Highlight.GetDepth(id);
            if (d < 0) return 0.22f;
            switch (d)
            {
                case 0:
                case 1: return 1f;
                case 2: return 0.8f;
                case 3: return 0.66f;
                default: return 0.52f;
            }
        }

        Color NodeBorder(string id, GraphNode node, out float width)
        {
            width = 1f;
            int d = _vm.Highlight.GetDepth(id);
            if (d == 0 && _vm.Roots.Contains(id))
            {
                width = 3.5f;
                return GraphStyles.Selected;
            }
            if (id == _vm.GraphSelectedNodeId)
            {
                width = 3f;
                return GraphStyles.Selected;
            }
            if (node.HasFlag(NodeFlags.Missing)) { width = 2f; return GraphStyles.Unresolved; }
            if (d > 0 && _nodeRole.TryGetValue(id, out var role))
            {
                width = d == 1 ? 2.2f : 1.5f;
                if (node.Kind == NodeKind.PrefabAsset) return GraphStyles.Prefab;
                return role == EdgeRole.Incoming ? GraphStyles.Incoming : GraphStyles.Outgoing;
            }
            return GraphStyles.Dark ? new Color(1, 1, 1, 0.18f) : new Color(0, 0, 0, 0.25f);
        }

        void EdgeStyle(GraphEdge e, out Color color, out float width)
        {
            if (_vm.PathEdges.Contains(e.Id))
            {
                color = GraphStyles.PathColor;
                width = 3.2f;
                return;
            }
            if (e.Id == _vm.SelectedEdgeId)
            {
                color = GraphStyles.Selected;
                width = 3f;
                return;
            }
            bool hasRoots = _vm.Roots.Count > 0;
            if (_vm.Highlight.EdgeRoles.TryGetValue(e.Id, out var role) && role != EdgeRole.Induced)
            {
                int d = Mathf.Max(_vm.Highlight.GetDepth(e.SourceId), _vm.Highlight.GetDepth(e.TargetId));
                width = d <= 1 ? 2.2f : 1.6f;
                if (EdgeKindInfo.IsStructural(e.Kind)) color = GraphStyles.Structural;
                else if (EdgeKindInfo.IsPrefabRelation(e.Kind)) color = GraphStyles.Prefab;
                else color = role == EdgeRole.Incoming ? GraphStyles.Incoming : GraphStyles.Outgoing;
                if (d > 1) color.a = 0.75f;
                return;
            }
            color = GraphStyles.EdgeBase(e.Kind);
            width = 1.1f;
            color.a = hasRoots ? (role == EdgeRole.Induced ? 0.45f : 0.12f) : 0.6f;
        }

        // ------------------------------------------------------------------ Drawing

        struct StyleKey : IEquatable<StyleKey>
        {
            public Color Color;
            public float Width;
            public bool Equals(StyleKey o) => Color == o.Color && Mathf.Approximately(Width, o.Width);
            public override bool Equals(object obj) => obj is StyleKey k && Equals(k);
            public override int GetHashCode() => Color.GetHashCode() ^ Width.GetHashCode();
        }

        void DrawBackground(MeshGenerationContext mgc)
        {
            DrawGrid(mgc.painter2D, contentRect);
        }

        /// <summary>ノード・エッジ・グループ。生成時のパン/ズーム（_genPan/_genZoom）の画面座標で描く。</summary>
        void Draw(MeshGenerationContext mgc)
        {
            DrawInner(mgc);
        }

        void DrawInner(MeshGenerationContext mgc)
        {
            var p = mgc.painter2D;
            if (_vm == null) return;
            EnsureVisualCache();
            float Zoom = _genZoom; // 以降の描画は生成時のズームで行う
            Func<Vector2, Vector2> WorldToScreen = GenToScreen;

            var view = GeneratedWorldRect;
            var margin = new Vector2(GraphStyles.NodeWidth, GraphStyles.NodeHeight);
            var cull = Rect.MinMaxRect(view.xMin - margin.x, view.yMin - margin.y, view.xMax + margin.x, view.yMax + margin.y);

            // グループ
            foreach (var g in _vm.Groups)
            {
                var r = new Rect(WorldToScreen(new Vector2(g.X, g.Y)), new Vector2(g.Width, g.Height) * Zoom);
                p.fillColor = GraphStyles.Dark ? new Color(1, 1, 1, 0.035f) : new Color(0, 0, 0, 0.04f);
                p.BeginPath();
                RoundedRect(p, r, 8 * Zoom);
                p.Fill();
                p.strokeColor = GraphStyles.Dark ? new Color(1, 1, 1, 0.15f) : new Color(0, 0, 0, 0.2f);
                p.lineWidth = 1f;
                p.BeginPath();
                RoundedRect(p, r, 8 * Zoom);
                p.Stroke();
            }

            // エッジ（スタイル別にまとめて描画）
            var edges = _vm.GetVisibleEdges();
            var pairCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var pairIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var eid in edges)
            {
                var e = _vm.Store.GetEdge(eid);
                if (e == null) continue;
                string key = PairKey(e);
                pairCount.TryGetValue(key, out int c);
                pairCount[key] = c + 1;
            }

            var lineBatches = new Dictionary<StyleKey, List<List<Vector2>>>();
            var arrowBatches = new Dictionary<Color, List<Vector2>>();
            var halfSize = new Vector2(GraphStyles.NodeWidth, GraphStyles.NodeHeight) * 0.5f * Zoom;
            var highlightedLast = new List<string>();

            void EmitEdge(GraphEdge e)
            {
                if (!_vm.Positions.TryGetValue(e.SourceId, out var ws) || !_vm.Positions.TryGetValue(e.TargetId, out var wt)) return;
                if (e.SourceId == e.TargetId) return;
                if (!cull.Overlaps(Rect.MinMaxRect(Mathf.Min(ws.x, wt.x), Mathf.Min(ws.y, wt.y), Mathf.Max(ws.x, wt.x), Mathf.Max(ws.y, wt.y)))) return;

                string key = PairKey(e);
                pairIndex.TryGetValue(key, out int idx);
                pairIndex[key] = idx + 1;
                int count = pairCount.TryGetValue(key, out int n) ? n : 1;

                var pts = BuildEdgePolyline(WorldToScreen(ws), WorldToScreen(wt), halfSize, count, idx, string.CompareOrdinal(e.SourceId, e.TargetId) > 0);
                if (pts.Count < 2) return;
                EdgeStyle(e, out var color, out float width);
                var sk = new StyleKey { Color = color, Width = width };
                if (!lineBatches.TryGetValue(sk, out var list)) lineBatches[sk] = list = new List<List<Vector2>>();
                var dash = GraphStyles.DashPattern(e.Evidence);
                if (dash == Vector2.zero) list.Add(pts);
                else DashPolyline(pts, dash.x * Mathf.Clamp(Zoom, 0.6f, 1.5f), dash.y * Mathf.Clamp(Zoom, 0.6f, 1.5f), list);

                if (Zoom > 0.25f)
                {
                    if (!arrowBatches.TryGetValue(color, out var arrows)) arrowBatches[color] = arrows = new List<Vector2>();
                    AddArrow(arrows, pts[pts.Count - 2], pts[pts.Count - 1], Mathf.Clamp(9f * Zoom, 5f, 11f) * (width > 2f ? 1.2f : 1f));
                }
            }

            foreach (var eid in edges)
            {
                var e = _vm.Store.GetEdge(eid);
                if (e == null) continue;
                bool emphasized = _vm.PathEdges.Contains(eid) || eid == _vm.SelectedEdgeId ||
                                  (_vm.Highlight.EdgeRoles.TryGetValue(eid, out var role) && role != EdgeRole.Induced);
                if (emphasized) { highlightedLast.Add(eid); continue; }
                EmitEdge(e);
            }
            foreach (var eid in highlightedLast) EmitEdge(_vm.Store.GetEdge(eid));

            var drawnNodes = _vm.GetDrawnNodes();
            if (drawnNodes.Count > FastDrawNodeThreshold || edges.Count > FastDrawEdgeThreshold)
            {
                DrawFast(mgc, lineBatches, arrowBatches, drawnNodes, cull, halfSize, Zoom);
                return;
            }

            foreach (var kv in lineBatches)
            {
                p.strokeColor = kv.Key.Color;
                p.lineWidth = kv.Key.Width;
                p.lineCap = LineCap.Butt;
                p.BeginPath();
                foreach (var poly in kv.Value)
                {
                    p.MoveTo(poly[0]);
                    for (int i = 1; i < poly.Count; i++) p.LineTo(poly[i]);
                }
                p.Stroke();
            }
            foreach (var kv in arrowBatches)
            {
                p.fillColor = kv.Key;
                p.BeginPath();
                var tri = kv.Value;
                for (int i = 0; i + 2 < tri.Count; i += 3)
                {
                    p.MoveTo(tri[i]);
                    p.LineTo(tri[i + 1]);
                    p.LineTo(tri[i + 2]);
                    p.ClosePath();
                }
                p.Fill();
            }

            // ノード
            float radius = 5f * Zoom;
            var drawn = new List<string>(_vm.GetDrawnNodes());
            // 強調ノードを最後に描く
            drawn.Sort((a, b) => _vm.Highlight.ContainsNode(a).CompareTo(_vm.Highlight.ContainsNode(b)));
            foreach (var id in drawn)
            {
                if (!_vm.Positions.TryGetValue(id, out var w) || !cull.Contains(w)) continue;
                var node = _vm.Store.GetNode(id);
                if (node == null) continue;
                var center = WorldToScreen(w);
                var r = new Rect(center - halfSize, halfSize * 2f);
                float alpha = NodeAlpha(id);

                var fill = GraphStyles.NodeFill(node.Kind);
                fill.a = alpha;
                p.fillColor = fill;
                p.BeginPath();
                RoundedRect(p, r, radius);
                p.Fill();

                // 種類アクセント（左端の帯）
                var accent = node.Kind == NodeKind.PrefabAsset || node.HasFlag(NodeFlags.PrefabInstanceRoot) ? GraphStyles.Prefab : GraphStyles.EdgeBase(KindAccentEdge(node.Kind));
                accent.a = alpha;
                p.fillColor = accent;
                p.BeginPath();
                p.MoveTo(new Vector2(r.xMin + radius * 0.3f, r.yMin + 2));
                p.LineTo(new Vector2(r.xMin + 4 * Zoom + 1, r.yMin + 2));
                p.LineTo(new Vector2(r.xMin + 4 * Zoom + 1, r.yMax - 2));
                p.LineTo(new Vector2(r.xMin + radius * 0.3f, r.yMax - 2));
                p.ClosePath();
                p.Fill();

                var border = NodeBorder(id, node, out float bw);
                border.a *= Mathf.Max(alpha, 0.5f);
                p.strokeColor = border;
                p.lineWidth = bw;
                if (node.IsUnloaded || node.IsPlaceholder)
                {
                    // 未ロード・仮ノードは破線枠（色に依存しない識別）
                    var outline = new List<Vector2> { r.min, new Vector2(r.xMax, r.yMin), r.max, new Vector2(r.xMin, r.yMax), r.min };
                    var dashes = new List<List<Vector2>>();
                    DashPolyline(outline, 6f, 4f, dashes);
                    p.BeginPath();
                    foreach (var d in dashes) { p.MoveTo(d[0]); p.LineTo(d[1]); }
                    p.Stroke();
                }
                else
                {
                    p.BeginPath();
                    RoundedRect(p, r, radius);
                    p.Stroke();
                }

                if (_vm.Pinned.Contains(id) && Zoom > 0.35f)
                {
                    p.fillColor = GraphStyles.Selected;
                    p.BeginPath();
                    p.Arc(new Vector2(r.xMax - 5, r.yMin + 5), 2.5f, 0f, 360f);
                    p.Fill();
                }
            }
        }

        /// <summary>
        /// 多数のノード・エッジ向けの描画。Painter2D のパス生成（角丸・線の結合）を使わず、四角形と三角形の頂点を直接作る。
        /// </summary>
        void DrawFast(MeshGenerationContext mgc, Dictionary<StyleKey, List<List<Vector2>>> lineBatches, Dictionary<Color, List<Vector2>> arrowBatches,
            IReadOnlyList<string> drawnNodes, Rect cull, Vector2 halfSize, float zoom)
        {
            var mesh = new MeshBuilder(mgc);
            foreach (var kv in lineBatches)
            {
                Color32 c = kv.Key.Color;
                float w = Mathf.Max(kv.Key.Width, 1f);
                foreach (var poly in kv.Value)
                    for (int i = 1; i < poly.Count; i++) mesh.Line(poly[i - 1], poly[i], w, c);
            }
            foreach (var kv in arrowBatches)
            {
                Color32 c = kv.Key;
                var tri = kv.Value;
                for (int i = 0; i + 2 < tri.Count; i += 3) mesh.Triangle(tri[i], tri[i + 1], tri[i + 2], c);
            }

            // 強調ノードを最後に描く
            var order = new List<string>(drawnNodes);
            order.Sort((a, b) => _vm.Highlight.ContainsNode(a).CompareTo(_vm.Highlight.ContainsNode(b)));
            foreach (var id in order)
            {
                if (!_vm.Positions.TryGetValue(id, out var w) || !cull.Contains(w)) continue;
                var node = _vm.Store.GetNode(id);
                if (node == null) continue;
                var center = GenToScreen(w);
                var r = new Rect(center - halfSize, halfSize * 2f);
                float alpha = NodeAlpha(id);

                var fill = GraphStyles.NodeFill(node.Kind);
                fill.a = alpha;
                mesh.Rect(r, fill);
                var accent = node.Kind == NodeKind.PrefabAsset || node.HasFlag(NodeFlags.PrefabInstanceRoot) ? GraphStyles.Prefab : GraphStyles.EdgeBase(KindAccentEdge(node.Kind));
                accent.a = alpha;
                mesh.Rect(new Rect(r.xMin, r.yMin, 4 * zoom + 1, r.height), accent);

                var border = NodeBorder(id, node, out float bw);
                border.a *= Mathf.Max(alpha, 0.5f);
                bw = Mathf.Max(bw, 1f);
                if (node.IsUnloaded || node.IsPlaceholder)
                {
                    var outline = new List<Vector2> { r.min, new Vector2(r.xMax, r.yMin), r.max, new Vector2(r.xMin, r.yMax), r.min };
                    var dashes = new List<List<Vector2>>();
                    DashPolyline(outline, 6f, 4f, dashes);
                    foreach (var d in dashes) mesh.Line(d[0], d[d.Count - 1], bw, border);
                }
                else
                {
                    mesh.Rect(new Rect(r.xMin, r.yMin, r.width, bw), border);
                    mesh.Rect(new Rect(r.xMin, r.yMax - bw, r.width, bw), border);
                    mesh.Rect(new Rect(r.xMin, r.yMin, bw, r.height), border);
                    mesh.Rect(new Rect(r.xMax - bw, r.yMin, bw, r.height), border);
                }
                if (_vm.Pinned.Contains(id) && zoom > 0.35f) mesh.Rect(new Rect(r.xMax - 7, r.yMin + 3, 4, 4), GraphStyles.Selected);
            }
            mesh.Flush();
        }

        /// <summary>頂点・インデックスを直接書き込むメッシュ（1 回の確保は 65,535 頂点まで）。</summary>
        sealed class MeshBuilder
        {
            readonly MeshGenerationContext _mgc;
            readonly List<Vertex> _vertices = new List<Vertex>(4096);
            readonly List<ushort> _indices = new List<ushort>(6144);
            const int MaxVertices = 60000;

            public MeshBuilder(MeshGenerationContext mgc) => _mgc = mgc;

            void Ensure(int vertices)
            {
                if (_vertices.Count + vertices > MaxVertices) Flush();
            }

            void Add(Vector2 p, Color32 c) => _vertices.Add(new Vertex { position = new Vector3(p.x, p.y, Vertex.nearZ), tint = c });

            public void Triangle(Vector2 a, Vector2 b, Vector2 c, Color32 color)
            {
                Ensure(3);
                // UI Toolkit は時計回り（画面座標で y 下向き）の三角形を描く
                if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) < 0) (b, c) = (c, b);
                int s = _vertices.Count;
                Add(a, color);
                Add(b, color);
                Add(c, color);
                _indices.Add((ushort)s);
                _indices.Add((ushort)(s + 1));
                _indices.Add((ushort)(s + 2));
            }

            public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color32 color)
            {
                Ensure(4);
                if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) < 0) (b, d) = (d, b);
                int s = _vertices.Count;
                Add(a, color);
                Add(b, color);
                Add(c, color);
                Add(d, color);
                _indices.Add((ushort)s);
                _indices.Add((ushort)(s + 1));
                _indices.Add((ushort)(s + 2));
                _indices.Add((ushort)s);
                _indices.Add((ushort)(s + 2));
                _indices.Add((ushort)(s + 3));
            }

            public void Rect(Rect r, Color32 color)
                => Quad(new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax), color);

            public void Line(Vector2 p0, Vector2 p1, float width, Color32 color)
            {
                var d = p1 - p0;
                float len = d.magnitude;
                if (len < 1e-3f) return;
                var n = new Vector2(-d.y, d.x) / len * (width * 0.5f);
                Quad(p0 + n, p1 + n, p1 - n, p0 - n, color);
            }

            public void Flush()
            {
                if (_vertices.Count == 0) return;
                var md = _mgc.Allocate(_vertices.Count, _indices.Count);
                md.SetAllVertices(_vertices.ToArray());
                md.SetAllIndices(_indices.ToArray());
                _vertices.Clear();
                _indices.Clear();
            }
        }

        static EdgeKind KindAccentEdge(NodeKind k)
        {
            switch (k)
            {
                case NodeKind.Component: return EdgeKind.SerializedReference;
                case NodeKind.ScriptAsset: return EdgeKind.ScriptUsage;
                case NodeKind.UdonProgram:
                case NodeKind.UdonVariable: return EdgeKind.UdonVariableReference;
                case NodeKind.UdonMethod:
                case NodeKind.UdonEvent: return EdgeKind.MethodControl;
                case NodeKind.AnimationClip:
                case NodeKind.AnimatorController:
                case NodeKind.AnimationPath: return EdgeKind.AnimationBinding;
                case NodeKind.UnresolvedReference: return EdgeKind.AssetDependency;
                default: return EdgeKind.ParentChild;
            }
        }

        static string PairKey(GraphEdge e)
            => string.CompareOrdinal(e.SourceId, e.TargetId) < 0 ? e.SourceId + "|" + e.TargetId : e.TargetId + "|" + e.SourceId;

        void DrawGrid(Painter2D p, Rect rect)
        {
            float step = 50f * Zoom;
            if (step < 12f) return;
            p.strokeColor = GraphStyles.GridLine;
            p.lineWidth = 1f;
            p.BeginPath();
            float ox = Mathf.Repeat(Pan.x, step), oy = Mathf.Repeat(Pan.y, step);
            for (float x = ox; x < rect.width; x += step) { p.MoveTo(new Vector2(x, 0)); p.LineTo(new Vector2(x, rect.height)); }
            for (float y = oy; y < rect.height; y += step) { p.MoveTo(new Vector2(0, y)); p.LineTo(new Vector2(rect.width, y)); }
            p.Stroke();
        }

        static void RoundedRect(Painter2D p, Rect r, float rad)
        {
            rad = Mathf.Min(rad, Mathf.Min(r.width, r.height) * 0.5f);
            p.MoveTo(new Vector2(r.xMin + rad, r.yMin));
            p.LineTo(new Vector2(r.xMax - rad, r.yMin));
            p.ArcTo(new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMin + rad), rad);
            p.LineTo(new Vector2(r.xMax, r.yMax - rad));
            p.ArcTo(new Vector2(r.xMax, r.yMax), new Vector2(r.xMax - rad, r.yMax), rad);
            p.LineTo(new Vector2(r.xMin + rad, r.yMax));
            p.ArcTo(new Vector2(r.xMin, r.yMax), new Vector2(r.xMin, r.yMax - rad), rad);
            p.LineTo(new Vector2(r.xMin, r.yMin + rad));
            p.ArcTo(new Vector2(r.xMin, r.yMin), new Vector2(r.xMin + rad, r.yMin), rad);
            p.ClosePath();
        }

        static Vector2 ClipToRect(Vector2 center, Vector2 half, Vector2 toward)
        {
            var d = toward - center;
            if (d.sqrMagnitude < 1e-4f) return center;
            float tx = Mathf.Abs(d.x) > 1e-4f ? half.x / Mathf.Abs(d.x) : float.MaxValue;
            float ty = Mathf.Abs(d.y) > 1e-4f ? half.y / Mathf.Abs(d.y) : float.MaxValue;
            float t = Mathf.Min(tx, ty, 1f);
            return center + d * t;
        }

        /// <summary>エッジの折れ線。多重エッジは二次ベジェで左右にずらす。</summary>
        static List<Vector2> BuildEdgePolyline(Vector2 s, Vector2 t, Vector2 half, int count, int index, bool flip)
        {
            var pts = new List<Vector2>();
            if (count <= 1)
            {
                var a = ClipToRect(s, half, t);
                var b = ClipToRect(t, half, s);
                if ((b - a).sqrMagnitude < 4f) return pts;
                pts.Add(a);
                pts.Add(b);
                return pts;
            }
            float offset = (index - (count - 1) * 0.5f) * 14f * (flip ? -1f : 1f);
            var dir = (t - s).normalized;
            var perp = new Vector2(-dir.y, dir.x);
            var ctrl = (s + t) * 0.5f + perp * offset * 2f;
            var start = ClipToRect(s, half, ctrl);
            var end = ClipToRect(t, half, ctrl);
            const int segments = 10;
            for (int i = 0; i <= segments; i++)
            {
                float u = i / (float)segments;
                var q = (1 - u) * (1 - u) * start + 2 * (1 - u) * u * ctrl + u * u * end;
                pts.Add(q);
            }
            return pts;
        }

        static void DashPolyline(List<Vector2> pts, float dash, float gap, List<List<Vector2>> output)
        {
            float pattern = dash + gap;
            float pos = 0f;
            List<Vector2> current = null;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[i + 1];
                float len = Vector2.Distance(a, b);
                if (len < 1e-3f) continue;
                var dir = (b - a) / len;
                float t = 0f;
                while (t < len)
                {
                    float phase = pos % pattern;
                    bool on = phase < dash;
                    float remain = on ? dash - phase : pattern - phase;
                    float step = Mathf.Min(remain, len - t);
                    var p0 = a + dir * t;
                    var p1 = a + dir * (t + step);
                    if (on)
                    {
                        if (current == null)
                        {
                            current = new List<Vector2> { p0 };
                            output.Add(current);
                        }
                        current.Add(p1);
                    }
                    else
                    {
                        current = null;
                    }
                    t += step;
                    pos += step;
                }
            }
        }

        static void AddArrow(List<Vector2> tris, Vector2 from, Vector2 tip, float size)
        {
            var d = tip - from;
            if (d.sqrMagnitude < 1e-4f) return;
            d.Normalize();
            var perp = new Vector2(-d.y, d.x);
            var b = tip - d * size;
            tris.Add(tip);
            tris.Add(b + perp * size * 0.45f);
            tris.Add(b - perp * size * 0.45f);
        }

        // ------------------------------------------------------------------ Labels

        static string Escape(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("<", "<​");

        void SyncLabels()
        {
            SyncLabelsInner();
        }

        void SyncLabelsInner()
        {
            if (_vm == null) return;
            EnsureVisualCache();
            float Zoom = _genZoom; // ラベルも生成時のパン/ズームで配置し、層の移動で追従する
            Func<Vector2, Vector2> WorldToScreen = GenToScreen;
            int used = 0;
            int usedEdges = 0;
            int usedGroups = 0;
            var view = GeneratedWorldRect;
            var half = new Vector2(GraphStyles.NodeWidth, GraphStyles.NodeHeight) * 0.5f;

            if (Zoom >= 0.42f)
            {
                bool twoLines = Zoom >= 0.7f;
                var candidates = new List<string>();
                foreach (var id in _vm.GetDrawnNodes())
                {
                    if (!_vm.Positions.TryGetValue(id, out var w)) continue;
                    if (w.x + half.x < view.xMin || w.x - half.x > view.xMax || w.y + half.y < view.yMin || w.y - half.y > view.yMax) continue;
                    candidates.Add(id);
                }
                if (candidates.Count > MaxNodeLabels)
                {
                    // ラベルが多すぎるとレイアウトと文字描画が重くなるため、選択に近いノード・表示中の範囲を優先する
                    var screen = WorldViewport;
                    int Priority(string id)
                    {
                        int d = _vm.Highlight.GetDepth(id);
                        int p = d < 0 ? 100 : d;
                        var w = _vm.Positions[id];
                        if (!screen.Contains(w)) p += 50;
                        return p;
                    }
                    candidates = candidates.OrderBy(Priority).Take(MaxNodeLabels).ToList();
                }
                foreach (var id in candidates)
                {
                    var w = _vm.Positions[id];
                    var node = _vm.Store.GetNode(id);
                    if (node == null) continue;
                    var label = GetLabel(_nodeLabels, used++, true);
                    var tl = WorldToScreen(w - half);
                    label.style.left = tl.x + 7 * Zoom;
                    label.style.top = tl.y + 1;
                    label.style.width = (GraphStyles.NodeWidth - 10) * Zoom;
                    label.style.height = GraphStyles.NodeHeight * Zoom - 2;
                    label.style.fontSize = Mathf.Clamp(11f * Zoom, 7f, 16f);
                    label.style.opacity = Mathf.Max(NodeAlpha(id), 0.45f);
                    label.text = BuildNodeLabel(id, node, twoLines);
                    label.tooltip = node.HierarchyPath ?? node.AssetPath ?? node.Name;
                }

                if (Zoom >= 0.75f)
                {
                    foreach (var eid in _vm.GetVisibleEdges())
                    {
                        bool emphasized = _vm.PathEdges.Contains(eid) || eid == _vm.SelectedEdgeId ||
                                          (_vm.Highlight.EdgeRoles.TryGetValue(eid, out var role) && role != EdgeRole.Induced);
                        if (!emphasized || usedEdges >= 150) continue;
                        var e = _vm.Store.GetEdge(eid);
                        if (e == null || !_vm.Positions.TryGetValue(e.SourceId, out var a) || !_vm.Positions.TryGetValue(e.TargetId, out var b)) continue;
                        var mid = (a + b) * 0.5f;
                        if (!view.Contains(mid)) continue;
                        var label = GetLabel(_edgeLabels, usedEdges++, false);
                        var s = WorldToScreen(mid);
                        label.style.left = s.x - 40;
                        label.style.top = s.y - 8;
                        label.style.fontSize = Mathf.Clamp(9f * Zoom, 8f, 12f);
                        string text = GraphStyles.EdgeAbbrev(e.Kind);
                        if (e.Evidence != EvidenceLevel.Confirmed) text += " (" + GraphStyles.EvidenceLabel(e.Evidence) + ")";
                        if (!string.IsNullOrEmpty(e.MemberName) && (e.Kind == EdgeKind.MethodControl || e.Kind == EdgeKind.UnityEventCall || e.Kind == EdgeKind.EventDispatch))
                            text += " · " + Truncate(e.MemberName, 28);
                        label.text = Escape(text);
                    }
                }
            }

            foreach (var g in _vm.Groups)
            {
                var label = GetLabel(_groupLabels, usedGroups++, false);
                var s = WorldToScreen(new Vector2(g.X, g.Y));
                label.style.left = s.x + 8;
                label.style.top = s.y + 4;
                label.style.fontSize = Mathf.Clamp(12f * Zoom, 8f, 16f);
                label.text = "<b>" + Escape(g.Label) + "</b>";
            }

            Hide(_nodeLabels, used);
            Hide(_edgeLabels, usedEdges);
            Hide(_groupLabels, usedGroups);
        }

        string BuildNodeLabel(string id, GraphNode node, bool twoLines)
        {
            string name = Escape(Truncate(node.Name ?? node.Id, 40));
            string extra = "";
            if (_vm.Highlight.TruncatedNeighbors.TryGetValue(id, out int more) && more > 0) extra += $" <color=#f0a040>+{more}</color>";
            if (_vm.CollapsedCounts.TryGetValue(id, out int hidden) && hidden > 0) extra += $" <color=#a0a0ff>[+{hidden}]</color>";
            if (!twoLines) return "<b>" + name + "</b>" + extra;
            string sub = GraphStyles.KindBadge(node.Kind);
            if (!string.IsNullOrEmpty(node.TypeName) && node.Kind != NodeKind.GameObject) sub += " · " + Escape(Truncate(ShortType(node.TypeName), 26));
            if (node.IsUnloaded) sub += " · 未ロード";
            if (node.HasFlag(NodeFlags.Inactive)) sub += " · 無効";
            if (node.HasFlag(NodeFlags.Missing)) sub += " · Missing";
            if (node.HasFlag(NodeFlags.PrefabInstanceRoot)) sub += " · Instance";
            return $"<b>{name}</b>{extra}\n<size=85%><color={GraphStyles.ToHtml(GraphStyles.SubText)}>{sub}</color></size>";
        }

        static string ShortType(string t)
        {
            int i = t.LastIndexOf('.');
            return i >= 0 ? t.Substring(i + 1) : t;
        }

        static string Truncate(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        Label GetLabel(List<Label> pool, int index, bool nodeLabel)
        {
            while (pool.Count <= index)
            {
                var l = new Label { pickingMode = PickingMode.Ignore, enableRichText = true };
                l.style.position = Position.Absolute;
                l.style.color = GraphStyles.Text;
                l.style.overflow = Overflow.Hidden;
                l.style.whiteSpace = WhiteSpace.NoWrap;
                l.style.unityTextAlign = nodeLabel ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter;
                if (!nodeLabel)
                {
                    l.style.width = 80;
                    l.style.backgroundColor = new Color(0, 0, 0, 0.0f);
                }
                _labelLayer.Add(l);
                pool.Add(l);
            }
            var label = pool[index];
            label.style.display = DisplayStyle.Flex;
            return label;
        }

        static void Hide(List<Label> pool, int from)
        {
            for (int i = from; i < pool.Count; i++) pool[i].style.display = DisplayStyle.None;
        }

        // ------------------------------------------------------------------ Hit testing

        public string HitNode(Vector2 screen)
        {
            if (_vm == null) return null;
            var w = ScreenToWorld(screen);
            var half = new Vector2(GraphStyles.NodeWidth, GraphStyles.NodeHeight) * 0.5f;
            string hit = null;
            bool hitHighlighted = false;
            foreach (var id in _vm.GetDrawnNodes())
            {
                if (!_vm.Positions.TryGetValue(id, out var p)) continue;
                if (Mathf.Abs(w.x - p.x) <= half.x && Mathf.Abs(w.y - p.y) <= half.y)
                {
                    bool h = _vm.Highlight.ContainsNode(id);
                    if (hit == null || (h && !hitHighlighted)) { hit = id; hitHighlighted = h; }
                }
            }
            return hit;
        }

        public string HitEdge(Vector2 screen)
        {
            if (_vm == null) return null;
            string best = null;
            float bestDist = 6f;
            foreach (var eid in _vm.GetVisibleEdges())
            {
                var e = _vm.Store.GetEdge(eid);
                if (e == null || !_vm.Positions.TryGetValue(e.SourceId, out var a) || !_vm.Positions.TryGetValue(e.TargetId, out var b)) continue;
                float d = DistanceToSegment(screen, WorldToScreen(a), WorldToScreen(b));
                if (d < bestDist)
                {
                    bestDist = d;
                    best = eid;
                }
            }
            return best;
        }

        static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-4f) return Vector2.Distance(p, a);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            return Vector2.Distance(p, a + ab * t);
        }

        // ------------------------------------------------------------------ Input

        void OnPointerDown(PointerDownEvent evt)
        {
            Focus();
            if (_pointerId >= 0) return;
            var pos = (Vector2)evt.localPosition;
            _pressButton = evt.button;
            _pressScreen = pos;
            _pressPan = Pan;
            _moved = false;

            if (evt.button == 1)
            {
                evt.StopPropagation();
                return;
            }

            _pointerId = evt.pointerId;
            this.CapturePointer(evt.pointerId);
            _pressNode = evt.button == 0 ? HitNode(pos) : null;
            if (_pressNode != null && _vm.Positions.TryGetValue(_pressNode, out var wp))
            {
                _dragOffset = wp - ScreenToWorld(pos);
                if (evt.clickCount == 2)
                {
                    NodeDoubleClicked?.Invoke(_pressNode);
                    EndInteraction();
                }
            }
            evt.StopPropagation();
        }

        void OnPointerMove(PointerMoveEvent evt)
        {
            if (_pointerId != evt.pointerId) return;
            var pos = (Vector2)evt.localPosition;
            if (!_moved && (pos - _pressScreen).sqrMagnitude > 9f) _moved = true;
            if (!_moved) return;
            if (_pressNode != null)
            {
                _dragging = true;
                _vm.MoveNode(_pressNode, ScreenToWorld(pos) + _dragOffset);
                Refresh();
            }
            else
            {
                _panning = true;
                Pan = _pressPan + (pos - _pressScreen);
                Refresh();
                ViewChanged?.Invoke();
            }
            evt.StopPropagation();
        }

        void OnPointerUp(PointerUpEvent evt)
        {
            var pos = (Vector2)evt.localPosition;
            if (evt.button == 1 && _pointerId < 0)
            {
                string node = HitNode(pos);
                if (node != null) NodeContextMenu?.Invoke(node, pos);
                else BackgroundContextMenu?.Invoke(pos);
                evt.StopPropagation();
                return;
            }
            if (_pointerId != evt.pointerId) return;
            if (!_moved && _pressButton == 0)
            {
                if (_pressNode != null)
                {
                    NodeClicked?.Invoke(_pressNode, evt.shiftKey || evt.actionKey);
                }
                else
                {
                    string edge = HitEdge(pos);
                    if (edge != null) EdgeClicked?.Invoke(edge);
                    else BackgroundClicked?.Invoke();
                }
            }
            EndInteraction();
            evt.StopPropagation();
        }

        void EndInteraction()
        {
            if (_pointerId >= 0 && this.HasPointerCapture(_pointerId)) this.ReleasePointer(_pointerId);
            _pointerId = -1;
            _pressNode = null;
            _dragging = false;
            _panning = false;
        }

        void OnWheel(WheelEvent evt)
        {
            var pos = (Vector2)evt.localMousePosition;
            float factor = Mathf.Pow(1.1f, -evt.delta.y / 3f);
            float newZoom = Mathf.Clamp(Zoom * factor, MinZoom, MaxZoom);
            var world = ScreenToWorld(pos);
            Zoom = newZoom;
            Pan = pos - world * Zoom;
            // ホイール操作中は拡大縮小の変換だけで追従し、止まってから描き直す
            BeginGesture();
            Refresh();
            ViewChanged?.Invoke();
            evt.StopPropagation();
        }

        public event Action<KeyCode> KeyCommand;

        void OnKeyDown(KeyDownEvent evt)
        {
            switch (evt.keyCode)
            {
                case KeyCode.F:
                    if (_vm != null && _vm.Roots.Count > 0) FrameNodes(_vm.Roots); else FrameAll();
                    evt.StopPropagation();
                    break;
                case KeyCode.A:
                    FrameAll();
                    evt.StopPropagation();
                    break;
                case KeyCode.Delete:
                case KeyCode.Backspace:
                case KeyCode.Escape:
                    KeyCommand?.Invoke(evt.keyCode);
                    evt.StopPropagation();
                    break;
            }
        }
    }

    /// <summary>右下のミニマップ。クリック/ドラッグで表示位置を移動する。</summary>
    public sealed class MiniMapElement : VisualElement
    {
        readonly GraphCanvas _canvas;
        GraphViewModel _vm;
        Rect _worldBounds;
        float _scale = 1f;
        Vector2 _offset;
        bool _dragging;
        readonly VisualElement _viewRect;

        public MiniMapElement(GraphCanvas canvas)
        {
            _canvas = canvas;
            // 表示範囲の枠は子要素の位置だけで動かす（パンのたびにノードを描き直さない）
            _viewRect = new VisualElement { pickingMode = PickingMode.Ignore };
            _viewRect.style.position = Position.Absolute;
            _viewRect.style.borderTopWidth = _viewRect.style.borderBottomWidth = _viewRect.style.borderLeftWidth = _viewRect.style.borderRightWidth = 1;
            _viewRect.style.borderTopColor = _viewRect.style.borderBottomColor = _viewRect.style.borderLeftColor = _viewRect.style.borderRightColor = new Color(1, 1, 1, 0.7f);
            Add(_viewRect);
            style.position = Position.Absolute;
            style.right = 8;
            style.bottom = 8;
            style.width = 180;
            style.height = 120;
            style.backgroundColor = new Color(0, 0, 0, 0.35f);
            style.borderTopWidth = style.borderBottomWidth = style.borderLeftWidth = style.borderRightWidth = 1;
            style.borderTopColor = style.borderBottomColor = style.borderLeftColor = style.borderRightColor = new Color(1, 1, 1, 0.15f);
            generateVisualContent += Draw;
            RegisterCallback<PointerDownEvent>(e => { _dragging = true; this.CapturePointer(e.pointerId); MoveTo(e.localPosition); e.StopPropagation(); });
            RegisterCallback<PointerMoveEvent>(e => { if (_dragging) { MoveTo(e.localPosition); e.StopPropagation(); } });
            RegisterCallback<PointerUpEvent>(e => { _dragging = false; this.ReleasePointer(e.pointerId); e.StopPropagation(); });
            RegisterCallback<WheelEvent>(e => e.StopPropagation());
        }

        public void Bind(GraphViewModel vm) => _vm = vm;

        /// <summary>キャンバスの表示が変わった。内容が変わったときだけノードを描き直す。</summary>
        public void OnViewChanged(bool contentChanged)
        {
            if (contentChanged || _scale <= 0) MarkDirtyRepaint();
            UpdateViewRect();
        }

        void UpdateViewRect()
        {
            var view = _canvas.WorldViewport;
            var size = contentRect.size;
            var vmin = (view.min - _worldBounds.min) * _scale + _offset;
            var vmax = (view.max - _worldBounds.min) * _scale + _offset;
            vmin = Vector2.Max(vmin, Vector2.zero);
            vmax = Vector2.Min(vmax, size);
            if (vmax.x <= vmin.x || vmax.y <= vmin.y)
            {
                _viewRect.style.display = DisplayStyle.None;
                return;
            }
            _viewRect.style.display = DisplayStyle.Flex;
            _viewRect.style.left = vmin.x;
            _viewRect.style.top = vmin.y;
            _viewRect.style.width = vmax.x - vmin.x;
            _viewRect.style.height = vmax.y - vmin.y;
        }

        void MoveTo(Vector2 local)
        {
            if (_scale <= 0) return;
            var world = (local - _offset) / _scale + _worldBounds.min;
            _canvas.CenterOn(world);
        }

        void Draw(MeshGenerationContext mgc)
        {
            if (_vm == null) return;
            var ids = _vm.GetDrawnNodes();
            if (ids.Count == 0) return;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var id in ids)
            {
                if (!_vm.Positions.TryGetValue(id, out var p)) continue;
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
            if (minX > maxX) return;
            // 範囲はノードのみ（表示範囲を含めるとパンのたびに縮尺が変わり描き直しが必要になる）
            float pad = GraphStyles.NodeWidth;
            _worldBounds = Rect.MinMaxRect(minX - pad, minY - pad, maxX + pad, maxY + pad);
            var size = contentRect.size - new Vector2(8, 8);
            _scale = Mathf.Min(size.x / Mathf.Max(_worldBounds.width, 1f), size.y / Mathf.Max(_worldBounds.height, 1f));
            _offset = new Vector2(4, 4) + (size - _worldBounds.size * _scale) * 0.5f;

            var p2 = mgc.painter2D;
            foreach (var id in ids)
            {
                if (!_vm.Positions.TryGetValue(id, out var p)) continue;
                var s = (p - _worldBounds.min) * _scale + _offset;
                int d = _vm.Highlight.GetDepth(id);
                p2.fillColor = d == 0 && _vm.Roots.Contains(id) ? GraphStyles.Selected : d > 0 ? GraphStyles.Outgoing : new Color(0.7f, 0.7f, 0.7f, 0.6f);
                p2.BeginPath();
                p2.MoveTo(s + new Vector2(-1.5f, -1.5f));
                p2.LineTo(s + new Vector2(1.5f, -1.5f));
                p2.LineTo(s + new Vector2(1.5f, 1.5f));
                p2.LineTo(s + new Vector2(-1.5f, 1.5f));
                p2.ClosePath();
                p2.Fill();
            }
            UpdateViewRect();
        }
    }

    /// <summary>凡例。色に加えて線種・略号で関係と確度を識別できることを示す。</summary>
    public sealed class LegendElement : VisualElement
    {
        public LegendElement()
        {
            style.position = Position.Absolute;
            style.right = 8;
            style.top = 6;
            style.width = 200;
            style.backgroundColor = new Color(0, 0, 0, 0.45f);
            style.paddingLeft = style.paddingRight = 6;
            style.paddingTop = style.paddingBottom = 4;
            style.borderTopLeftRadius = style.borderTopRightRadius = style.borderBottomLeftRadius = style.borderBottomRightRadius = 4;
            pickingMode = PickingMode.Ignore;

            AddHeader("ノード");
            AddRow(GraphStyles.Selected, Vector2.zero, "選択（太枠）", true);
            AddRow(GraphStyles.Incoming, Vector2.zero, "参照元 (Incoming)", true);
            AddRow(GraphStyles.Outgoing, Vector2.zero, "参照先 (Outgoing)", true);
            AddRow(new Color(0.8f, 0.8f, 0.8f), new Vector2(6, 4), "未ロード / 仮ノード（破線枠）", true);
            AddHeader("エッジ（矢印 = 方向）");
            AddRow(GraphStyles.Incoming, Vector2.zero, "参照元側の関係", false);
            AddRow(GraphStyles.Outgoing, Vector2.zero, "参照先側の関係", false);
            AddRow(GraphStyles.Structural, Vector2.zero, "親子 / Component 所属", false);
            AddRow(GraphStyles.Prefab, Vector2.zero, "Prefab Source / Variant", false);
            AddRow(GraphStyles.PathColor, Vector2.zero, "経路探索の結果", false);
            AddHeader("確度（線種）");
            AddRow(GraphStyles.Structural, Vector2.zero, "確定 Confirmed（実線）", false);
            AddRow(GraphStyles.Structural, GraphStyles.DashPattern(EvidenceLevel.Inferred), "推定 Inferred（破線）", false);
            AddRow(GraphStyles.Structural, GraphStyles.DashPattern(EvidenceLevel.Unresolved), "未解決 Unresolved（点線）", false);
            var note = new Label("ズーム時のエッジ略号: child, comp, ref, uevent, prefab, variant, udon-var, program, proxy, call, control, event, anim, clip, dep");
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.fontSize = 9;
            note.style.color = GraphStyles.SubText;
            note.style.marginTop = 2;
            Add(note);
        }

        void AddHeader(string text)
        {
            var l = new Label(text);
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.fontSize = 10;
            l.style.color = GraphStyles.Text;
            l.style.marginTop = 2;
            Add(l);
        }

        void AddRow(Color color, Vector2 dash, string text, bool box)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.height = 14;
            var swatch = new VisualElement();
            swatch.style.width = 34;
            swatch.style.height = 12;
            swatch.style.marginRight = 6;
            swatch.generateVisualContent += mgc =>
            {
                var p = mgc.painter2D;
                p.strokeColor = color;
                p.lineWidth = box ? 2f : 2f;
                var r = swatch.contentRect;
                if (box)
                {
                    var pts = new List<Vector2> { new Vector2(2, 1), new Vector2(r.width - 2, 1), new Vector2(r.width - 2, r.height - 1), new Vector2(2, r.height - 1), new Vector2(2, 1) };
                    StrokeDashed(p, pts, dash);
                }
                else
                {
                    var pts = new List<Vector2> { new Vector2(1, r.height * 0.5f), new Vector2(r.width - 6, r.height * 0.5f) };
                    StrokeDashed(p, pts, dash);
                    p.fillColor = color;
                    p.BeginPath();
                    p.MoveTo(new Vector2(r.width - 1, r.height * 0.5f));
                    p.LineTo(new Vector2(r.width - 7, r.height * 0.5f - 3.5f));
                    p.LineTo(new Vector2(r.width - 7, r.height * 0.5f + 3.5f));
                    p.ClosePath();
                    p.Fill();
                }
            };
            row.Add(swatch);
            var label = new Label(text);
            label.style.fontSize = 10;
            label.style.color = GraphStyles.Text;
            row.Add(label);
            Add(row);
        }

        static void StrokeDashed(Painter2D p, List<Vector2> pts, Vector2 dash)
        {
            p.BeginPath();
            if (dash == Vector2.zero)
            {
                p.MoveTo(pts[0]);
                for (int i = 1; i < pts.Count; i++) p.LineTo(pts[i]);
                p.Stroke();
                return;
            }
            float pattern = dash.x + dash.y;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[i + 1];
                float len = Vector2.Distance(a, b);
                var d = (b - a) / Mathf.Max(len, 1e-3f);
                for (float t = 0; t < len; t += pattern)
                {
                    p.MoveTo(a + d * t);
                    p.LineTo(a + d * Mathf.Min(t + dash.x, len));
                }
            }
            p.Stroke();
        }
    }
}

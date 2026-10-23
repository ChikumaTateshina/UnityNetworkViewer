using System.Collections.Generic;
using System.Globalization;
using UnityNetworkViewer.Core;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityNetworkViewer.Editor.UI
{
    /// <summary>
    /// Scene View 上に選択と関連オブジェクトの線・枠を描画する任意機能（既定無効）。
    /// Repaint イベントでのみ描画し、選択・Gizmo 操作を妨げない。未ロード Scene の対象は描画しない。
    /// </summary>
    public static class SceneViewOverlay
    {
        static GraphViewModel _vm;
        static long _builtRevision = -1;
        static readonly List<(Vector3 from, Vector3 to, Color color)> Lines = new List<(Vector3, Vector3, Color)>();
        static readonly List<(Bounds bounds, Color color, bool root)> Boxes = new List<(Bounds, Color, bool)>();
        const int MaxObjects = 200;

        public static void Attach(GraphViewModel vm)
        {
            _vm = vm;
            _builtRevision = -1;
            SceneView.duringSceneGui -= OnSceneGui;
            SceneView.duringSceneGui += OnSceneGui;
        }

        public static void Detach(GraphViewModel vm)
        {
            if (_vm != vm) return;
            _vm = null;
            SceneView.duringSceneGui -= OnSceneGui;
            Lines.Clear();
            Boxes.Clear();
        }

        public static void Repaint()
        {
            _builtRevision = -1;
            SceneView.RepaintAll();
        }

        static void OnSceneGui(SceneView view)
        {
            if (_vm == null || !UnvSettings.instance.sceneViewOverlay) return;
            if (Event.current.type != EventType.Repaint) return;
            if (_builtRevision != _vm.ViewRevision) Build();

            var prevZ = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            foreach (var b in Boxes)
            {
                Handles.color = b.color;
                Handles.DrawWireCube(b.bounds.center, b.bounds.size);
                if (b.root) Handles.DrawWireCube(b.bounds.center, b.bounds.size * 1.04f);
            }
            foreach (var l in Lines)
            {
                Handles.color = l.color;
                Handles.DrawDottedLine(l.from, l.to, 4f);
            }
            Handles.zTest = prevZ;
        }

        static void Build()
        {
            _builtRevision = _vm.ViewRevision;
            Lines.Clear();
            Boxes.Clear();
            if (_vm.Roots.Count == 0) return;

            var ids = new List<string>();
            foreach (var kv in _vm.Highlight.NodeDepth)
            {
                if (ids.Count >= MaxObjects) break;
                if (NodeIds.IsSceneObject(kv.Key) || NodeIds.IsSession(kv.Key)) ids.Add(kv.Key);
            }
            var objects = ResolveBatch(ids);
            var pos = new Dictionary<string, Bounds>();
            foreach (var kv in objects)
            {
                GameObject go = kv.Value as GameObject ?? (kv.Value as Component)?.gameObject;
                if (go == null || !go.scene.isLoaded) continue;
                pos[kv.Key] = GetBounds(go);
            }

            foreach (var kv in pos)
            {
                int d = _vm.Highlight.GetDepth(kv.Key);
                bool root = d == 0;
                var c = root ? GraphStyles.Selected : GraphStyles.Outgoing;
                c.a = root ? 1f : d == 1 ? 0.8f : 0.45f;
                Boxes.Add((kv.Value, c, root));
            }
            foreach (var kv in _vm.Highlight.EdgeRoles)
            {
                if (kv.Value == EdgeRole.Induced) continue;
                var e = _vm.Store.GetEdge(kv.Key);
                if (e == null || EdgeKindInfo.IsStructural(e.Kind)) continue;
                if (!pos.TryGetValue(e.SourceId, out var a) || !pos.TryGetValue(e.TargetId, out var b)) continue;
                if (a.center == b.center) continue;
                Lines.Add((a.center, b.center, kv.Value == EdgeRole.Incoming ? GraphStyles.Incoming : GraphStyles.Outgoing));
            }
        }

        static Dictionary<string, Object> ResolveBatch(List<string> ids)
        {
            var result = new Dictionary<string, Object>();
            var gids = new List<GlobalObjectId>();
            var gidKeys = new List<string>();
            foreach (var id in ids)
            {
                if (NodeIds.IsSession(id))
                {
                    var o = EditorUtility.InstanceIDToObject(NodeIds.TryGetSessionInstanceId(id));
                    if (o != null) result[id] = o;
                    continue;
                }
                if (!NodeIds.TryParseSceneObject(id, out string scene, out long obj, out long prefab)) continue;
                string text = string.Format(CultureInfo.InvariantCulture, "GlobalObjectId_V1-2-{0}-{1}-{2}", scene, unchecked((ulong)obj), unchecked((ulong)prefab));
                if (GlobalObjectId.TryParse(text, out var gid))
                {
                    gids.Add(gid);
                    gidKeys.Add(id);
                }
            }
            if (gids.Count > 0)
            {
                var objs = new Object[gids.Count];
                GlobalObjectId.GlobalObjectIdentifiersToObjectsSlow(gids.ToArray(), objs);
                for (int i = 0; i < objs.Length; i++) if (objs[i] != null) result[gidKeys[i]] = objs[i];
            }
            return result;
        }

        static Bounds GetBounds(GameObject go)
        {
            var r = go.GetComponent<Renderer>();
            if (r != null) return r.bounds;
            var c = go.GetComponent<Collider>();
            if (c != null) return c.bounds;
            return new Bounds(go.transform.position, Vector3.one * 0.25f);
        }
    }
}

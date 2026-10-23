using System;
using System.Collections.Generic;
using System.Linq;
using UnityNetworkViewer.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityNetworkViewer.Editor.UI
{
    public sealed class ResolvedSelection
    {
        public readonly List<string> Ids = new List<string>();
        public readonly List<Object> Objects = new List<Object>();
    }

    /// <summary>
    /// Unity Selection と Graph の双方向同期。Hierarchy / Scene View / Project の選択は Unity の同一 Selection として扱う。
    /// ループ防止: (1) Graph から設定中の再入ガード、(2) Graph が最後に設定したIDとの比較、(3) 前回IDとの比較。
    /// </summary>
    public sealed class SelectionBridge : IDisposable
    {
        public const int MaxResolvedSelection = 200;
        bool _pushing;
        string[] _lastIds = Array.Empty<string>();
        string[] _pushedIds;

        public event Action<ResolvedSelection> UnitySelectionChanged;

        public SelectionBridge()
        {
            Selection.selectionChanged += OnSelectionChanged;
        }

        public void Dispose()
        {
            Selection.selectionChanged -= OnSelectionChanged;
        }

        public static ResolvedSelection ResolveCurrent()
        {
            var r = new ResolvedSelection();
            var objects = Selection.objects;
            // 大量選択時は先頭のみ解決する（選択反映の応答性を優先）
            for (int i = 0; i < objects.Length && i < MaxResolvedSelection; i++)
            {
                var o = objects[i];
                if (o == null) continue;
                // Prefab Stage 内のオブジェクトは Prefab Asset 側の対応オブジェクトに読み替える
                var mapped = MapPrefabStageObject(o);
                string id = ObjectIdUtility.GetId(mapped);
                if (id == null || r.Ids.Contains(id)) continue;
                r.Ids.Add(id);
                r.Objects.Add(mapped);
            }
            return r;
        }

        static Object MapPrefabStageObject(Object o)
        {
            GameObject go = o as GameObject ?? (o as Component)?.gameObject;
            if (go == null) return o;
            var stage = PrefabStageUtility.GetPrefabStage(go);
            if (stage == null) return o;
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(stage.assetPath);
            if (asset == null) return o;
            // ステージ内のパスから Asset 側の同じオブジェクトを探す
            string rel = RelativePath(stage.prefabContentsRoot.transform, go.transform);
            if (rel == null) return o;
            var t = rel.Length == 0 ? asset.transform : asset.transform.Find(rel);
            if (t == null) return o;
            if (o is Component c)
            {
                var comps = go.GetComponents(c.GetType());
                int index = Array.IndexOf(comps, c);
                var targets = t.GetComponents(c.GetType());
                return index >= 0 && index < targets.Length ? targets[index] : (Object)t.gameObject;
            }
            return t.gameObject;
        }

        static string RelativePath(Transform root, Transform t)
        {
            var parts = new List<string>();
            while (t != null && t != root)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            if (t != root) return null;
            parts.Reverse();
            return string.Join("/", parts);
        }

        void OnSelectionChanged()
        {
            if (_pushing) return;
            var sel = ResolveCurrent();
            var ids = sel.Ids.ToArray();
            if (ids.SequenceEqual(_lastIds)) return;
            _lastIds = ids;
            // Graph 側が設定した選択の通知は Graph へ戻さない
            if (_pushedIds != null && ids.SequenceEqual(_pushedIds))
            {
                _pushedIds = null;
                return;
            }
            _pushedIds = null;
            UnitySelectionChanged?.Invoke(sel);
        }

        /// <summary>Graph から Unity の Selection を設定する。未ロード Scene を開くことはしない。</summary>
        public void PushToUnity(IList<string> ids, bool ping)
        {
            var objects = new List<Object>();
            var resolvedIds = new List<string>();
            foreach (var id in ids)
            {
                if (ObjectResolver.TryResolve(id, out var obj) == ResolveStatus.Resolved && obj != null)
                {
                    objects.Add(obj);
                    resolvedIds.Add(id);
                }
            }
            if (objects.Count == 0) return;
            _pushedIds = resolvedIds.ToArray();
            _lastIds = _pushedIds;
            _pushing = true;
            try
            {
                Selection.objects = objects.ToArray();
            }
            finally
            {
                _pushing = false;
            }
            if (ping) EditorGUIUtility.PingObject(objects[0]);
        }
    }
}

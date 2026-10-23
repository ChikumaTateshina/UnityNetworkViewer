using System.Collections.Generic;
using ObjectRelationshipExplorer.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ObjectRelationshipExplorer.Editor
{
    /// <summary>
    /// ロード済み Scene / Prefab Asset 内の GameObject と Component を一度だけ列挙し、
    /// ID をバッチで計算して Provider 間で共有する。読み取りのみで Dirty にしない。
    /// </summary>
    public sealed class ObjectTable
    {
        public readonly AnalysisTarget Target;
        public readonly bool IsPrefabAsset;
        public readonly string SceneGuid;
        public readonly string AssetGuid;
        public readonly string AssetPath;
        /// <summary>Scene ノード、または Prefab Asset ノードのID。</summary>
        public readonly string ContainerId;
        public readonly GameObject PrefabRoot;
        public readonly List<GameObject> GameObjects = new List<GameObject>();
        /// <summary>GameObjects と同順。Missing Script の位置は null。</summary>
        public readonly List<Component[]> ComponentsOf = new List<Component[]>();
        public readonly List<Component> Components = new List<Component>();
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; }

        readonly Dictionary<int, string> _ids = new Dictionary<int, string>();
        readonly Dictionary<int, string> _paths = new Dictionary<int, string>();
        readonly Dictionary<int, int> _goIndex = new Dictionary<int, int>();

        ObjectTable(AnalysisTarget target, bool isPrefab, string sceneGuid, string assetGuid, string assetPath, string containerId, GameObject prefabRoot)
        {
            Target = target;
            IsPrefabAsset = isPrefab;
            SceneGuid = sceneGuid;
            AssetGuid = assetGuid;
            AssetPath = assetPath;
            ContainerId = containerId;
            PrefabRoot = prefabRoot;
        }

        public static ObjectTable Create(AnalysisTarget target)
        {
            if (target.Kind == AnalysisTargetKind.LoadedScene)
            {
                var scene = target.GetScene();
                var table = new ObjectTable(target, false, ObjectIdUtility.GetSceneGuid(scene), null, scene.path,
                    scene.IsValid() ? ObjectIdUtility.GetSceneNodeId(scene) : null, null);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    table.InvalidReason = "Scene is not loaded";
                    return table;
                }
                table.Collect(scene.GetRootGameObjects());
                return table;
            }

            if (target.Kind == AnalysisTargetKind.Asset && target.Path != null && target.Path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(target.Path);
                var table = new ObjectTable(target, true, null, target.Guid, target.Path, NodeIds.Asset(target.Guid), root);
                if (root == null)
                {
                    table.InvalidReason = "Prefab could not be loaded";
                    return table;
                }
                table.Collect(new[] { root });
                return table;
            }

            var empty = new ObjectTable(target, false, null, target.Guid, target.Path, null, null) { InvalidReason = "Target has no GameObject hierarchy" };
            return empty;
        }

        void Collect(GameObject[] roots)
        {
            var stack = new Stack<Transform>();
            for (int i = roots.Length - 1; i >= 0; i--) if (roots[i] != null) stack.Push(roots[i].transform);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                var go = t.gameObject;
                _goIndex[go.GetInstanceID()] = GameObjects.Count;
                GameObjects.Add(go);
                var comps = go.GetComponents<Component>();
                ComponentsOf.Add(comps);
                foreach (var c in comps) if (c != null) Components.Add(c);
                for (int i = t.childCount - 1; i >= 0; i--) stack.Push(t.GetChild(i));
            }

            var all = new Object[GameObjects.Count + Components.Count];
            int k = 0;
            foreach (var go in GameObjects) all[k++] = go;
            foreach (var c in Components) all[k++] = c;
            var ids = ObjectIdUtility.GetIds(all);
            for (int i = 0; i < all.Length; i++) _ids[all[i].GetInstanceID()] = ids[i];
            IsValid = true;
        }

        public bool Contains(Object obj) => obj != null && _ids.ContainsKey(obj.GetInstanceID());

        public int IndexOf(GameObject go) => go != null && _goIndex.TryGetValue(go.GetInstanceID(), out int i) ? i : -1;

        /// <summary>表内のオブジェクトはバッチ計算済みID、表外（他 Scene/Asset）は個別に計算してキャッシュする。</summary>
        public string IdOf(Object obj)
        {
            if (obj == null) return null;
            int iid = obj.GetInstanceID();
            if (_ids.TryGetValue(iid, out var id)) return id;
            id = ObjectIdUtility.GetId(obj);
            _ids[iid] = id;
            return id;
        }

        public string PathOf(GameObject go)
        {
            if (go == null) return null;
            int iid = go.GetInstanceID();
            if (_paths.TryGetValue(iid, out var p)) return p;
            var parent = go.transform.parent;
            p = parent == null ? go.name : PathOf(parent.gameObject) + "/" + go.name;
            _paths[iid] = p;
            return p;
        }

        public string MissingScriptId(GameObject go, int index)
            => NodeIds.Unresolved(IdOf(go), "missing-script#" + index);

        /// <summary>表内 GameObject のノード。</summary>
        public GraphNode CreateGameObjectNode(GameObject go, string providerId)
        {
            string id = IdOf(go);
            bool isPrefabRoot = IsPrefabAsset && go == PrefabRoot;
            var node = new GraphNode
            {
                Id = id,
                Kind = isPrefabRoot ? NodeKind.PrefabAsset : NodeKind.GameObject,
                Name = go.name,
                TypeName = isPrefabRoot ? PrefabUtility.GetPrefabAssetType(go).ToString() + " Prefab" : "GameObject",
                SceneGuid = SceneGuid,
                AssetGuid = AssetGuid,
                AssetPath = AssetPath,
                HierarchyPath = PathOf(go),
                ProviderId = providerId,
            };
            if (!go.activeSelf) node.Flags |= NodeFlags.Inactive;
            if (go.CompareTag("EditorOnly")) node.Flags |= NodeFlags.EditorOnly;
            if ((go.hideFlags & HideFlags.HideInHierarchy) != 0) node.Flags |= NodeFlags.Hidden;
            if (NodeIds.IsSession(id)) node.Flags |= NodeFlags.Transient;
            if (IsPrefabAsset) node.Flags |= NodeFlags.PrefabAssetContent;
            if (isPrefabRoot && PrefabUtility.GetPrefabAssetType(go) == PrefabAssetType.Variant) node.Flags |= NodeFlags.PrefabVariant;
            if (PrefabUtility.IsPartOfPrefabInstance(go)) node.Flags |= NodeFlags.PrefabInstance;
            if (PrefabUtility.IsAnyPrefabInstanceRoot(go)) node.Flags |= NodeFlags.PrefabInstanceRoot;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(go, out _, out long lid)) node.LocalFileId = lid;
            return node;
        }

        public GraphNode CreateComponentNode(Component c, string providerId)
        {
            string id = IdOf(c);
            var node = new GraphNode
            {
                Id = id,
                Kind = NodeKind.Component,
                Name = c.GetType().Name,
                TypeName = c.GetType().FullName,
                SceneGuid = SceneGuid,
                AssetGuid = AssetGuid,
                AssetPath = AssetPath,
                HierarchyPath = PathOf(c.gameObject) + "/" + c.GetType().Name,
                ProviderId = providerId,
            };
            if ((c.hideFlags & HideFlags.HideInInspector) != 0) node.Flags |= NodeFlags.Hidden;
            if (c is Behaviour b && !b.enabled) node.Flags |= NodeFlags.Inactive;
            if (NodeIds.IsSession(id)) node.Flags |= NodeFlags.Transient;
            if (IsPrefabAsset) node.Flags |= NodeFlags.PrefabAssetContent;
            if (PrefabUtility.IsPartOfPrefabInstance(c)) node.Flags |= NodeFlags.PrefabInstance;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(c, out _, out long lid)) node.LocalFileId = lid;
            return node;
        }

        /// <summary>任意の Object のノード。表内なら実体、表外なら仮ノードを返す。</summary>
        public GraphNode CreateNodeFor(Object obj, string providerId)
        {
            if (obj is GameObject go && Contains(go)) return CreateGameObjectNode(go, providerId);
            if (obj is Component c && Contains(c)) return CreateComponentNode(c, providerId);
            var ph = ObjectIdUtility.CreatePlaceholder(obj, IdOf(obj));
            ph.ProviderId = providerId;
            return ph;
        }
    }
}

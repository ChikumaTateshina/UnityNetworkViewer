using System.Collections.Generic;
using UnityNetworkViewer.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UnityNetworkViewer.Editor
{
    /// <summary>
    /// ロード済み Scene（全体またはチャンク）/ Prefab Asset 内の GameObject と Component を一度だけ列挙し、
    /// ID をバッチで計算して Provider 間で共有する。読み取りのみで Dirty にしない。
    /// Scene の場合は instanceId→ID のキャッシュを Scheduler と共有し、チャンクをまたぐ参照の ID 計算を省く。
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
        /// <summary>Scene の一部（チャンク）だけを列挙した表か。</summary>
        public bool IsChunk => Target != null && Target.Kind == AnalysisTargetKind.SceneChunk;
        public readonly List<GameObject> GameObjects = new List<GameObject>();
        /// <summary>GameObjects と同順。Missing Script の位置は null。</summary>
        public readonly List<Component[]> ComponentsOf = new List<Component[]>();
        public readonly List<Component> Components = new List<Component>();
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; }

        readonly Dictionary<int, string> _ids = new Dictionary<int, string>();
        // 表に列挙したオブジェクト（ID を計算しただけの表外のオブジェクトは含まない）
        readonly HashSet<int> _members = new HashSet<int>();
        Dictionary<int, string> _shared;
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

        public static ObjectTable Create(AnalysisTarget target) => Create(target, null);

        /// <param name="sharedIds">Scene 単位で共有する instanceId→ID キャッシュ（null 可）。</param>
        public static ObjectTable Create(AnalysisTarget target, Dictionary<int, string> sharedIds)
        {
            ObjectTable table = null;
            foreach (var step in CreateIncrementally(target, sharedIds)) if (step != null) table = step;
            return table;
        }

        internal static IEnumerable<ObjectTable> CreateIncrementally(AnalysisTarget target, Dictionary<int, string> sharedIds)
        {
            if (target.Kind == AnalysisTargetKind.LoadedScene || target.Kind == AnalysisTargetKind.SceneChunk)
            {
                var scene = target.GetScene();
                var table = new ObjectTable(target, false, ObjectIdUtility.GetSceneGuid(scene), null, scene.path,
                    scene.IsValid() ? ObjectIdUtility.GetSceneNodeId(scene) : null, null) { _shared = sharedIds };
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    table.InvalidReason = "Scene is not loaded";
                    yield return table;
                    yield break;
                }
                if (target.Kind == AnalysisTargetKind.LoadedScene)
                {
                    foreach (var step in table.Collect(scene.GetRootGameObjects(), false)) yield return null;
                    yield return table;
                    yield break;
                }
                var rootIds = target.ChunkRoots ?? new[] { target.ChunkRootInstanceId };
                var roots = new GameObject[rootIds.Length];
                for (int i = 0; i < rootIds.Length; i++)
                {
                    roots[i] = EditorUtility.InstanceIDToObject(rootIds[i]) as GameObject;
                    if (roots[i] == null || roots[i].scene != scene)
                    {
                        table.InvalidReason = "Chunk root no longer exists in the scene";
                        yield return table;
                        yield break;
                    }
                }
                foreach (var step in table.Collect(roots, target.ChunkIsSingle)) yield return null;
                yield return table;
                yield break;
            }

            if (target.Kind == AnalysisTargetKind.Asset && target.Path != null && target.Path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(target.Path);
                var table = new ObjectTable(target, true, null, target.Guid, target.Path, NodeIds.Asset(target.Guid), root);
                if (root == null)
                {
                    table.InvalidReason = "Prefab could not be loaded";
                    yield return table;
                    yield break;
                }
                foreach (var step in table.Collect(new[] { root }, false)) yield return null;
                yield return table;
                yield break;
            }

            var empty = new ObjectTable(target, false, null, target.Guid, target.Path, null, null) { InvalidReason = "Target has no GameObject hierarchy" };
            yield return empty;
        }

        IEnumerable<int> Collect(GameObject[] roots, bool rootsOnly)
        {
            var stack = new Stack<Transform>();
            for (int i = roots.Length - 1; i >= 0; i--) if (roots[i] != null) stack.Push(roots[i].transform);
            while (stack.Count > 0)
            {
                yield return 0;
                var t = stack.Pop();
                var go = t.gameObject;
                _goIndex[go.GetInstanceID()] = GameObjects.Count;
                GameObjects.Add(go);
                var comps = go.GetComponents<Component>();
                ComponentsOf.Add(comps);
                foreach (var c in comps) if (c != null) Components.Add(c);
                if (rootsOnly) continue;
                for (int i = t.childCount - 1; i >= 0; i--) stack.Push(t.GetChild(i));
            }

            // Bound GlobalObjectId batch size and temporary memory for huge prefabs.
            var batch = new List<Object>(128);
            foreach (var go in GameObjects)
            {
                _members.Add(go.GetInstanceID());
                batch.Add(go);
                if (batch.Count < 128) continue;
                PrimeIds(batch);
                batch.Clear();
                yield return 0;
            }
            foreach (var c in Components)
            {
                _members.Add(c.GetInstanceID());
                batch.Add(c);
                if (batch.Count < 128) continue;
                PrimeIds(batch);
                batch.Clear();
                yield return 0;
            }
            PrimeIds(batch);
            IsValid = true;
        }

        /// <summary>まだ ID の無いオブジェクトの ID をまとめて計算する（GlobalObjectId のバッチ API）。</summary>
        public void PrimeIds(IEnumerable<Object> objects)
        {
            List<Object> missing = null;
            HashSet<int> seen = null;
            foreach (var o in objects)
            {
                if (o == null) continue;
                int iid = o.GetInstanceID();
                if (_ids.ContainsKey(iid)) continue;
                if (_shared != null && _shared.TryGetValue(iid, out var cached))
                {
                    _ids[iid] = cached;
                    continue;
                }
                // 未保存の新しい Component（ID を知らないもの）は一時 ID にする（GlobalObjectId の割り当てを起こさない）
                if (o is Component comp && NewObjectRegistry.HasNewComponents(comp.gameObject))
                {
                    Remember(o, NodeIds.SessionObject(iid));
                    continue;
                }
                if (missing == null)
                {
                    missing = new List<Object>();
                    seen = new HashSet<int>();
                }
                if (seen.Add(iid)) missing.Add(o);
            }
            if (missing == null) return;
            var arr = missing.ToArray();
            var ids = ObjectIdUtility.GetIds(arr);
            for (int i = 0; i < arr.Length; i++) Remember(arr[i], ids[i]);
        }

        internal IEnumerable<int> PrimeIdsIncrementally(IEnumerable<Object> objects)
        {
            var batch = new List<Object>(128);
            foreach (var obj in objects)
            {
                batch.Add(obj);
                if (batch.Count < 128) continue;
                PrimeIds(batch);
                batch.Clear();
                yield return 0;
            }
            PrimeIds(batch);
        }

        void Remember(Object o, string id)
        {
            int iid = o.GetInstanceID();
            _ids[iid] = id;
            // Scene キャッシュには Scene 内オブジェクトの永続 ID のみ入れる（一時 ID は保存後に変わるため入れない）
            if (_shared != null && id != null && !NodeIds.IsSession(id) && !EditorUtility.IsPersistent(o)) _shared[iid] = id;
        }

        /// <summary>表に列挙したオブジェクトか（チャンクの場合はチャンク内）。表外の参照先は仮ノードとして扱う。</summary>
        public bool Contains(Object obj) => obj != null && _members.Contains(obj.GetInstanceID());

        public int IndexOf(GameObject go) => go != null && _goIndex.TryGetValue(go.GetInstanceID(), out int i) ? i : -1;

        /// <summary>表内のオブジェクトはバッチ計算済みID、表外（他 Scene/Asset）は個別に計算してキャッシュする。</summary>
        public string IdOf(Object obj)
        {
            if (obj == null) return null;
            int iid = obj.GetInstanceID();
            if (_ids.TryGetValue(iid, out var id)) return id;
            if (_shared != null && _shared.TryGetValue(iid, out id))
            {
                _ids[iid] = id;
                return id;
            }
            id = ObjectIdUtility.GetId(obj);
            Remember(obj, id);
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

            // 同じ Scene の表外（別チャンク）のオブジェクトは、Scene GUID と階層パスを表のキャッシュから作る軽量な仮ノードにする
            var owner = obj as GameObject ?? (obj as Component)?.gameObject;
            if (owner != null && Target != null && SceneGuid != null && !EditorUtility.IsPersistent(obj) && owner.scene.path == AssetPath)
            {
                string id = IdOf(obj);
                var node = new GraphNode
                {
                    Id = id,
                    Kind = obj is GameObject ? NodeKind.GameObject : NodeKind.Component,
                    Name = obj is Component comp ? comp.GetType().Name : owner.name,
                    TypeName = obj.GetType().Name,
                    SceneGuid = SceneGuid,
                    HierarchyPath = obj is Component ? PathOf(owner) + "/" + obj.GetType().Name : PathOf(owner),
                    Flags = NodeFlags.Placeholder,
                    ProviderId = providerId,
                };
                if (NodeIds.IsSession(id)) node.Flags |= NodeFlags.Transient;
                return node;
            }

            var ph = ObjectIdUtility.CreatePlaceholder(obj, IdOf(obj));
            ph.ProviderId = providerId;
            return ph;
        }
    }
}

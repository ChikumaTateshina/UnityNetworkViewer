using System;
using System.Collections.Generic;
using UnityNetworkViewer.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UnityNetworkViewer.Editor
{
    /// <summary>
    /// Unity Object とノードIDの相互変換。
    /// - 保存済み Scene 内: GlobalObjectId の targetObjectId/targetPrefabId から so1:
    /// - 永続 Asset: メインアセットは asset1:{guid}、サブオブジェクトは ao1:{guid}:{localId}
    /// - 未保存・Prefab Stage 等: session-scoped な ses1:{instanceId}
    /// GlobalObjectId は Scene 移動・再生成で変わり得るため絶対不変としては扱わない。
    /// </summary>
    /// <summary>
    /// 作成後まだ保存されていないオブジェクト。これらに GlobalObjectId を求めると Unity が Scene 全体の
    /// ファイル ID を割り当て直す（大きな Scene では数百 ms 以上）ため、保存されるまで一時 ID（ses1:）で扱う。
    /// 保存後に一時 ID を含むチャンクを再解析して永続 ID に置き換える。
    /// </summary>
    public static class NewObjectRegistry
    {
        sealed class PerScene
        {
            public readonly HashSet<int> GameObjects = new HashSet<int>();
            public readonly HashSet<int> ComponentHosts = new HashSet<int>();
        }

        static readonly Dictionary<int, PerScene> Scenes = new Dictionary<int, PerScene>();

        static PerScene For(Scene scene, bool create)
        {
            if (!scene.IsValid()) return null;
            if (!Scenes.TryGetValue(scene.handle, out var s) && create)
            {
                s = new PerScene();
                Scenes[scene.handle] = s;
            }
            return s;
        }

        /// <summary>作成された GameObject（子孫を含む）を登録する。</summary>
        public static void MarkCreated(GameObject root)
        {
            if (root == null) return;
            var s = For(root.scene, true);
            if (s == null) return;
            var stack = new Stack<Transform>();
            stack.Push(root.transform);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                s.GameObjects.Add(t.gameObject.GetInstanceID());
                for (int i = 0; i < t.childCount; i++) stack.Push(t.GetChild(i));
            }
        }

        /// <summary>Component が追加された GameObject を登録する（既知の Component 以外は一時 ID になる）。</summary>
        public static void MarkComponentsChanged(GameObject go)
        {
            if (go == null) return;
            For(go.scene, true)?.ComponentHosts.Add(go.GetInstanceID());
        }

        public static bool IsNew(GameObject go)
        {
            if (go == null || Scenes.Count == 0) return false;
            var s = For(go.scene, false);
            return s != null && s.GameObjects.Contains(go.GetInstanceID());
        }

        public static bool HasNewComponents(GameObject go)
        {
            if (go == null || Scenes.Count == 0) return false;
            var s = For(go.scene, false);
            return s != null && (s.ComponentHosts.Contains(go.GetInstanceID()) || s.GameObjects.Contains(go.GetInstanceID()));
        }

        /// <summary>Scene の保存・クローズ時。以後は永続 ID を使う。</summary>
        public static void ClearScene(Scene scene)
        {
            if (scene.IsValid()) Scenes.Remove(scene.handle);
        }

        public static void Clear() => Scenes.Clear();

        /// <summary>新規オブジェクト（またはその Component）か。</summary>
        public static bool IsNewObject(Object obj)
        {
            GameObject go = obj as GameObject ?? (obj as Component)?.gameObject;
            if (go == null) return false;
            // 未保存の Scene のオブジェクトは常に一時 ID（GlobalObjectId を求めない）
            if (string.IsNullOrEmpty(go.scene.path) && go.scene.IsValid() && !UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(go.scene)) return true;
            return Scenes.Count > 0 && IsNew(go);
        }
    }

    public static class ObjectIdUtility
    {
        public static string GetSceneGuid(Scene scene)
        {
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) return null;
            string guid = AssetDatabase.AssetPathToGUID(scene.path);
            return string.IsNullOrEmpty(guid) ? null : guid;
        }

        public static string GetSceneNodeId(Scene scene)
        {
            string guid = GetSceneGuid(scene);
            return guid != null ? NodeIds.Scene(guid) : NodeIds.UnsavedScene(scene.handle);
        }

        /// <summary>単一オブジェクトの ID（選択解決など少数向け。大量には <see cref="GetIds"/> を使う）。</summary>
        public static string GetId(Object obj)
        {
            if (obj == null) return null;
            if (obj is SceneAsset sceneAsset)
            {
                string path = AssetDatabase.GetAssetPath(sceneAsset);
                return NodeIds.Scene(AssetDatabase.AssetPathToGUID(path));
            }
            if (EditorUtility.IsPersistent(obj)) return GetAssetObjectId(obj);
            if (NewObjectRegistry.IsNewObject(obj)) return NodeIds.SessionObject(obj.GetInstanceID());
            var gid = GlobalObjectId.GetGlobalObjectIdSlow(obj);
            return FromGlobalId(obj, gid);
        }

        public static string GetAssetObjectId(Object obj)
        {
            if (obj == null) return null;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long localId) && !string.IsNullOrEmpty(guid))
            {
                if (AssetDatabase.IsMainAsset(obj)) return NodeIds.Asset(guid);
                return NodeIds.AssetObject(guid, localId);
            }
            return NodeIds.SessionObject(obj.GetInstanceID());
        }

        /// <summary>多数のシーンオブジェクトの ID を一括取得する（GlobalObjectId のバッチ API を使用）。</summary>
        public static string[] GetIds(Object[] objects)
        {
            var result = new string[objects.Length];
            var sceneIndices = new List<int>();
            for (int i = 0; i < objects.Length; i++)
            {
                var o = objects[i];
                if (o == null) continue;
                if (EditorUtility.IsPersistent(o)) result[i] = GetAssetObjectId(o);
                else if (NewObjectRegistry.IsNewObject(o)) result[i] = NodeIds.SessionObject(o.GetInstanceID());
                else sceneIndices.Add(i);
            }
            if (sceneIndices.Count > 0)
            {
                var objs = new Object[sceneIndices.Count];
                for (int k = 0; k < objs.Length; k++) objs[k] = objects[sceneIndices[k]];
                var gids = new GlobalObjectId[objs.Length];
                GlobalObjectId.GetGlobalObjectIdsSlow(objs, gids);
                for (int k = 0; k < objs.Length; k++) result[sceneIndices[k]] = FromGlobalId(objs[k], gids[k]);
            }
            return result;
        }

        static string FromGlobalId(Object obj, GlobalObjectId gid)
        {
            if (gid.identifierType == 2 && gid.targetObjectId != 0 && !IsInPreviewScene(obj))
            {
                string guid = gid.assetGUID.ToString();
                if (!string.IsNullOrEmpty(guid) && guid != "00000000000000000000000000000000")
                    return NodeIds.SceneObject(guid, unchecked((long)gid.targetObjectId), unchecked((long)gid.targetPrefabId));
            }
            return NodeIds.SessionObject(obj.GetInstanceID());
        }

        static bool IsInPreviewScene(Object obj)
        {
            Scene scene = default;
            if (obj is GameObject go) scene = go.scene;
            else if (obj is Component c) scene = c.gameObject.scene;
            return scene.IsValid() && EditorSceneManager.IsPreviewScene(scene);
        }

        /// <summary>Object をノード種別に分類する。VRChat 型は SDK 非導入時も扱えるよう型名で判定する。</summary>
        public static NodeKind Classify(Object obj)
        {
            switch (obj)
            {
                case null: return NodeKind.UnresolvedReference;
                case SceneAsset _: return NodeKind.Scene;
                case GameObject go:
                    return EditorUtility.IsPersistent(go) && AssetDatabase.IsMainAsset(go) &&
                           PrefabUtility.GetPrefabAssetType(go) != PrefabAssetType.NotAPrefab &&
                           PrefabUtility.GetPrefabAssetType(go) != PrefabAssetType.Model
                        ? NodeKind.PrefabAsset
                        : NodeKind.GameObject;
                case Component _: return NodeKind.Component;
                case MonoScript _: return NodeKind.ScriptAsset;
                case AnimationClip _: return NodeKind.AnimationClip;
                case RuntimeAnimatorController _: return NodeKind.AnimatorController;
            }
            string typeName = obj.GetType().Name;
            if (typeName == "UdonSharpProgramAsset" || typeName == "UdonGraphProgramAsset" ||
                typeName == "UdonAssemblyProgramAsset" || typeName == "SerializedUdonProgramAsset")
                return NodeKind.UdonProgram;
            return NodeKind.OtherAsset;
        }

        public static bool IsPackagePath(string path)
            => path != null && path.StartsWith("Packages/", StringComparison.Ordinal);

        /// <summary>参照先などとして使う仮ノード。所有 Provider が解析したら実体で置き換わる。</summary>
        public static GraphNode CreatePlaceholder(Object obj, string id)
        {
            var node = new GraphNode
            {
                Id = id,
                Kind = Classify(obj),
                Name = obj != null ? obj.name : id,
                TypeName = obj != null ? obj.GetType().Name : null,
                Flags = NodeFlags.Placeholder,
            };
            if (obj == null) return node;

            if (EditorUtility.IsPersistent(obj))
            {
                string path = AssetDatabase.GetAssetPath(obj);
                node.AssetPath = path;
                node.AssetGuid = AssetDatabase.AssetPathToGUID(path);
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out _, out long localId)) node.LocalFileId = localId;
                if (IsPackagePath(path)) node.Flags |= NodeFlags.ExternalPackage;
                if (!AssetDatabase.IsMainAsset(obj))
                {
                    var main = AssetDatabase.LoadMainAssetAtPath(path);
                    if (main != null && obj is Component comp) node.Name = comp.gameObject.name + " · " + comp.GetType().Name;
                    else if (main != null && main != obj && !(obj is GameObject)) node.HierarchyPath = main.name + "/" + obj.name;
                    node.Flags |= NodeFlags.PrefabAssetContent;
                }
            }
            else
            {
                GameObject go = obj as GameObject ?? (obj as Component)?.gameObject;
                if (go != null)
                {
                    node.SceneGuid = GetSceneGuid(go.scene);
                    node.HierarchyPath = GetHierarchyPath(go.transform);
                    if (obj is Component c) node.Name = c.GetType().Name;
                }
                if (NodeIds.IsSession(id)) node.Flags |= NodeFlags.Transient;
            }
            return node;
        }

        public static string GetHierarchyPath(Transform t)
        {
            if (t == null) return null;
            var parts = new List<string>();
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        public static string GetDependencyHash(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            var hash = AssetDatabase.GetAssetDependencyHash(assetPath);
            return hash.isValid ? hash.ToString() : null;
        }
    }
}

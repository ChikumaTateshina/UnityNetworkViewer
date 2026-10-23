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

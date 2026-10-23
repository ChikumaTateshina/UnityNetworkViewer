using System.Collections.Generic;
using System.Globalization;
using ObjectRelationshipExplorer.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ObjectRelationshipExplorer.Editor
{
    public enum ResolveStatus
    {
        Resolved,
        /// <summary>対象 Scene が未ロード。開くにはユーザーの明示操作が必要。</summary>
        SceneNotLoaded,
        /// <summary>削除済み・Missing・解析上の仮想ノードなど、Unity Object に対応しない。</summary>
        NotAnObject,
        NotFound,
    }

    /// <summary>ノードID → Unity Object の解決。Scene を勝手に開いたりロードしたりしない。</summary>
    public static class ObjectResolver
    {
        public static ResolveStatus TryResolve(string id, out Object obj)
        {
            obj = null;
            if (string.IsNullOrEmpty(id)) return ResolveStatus.NotFound;

            if (NodeIds.IsSession(id))
            {
                obj = EditorUtility.InstanceIDToObject(NodeIds.TryGetSessionInstanceId(id));
                return obj != null ? ResolveStatus.Resolved : ResolveStatus.NotFound;
            }

            if (id.StartsWith(NodeIds.ScenePrefix, System.StringComparison.Ordinal))
            {
                string path = AssetDatabase.GUIDToAssetPath(id.Substring(NodeIds.ScenePrefix.Length));
                obj = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
                return obj != null ? ResolveStatus.Resolved : ResolveStatus.NotFound;
            }

            if (NodeIds.TryParseSceneObject(id, out string sceneGuid, out long objectId, out long prefabId))
            {
                string scenePath = AssetDatabase.GUIDToAssetPath(sceneGuid);
                if (string.IsNullOrEmpty(scenePath)) return ResolveStatus.NotFound;
                var scene = SceneManager.GetSceneByPath(scenePath);
                if (!scene.IsValid() || !scene.isLoaded) return ResolveStatus.SceneNotLoaded;
                string gidText = string.Format(CultureInfo.InvariantCulture, "GlobalObjectId_V1-2-{0}-{1}-{2}",
                    sceneGuid, unchecked((ulong)objectId), unchecked((ulong)prefabId));
                if (!GlobalObjectId.TryParse(gidText, out var gid)) return ResolveStatus.NotFound;
                obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                return obj != null ? ResolveStatus.Resolved : ResolveStatus.NotFound;
            }

            if (NodeIds.IsAsset(id))
            {
                string path = AssetDatabase.GUIDToAssetPath(id.Substring(NodeIds.AssetPrefix.Length));
                obj = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadMainAssetAtPath(path);
                return obj != null ? ResolveStatus.Resolved : ResolveStatus.NotFound;
            }

            if (NodeIds.TryParseAssetObject(id, out string guid, out long localId))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) return ResolveStatus.NotFound;
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (o != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out _, out long lid) && lid == localId)
                    {
                        obj = o;
                        return ResolveStatus.Resolved;
                    }
                }
                return ResolveStatus.NotFound;
            }

            return ResolveStatus.NotAnObject;
        }

        public static Object Resolve(string id) => TryResolve(id, out var o) == ResolveStatus.Resolved ? o : null;

        public static List<Object> ResolveMany(IEnumerable<string> ids)
        {
            var list = new List<Object>();
            foreach (var id in ids)
            {
                var o = Resolve(id);
                if (o != null) list.Add(o);
            }
            return list;
        }

        /// <summary>ノードが属する Scene のパス（scene ノード自身・シーンオブジェクト）。</summary>
        public static string GetScenePath(GraphNode node)
        {
            string guid = node?.SceneGuid ?? NodeIds.TryGetSceneGuid(node?.Id);
            return string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
        }

        public static bool IsSceneLoaded(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath)) return false;
            var scene = SceneManager.GetSceneByPath(scenePath);
            return scene.IsValid() && scene.isLoaded;
        }
    }
}

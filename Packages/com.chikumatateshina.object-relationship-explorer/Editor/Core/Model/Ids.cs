using System;
using System.Globalization;
using System.Text;

namespace ObjectRelationshipExplorer.Core
{
    /// <summary>決定的な 64bit FNV-1a ハッシュ。プロセス・セッションを跨いで安定。</summary>
    public static class StableHash
    {
        const ulong OffsetBasis = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;

        public static ulong Begin() => OffsetBasis;

        public static ulong Add(ulong hash, string value)
        {
            if (value != null)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    hash ^= (byte)c;
                    hash *= Prime;
                    hash ^= (byte)(c >> 8);
                    hash *= Prime;
                }
            }
            // フィールド区切り
            hash ^= 0xFF;
            hash *= Prime;
            return hash;
        }

        public static ulong Add(ulong hash, long value)
        {
            for (int i = 0; i < 8; i++)
            {
                hash ^= (byte)(value >> (i * 8));
                hash *= Prime;
            }
            return hash;
        }

        public static string ToHex(ulong hash) => hash.ToString("x16", CultureInfo.InvariantCulture);

        public static string Compute(params string[] parts)
        {
            ulong h = Begin();
            foreach (var p in parts) h = Add(h, p);
            return ToHex(h);
        }
    }

    /// <summary>
    /// ノードIDの組み立て。表示名やパスは ID に使わない。
    /// 形式は "{種類}{スキーマ版}:{本体}"。スキーマ版を上げた場合キャッシュは無効化される。
    /// </summary>
    public static class NodeIds
    {
        public const int SchemaVersion = 1;

        public const string ScenePrefix = "scene1:";
        public const string UnsavedScenePrefix = "scenes1:";
        public const string SceneObjectPrefix = "so1:";
        public const string SessionObjectPrefix = "ses1:";
        public const string AssetPrefix = "asset1:";
        public const string AssetObjectPrefix = "ao1:";
        public const string MethodPrefix = "method1:";
        public const string FieldPrefix = "field1:";
        public const string UdonEventPrefix = "uevt1:";
        public const string UdonVariablePrefix = "uvar1:";
        public const string AnimationPathPrefix = "anim1:";
        public const string UnresolvedPrefix = "unres1:";
        public const string MissingPrefix = "missing1:";

        static string L(long v) => v.ToString(CultureInfo.InvariantCulture);

        public static string Scene(string sceneGuid) => ScenePrefix + sceneGuid;
        public static string UnsavedScene(int handle) => UnsavedScenePrefix + handle.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// 保存済み Scene 内のオブジェクト。GlobalObjectId の targetObjectId / targetPrefabId と同じ値を用いるため、
        /// ロード済み解析と未ロード（テキスト）解析で同一IDになる。
        /// </summary>
        public static string SceneObject(string sceneGuid, long objectId, long prefabInstanceId)
            => SceneObjectPrefix + sceneGuid + ":" + L(objectId) + ":" + L(prefabInstanceId);

        public static string SessionObject(int instanceId) => SessionObjectPrefix + instanceId.ToString(CultureInfo.InvariantCulture);
        public static string Asset(string guid) => AssetPrefix + guid;
        public static string AssetObject(string guid, long localId) => AssetObjectPrefix + guid + ":" + L(localId);
        public static string Method(string scriptGuid, string qualifiedName) => MethodPrefix + scriptGuid + ":" + qualifiedName;
        public static string Field(string scriptGuid, string qualifiedName) => FieldPrefix + scriptGuid + ":" + qualifiedName;
        public static string UdonEvent(string programGuid, string eventName) => UdonEventPrefix + programGuid + ":" + eventName;
        public static string UdonVariable(string programGuid, string name) => UdonVariablePrefix + programGuid + ":" + name;
        public static string AnimationPath(string ownerGuid, string path) => AnimationPathPrefix + ownerGuid + ":" + path;
        public static string Missing(string guid, long localId) => MissingPrefix + (guid ?? "") + ":" + L(localId);

        public static string Unresolved(string ownerNodeId, string detail)
            => UnresolvedPrefix + StableHash.Compute(ownerNodeId, detail);

        public static bool IsScene(string id) => id != null && (id.StartsWith(ScenePrefix, StringComparison.Ordinal) || id.StartsWith(UnsavedScenePrefix, StringComparison.Ordinal));
        public static bool IsSceneObject(string id) => id != null && id.StartsWith(SceneObjectPrefix, StringComparison.Ordinal);
        public static bool IsSession(string id) => id != null && id.StartsWith(SessionObjectPrefix, StringComparison.Ordinal);
        public static bool IsAsset(string id) => id != null && id.StartsWith(AssetPrefix, StringComparison.Ordinal);
        public static bool IsAssetObject(string id) => id != null && id.StartsWith(AssetObjectPrefix, StringComparison.Ordinal);

        public static bool TryParseSceneObject(string id, out string sceneGuid, out long objectId, out long prefabId)
        {
            sceneGuid = null;
            objectId = 0;
            prefabId = 0;
            if (!IsSceneObject(id)) return false;
            string[] parts = id.Substring(SceneObjectPrefix.Length).Split(':');
            if (parts.Length != 3) return false;
            sceneGuid = parts[0];
            return long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out objectId)
                   && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out prefabId);
        }

        public static bool TryParseAssetObject(string id, out string guid, out long localId)
        {
            guid = null;
            localId = 0;
            if (!IsAssetObject(id)) return false;
            string body = id.Substring(AssetObjectPrefix.Length);
            int sep = body.LastIndexOf(':');
            if (sep <= 0) return false;
            guid = body.Substring(0, sep);
            return long.TryParse(body.Substring(sep + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out localId);
        }

        public static string TryGetSceneGuid(string id)
        {
            if (id == null) return null;
            if (id.StartsWith(ScenePrefix, StringComparison.Ordinal)) return id.Substring(ScenePrefix.Length);
            if (TryParseSceneObject(id, out string guid, out _, out _)) return guid;
            return null;
        }

        public static string TryGetAssetGuid(string id)
        {
            if (id == null) return null;
            if (id.StartsWith(AssetPrefix, StringComparison.Ordinal)) return id.Substring(AssetPrefix.Length);
            if (TryParseAssetObject(id, out string guid, out _)) return guid;
            return null;
        }

        public static int TryGetSessionInstanceId(string id)
        {
            if (!IsSession(id)) return 0;
            int.TryParse(id.Substring(SessionObjectPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value);
            return value;
        }
    }

    public static class EdgeIds
    {
        /// <summary>source/target/kind/provider/propertyPath/member/qualifier の決定的ハッシュ。</summary>
        public static string Create(GraphEdge e)
        {
            ulong h = StableHash.Begin();
            h = StableHash.Add(h, e.SourceId);
            h = StableHash.Add(h, e.TargetId);
            h = StableHash.Add(h, ((int)e.Kind).ToString(CultureInfo.InvariantCulture));
            h = StableHash.Add(h, e.ProviderId);
            h = StableHash.Add(h, e.PropertyPath);
            h = StableHash.Add(h, e.MemberName);
            h = StableHash.Add(h, e.Qualifiers);
            if (e.SourceLine != 0) h = StableHash.Add(h, e.SourceLine);
            return "e" + StableHash.ToHex(h);
        }
    }

    public static class OwnerKeys
    {
        /// <summary>owner は「Provider × 解析単位」。解析単位ごとに結果を原子的に差し替える。</summary>
        public static string Create(string providerId, string targetKey) => providerId + "|" + targetKey;

        public static string GetProviderId(string ownerKey)
        {
            int i = ownerKey?.IndexOf('|') ?? -1;
            return i < 0 ? ownerKey : ownerKey.Substring(0, i);
        }

        public static string GetTargetKey(string ownerKey)
        {
            int i = ownerKey?.IndexOf('|') ?? -1;
            return i < 0 ? string.Empty : ownerKey.Substring(i + 1);
        }
    }

    internal static class StringUtil
    {
        public static bool ContainsIgnoreCase(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return true;
            if (string.IsNullOrEmpty(haystack)) return false;
            return CultureInfo.InvariantCulture.CompareInfo.IndexOf(haystack, needle, CompareOptions.IgnoreCase) >= 0;
        }

        public static string Join(char sep, params string[] parts)
        {
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (sb.Length > 0) sb.Append(sep);
                sb.Append(p);
            }
            return sb.ToString();
        }
    }
}

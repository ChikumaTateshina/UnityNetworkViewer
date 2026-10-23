using System;
using System.Collections.Generic;
using UnityNetworkViewer.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityNetworkViewer.Editor
{
    public enum AnalysisTargetKind
    {
        /// <summary>ロード済み Scene（Unity API で直接走査）。</summary>
        LoadedScene,
        /// <summary>未ロード Scene（テキスト索引）。</summary>
        UnloadedScene,
        /// <summary>Prefab / Controller / Clip / Script / Udon Program 等の Asset。</summary>
        Asset,
        /// <summary>
        /// ロード済み Scene の一部（GameObject の部分木、または単独の GameObject）。
        /// 大きな Scene を小さな単位に分けて時間予算内で解析し、変更のあったチャンクだけを再解析するために使う。
        /// </summary>
        SceneChunk,
    }

    /// <summary>
    /// 解析単位。キーは「scene:{guid}」「asset:{guid}」で、Provider と組み合わせて owner キーになる。
    /// チャンクのキーは「scene:{guid}#c{instanceId}」（部分木）/「scene:{guid}#s{instanceId}」（単独）。
    /// </summary>
    public sealed class AnalysisTarget : IEquatable<AnalysisTarget>
    {
        public AnalysisTargetKind Kind { get; private set; }
        public string Guid { get; private set; }
        public string Path { get; private set; }
        /// <summary>未保存 Scene のみ使用。</summary>
        public int SceneHandle { get; private set; }
        /// <summary>チャンクのキーとなる GameObject の instanceId（SceneChunk のみ。部分木の組では先頭の部分木）。</summary>
        public int ChunkRootInstanceId { get; private set; }
        /// <summary>true ならチャンクはルート GameObject 単独（子は別チャンク）。</summary>
        public bool ChunkIsSingle { get; private set; }
        /// <summary>チャンクに含まれる部分木のルート（兄弟の小さな部分木をまとめたもの）。null の場合は <see cref="ChunkRootInstanceId"/> のみ。</summary>
        public int[] ChunkRoots { get; private set; }

        /// <summary>Scene 単位のキー（チャンクの場合も所属 Scene のキー）。</summary>
        public string SceneKey => string.IsNullOrEmpty(Guid) ? "scene:h" + SceneHandle : "scene:" + Guid;

        public string Key
        {
            get
            {
                switch (Kind)
                {
                    case AnalysisTargetKind.LoadedScene:
                    case AnalysisTargetKind.UnloadedScene:
                        return SceneKey;
                    case AnalysisTargetKind.SceneChunk:
                        return ChunkKey(SceneKey, ChunkRootInstanceId, ChunkIsSingle);
                    default:
                        return "asset:" + Guid;
                }
            }
        }

        public static string ChunkKey(string sceneKey, int rootInstanceId, bool single)
            => sceneKey + (single ? "#s" : "#c") + rootInstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public bool IsScene => Kind != AnalysisTargetKind.Asset;

        public static AnalysisTarget ForSceneChunk(Scene scene, int rootInstanceId, bool single, int[] roots = null)
        {
            return new AnalysisTarget
            {
                Kind = AnalysisTargetKind.SceneChunk,
                Guid = ObjectIdUtility.GetSceneGuid(scene),
                Path = scene.path,
                SceneHandle = scene.handle,
                ChunkRootInstanceId = rootInstanceId,
                ChunkIsSingle = single,
                ChunkRoots = roots,
            };
        }

        public static AnalysisTarget ForLoadedScene(Scene scene)
        {
            return new AnalysisTarget
            {
                Kind = AnalysisTargetKind.LoadedScene,
                Guid = ObjectIdUtility.GetSceneGuid(scene),
                Path = scene.path,
                SceneHandle = scene.handle,
            };
        }

        public static AnalysisTarget ForUnloadedScene(string guid, string path)
            => new AnalysisTarget { Kind = AnalysisTargetKind.UnloadedScene, Guid = guid, Path = path };

        public static AnalysisTarget ForAsset(string guid, string path)
            => new AnalysisTarget { Kind = AnalysisTargetKind.Asset, Guid = guid, Path = path };

        /// <summary>現在の状態（ロード有無）に応じた Scene ターゲット。</summary>
        public static AnalysisTarget ForScenePath(string path)
        {
            var scene = SceneManager.GetSceneByPath(path);
            if (scene.IsValid() && scene.isLoaded) return ForLoadedScene(scene);
            return ForUnloadedScene(AssetDatabase.AssetPathToGUID(path), path);
        }

        /// <summary>owner の targetKey からターゲットを復元する（キャッシュ検証用）。</summary>
        public static AnalysisTarget FromKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            int hash = key.IndexOf('#');
            if (hash > 0 && key.StartsWith("scene:", StringComparison.Ordinal))
            {
                // チャンク: ルート GameObject が同じ Scene に残っていれば復元する
                if (hash + 2 > key.Length) return null;
                bool single = key[hash + 1] == 's';
                if (!int.TryParse(key.Substring(hash + 2), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int iid)) return null;
                var go = EditorUtility.InstanceIDToObject(iid) as GameObject;
                if (go == null || !go.scene.IsValid() || !go.scene.isLoaded) return null;
                var chunk = ForSceneChunk(go.scene, iid, single);
                return chunk.SceneKey == key.Substring(0, hash) ? chunk : null;
            }
            if (key.StartsWith("scene:h", StringComparison.Ordinal)) return null;
            if (key.StartsWith("scene:", StringComparison.Ordinal))
            {
                string guid = key.Substring(6);
                string path = AssetDatabase.GUIDToAssetPath(guid);
                return string.IsNullOrEmpty(path) ? null : ForScenePath(path);
            }
            if (key.StartsWith("asset:", StringComparison.Ordinal))
            {
                string guid = key.Substring(6);
                string path = AssetDatabase.GUIDToAssetPath(guid);
                return string.IsNullOrEmpty(path) ? null : ForAsset(guid, path);
            }
            return null;
        }

        public Scene GetScene()
        {
            if (Kind != AnalysisTargetKind.LoadedScene && Kind != AnalysisTargetKind.SceneChunk) return default;
            if (!string.IsNullOrEmpty(Path))
            {
                var s = SceneManager.GetSceneByPath(Path);
                if (s.IsValid()) return s;
            }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.handle == SceneHandle) return s;
            }
            return default;
        }

        public bool Equals(AnalysisTarget other) => other != null && other.Kind == Kind && other.Key == Key;
        public override bool Equals(object obj) => Equals(obj as AnalysisTarget);
        public override int GetHashCode() => Key.GetHashCode() ^ (int)Kind;
        public override string ToString() => $"{Kind} {Path} ({Key})";
    }

    /// <summary>
    /// 解析 Provider の契約。Provider は GraphStore を直接変更せず <see cref="AnalysisResult"/> を返す。
    /// 例外は Scheduler が捕捉し診断として記録する（他 Provider を停止させない）。
    /// パラメータなしコンストラクタを持つ非抽象クラスは自動登録される。
    /// </summary>
    public interface IAnalysisProvider
    {
        string Id { get; }
        /// <summary>出力形式を変えた場合に上げる。キャッシュ無効化に使用。</summary>
        int Version { get; }
        /// <summary>同一ターゲット内の実行順（小さいほど先）。</summary>
        int Order { get; }
        bool CanAnalyze(AnalysisTarget target);
        AnalysisResult Analyze(AnalysisTarget target, AnalysisContext context);
    }

    /// <summary>全体再解析時に Asset を発見するための AssetDatabase.FindAssets フィルタを提供する。</summary>
    public interface IAssetDiscoveryProvider
    {
        IEnumerable<string> AssetSearchFilters { get; }
    }

    /// <summary>
    /// 解析の前に、複数フレームに分けて準備（ファイルの読み込み・依存 Asset の構築など）が必要な Provider。
    /// Scheduler は <see cref="Prepare"/> が true を返すまで毎フレーム呼び、その後 Analyze を呼ぶ。
    /// Prepare は時間予算（<see cref="AnalysisContext.OutOfTime"/>）を見て早めに戻ること。
    /// Prepare を呼ばずに Analyze された場合も（同期的に）正しく動作すること。
    /// </summary>
    public interface IPreparedAnalysisProvider
    {
        bool Prepare(AnalysisTarget target, AnalysisContext context);
    }

    public sealed class AnalysisContext
    {
        public UnvSettings Settings { get; }
        public PrefabStructureCache PrefabStructures { get; }
        readonly Dictionary<string, ObjectTable> _tables = new Dictionary<string, ObjectTable>();
        readonly Func<string, Dictionary<int, string>> _sceneIdCache;
        readonly Func<bool> _outOfTime;

        public AnalysisContext(UnvSettings settings, PrefabStructureCache prefabStructures)
            : this(settings, prefabStructures, null, null)
        {
        }

        /// <param name="sceneIdCache">Scene キー → instanceId→ノードID のキャッシュ（チャンク間で共有）。</param>
        /// <param name="outOfTime">フレームの時間予算を使い切ったか。</param>
        public AnalysisContext(UnvSettings settings, PrefabStructureCache prefabStructures,
            Func<string, Dictionary<int, string>> sceneIdCache, Func<bool> outOfTime)
        {
            Settings = settings;
            PrefabStructures = prefabStructures;
            _sceneIdCache = sceneIdCache;
            _outOfTime = outOfTime;
        }

        /// <summary>このフレームの時間予算を使い切ったか（予算なしの同期実行では常に false）。</summary>
        public bool OutOfTime => _outOfTime != null && _outOfTime();

        /// <summary>ロード済み Scene / チャンク / Prefab Asset のオブジェクト表。同一ターゲットの Provider 間で共有する。</summary>
        public ObjectTable GetTable(AnalysisTarget target)
        {
            if (_tables.TryGetValue(target.Key, out var table)) return table;
            var cache = target.Kind == AnalysisTargetKind.SceneChunk || target.Kind == AnalysisTargetKind.LoadedScene
                ? _sceneIdCache?.Invoke(target.SceneKey)
                : null;
            table = ObjectTable.Create(target, cache);
            _tables[target.Key] = table;
            return table;
        }

        public void ReleaseTable(AnalysisTarget target)
        {
            _tables.Remove(target.Key);
        }
    }

    public static class ProviderIds
    {
        public const string Hierarchy = "unity.hierarchy";
        public const string Serialized = "unity.serialized";
        public const string Prefab = "unity.prefab";
        public const string UnloadedScene = "unity.unloadedScene";
        public const string Animation = "unity.animation";
        public const string AnimatorBinding = "unity.animatorBinding";
        public const string ScriptableAsset = "unity.scriptableAsset";
    }

    public static class ProviderRegistry
    {
        static List<IAnalysisProvider> _providers;

        public static IReadOnlyList<IAnalysisProvider> Providers
        {
            get
            {
                if (_providers == null) Discover();
                return _providers;
            }
        }

        public static IAnalysisProvider Get(string id)
        {
            foreach (var p in Providers) if (p.Id == id) return p;
            return null;
        }

        public static void Discover()
        {
            _providers = new List<IAnalysisProvider>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<IAnalysisProvider>())
            {
                if (type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) == null) continue;
                try
                {
                    _providers.Add((IAnalysisProvider)Activator.CreateInstance(type));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UnityNetworkViewer] Provider {type.FullName} を初期化できません: {ex.Message}");
                }
            }
            _providers.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Id, b.Id));
        }

        public static string DescribeVersions()
        {
            var parts = new List<string>();
            foreach (var p in Providers) parts.Add(p.Id + "@" + p.Version);
            return string.Join(",", parts);
        }

        public static IEnumerable<string> GetAssetSearchFilters()
        {
            var set = new HashSet<string>();
            foreach (var p in Providers)
                if (p is IAssetDiscoveryProvider d)
                    foreach (var f in d.AssetSearchFilters)
                        if (!string.IsNullOrEmpty(f)) set.Add(f);
            return set;
        }
    }
}

using System;
using System.Collections.Generic;
using ObjectRelationshipExplorer.Core;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace ObjectRelationshipExplorer.Editor
{
    /// <summary>
    /// 索引サービス。ウィンドウ（またはオーバーレイ）が使用している間だけ稼働する（参照カウント）。
    /// Domain Reload 前にキャッシュを保存して購読を解除し、再読み込み後に重複購読しない。
    /// </summary>
    public sealed class OreService
    {
        static OreService _instance;
        static int _refCount;

        public static OreService Instance => _instance;

        public GraphStore Store { get; }
        public GraphQueryService Query { get; }
        public AnalysisScheduler Scheduler { get; }
        public ChangeTracker Tracker { get; }

        readonly List<string> _log = new List<string>();
        double _saveRequestedAt = -1;
        bool _initialized;

        public event Action<string> LogAdded;
        public IReadOnlyList<string> LogLines => _log;

        OreService()
        {
            Store = new GraphStore();
            Query = new GraphQueryService(Store);
            Scheduler = new AnalysisScheduler(Store);
            Tracker = new ChangeTracker(this);
        }

        public static OreService Acquire()
        {
            if (_instance == null)
            {
                _instance = new OreService();
                _instance.Initialize();
            }
            _refCount++;
            return _instance;
        }

        public static void Release()
        {
            if (_instance == null) return;
            _refCount = Math.Max(0, _refCount - 1);
            if (_refCount > 0) return;
            _instance.Shutdown(true);
            _instance = null;
        }

        void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            ProviderRegistry.Discover();
            Scheduler.Log += AddLog;
            Scheduler.StatusChanged += OnSchedulerStatus;
            Scheduler.Start();
            Tracker.Subscribe();
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnQuitting;
            EditorApplication.update += OnUpdate;

            var settings = OreSettings.instance;
            HashSet<string> restored = null;
            if (settings.useCache)
            {
                var report = GraphCacheStorage.Load(Store);
                AddLog(report.Message);
                restored = report.RestoredTargetKeys;
                foreach (var t in report.StaleTargets) Scheduler.Enqueue(t, AnalysisPriority.Normal);
            }
            IndexLoadedScenes();
            if (settings.indexProjectOnOpen) EnqueueProjectDiscovery(restored, AnalysisPriority.Low);
        }

        void Shutdown(bool save)
        {
            if (!_initialized) return;
            _initialized = false;
            if (save && OreSettings.instance.useCache) GraphCacheStorage.Save(Store);
            Scheduler.Stop();
            Tracker.Unsubscribe();
            Scheduler.Log -= AddLog;
            Scheduler.StatusChanged -= OnSchedulerStatus;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            EditorApplication.quitting -= OnQuitting;
            EditorApplication.update -= OnUpdate;
        }

        void OnBeforeAssemblyReload()
        {
            // 再読み込み後はウィンドウの OnEnable から再取得される
            Shutdown(true);
            _instance = null;
            _refCount = 0;
        }

        void OnQuitting()
        {
            if (OreSettings.instance.useCache) GraphCacheStorage.Save(Store);
        }

        void OnSchedulerStatus(SchedulerStatus status)
        {
            if (!status.Running && OreSettings.instance.useCache) _saveRequestedAt = EditorApplication.timeSinceStartup;
        }

        void OnUpdate()
        {
            // 索引が落ち着いてから少し待ってキャッシュを保存する
            if (_saveRequestedAt > 0 && Scheduler.IsIdle && EditorApplication.timeSinceStartup - _saveRequestedAt > 5.0)
            {
                _saveRequestedAt = -1;
                GraphCacheStorage.Save(Store);
            }
        }

        public void AddLog(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            _log.Add(line);
            if (_log.Count > 500) _log.RemoveRange(0, _log.Count - 500);
            LogAdded?.Invoke(line);
        }

        public void IndexLoadedScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded) Scheduler.Enqueue(AnalysisTarget.ForLoadedScene(s), AnalysisPriority.High);
            }
        }

        /// <summary>全体再解析: 索引とキャッシュを破棄して作り直す。</summary>
        public void RebuildAll()
        {
            Scheduler.Cancel();
            EditorApplication.delayCall += () =>
            {
                Store.Clear();
                Scheduler.PrefabStructures.Clear();
                GraphCacheStorage.Delete();
                ProviderRegistry.Discover();
                AddLog("全体再解析を開始します");
                IndexLoadedScenes();
                EnqueueProjectDiscovery(null, AnalysisPriority.Low);
            };
        }

        /// <summary>
        /// プロジェクト内の Scene / Prefab / Controller / Clip / Udon Program 等を発見して投入する。
        /// skipKeys に含まれるターゲット（キャッシュから復元済み）は投入しない。
        /// </summary>
        public int EnqueueProjectDiscovery(HashSet<string> skipKeys, AnalysisPriority priority)
        {
            string[] folders = OreSettings.instance.includePackages ? null : new[] { "Assets" };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            foreach (var filter in ProviderRegistry.GetAssetSearchFilters())
            {
                string[] guids = folders == null ? AssetDatabase.FindAssets(filter) : AssetDatabase.FindAssets(filter, folders);
                foreach (var guid in guids)
                {
                    if (!seen.Add(guid)) continue;
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) continue;
                    AnalysisTarget target;
                    if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    {
                        target = AnalysisTarget.ForScenePath(path);
                        if (target.Kind == AnalysisTargetKind.LoadedScene) continue;
                    }
                    else
                    {
                        target = AnalysisTarget.ForAsset(guid, path);
                    }
                    if (skipKeys != null && skipKeys.Contains(target.Key)) continue;
                    if (!AnyProviderCanAnalyze(target)) continue;
                    Scheduler.Enqueue(target, priority);
                    count++;
                }
            }
            if (count > 0) AddLog($"{count} 件の Asset/Scene を索引キューに追加しました");
            return count;
        }

        static bool AnyProviderCanAnalyze(AnalysisTarget target)
        {
            foreach (var p in ProviderRegistry.Providers)
            {
                try
                {
                    if (p.CanAnalyze(target)) return true;
                }
                catch (Exception)
                {
                    // Provider のエラーは解析時に報告する
                }
            }
            return false;
        }

        /// <summary>ノードIDが索引に無い場合、その所属 Scene / Asset を優先解析する。</summary>
        public bool RequestPriorityScan(string nodeId)
        {
            string sceneGuid = NodeIds.TryGetSceneGuid(nodeId);
            if (sceneGuid != null)
            {
                string path = AssetDatabase.GUIDToAssetPath(sceneGuid);
                if (string.IsNullOrEmpty(path)) return false;
                Scheduler.Enqueue(AnalysisTarget.ForScenePath(path), AnalysisPriority.High);
                return true;
            }
            string assetGuid = NodeIds.TryGetAssetGuid(nodeId);
            if (assetGuid != null)
            {
                string path = AssetDatabase.GUIDToAssetPath(assetGuid);
                if (string.IsNullOrEmpty(path)) return false;
                var t = path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? AnalysisTarget.ForScenePath(path) : AnalysisTarget.ForAsset(assetGuid, path);
                if (!AnyProviderCanAnalyze(t)) return false;
                Scheduler.Enqueue(t, AnalysisPriority.High);
                return true;
            }
            if (NodeIds.IsSession(nodeId))
            {
                IndexLoadedScenes();
                return true;
            }
            return false;
        }
    }
}

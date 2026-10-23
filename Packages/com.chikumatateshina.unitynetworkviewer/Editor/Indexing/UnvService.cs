using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityNetworkViewer.Core;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace UnityNetworkViewer.Editor
{
    /// <summary>
    /// 索引サービス。ウィンドウ（またはオーバーレイ）が使用している間だけ稼働する（参照カウント）。
    /// Domain Reload 前にキャッシュを保存して購読を解除し、再読み込み後に重複購読しない。
    /// 重い処理（キャッシュの読み書き・プロジェクト探索）はワーカースレッドか、フレームごとの時間予算内で行う。
    /// </summary>
    public sealed class UnvService
    {
        static UnvService _instance;
        static int _refCount;

        public static UnvService Instance => _instance;

        public GraphStore Store { get; }
        public GraphQueryService Query { get; }
        public AnalysisScheduler Scheduler { get; }
        public ChangeTracker Tracker { get; }

        readonly List<string> _log = new List<string>();
        readonly Stopwatch _frame = new Stopwatch();
        double _saveRequestedAt = -1;
        long _savedPersistentRevision;
        bool _initialized;
        GraphCacheStorage.LoadJob _cacheLoad;
        GraphCacheStorage.SaveJob _cacheSave;
        DiscoveryJob _discovery;
        bool _discoverAfterRestore;

        const long UpdateBudgetMs = 6;

        public event Action<string> LogAdded;
        public IReadOnlyList<string> LogLines => _log;

        /// <summary>キャッシュ復元・プロジェクト探索を含めて処理中か。</summary>
        public bool IsBusy => _cacheLoad != null || _discovery != null || !Scheduler.IsIdle;

        UnvService()
        {
            Store = new GraphStore();
            Query = new GraphQueryService(Store);
            Scheduler = new AnalysisScheduler(Store);
            Tracker = new ChangeTracker(this);
        }

        public static UnvService Acquire()
        {
            if (_instance == null)
            {
                _instance = new UnvService();
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

            var settings = UnvSettings.instance;
            // 開いている Scene を最優先で（チャンクに分けて）解析する
            IndexLoadedScenes();
            _discoverAfterRestore = settings.indexProjectOnOpen;
            if (settings.useCache)
            {
                // ファイルの読み込みと復号はワーカースレッド、ストアへの復元は OnUpdate で少しずつ行う
                _cacheLoad = GraphCacheStorage.BeginLoad();
            }
            else if (_discoverAfterRestore)
            {
                EnqueueProjectDiscovery(null, AnalysisPriority.Low);
            }
        }

        void Shutdown(bool save)
        {
            if (!_initialized) return;
            _initialized = false;
            if (save && UnvSettings.instance.useCache) SaveNow();
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
            if (UnvSettings.instance.useCache) SaveNow();
        }

        /// <summary>未保存の変更があれば同期的に保存する（終了・Domain Reload 時）。</summary>
        void SaveNow()
        {
            _cacheSave?.Wait();
            _cacheSave = null;
            // 復元途中の場合は不完全な索引で上書きしない
            if (_cacheLoad != null) return;
            if (Store.PersistentRevision == _savedPersistentRevision) return;
            if (GraphCacheStorage.Save(Store)) _savedPersistentRevision = Store.PersistentRevision;
        }

        void OnSchedulerStatus(SchedulerStatus status)
        {
            if (!status.Running && UnvSettings.instance.useCache) _saveRequestedAt = EditorApplication.timeSinceStartup;
        }

        void OnUpdate()
        {
            _frame.Restart();

            if (_cacheLoad != null) UpdateCacheRestore();
            if (_discovery != null && _cacheLoad == null) UpdateDiscovery();

            if (_cacheSave != null && _cacheSave.IsCompleted)
            {
                if (_cacheSave.Error != null) AddLog("キャッシュを保存できません: " + _cacheSave.Error.Message);
                _cacheSave = null;
            }

            // 索引が落ち着いてから少し待ち、永続対象が変わっていればワーカースレッドで保存する
            if (_saveRequestedAt > 0 && !IsBusy && _cacheSave == null && EditorApplication.timeSinceStartup - _saveRequestedAt > 5.0)
            {
                _saveRequestedAt = -1;
                if (UnvSettings.instance.useCache && Store.PersistentRevision != _savedPersistentRevision)
                {
                    _savedPersistentRevision = Store.PersistentRevision;
                    _cacheSave = GraphCacheStorage.BeginSave(Store);
                }
            }
        }

        void UpdateCacheRestore()
        {
            if (!_cacheLoad.Step(Store, Scheduler, () => _frame.ElapsedMilliseconds >= UpdateBudgetMs)) return;
            var report = _cacheLoad.Report;
            _cacheLoad = null;
            AddLog(report.Message);
            _savedPersistentRevision = Store.PersistentRevision;
            foreach (var t in report.StaleTargets) Scheduler.Enqueue(t, AnalysisPriority.Normal);
            if (_discoverAfterRestore) EnqueueProjectDiscovery(report.RestoredTargetKeys, AnalysisPriority.Low);
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
                if (s.isLoaded) Scheduler.RequestScene(s, true, AnalysisPriority.High);
            }
        }

        /// <summary>全体再解析: 索引とキャッシュを破棄して作り直す。</summary>
        public void RebuildAll()
        {
            Scheduler.Cancel();
            EditorApplication.delayCall += () =>
            {
                _cacheSave?.Wait();
                _cacheSave = null;
                _cacheLoad = null;
                _discovery = null;
                Store.Clear();
                Scheduler.PrefabStructures.Clear();
                GraphCacheStorage.Delete();
                ProviderRegistry.Discover();
                AddLog("全体再解析を開始します");
                IndexLoadedScenes();
                EnqueueProjectDiscovery(null, AnalysisPriority.Low);
            };
        }

        // ------------------------------------------------------------------ Project discovery

        sealed class DiscoveryJob
        {
            public readonly List<string> Guids = new List<string>();
            public HashSet<string> SkipKeys;
            public AnalysisPriority Priority;
            public int Next;
            public int Count;
        }

        /// <summary>
        /// プロジェクト内の Scene / Prefab / Controller / Clip / Udon Program 等を発見して投入する。
        /// skipKeys に含まれるターゲット（キャッシュから復元済み）は投入しない。候補の判定は複数フレームに分けて行う。
        /// </summary>
        public void EnqueueProjectDiscovery(HashSet<string> skipKeys, AnalysisPriority priority)
        {
            string[] folders = UnvSettings.instance.includePackages ? null : new[] { "Assets" };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var job = new DiscoveryJob { SkipKeys = skipKeys, Priority = priority };
            foreach (var filter in ProviderRegistry.GetAssetSearchFilters())
            {
                string[] guids = folders == null ? AssetDatabase.FindAssets(filter) : AssetDatabase.FindAssets(filter, folders);
                foreach (var guid in guids)
                    if (seen.Add(guid)) job.Guids.Add(guid);
            }
            _discovery = job;
        }

        void UpdateDiscovery()
        {
            var job = _discovery;
            while (job.Next < job.Guids.Count && _frame.ElapsedMilliseconds < UpdateBudgetMs)
            {
                string guid = job.Guids[job.Next++];
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
                if (job.SkipKeys != null && job.SkipKeys.Contains(target.Key)) continue;
                if (!AnyProviderCanAnalyze(target)) continue;
                Scheduler.Enqueue(target, job.Priority);
                job.Count++;
            }
            if (job.Next < job.Guids.Count) return;
            _discovery = null;
            if (job.Count > 0) AddLog($"{job.Count} 件の Asset/Scene を索引キューに追加しました");
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

        /// <summary>
        /// ノードIDが索引に無い場合、その所属 Scene / Asset を優先解析する。
        /// すでに索引済み（または解析待ち）の Scene / Asset は再解析しない（選択のたびに重い解析を起こさない）。
        /// </summary>
        public bool RequestPriorityScan(string nodeId)
        {
            string sceneGuid = NodeIds.TryGetSceneGuid(nodeId);
            if (sceneGuid != null)
            {
                string path = AssetDatabase.GUIDToAssetPath(sceneGuid);
                if (string.IsNullOrEmpty(path)) return false;
                var scene = SceneManager.GetSceneByPath(path);
                if (scene.IsValid() && scene.isLoaded)
                {
                    if (Scheduler.IsSceneKnown("scene:" + sceneGuid)) return false;
                    Scheduler.RequestScene(scene, false, AnalysisPriority.High);
                    return true;
                }
                if (Store.HasOwnersForTarget("scene:" + sceneGuid)) return false;
                Scheduler.Enqueue(AnalysisTarget.ForUnloadedScene(sceneGuid, path), AnalysisPriority.High);
                return true;
            }
            string assetGuid = NodeIds.TryGetAssetGuid(nodeId);
            if (assetGuid != null)
            {
                string path = AssetDatabase.GUIDToAssetPath(assetGuid);
                if (string.IsNullOrEmpty(path)) return false;
                var t = path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? AnalysisTarget.ForScenePath(path) : AnalysisTarget.ForAsset(assetGuid, path);
                if (Store.HasOwnersForTarget(t.Key) || !AnyProviderCanAnalyze(t)) return false;
                Scheduler.Enqueue(t, AnalysisPriority.High);
                return true;
            }
            if (NodeIds.IsSession(nodeId))
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    if (s.isLoaded && !Scheduler.IsSceneKnown(AnalysisScheduler.SceneKeyOf(s))) Scheduler.RequestScene(s, false, AnalysisPriority.High);
                }
                return true;
            }
            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using ObjectRelationshipExplorer.Core;
using UnityEditor;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace ObjectRelationshipExplorer.Editor
{
    public enum AnalysisPriority
    {
        /// <summary>選択中オブジェクトの未索引 Scene など、即座に必要なもの。</summary>
        High = 0,
        /// <summary>差分更新。</summary>
        Normal = 1,
        /// <summary>全体再解析のバックグラウンド処理。</summary>
        Low = 2,
    }

    public sealed class SchedulerStatus
    {
        public bool Running;
        public int Pending;
        public int Completed;
        public int Total;
        public string CurrentTarget;
        public int ErrorCount;
        public double ElapsedSeconds;
        public string LastMessage;
    }

    /// <summary>
    /// 解析キュー。Unity API はメインスレッドで、フレームあたりの時間予算内で処理する。
    /// Provider の結果は owner（Provider × ターゲット）ごとに GraphStore へ原子的にコミットする。
    /// </summary>
    public sealed class AnalysisScheduler
    {
        readonly GraphStore _store;
        readonly PrefabStructureCache _prefabCache = new PrefabStructureCache();
        readonly LinkedList<AnalysisTarget>[] _queues = { new LinkedList<AnalysisTarget>(), new LinkedList<AnalysisTarget>(), new LinkedList<AnalysisTarget>() };
        readonly Dictionary<string, (AnalysisPriority priority, LinkedListNode<AnalysisTarget> node)> _queued = new Dictionary<string, (AnalysisPriority, LinkedListNode<AnalysisTarget>)>();
        readonly SchedulerStatus _status = new SchedulerStatus();
        readonly Stopwatch _batchWatch = new Stopwatch();
        int _progressId = -1;
        bool _subscribed;
        bool _cancelRequested;

        public event Action<SchedulerStatus> StatusChanged;
        public event Action<string> Log;
        /// <summary>ターゲットの解析結果がコミットされたとき（キー）。</summary>
        public event Action<string> TargetCommitted;

        public AnalysisScheduler(GraphStore store)
        {
            _store = store;
        }

        public SchedulerStatus Status => _status;
        public PrefabStructureCache PrefabStructures => _prefabCache;
        public bool IsIdle => _queued.Count == 0;

        public void Start()
        {
            if (_subscribed) return;
            EditorApplication.update += Tick;
            _subscribed = true;
        }

        public void Stop()
        {
            if (!_subscribed) return;
            EditorApplication.update -= Tick;
            _subscribed = false;
            FinishProgress(Progress.Status.Canceled);
        }

        public bool IsQueued(string key) => _queued.ContainsKey(key);

        public void Enqueue(AnalysisTarget target, AnalysisPriority priority)
        {
            if (target == null) return;
            string key = target.Kind + "|" + target.Key;
            if (_queued.TryGetValue(key, out var existing))
            {
                if (existing.priority <= priority) return;
                _queues[(int)existing.priority].Remove(existing.node);
                _queued.Remove(key);
            }
            var node = _queues[(int)priority].AddLast(target);
            _queued[key] = (priority, node);
            _status.Total++;
            _status.Pending = _queued.Count;
            if (!_status.Running)
            {
                _status.Running = true;
                _status.Completed = 0;
                _status.Total = _queued.Count;
                _status.ErrorCount = 0;
                _batchWatch.Restart();
            }
        }

        public void Cancel()
        {
            _cancelRequested = true;
        }

        void Tick()
        {
            if (_cancelRequested)
            {
                _cancelRequested = false;
                int dropped = _queued.Count;
                foreach (var q in _queues) q.Clear();
                _queued.Clear();
                _status.Running = false;
                _status.Pending = 0;
                _status.LastMessage = $"解析をキャンセルしました（未処理 {dropped} 件）";
                Log?.Invoke(_status.LastMessage);
                FinishProgress(Progress.Status.Canceled);
                StatusChanged?.Invoke(_status);
                return;
            }
            if (_queued.Count == 0) return;
            // Play Mode 中は実行時オブジェクトを索引しない
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            var settings = OreSettings.instance;
            var budget = Stopwatch.StartNew();
            var context = new AnalysisContext(settings, _prefabCache);
            int processed = 0;
            _store.BeginBatch();
            try
            {
                while (_queued.Count > 0 && (processed == 0 || budget.ElapsedMilliseconds < settings.frameBudgetMilliseconds))
                {
                    var target = Dequeue();
                    if (target == null) break;
                    _status.CurrentTarget = target.Path;
                    ProcessTarget(target, context);
                    context.ReleaseTable(target);
                    processed++;
                    _status.Completed++;
                }
            }
            finally
            {
                _store.EndBatch();
            }

            _status.Pending = _queued.Count;
            _status.ElapsedSeconds = _batchWatch.Elapsed.TotalSeconds;
            ReportProgress();
            if (_queued.Count == 0)
            {
                _status.Running = false;
                _status.CurrentTarget = null;
                _status.LastMessage = $"索引完了: {_status.Completed} 件 / {_status.ElapsedSeconds:0.0} 秒 / エラー {_status.ErrorCount} 件 (ノード {_store.NodeCount:N0}, エッジ {_store.EdgeCount:N0})";
                Log?.Invoke(_status.LastMessage);
                FinishProgress(_status.ErrorCount > 0 ? Progress.Status.Failed : Progress.Status.Succeeded);
            }
            StatusChanged?.Invoke(_status);
        }

        AnalysisTarget Dequeue()
        {
            foreach (var q in _queues)
            {
                if (q.Count == 0) continue;
                var t = q.First.Value;
                q.RemoveFirst();
                _queued.Remove(t.Kind + "|" + t.Key);
                return t;
            }
            return null;
        }

        /// <summary>1 ターゲットに対して適用可能な全 Provider を実行し、owner ごとにコミットする。</summary>
        void ProcessTarget(AnalysisTarget target, AnalysisContext context)
        {
            // 状態が変わっている可能性があるため現在の状態で解決し直す
            if (target.IsScene && !string.IsNullOrEmpty(target.Path))
            {
                var scene = SceneManager.GetSceneByPath(target.Path);
                bool loaded = scene.IsValid() && scene.isLoaded;
                if (target.Kind == AnalysisTargetKind.UnloadedScene && loaded) target = AnalysisTarget.ForLoadedScene(scene);
                else if (target.Kind == AnalysisTargetKind.LoadedScene && !loaded && !string.IsNullOrEmpty(target.Guid))
                    target = AnalysisTarget.ForUnloadedScene(target.Guid, target.Path);
            }
            if (target.Kind != AnalysisTargetKind.LoadedScene && string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(target.Guid)))
            {
                RemoveTarget(target.Key);
                return;
            }

            string depHash = target.Kind == AnalysisTargetKind.LoadedScene ? null : ObjectIdUtility.GetDependencyHash(target.Path);
            var applicable = new HashSet<string>();
            foreach (var provider in ProviderRegistry.Providers)
            {
                bool can;
                try
                {
                    can = provider.CanAnalyze(target);
                }
                catch (Exception ex)
                {
                    ReportError(provider, target, ex);
                    continue;
                }
                if (!can) continue;
                string ownerKey = OwnerKeys.Create(provider.Id, target.Key);
                applicable.Add(ownerKey);

                AnalysisResult result;
                try
                {
                    result = provider.Analyze(target, context) ?? new AnalysisResult(provider.Id);
                }
                catch (Exception ex)
                {
                    ReportError(provider, target, ex);
                    // 失敗した Provider の古い結果は残さず、診断のみ記録する（障害分離）
                    result = new AnalysisResult(provider.Id);
                    result.AddDiagnostic(DiagnosticSeverity.Error, DiagnosticCodes.ProviderError, $"{provider.Id}: {ex.GetType().Name}: {ex.Message}", null, target.Path);
                }

                long now = DateTime.UtcNow.Ticks;
                foreach (var e in result.Edges) if (e.AnalyzedAtUtcTicks == 0) e.AnalyzedAtUtcTicks = now;
                bool persistent = target.Kind != AnalysisTargetKind.LoadedScene && depHash != null;
                _store.ReplaceOwner(ownerKey, result, new OwnerMetadata
                {
                    ProviderId = provider.Id,
                    ProviderVersion = provider.Version,
                    TargetKey = target.Key,
                    DependencyHash = depHash,
                    AnalyzedAtUtcTicks = now,
                    Persistent = persistent,
                });
            }

            // このターゲットに適用されなくなった Provider（ロード/未ロード切替など）の結果を除去
            foreach (var owner in _store.FindOwnersByTarget(target.Key))
                if (!applicable.Contains(owner)) _store.RemoveOwner(owner);

            if (target.Kind == AnalysisTargetKind.Asset && target.Path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                _prefabCache.Invalidate(target.Guid);

            TargetCommitted?.Invoke(target.Key);
        }

        public void RemoveTarget(string targetKey)
        {
            foreach (var owner in _store.FindOwnersByTarget(targetKey)) _store.RemoveOwner(owner);
        }

        void ReportError(IAnalysisProvider provider, AnalysisTarget target, Exception ex)
        {
            _status.ErrorCount++;
            string msg = $"[{provider.Id}] {target.Path}: {ex.GetType().Name}: {ex.Message}";
            Log?.Invoke(msg);
            Debug.LogWarning("[ObjectRelationshipExplorer] " + msg + "\n" + ex.StackTrace);
        }

        void ReportProgress()
        {
            if (_status.Total < 8) return; // 小さな差分更新では進捗表示しない
            if (_progressId < 0 || !Progress.Exists(_progressId))
            {
                _progressId = Progress.Start("Object Relationship Explorer", "索引中…", Progress.Options.None);
                Progress.RegisterCancelCallback(_progressId, () =>
                {
                    Cancel();
                    return true;
                });
            }
            float p = _status.Total == 0 ? 1f : (float)_status.Completed / _status.Total;
            Progress.Report(_progressId, _status.Completed, Math.Max(_status.Total, 1), _status.CurrentTarget ?? "");
            _ = p;
        }

        void FinishProgress(Progress.Status status)
        {
            if (_progressId >= 0 && Progress.Exists(_progressId)) Progress.Finish(_progressId, status);
            _progressId = -1;
        }
    }
}

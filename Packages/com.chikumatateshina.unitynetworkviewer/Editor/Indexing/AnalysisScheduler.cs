using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityNetworkViewer.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace UnityNetworkViewer.Editor
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
    /// 1 ステップ =「1 ターゲット × 1 Provider」で、Provider の結果は owner（Provider × ターゲット）ごとに
    /// GraphStore へ原子的にコミットする。ターゲットの途中でフレームをまたいでもよい。
    ///
    /// ロード済み Scene は一括で解析せず、GameObject の部分木（チャンク）に分割して解析する。
    /// Scene のターゲット（コンテナ）は分割と差分検出だけを行い、変化したチャンクだけを投入する。
    /// 値の変更は変更されたオブジェクトを含むチャンクだけを再解析する。
    /// </summary>
    public sealed class AnalysisScheduler
    {
        readonly GraphStore _store;
        readonly PrefabStructureCache _prefabCache = new PrefabStructureCache();
        readonly LinkedList<QueuedTarget>[] _queues = { new LinkedList<QueuedTarget>(), new LinkedList<QueuedTarget>(), new LinkedList<QueuedTarget>() };
        readonly Dictionary<string, (AnalysisPriority priority, LinkedListNode<QueuedTarget> node)> _queued = new Dictionary<string, (AnalysisPriority, LinkedListNode<QueuedTarget>)>();
        readonly Dictionary<string, ScenePartition> _partitions = new Dictionary<string, ScenePartition>(StringComparer.Ordinal);
        readonly List<Job> _parked = new List<Job>();
        readonly SchedulerStatus _status = new SchedulerStatus();
        readonly Stopwatch _batchWatch = new Stopwatch();
        readonly Stopwatch _frame = new Stopwatch();
        Job _current;
        int _progressId = -1;
        bool _subscribed;
        bool _cancelRequested;
        long _frameBudgetMs = 12;

        const int MaxParkedJobs = 4;

        public event Action<SchedulerStatus> StatusChanged;
        public event Action<string> Log;
        /// <summary>ターゲットの解析結果がコミットされたとき（キー）。</summary>
        public event Action<string> TargetCommitted;

        sealed class QueuedTarget
        {
            public AnalysisTarget Target;
            public AnalysisPriority Priority;
        }

        sealed class Job
        {
            public AnalysisTarget Target;
            public AnalysisPriority Priority;
            public bool Started;
            public List<IAnalysisProvider> Providers;
            public HashSet<string> Applicable;
            public HashSet<IAnalysisProvider> Prepared;
            public string DepHash;
            public int Index;
            public bool HasTransient;
            public AnalysisContext Context;
            public IEnumerator<int> Work;
            public AnalysisResult PartialResult;
        }

        enum StepResult
        {
            Progress,
            Done,
            Waiting,
        }

        sealed class ChunkInfo
        {
            /// <summary>キー（先頭の部分木のルート）。</summary>
            public int RootInstanceId;
            /// <summary>含まれる部分木のルート（Single の場合はその GameObject のみ）。</summary>
            public int[] Roots;
            public bool Single;
            public ulong Hash;
            public bool HasTransient;
        }

        /// <summary>ロード済み Scene のチャンク分割と、Scene 内オブジェクトの ID キャッシュ。</summary>
        sealed class ScenePartition
        {
            public string SceneKey;
            public int Handle;
            public Dictionary<int, ChunkInfo> Chunks = new Dictionary<int, ChunkInfo>();
            /// <summary>部分木のルート → チャンク。</summary>
            public Dictionary<int, ChunkInfo> ByRoot = new Dictionary<int, ChunkInfo>();
            public readonly Dictionary<int, string> IdCache = new Dictionary<int, string>();
            public readonly HashSet<int> PendingDirty = new HashSet<int>();
            public readonly HashSet<int> PendingSubtrees = new HashSet<int>();
            public bool FullPending = true;
            public bool Partitioned;
        }

        public AnalysisScheduler(GraphStore store)
        {
            _store = store;
        }

        public SchedulerStatus Status => _status;
        public PrefabStructureCache PrefabStructures => _prefabCache;
        public bool IsIdle => _queued.Count == 0 && _current == null && _parked.Count == 0;

        /// <summary>チャンク 1 つあたりの GameObject 数の上限。</summary>
        public static int ChunkSize => Math.Max(16, UnvSettings.instance.chunkSize);

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

        public bool IsQueued(string key)
        {
            foreach (var k in _queued.Keys)
                if (k.EndsWith("|" + key, StringComparison.Ordinal)) return true;
            return _current != null && _current.Target.Key == key;
        }

        static string QueueKey(AnalysisTarget t) => t.Kind + "|" + t.Key;

        public void Enqueue(AnalysisTarget target, AnalysisPriority priority)
        {
            if (target == null) return;
            string key = QueueKey(target);
            if (_queued.TryGetValue(key, out var existing))
            {
                if (existing.priority <= priority) return;
                _queues[(int)existing.priority].Remove(existing.node);
                _queued.Remove(key);
            }
            var node = _queues[(int)priority].AddLast(new QueuedTarget { Target = target, Priority = priority });
            _queued[key] = (priority, node);
            if (!_status.Running)
            {
                _status.Running = true;
                _status.Completed = 0;
                _status.Total = 0;
                _status.ErrorCount = 0;
                _batchWatch.Restart();
            }
            _status.Total++;
            _status.Pending = _queued.Count;
        }

        public void Cancel()
        {
            _cancelRequested = true;
        }

        // ------------------------------------------------------------------ Scene API（ChangeTracker / Service から）

        public static string SceneKeyOf(Scene scene) => AnalysisTarget.ForLoadedScene(scene).SceneKey;

        /// <summary>ロード済み Scene の分割と差分解析を要求する。full = true なら全チャンクを再解析する。</summary>
        public void RequestScene(Scene scene, bool full, AnalysisPriority priority)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            var part = GetPartition(SceneKeyOf(scene), true);
            part.FullPending |= full;
            Enqueue(AnalysisTarget.ForLoadedScene(scene), priority);
        }

        /// <summary>
        /// Scene の構造（生成・削除・親子・Prefab 構造）が変わった。subtreeRoots 以下は内容も再解析する。
        /// idChangedRoots 以下は ID が変わり得る（Prefab の Unpack・Scene 間の移動）ため ID キャッシュから除く。
        /// </summary>
        public void NotifyStructureChanged(Scene scene, IEnumerable<int> subtreeRoots, IEnumerable<int> idChangedRoots = null)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            var part = GetPartition(SceneKeyOf(scene), true);
            if (idChangedRoots != null) foreach (var id in idChangedRoots) InvalidateIds(part, id);
            if (subtreeRoots != null) foreach (var id in subtreeRoots) part.PendingSubtrees.Add(id);
            Enqueue(AnalysisTarget.ForLoadedScene(scene), AnalysisPriority.Normal);
        }

        /// <summary>部分木の GameObject / Component の ID をキャッシュから除く（全体は捨てない）。</summary>
        void InvalidateIds(ScenePartition part, int rootInstanceId)
        {
            part.IdCache.Remove(rootInstanceId);
            var go = EditorUtility.InstanceIDToObject(rootInstanceId) as GameObject;
            if (go == null) return;
            var stack = new Stack<Transform>();
            stack.Push(go.transform);
            var comps = new List<Component>();
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                part.IdCache.Remove(t.gameObject.GetInstanceID());
                t.GetComponents(comps);
                foreach (var c in comps) if (c != null) part.IdCache.Remove(c.GetInstanceID());
                for (int i = 0; i < t.childCount; i++) stack.Push(t.GetChild(i));
            }
        }

        /// <summary>オブジェクト（GameObject / Component）の値が変わった。含まれるチャンクだけを再解析する。</summary>
        public void NotifyObjectsChanged(Scene scene, IEnumerable<int> instanceIds)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            var part = GetPartition(SceneKeyOf(scene), true);
            bool needContainer = !part.Partitioned;
            foreach (var iid in instanceIds)
            {
                if (needContainer || !MarkObjectDirty(scene, part, iid, AnalysisPriority.Normal))
                {
                    part.PendingDirty.Add(iid);
                    needContainer = true;
                }
            }
            if (needContainer) Enqueue(AnalysisTarget.ForLoadedScene(scene), AnalysisPriority.Normal);
        }

        /// <summary>Scene の保存後。初回保存なら ID が永続 ID に変わるため作り直し、それ以外は一時 ID を含むチャンクだけ再解析する。</summary>
        public void NotifySceneSaved(Scene scene)
        {
            string unsavedKey = "scene:h" + scene.handle;
            if (_partitions.ContainsKey(unsavedKey) || _store.HasOwnersForBaseTarget(unsavedKey))
            {
                ForgetScene(unsavedKey);
                RequestScene(scene, true, AnalysisPriority.Normal);
                return;
            }
            if (!_partitions.TryGetValue(SceneKeyOf(scene), out var part) || !part.Partitioned)
            {
                RequestScene(scene, false, AnalysisPriority.Normal);
                return;
            }
            foreach (var c in part.Chunks.Values)
                if (c.HasTransient) Enqueue(ChunkTarget(scene, c), AnalysisPriority.Normal);
        }

        /// <summary>Scene が閉じられた。ロード済み解析の結果（チャンク）を取り除く。</summary>
        public void ForgetScene(string sceneKey)
        {
            _partitions.Remove(sceneKey);
            foreach (var owner in _store.FindOwnersByBaseTarget(sceneKey)) _store.RemoveOwner(owner);
        }

        /// <summary>Scene がすでに索引されている（または解析待ち）か。</summary>
        public bool IsSceneKnown(string sceneKey)
            => _partitions.ContainsKey(sceneKey) || _store.HasOwnersForBaseTarget(sceneKey);

        ScenePartition GetPartition(string sceneKey, bool create)
        {
            if (_partitions.TryGetValue(sceneKey, out var p) || !create) return p;
            p = new ScenePartition { SceneKey = sceneKey };
            _partitions[sceneKey] = p;
            return p;
        }

        Dictionary<int, string> GetSceneIdCache(string sceneKey) => GetPartition(sceneKey, true).IdCache;

        // ------------------------------------------------------------------ Tick

        /// <summary>1 フレーム分の処理を行う（テスト・計測用。通常は EditorApplication.update から呼ばれる）。</summary>
        public void Step() => Tick();

        void Tick()
        {
            if (_cancelRequested)
            {
                _cancelRequested = false;
                int dropped = _queued.Count + (_current != null ? 1 : 0) + _parked.Count;
                foreach (var q in _queues) q.Clear();
                _queued.Clear();
                foreach (var job in _parked)
                {
                    job.Work?.Dispose();
                    job.Context?.ReleaseTable(job.Target);
                }
                _current?.Work?.Dispose();
                if (_current != null) _current.Context?.ReleaseTable(_current.Target);
                _parked.Clear();
                _current = null;
                _status.Running = false;
                _status.Pending = 0;
                _status.LastMessage = $"解析をキャンセルしました（未処理 {dropped} 件）";
                Log?.Invoke(_status.LastMessage);
                FinishProgress(Progress.Status.Canceled);
                StatusChanged?.Invoke(_status);
                return;
            }
            if (IsIdle) return;
            // Play Mode 中は実行時オブジェクトを索引しない
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            var settings = UnvSettings.instance;
            _frameBudgetMs = Math.Max(1, settings.frameBudgetMilliseconds);
            _frame.Restart();
            var context = new AnalysisContext(settings, _prefabCache, GetSceneIdCache, () => _frame.ElapsedMilliseconds >= _frameBudgetMs);
            int steps = 0;
            _store.BeginBatch();
            try
            {
                // 準備待ち（ファイル読み込み中など）のジョブを先に確認する
                for (int i = 0; i < _parked.Count && _frame.ElapsedMilliseconds < _frameBudgetMs; i++)
                {
                    var job = _parked[i];
                    var r = RunStep(job, context);
                    steps++;
                    if (r == StepResult.Waiting) continue;
                    if (r == StepResult.Done)
                    {
                        _parked.RemoveAt(i--);
                        FinishJob(job, context);
                    }
                    else if (_current == null)
                    {
                        _parked.RemoveAt(i--);
                        _current = job;
                    }
                    // それ以外は待ち列に残し、次フレーム以降に続きを処理する
                }

                while (steps == 0 || _frame.ElapsedMilliseconds < _frameBudgetMs)
                {
                    var job = _current ?? NextJob();
                    if (job == null) break;
                    _current = job;
                    _status.CurrentTarget = job.Target.Path;
                    var r = RunStep(job, context);
                    steps++;
                    if (r == StepResult.Done)
                    {
                        _current = null;
                        FinishJob(job, context);
                    }
                    else if (r == StepResult.Waiting)
                    {
                        _current = null;
                        if (_parked.Count < MaxParkedJobs) _parked.Add(job);
                        else
                        {
                            // 待ちが詰まっている場合は先頭に戻して次フレームで再確認
                            _current = job;
                            break;
                        }
                    }
                }
            }
            finally
            {
                _store.EndBatch();
            }

            _status.Pending = _queued.Count + _parked.Count + (_current != null ? 1 : 0);
            _status.ElapsedSeconds = _batchWatch.Elapsed.TotalSeconds;
            ReportProgress();
            if (IsIdle && _status.Running)
            {
                _status.Running = false;
                _status.CurrentTarget = null;
                _status.LastMessage = $"索引完了: {_status.Completed} 件 / {_status.ElapsedSeconds:0.0} 秒 / エラー {_status.ErrorCount} 件 (ノード {_store.NodeCount:N0}, エッジ {_store.EdgeCount:N0})";
                Log?.Invoke(_status.LastMessage);
                FinishProgress(_status.ErrorCount > 0 ? Progress.Status.Failed : Progress.Status.Succeeded);
            }
            StatusChanged?.Invoke(_status);
        }

        Job NextJob()
        {
            foreach (var q in _queues)
            {
                if (q.Count == 0) continue;
                var t = q.First.Value;
                q.RemoveFirst();
                _queued.Remove(QueueKey(t.Target));
                return new Job { Target = t.Target, Priority = t.Priority };
            }
            return null;
        }

        void FinishJob(Job job, AnalysisContext context)
        {
            _status.Completed++;
            job.Work?.Dispose();
            (job.Context ?? context).ReleaseTable(job.Target);
        }

        // ------------------------------------------------------------------ Job steps

        StepResult RunStep(Job job, AnalysisContext context)
        {
            if (job.Context == null) job.Context = context;
            context = job.Context;
            if (!job.Started)
            {
                job.Started = true;
                if (!BeginJob(job, context)) return StepResult.Done;
            }
            if (job.Index >= job.Providers.Count) return CompleteJob(job);

            var provider = job.Providers[job.Index];
            var target = job.Target;
            if (provider is IPreparedAnalysisProvider prepared && !job.Prepared.Contains(provider))
            {
                bool ready;
                try
                {
                    ready = prepared.Prepare(target, context);
                }
                catch (Exception ex)
                {
                    ReportError(provider, target, ex);
                    ready = true; // Analyze 側でエラーを記録する
                }
                if (!ready) return StepResult.Waiting;
                job.Prepared.Add(provider);
                if (context.OutOfTime) return StepResult.Progress;
            }

            AnalysisResult result;
            try
            {
                if (TargetFilters.IsHierarchyTarget(target) && !context.PrepareTable(target)) return StepResult.Progress;
                if (provider is IIncrementalAnalysisProvider incremental)
                {
                    if (job.Work == null)
                    {
                        job.PartialResult = new AnalysisResult(provider.Id);
                        job.Work = incremental.AnalyzeIncrementally(target, context, job.PartialResult).GetEnumerator();
                    }
                    // At least one unit per step, then yield to the next editor update.
                    while (job.Work.MoveNext())
                        if (context.OutOfTime) return StepResult.Progress;
                    result = job.PartialResult;
                    job.Work.Dispose();
                    job.Work = null;
                    job.PartialResult = null;
                }
                else result = provider.Analyze(target, context) ?? new AnalysisResult(provider.Id);
            }
            catch (Exception ex)
            {
                job.Work?.Dispose();
                job.Work = null;
                job.PartialResult = null;
                context.ReleaseTable(target);
                if (target.Kind == AnalysisTargetKind.SceneChunk && (ex is MissingReferenceException || ex is NullReferenceException))
                {
                    // 解析中にオブジェクトが削除された。古い結果は残し、Scene を分割し直す
                    RequestRepartition(target);
                    return StepResult.Done;
                }
                ReportError(provider, target, ex);
                // 失敗した Provider の古い結果は残さず、診断のみ記録する（障害分離）
                result = new AnalysisResult(provider.Id);
                result.AddDiagnostic(DiagnosticSeverity.Error, DiagnosticCodes.ProviderError, $"{provider.Id}: {ex.GetType().Name}: {ex.Message}", null, target.Path);
            }

            long now = DateTime.UtcNow.Ticks;
            foreach (var e in result.Edges) if (e.AnalyzedAtUtcTicks == 0) e.AnalyzedAtUtcTicks = now;
            if (target.Kind == AnalysisTargetKind.SceneChunk && !job.HasTransient)
            {
                foreach (var n in result.Nodes)
                {
                    if (!NodeIds.IsSession(n.Id) || n.SceneGuid != target.Guid) continue;
                    job.HasTransient = true;
                    break;
                }
            }
            bool persistent = (target.Kind == AnalysisTargetKind.Asset || target.Kind == AnalysisTargetKind.UnloadedScene) && job.DepHash != null;
            string ownerKey = OwnerKeys.Create(provider.Id, target.Key);
            // チャンクでは、前回あったオブジェクトが消えた場合に、それを参照する他のチャンクを再解析する（Missing の検出）
            var previous = target.Kind == AnalysisTargetKind.SceneChunk ? RealNodeIds(_store.GetOwner(ownerKey)) : null;
            _store.ReplaceOwner(ownerKey, result, new OwnerMetadata
            {
                ProviderId = provider.Id,
                ProviderVersion = provider.Version,
                TargetKey = target.Key,
                DependencyHash = job.DepHash,
                AnalyzedAtUtcTicks = now,
                Persistent = persistent,
            });
            if (previous != null) RequeueReferrers(previous, target.Key);
            job.Index++;
            return job.Index >= job.Providers.Count ? CompleteJob(job) : StepResult.Progress;
        }

        /// <summary>ジョブの開始。false の場合はこのターゲットでの解析は不要（完了扱い）。</summary>
        bool BeginJob(Job job, AnalysisContext context)
        {
            var target = job.Target;
            // 状態が変わっている可能性があるため現在の状態で解決し直す
            if ((target.Kind == AnalysisTargetKind.UnloadedScene || target.Kind == AnalysisTargetKind.LoadedScene) && !string.IsNullOrEmpty(target.Path))
            {
                var scene = SceneManager.GetSceneByPath(target.Path);
                bool loaded = scene.IsValid() && scene.isLoaded;
                if (loaded)
                {
                    RunContainer(scene, job.Priority);
                    return false;
                }
                if (target.Kind == AnalysisTargetKind.LoadedScene)
                {
                    if (string.IsNullOrEmpty(target.Guid)) return false;
                    job.Target = target = AnalysisTarget.ForUnloadedScene(target.Guid, target.Path);
                }
            }
            else if (target.Kind == AnalysisTargetKind.LoadedScene)
            {
                // 未保存 Scene
                var scene = target.GetScene();
                if (scene.IsValid() && scene.isLoaded) RunContainer(scene, job.Priority);
                return false;
            }

            if (target.Kind == AnalysisTargetKind.SceneChunk)
            {
                if (!ValidateChunk(job)) return false;
                target = job.Target;
            }
            else if (string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(target.Guid)))
            {
                RemoveTarget(target.Key);
                return false;
            }

            job.DepHash = target.Kind == AnalysisTargetKind.Asset || target.Kind == AnalysisTargetKind.UnloadedScene ? ObjectIdUtility.GetDependencyHash(target.Path) : null;
            job.Providers = new List<IAnalysisProvider>();
            job.Applicable = new HashSet<string>(StringComparer.Ordinal);
            job.Prepared = new HashSet<IAnalysisProvider>();
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
                job.Providers.Add(provider);
                job.Applicable.Add(OwnerKeys.Create(provider.Id, target.Key));
            }
            return true;
        }

        StepResult CompleteJob(Job job)
        {
            var target = job.Target;
            // このターゲットに適用されなくなった Provider（ロード/未ロード切替など）の結果を除去
            foreach (var owner in _store.FindOwnersByTarget(target.Key))
                if (!job.Applicable.Contains(owner)) _store.RemoveOwner(owner);

            if (target.Kind == AnalysisTargetKind.SceneChunk && _partitions.TryGetValue(target.SceneKey, out var part)
                && part.Chunks.TryGetValue(target.ChunkRootInstanceId, out var info))
                info.HasTransient = job.HasTransient;

            TargetCommitted?.Invoke(target.Key);
            return StepResult.Done;
        }

        /// <summary>チャンクが現在の分割に存在するか確認し、最新の構成（部分木のルート）でターゲットを作り直す。</summary>
        bool ValidateChunk(Job job)
        {
            var target = job.Target;
            if (!_partitions.TryGetValue(target.SceneKey, out var part) || !part.Chunks.TryGetValue(target.ChunkRootInstanceId, out var info) || info.Single != target.ChunkIsSingle)
                return false; // 分割し直されて不要になったチャンク
            foreach (var iid in info.Roots)
            {
                var root = EditorUtility.InstanceIDToObject(iid) as GameObject;
                if (root == null || root.scene.handle != target.SceneHandle)
                {
                    RequestRepartition(target);
                    return false;
                }
            }
            var scene = target.GetScene();
            if (!scene.IsValid()) return false;
            job.Target = ChunkTarget(scene, info);
            return true;
        }

        static AnalysisTarget ChunkTarget(Scene scene, ChunkInfo c) => AnalysisTarget.ForSceneChunk(scene, c.RootInstanceId, c.Single, c.Roots);

        void RequestRepartition(AnalysisTarget chunk)
        {
            var scene = chunk.GetScene();
            if (scene.IsValid() && scene.isLoaded) Enqueue(AnalysisTarget.ForLoadedScene(scene), AnalysisPriority.Normal);
        }

        // ------------------------------------------------------------------ Scene container / partition

        /// <summary>
        /// ロード済み Scene のコンテナ処理: Scene ノードを出し、GameObject 階層をチャンクに分割して前回と比較し、
        /// 新規・変化したチャンクだけを投入する。消えたチャンクの結果は取り除く。
        /// </summary>
        void RunContainer(Scene scene, AnalysisPriority priority)
        {
            string sceneKey = SceneKeyOf(scene);
            var part = GetPartition(sceneKey, true);
            part.Handle = scene.handle;

            string containerOwner = OwnerKeys.Create(ProviderIds.Hierarchy, sceneKey);
            var sceneResult = new AnalysisResult(ProviderIds.Hierarchy);
            sceneResult.AddNode(SceneHierarchyProvider.CreateSceneNode(scene, ProviderIds.Hierarchy));
            _store.ReplaceOwner(containerOwner, sceneResult, new OwnerMetadata
            {
                ProviderId = ProviderIds.Hierarchy,
                ProviderVersion = 1,
                TargetKey = sceneKey,
                AnalyzedAtUtcTicks = DateTime.UtcNow.Ticks,
            });

            var chunks = ComputePartition(scene, ChunkSize);
            bool full = part.FullPending;
            part.FullPending = false;
            var validKeys = new HashSet<string>(StringComparer.Ordinal);
            int enqueued = 0;
            foreach (var c in chunks.Values)
            {
                string key = AnalysisTarget.ChunkKey(sceneKey, c.RootInstanceId, c.Single);
                validKeys.Add(key);
                if (!full && part.Chunks.TryGetValue(c.RootInstanceId, out var old) && old.Single == c.Single && old.Hash == c.Hash && _store.HasOwnersForTarget(key))
                {
                    c.HasTransient = old.HasTransient;
                    continue;
                }
                Enqueue(ChunkTarget(scene, c), priority);
                enqueued++;
            }

            // 未ロード索引・旧チャンクなど、現在の分割に無い結果を取り除く
            var removedNodes = new List<string>();
            foreach (var owner in _store.FindOwnersByBaseTarget(sceneKey))
            {
                if (owner == containerOwner) continue;
                string tk = OwnerKeys.GetTargetKey(owner);
                if (tk != sceneKey && validKeys.Contains(tk)) continue;
                if (tk != sceneKey) removedNodes.AddRange(RealNodeIds(_store.GetOwner(owner)));
                _store.RemoveOwner(owner);
            }
            RequeueReferrers(removedNodes, null);

            part.Chunks = chunks;
            part.ByRoot = new Dictionary<int, ChunkInfo>();
            foreach (var c in chunks.Values) foreach (var r in c.Roots) part.ByRoot[r] = c;
            part.Partitioned = true;

            foreach (var iid in part.PendingSubtrees) MarkSubtreeDirty(scene, part, iid, priority);
            part.PendingSubtrees.Clear();
            foreach (var iid in part.PendingDirty) MarkObjectDirty(scene, part, iid, priority);
            part.PendingDirty.Clear();

            TargetCommitted?.Invoke(sceneKey);
            if (enqueued > 0 && full) Log?.Invoke($"{scene.name}: {chunks.Count} チャンクに分割して解析します");
        }

        /// <summary>
        /// 階層を前順に並べてチャンクに分ける。上限を超える部分木のルートは単独チャンクにして子へ進み、
        /// 上限以下の兄弟の部分木は上限まで 1 チャンクにまとめる（深い階層でもチャンク数が増えすぎないように）。
        /// 部分木は前順で連続するため、メンバーのハッシュは範囲で計算できる（並び順には依存しない）。
        /// </summary>
        static Dictionary<int, ChunkInfo> ComputePartition(Scene scene, int limit)
        {
            var order = new List<Transform>(1024);
            var parent = new List<int>(1024);
            var stack = new Stack<(Transform t, int parent)>();
            var roots = scene.GetRootGameObjects();
            for (int i = roots.Length - 1; i >= 0; i--) if (roots[i] != null) stack.Push((roots[i].transform, -1));
            while (stack.Count > 0)
            {
                var (t, p) = stack.Pop();
                int index = order.Count;
                order.Add(t);
                parent.Add(p);
                for (int i = t.childCount - 1; i >= 0; i--) stack.Push((t.GetChild(i), index));
            }

            int n = order.Count;
            var size = new int[n];
            var ids = new int[n];
            for (int i = 0; i < n; i++)
            {
                size[i] = 1;
                ids[i] = order[i].gameObject.GetInstanceID();
            }
            for (int i = n - 1; i >= 0; i--) if (parent[i] >= 0) size[parent[i]] += size[i];

            var chunks = new Dictionary<int, ChunkInfo>();
            var group = new List<int>();
            int groupSize = 0;

            void Flush()
            {
                if (group.Count == 0) return;
                ulong hash = (ulong)groupSize;
                var rootIds = new int[group.Count];
                for (int g = 0; g < group.Count; g++)
                {
                    int r = group[g];
                    rootIds[g] = ids[r];
                    for (int j = r; j < r + size[r]; j++) hash += Mix((ulong)(uint)ids[j]);
                }
                chunks[rootIds[0]] = new ChunkInfo { RootInstanceId = rootIds[0], Roots = rootIds, Single = false, Hash = hash };
                group.Clear();
                groupSize = 0;
            }

            // -1 は Scene ルートの並び（仮想的な親）
            var work = new Stack<int>();
            work.Push(-1);
            while (work.Count > 0)
            {
                int p = work.Pop();
                int start = p < 0 ? 0 : p + 1;
                int end = p < 0 ? n : p + size[p];
                for (int c = start; c < end; c += size[c])
                {
                    if (size[c] > limit)
                    {
                        Flush();
                        chunks[ids[c]] = new ChunkInfo { RootInstanceId = ids[c], Roots = new[] { ids[c] }, Single = true, Hash = Mix((ulong)(uint)ids[c]) };
                        work.Push(c);
                        continue;
                    }
                    if (groupSize + size[c] > limit) Flush();
                    group.Add(c);
                    groupSize += size[c];
                }
                Flush();
            }
            return chunks;
        }

        static ulong Mix(ulong x)
        {
            x ^= x >> 33;
            x *= 0xff51afd7ed558ccdUL;
            x ^= x >> 33;
            x *= 0xc4ceb9fe1a85ec53UL;
            x ^= x >> 33;
            return x;
        }

        /// <summary>オブジェクトを含むチャンクを投入する。分割に含まれない（新規など）場合は false。</summary>
        bool MarkObjectDirty(Scene scene, ScenePartition part, int instanceId, AnalysisPriority priority)
        {
            var obj = EditorUtility.InstanceIDToObject(instanceId);
            var go = obj as GameObject ?? (obj as Component)?.gameObject;
            if (go == null) return true; // 削除済み（構造変更のイベントで扱う）
            if (go.scene.handle != scene.handle) return false;
            var chunk = FindChunk(part, go);
            if (chunk == null) return false;
            Enqueue(ChunkTarget(scene, chunk), priority);

            // 単独チャンクの GameObject の名前が変わると、子孫チャンクの階層パスも変わる
            if (chunk.Single && ReferenceEquals(obj, go) && part.IdCache.TryGetValue(go.GetInstanceID(), out var id))
            {
                var node = _store.GetNode(id);
                if (node != null && node.Name != go.name) MarkSubtreeDirty(scene, part, go.GetInstanceID(), priority);
            }
            return true;
        }

        void MarkSubtreeDirty(Scene scene, ScenePartition part, int rootInstanceId, AnalysisPriority priority)
        {
            var go = EditorUtility.InstanceIDToObject(rootInstanceId) as GameObject;
            if (go == null || go.scene.handle != scene.handle) return;
            var containing = FindChunk(part, go);
            if (containing != null) Enqueue(ChunkTarget(scene, containing), priority);
            var root = go.transform;
            var done = new HashSet<ChunkInfo>();
            if (containing != null) done.Add(containing);
            foreach (var kv in part.ByRoot)
            {
                if (done.Contains(kv.Value)) continue;
                var t = (EditorUtility.InstanceIDToObject(kv.Key) as GameObject)?.transform;
                if (t == null || t == root || !t.IsChildOf(root)) continue;
                done.Add(kv.Value);
                Enqueue(ChunkTarget(scene, kv.Value), priority);
            }
        }

        static ChunkInfo FindChunk(ScenePartition part, GameObject go)
        {
            for (var t = go.transform; t != null; t = t.parent)
            {
                if (!part.ByRoot.TryGetValue(t.gameObject.GetInstanceID(), out var c)) continue;
                // 単独チャンクに一致するのはそのオブジェクト自身のみ（子は別チャンクに属するはず）
                if (c.Single && t.gameObject != go) return null;
                return c;
            }
            return null;
        }

        static List<string> RealNodeIds(OwnerRecord record)
        {
            var list = new List<string>();
            if (record == null) return list;
            foreach (var n in record.Nodes) if (!n.IsPlaceholder) list.Add(n.Id);
            return list;
        }

        /// <summary>
        /// 実体が無くなった（削除・仮ノード化した）ノードを参照している、ロード済み Scene のチャンクを再解析に回す。
        /// 参照元の Component は変更されていないためイベントが来ないが、参照は Missing になっている。
        /// </summary>
        void RequeueReferrers(List<string> nodeIds, string exceptTargetKey)
        {
            HashSet<string> seen = null;
            foreach (var id in nodeIds)
            {
                var n = _store.GetNode(id);
                if (n != null && !n.IsPlaceholder) continue;
                foreach (var eid in _store.GetInEdgeIds(id))
                {
                    string tk = OwnerKeys.GetTargetKey(_store.GetEdgeOwner(eid));
                    if (tk == exceptTargetKey || tk.IndexOf('#') < 0) continue;
                    if (seen == null) seen = new HashSet<string>(StringComparer.Ordinal);
                    if (!seen.Add(tk)) continue;
                    var t = AnalysisTarget.FromKey(tk);
                    if (t != null) Enqueue(t, AnalysisPriority.Normal);
                }
            }
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
            Debug.LogWarning("[UnityNetworkViewer] " + msg + "\n" + ex.StackTrace);
        }

        void ReportProgress()
        {
            if (_status.Total < 8) return; // 小さな差分更新では進捗表示しない
            if (_progressId < 0 || !Progress.Exists(_progressId))
            {
                _progressId = Progress.Start("UnityNetworkViewer", "索引中…", Progress.Options.None);
                Progress.RegisterCancelCallback(_progressId, () =>
                {
                    Cancel();
                    return true;
                });
            }
            Progress.Report(_progressId, _status.Completed, Math.Max(_status.Total, 1), _status.CurrentTarget ?? "");
        }

        void FinishProgress(Progress.Status status)
        {
            if (_progressId >= 0 && Progress.Exists(_progressId)) Progress.Finish(_progressId, status);
            _progressId = -1;
        }
    }
}

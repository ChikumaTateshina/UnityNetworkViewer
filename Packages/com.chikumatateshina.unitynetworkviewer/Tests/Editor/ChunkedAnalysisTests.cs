using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityNetworkViewer.Core;
using UnityNetworkViewer.Editor;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityNetworkViewer.Tests
{
    /// <summary>
    /// ロード済み Scene をチャンクに分けて解析した結果が、Scene 全体を一括で解析した結果と一致すること、
    /// 差分更新（値の変更・作成・削除・親子変更・名前変更）の後も一致することを確認する。
    /// </summary>
    public class ChunkedAnalysisTests
    {
        const string Folder = "Assets/__UnvChunkTests";
        int _oldChunkSize;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "__UnvChunkTests");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _oldChunkSize = UnvSettings.instance.chunkSize;
            UnvSettings.instance.chunkSize = 16; // 小さな Scene でも複数チャンク（単独チャンク・組）になるように
            NewObjectRegistry.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            UnvSettings.instance.chunkSize = _oldChunkSize;
            NewObjectRegistry.Clear();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Folder);
        }

        /// <summary>3 つのルート（大・中・小）と深い入れ子、チャンクをまたぐ参照と UnityEvent を持つ保存済み Scene。</summary>
        static (Scene scene, List<GameObject> all, List<UnvTestBehaviour> behaviours) BuildSavedScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var all = new List<GameObject>();
            var behaviours = new List<UnvTestBehaviour>();
            int[] sizes = { 60, 20, 5 };
            for (int r = 0; r < sizes.Length; r++)
            {
                var root = new GameObject("Root" + r);
                all.Add(root);
                var parent = root.transform;
                for (int i = 1; i < sizes[r]; i++)
                {
                    var go = new GameObject($"Obj{r}_{i}");
                    go.transform.SetParent(i % 4 == 1 ? root.transform : parent, false);
                    if (i % 4 == 0) parent = go.transform;
                    all.Add(go);
                }
            }
            for (int i = 0; i < all.Count; i += 3)
            {
                var b = all[i].AddComponent<UnvTestBehaviour>();
                b.target = all[(i * 7 + 11) % all.Count];
                b.targets = new[] { all[(i * 13 + 5) % all.Count], all[(i + 40) % all.Count] };
                b.componentRef = all[(i * 3 + 1) % all.Count].transform;
                if (i % 9 == 0) UnityEventTools.AddPersistentListener(b.onUse, b.Use);
                behaviours.Add(b);
            }
            EditorSceneManager.SaveScene(scene, Folder + "/Chunks.unity");
            return (scene, all, behaviours);
        }

        static GraphStore AnalyzeWhole(Scene scene)
        {
            var store = new GraphStore();
            var target = AnalysisTarget.ForLoadedScene(scene);
            var context = new AnalysisContext(UnvSettings.instance, new PrefabStructureCache());
            foreach (var p in ProviderRegistry.Providers)
                if (p.CanAnalyze(target)) store.ReplaceOwner(OwnerKeys.Create(p.Id, target.Key), p.Analyze(target, context), null);
            return store;
        }

        static int RunToIdle(AnalysisScheduler scheduler)
        {
            int frames = 0;
            while (!scheduler.IsIdle && frames < 10000)
            {
                scheduler.Step();
                frames++;
            }
            var st = scheduler.Status;
            Assert.IsTrue(scheduler.IsIdle, $"解析が終わらない: completed={st.Completed}/{st.Total} pending={st.Pending} current={st.CurrentTarget} " +
                                            $"compiling={EditorApplication.isCompiling} updating={EditorApplication.isUpdating} playing={EditorApplication.isPlayingOrWillChangePlaymode}");
            return frames;
        }

        static List<string> NodeSignatures(GraphStore s)
            => s.Nodes.Select(n => $"{n.Id}|{n.Kind}|{n.Name}|{n.HierarchyPath}|{n.IsPlaceholder}").OrderBy(x => x).ToList();

        static List<string> EdgeSignatures(GraphStore s)
            => s.Edges.Select(e => $"{e.SourceId}|{e.TargetId}|{e.Kind}|{e.PropertyPath}|{e.MemberName}|{e.Evidence}").OrderBy(x => x).ToList();

        static void AssertSameGraph(Scene scene, GraphStore chunked, string message)
        {
            var whole = AnalyzeWhole(scene);
            CollectionAssert.AreEqual(NodeSignatures(whole), NodeSignatures(chunked), message + "（ノード）");
            CollectionAssert.AreEqual(EdgeSignatures(whole), EdgeSignatures(chunked), message + "（エッジ）");
        }

        static (GraphStore store, AnalysisScheduler scheduler) StartChunked(Scene scene)
        {
            var store = new GraphStore();
            var scheduler = new AnalysisScheduler(store);
            scheduler.RequestScene(scene, true, AnalysisPriority.High);
            RunToIdle(scheduler);
            return (store, scheduler);
        }

        [Test]
        public void Chunked_MatchesWholeSceneAnalysis()
        {
            var (scene, all, _) = BuildSavedScene();
            var (store, _) = StartChunked(scene);
            Assert.Greater(store.FindOwnersByBaseTarget(AnalysisScheduler.SceneKeyOf(scene)).Count, 8, "複数のチャンクに分割されている");
            AssertSameGraph(scene, store, "チャンク解析と一括解析");
            Assert.IsFalse(scene.isDirty, "解析で Scene を Dirty にしない");
        }

        [Test]
        public void PropertyChange_ReanalyzesOnlyAffectedChunk()
        {
            var (scene, all, behaviours) = BuildSavedScene();
            var (store, scheduler) = StartChunked(scene);

            var b = behaviours[4];
            var newTarget = all[all.Count - 1];
            b.target = newTarget;
            scheduler.NotifyObjectsChanged(scene, new[] { b.GetInstanceID() });
            int frames = RunToIdle(scheduler);
            Assert.LessOrEqual(frames, 2, "変更したオブジェクトのチャンクだけを解析する");

            string bId = ObjectIdUtility.GetId(b);
            Assert.IsTrue(store.GetOutEdges(bId).Any(e => e.PropertyPath == "target" && e.TargetId == ObjectIdUtility.GetId(newTarget)));
            AssertSameGraph(scene, store, "値の変更後");
        }

        [Test]
        public void StructureChanges_CreateDeleteReparentRename_StayConsistent()
        {
            var (scene, all, behaviours) = BuildSavedScene();
            var (store, scheduler) = StartChunked(scene);

            // 作成（未保存の新規オブジェクトは一時 ID）
            var created = new GameObject("Created");
            created.transform.SetParent(all[10].transform, false);
            created.AddComponent<UnvTestBehaviour>().target = all[2];
            NewObjectRegistry.MarkCreated(created);
            scheduler.NotifyStructureChanged(scene, new[] { created.GetInstanceID() });
            RunToIdle(scheduler);
            Assert.IsTrue(NodeIds.IsSession(ObjectIdUtility.GetId(created)), "未保存の新規オブジェクトは一時 ID");
            Assert.IsTrue(store.ContainsNode(ObjectIdUtility.GetId(created)));
            AssertSameGraph(scene, store, "作成後");

            // 削除: 参照していた別チャンクの参照が Missing になる
            var victim = behaviours[2].target;
            string victimId = ObjectIdUtility.GetId(victim);
            var referrer = behaviours[2];
            Object.DestroyImmediate(victim);
            scheduler.NotifyStructureChanged(scene, null);
            RunToIdle(scheduler);
            Assert.IsFalse(store.ContainsNode(victimId), "削除したオブジェクトのノードが残らない");
            Assert.IsTrue(store.GetOutEdges(ObjectIdUtility.GetId(referrer)).Any(e => e.PropertyPath == "target" && e.Evidence == EvidenceLevel.Unresolved), "参照元に Missing が出る");
            AssertSameGraph(scene, store, "削除後");

            // 親の変更（部分木の階層パスが変わる）
            var moved = all[45];
            moved.transform.SetParent(all[61].transform, true);
            scheduler.NotifyStructureChanged(scene, new[] { moved.GetInstanceID() });
            RunToIdle(scheduler);
            AssertSameGraph(scene, store, "親の変更後");

            // 単独チャンクになっている大きなルートの名前変更（子孫チャンクの階層パスも変わる）
            all[0].name = "RenamedRoot";
            scheduler.NotifyObjectsChanged(scene, new[] { all[0].GetInstanceID() });
            RunToIdle(scheduler);
            AssertSameGraph(scene, store, "名前の変更後");

            // 保存後は一時 ID が永続 ID に置き換わる
            EditorSceneManager.SaveScene(scene);
            NewObjectRegistry.ClearScene(scene);
            scheduler.NotifySceneSaved(scene);
            RunToIdle(scheduler);
            Assert.IsFalse(NodeIds.IsSession(ObjectIdUtility.GetId(created)));
            Assert.IsTrue(store.ContainsNode(ObjectIdUtility.GetId(created)), "保存後の永続 ID で索引される");
            Assert.IsFalse(store.Nodes.Any(n => NodeIds.IsSession(n.Id)), "一時 ID が残らない");
            AssertSameGraph(scene, store, "保存後");
        }

        [Test]
        public void UnloadedScene_PreparedInBackground_MatchesSynchronousAnalysis()
        {
            var (scene, _, _) = BuildSavedScene();
            string path = scene.path;
            string guid = AssetDatabase.AssetPathToGUID(path);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var target = AnalysisTarget.ForUnloadedScene(guid, path);
            var sync = new GraphStore();
            var provider = ProviderRegistry.Get(ProviderIds.UnloadedScene);
            sync.ReplaceOwner(OwnerKeys.Create(provider.Id, target.Key), provider.Analyze(target, new AnalysisContext(UnvSettings.instance, new PrefabStructureCache())), null);

            var store = new GraphStore();
            var scheduler = new AnalysisScheduler(store);
            scheduler.Enqueue(target, AnalysisPriority.High);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!scheduler.IsIdle && sw.ElapsedMilliseconds < 20000) scheduler.Step();
            Assert.IsTrue(scheduler.IsIdle, "未ロード Scene の解析が終わらない");
            Assert.IsTrue(store.HasOwner(OwnerKeys.Create(provider.Id, target.Key)));
            CollectionAssert.AreEqual(NodeSignatures(sync), NodeSignatures(store));
            CollectionAssert.AreEqual(EdgeSignatures(sync), EdgeSignatures(store));
        }

        [Test]
        public void ForgetScene_RemovesChunkResults()
        {
            var (scene, _, _) = BuildSavedScene();
            var (store, scheduler) = StartChunked(scene);
            string key = AnalysisScheduler.SceneKeyOf(scene);
            Assert.IsTrue(scheduler.IsSceneKnown(key));
            scheduler.ForgetScene(key);
            Assert.AreEqual(0, store.FindOwnersByBaseTarget(key).Count);
            Assert.AreEqual(0, store.NodeCount);
        }
    }
}

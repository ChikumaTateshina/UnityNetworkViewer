using System.Linq;
using NUnit.Framework;
using UnityNetworkViewer.Core;
using UnityNetworkViewer.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityNetworkViewer.Tests
{
    /// <summary>Animator / AnimationClip の解析（バインディングの統合・キャッシュ・キーフレーム参照の集約）。</summary>
    public class AnimationProviderTests
    {
        const string Folder = "Assets/__UnvAnimTests";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "__UnvAnimTests");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Folder);
        }

        static AnimationClip CreateClip(string name, params string[] paths)
        {
            var clip = new AnimationClip();
            foreach (var p in paths) clip.SetCurve(p, typeof(Transform), "localPosition.x", AnimationCurve.Linear(0, 0, 1, 1));
            AssetDatabase.CreateAsset(clip, $"{Folder}/{name}.anim");
            return clip;
        }

        static GraphStore Analyze(AnalysisTarget target, params string[] providerIds)
        {
            var store = new GraphStore();
            var context = new AnalysisContext(UnvSettings.instance, new PrefabStructureCache());
            foreach (var id in providerIds)
            {
                var p = ProviderRegistry.Get(id);
                store.ReplaceOwner(OwnerKeys.Create(p.Id, target.Key), p.Analyze(target, context), null);
            }
            return store;
        }

        [Test]
        public void AnimatorBindings_AreMergedAcrossClips_AndRefreshWhenClipChanges()
        {
            var a = CreateClip("A", "Door", "Missing/Path");
            var b = CreateClip("B", "Door");
            var controller = AnimatorController.CreateAnimatorControllerAtPath($"{Folder}/C.controller");
            controller.AddMotion(a);
            controller.AddMotion(b);

            var root = new GameObject("Root");
            var door = new GameObject("Door");
            door.transform.SetParent(root.transform);
            var animator = root.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;

            var target = AnalysisTarget.ForLoadedScene(SceneManager.GetActiveScene());
            var store = Analyze(target, ProviderIds.AnimatorBinding);
            string animatorId = ObjectIdUtility.GetId(animator);
            var edges = store.GetOutEdges(animatorId).Where(e => e.Kind == EdgeKind.AnimationBinding).ToList();

            var toDoor = edges.Where(e => e.TargetId == ObjectIdUtility.GetId(door)).ToList();
            Assert.AreEqual(1, toDoor.Count, "同じパス・型を動かす複数の Clip は 1 本のエッジにまとめる");
            StringAssert.Contains("全 2 Clip", toDoor[0].MemberName);
            Assert.AreEqual(EvidenceLevel.Inferred, toDoor[0].Evidence);

            var missing = edges.Single(e => e.PropertyPath == "Missing/Path");
            Assert.AreEqual(EvidenceLevel.Unresolved, missing.Evidence);
            StringAssert.Contains("A", missing.MemberName);

            // Clip を変更すると（dirty count が変わり）キャッシュが作り直される
            var lamp = new GameObject("Lamp");
            lamp.transform.SetParent(root.transform);
            b.SetCurve("Lamp", typeof(Transform), "localPosition.y", AnimationCurve.Linear(0, 0, 1, 1));
            EditorUtility.SetDirty(b);
            store = Analyze(target, ProviderIds.AnimatorBinding);
            Assert.IsTrue(store.GetOutEdges(animatorId).Any(e => e.Kind == EdgeKind.AnimationBinding && e.TargetId == ObjectIdUtility.GetId(lamp)),
                "変更後の Clip のバインディングが反映される");
        }

        [Test]
        public void ClipKeyframeReferences_AreAggregatedPerTarget()
        {
            var m1 = new Material(Shader.Find("Hidden/Internal-Colored"));
            var m2 = new Material(Shader.Find("Hidden/Internal-Colored"));
            AssetDatabase.CreateAsset(m1, Folder + "/M1.mat");
            AssetDatabase.CreateAsset(m2, Folder + "/M2.mat");
            var clip = new AnimationClip();
            var keys = new[]
            {
                new ObjectReferenceKeyframe { time = 0f, value = m1 },
                new ObjectReferenceKeyframe { time = 0.1f, value = m2 },
                new ObjectReferenceKeyframe { time = 0.2f, value = m1 },
                new ObjectReferenceKeyframe { time = 0.3f, value = m1 },
            };
            AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("", typeof(MeshRenderer), "m_Materials.Array.data[0]"), keys);
            AssetDatabase.CreateAsset(clip, Folder + "/Swap.anim");
            string path = AssetDatabase.GetAssetPath(clip);

            var target = AnalysisTarget.ForAsset(AssetDatabase.AssetPathToGUID(path), path);
            var store = Analyze(target, ProviderIds.Animation);
            var refs = store.GetOutEdges(NodeIds.Asset(target.Guid)).Where(e => e.HasQualifier("keyframe")).ToList();
            Assert.AreEqual(2, refs.Count, "同じ参照先のキーフレームは 1 本にまとめる");
            var toM1 = refs.Single(e => e.TargetId == ObjectIdUtility.GetId(m1));
            StringAssert.Contains("3", toM1.MemberName);
        }
    }
}

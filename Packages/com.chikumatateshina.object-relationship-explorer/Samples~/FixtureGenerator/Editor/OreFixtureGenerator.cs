using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace ObjectRelationshipExplorer.Samples
{
    /// <summary>
    /// 手動確認用の Fixture を Assets/OREFixtures に生成する。
    /// 複数 Scene / Prefab Variant / Nested Prefab / Instance Override / Missing Reference / 配列参照 / UnityEvent / Animator を含む。
    /// UdonSharp / Udon Graph の Fixture は SDK 導入プロジェクトで手動作成してください（README 参照）。
    /// </summary>
    public static class OreFixtureGenerator
    {
        const string Root = "Assets/OREFixtures";

        [MenuItem("Tools/Object Relationship Explorer/Generate Fixtures")]
        public static void Generate()
        {
            if (!EditorUtility.DisplayDialog("Fixture の生成", $"{Root} にテスト用の Scene / Prefab / Animation を生成します。\n現在の Scene の変更は保存を確認します。", "生成", "キャンセル")) return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (!AssetDatabase.IsValidFolder(Root)) AssetDatabase.CreateFolder("Assets", "OREFixtures");

            // Animation
            var clip = new AnimationClip { name = "DoorOpen" };
            clip.SetCurve("Handle", typeof(Transform), "localPosition.y", AnimationCurve.Linear(0, 0, 1, 1));
            clip.SetCurve("Missing/Path", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0, 0, 1, 1));
            AssetDatabase.CreateAsset(clip, Root + "/DoorOpen.anim");
            var controller = AnimatorController.CreateAnimatorControllerAtPathWithClip(Root + "/Door.controller", clip);

            // Prefab / Nested / Variant
            var lamp = new GameObject("Lamp");
            new GameObject("Bulb").transform.SetParent(lamp.transform);
            var lampPrefab = PrefabUtility.SaveAsPrefabAsset(lamp, Root + "/Lamp.prefab");
            Object.DestroyImmediate(lamp);

            var door = new GameObject("Door");
            var handle = new GameObject("Handle");
            handle.transform.SetParent(door.transform);
            var nestedLamp = (GameObject)PrefabUtility.InstantiatePrefab(lampPrefab);
            nestedLamp.transform.SetParent(door.transform);
            var animator = door.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            var doorPrefab = PrefabUtility.SaveAsPrefabAsset(door, Root + "/Door.prefab");
            Object.DestroyImmediate(door);

            var variantSource = (GameObject)PrefabUtility.InstantiatePrefab(doorPrefab);
            variantSource.name = "DoorVariant";
            PrefabUtility.SaveAsPrefabAsset(variantSource, Root + "/DoorVariant.prefab");
            Object.DestroyImmediate(variantSource);

            // Scene: Main
            var main = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var d1 = (GameObject)PrefabUtility.InstantiatePrefab(doorPrefab, main);
            d1.name = "Door (overridden)";
            var d2 = (GameObject)PrefabUtility.InstantiatePrefab(doorPrefab, main);
            d2.transform.position = new Vector3(3, 0, 0);
            var button = new GameObject("Button");
            var relay = button.AddComponent<OreFixtureRelay>();
            relay.target = d1;
            relay.targets = new[] { d1, d2, null };
            relay.animator = d1.GetComponent<Animator>();
            UnityEventTools.AddBoolPersistentListener(relay.onPress, d1.SetActive, false);
            var missing = new GameObject("WillBeMissing");
            relay.missing = missing;
            Object.DestroyImmediate(missing);
            EditorSceneManager.SaveScene(main, Root + "/Main.unity");

            // Scene: Sub（未ロード Scene の横断検索用）
            var sub = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var d3 = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/DoorVariant.prefab"), sub);
            var cycleA = new GameObject("CycleA").AddComponent<OreFixtureRelay>();
            var cycleB = new GameObject("CycleB").AddComponent<OreFixtureRelay>();
            cycleA.target = cycleB.gameObject;
            cycleB.target = cycleA.gameObject;
            cycleA.animator = d3.GetComponent<Animator>();
            EditorSceneManager.SaveScene(sub, Root + "/Sub.unity");

            EditorSceneManager.OpenScene(Root + "/Main.unity");
            AssetDatabase.SaveAssets();
            Debug.Log("[ObjectRelationshipExplorer] Fixture を生成しました: " + Root + "（Sub.unity は閉じた状態で未ロード索引を確認できます）");
        }

        [MenuItem("Tools/Object Relationship Explorer/Delete Fixtures")]
        public static void Delete()
        {
            if (!AssetDatabase.IsValidFolder(Root)) return;
            if (!EditorUtility.DisplayDialog("Fixture の削除", $"{Root} を削除しますか？", "削除", "キャンセル")) return;
            if (SceneManager.GetActiveScene().path.StartsWith(Root)) EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Root);
        }
    }
}

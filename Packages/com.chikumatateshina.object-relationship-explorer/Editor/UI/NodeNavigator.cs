using System.IO;
using ObjectRelationshipExplorer.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ObjectRelationshipExplorer.Editor.UI
{
    /// <summary>
    /// Graph → Editor の移動。ロード済みなら Selection / Ping / Frame、未ロード Scene は確認ダイアログで明示操作を求める。
    /// Scene・Prefab を保存しない（Single で開く場合のみ Unity 標準の保存確認を表示）。
    /// </summary>
    public static class NodeNavigator
    {
        public static void Navigate(GraphNode node, GraphStore store, SelectionBridge bridge, bool frame)
        {
            if (node == null) return;
            var status = ObjectResolver.TryResolve(node.Id, out var obj);
            switch (status)
            {
                case ResolveStatus.Resolved:
                    bridge.PushToUnity(new[] { node.Id }, true);
                    if (frame && (obj is GameObject || obj is Component) && !EditorUtility.IsPersistent(obj) && SceneView.lastActiveSceneView != null)
                        SceneView.lastActiveSceneView.FrameSelected();
                    return;
                case ResolveStatus.SceneNotLoaded:
                    if (OpenSceneWithConfirmation(node) && ObjectResolver.TryResolve(node.Id, out obj) == ResolveStatus.Resolved)
                    {
                        bridge.PushToUnity(new[] { node.Id }, true);
                        if (frame && SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
                    }
                    return;
                case ResolveStatus.NotAnObject:
                    if (TryOpenSource(node, store)) return;
                    ShowNotification($"「{node.Name}」は解析上のノードのため Unity オブジェクトに対応しません。");
                    return;
                default:
                    if (node.HasFlag(NodeFlags.Missing))
                        ShowNotification("Missing のため移動できません。診断パネルを確認してください。");
                    else
                        ShowNotification($"「{node.Name}」を解決できません（削除済み・再生成・未保存などでIDが変化した可能性があります）。再解析してください。");
                    return;
            }
        }

        public static bool OpenSceneWithConfirmation(GraphNode node)
        {
            string scenePath = ObjectResolver.GetScenePath(node);
            if (string.IsNullOrEmpty(scenePath)) return false;
            if (ObjectResolver.IsSceneLoaded(scenePath)) return true;
            if (EditorApplication.isPlaying)
            {
                ShowNotification("Play Mode 中は Scene を開けません。");
                return false;
            }
            int choice = EditorUtility.DisplayDialogComplex(
                "Scene を開きますか？",
                $"「{node.Name}」は未ロードの Scene にあります:\n{scenePath}\n\n現在の Scene を保持したまま追加で開く（Additive）か、置き換えて開くかを選択してください。",
                "追加で開く (Additive)", "キャンセル", "置き換えて開く");
            if (choice == 1) return false;
            if (choice == 2)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            }
            else
            {
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            }
            return ObjectResolver.IsSceneLoaded(scenePath);
        }

        public static void Ping(GraphNode node)
        {
            if (node == null) return;
            var status = ObjectResolver.TryResolve(node.Id, out var obj);
            if (status == ResolveStatus.Resolved) EditorGUIUtility.PingObject(obj);
            else if (status == ResolveStatus.SceneNotLoaded)
            {
                var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(ObjectResolver.GetScenePath(node));
                if (sceneAsset != null) EditorGUIUtility.PingObject(sceneAsset);
            }
        }

        public static void OpenPrefabMode(GraphNode node)
        {
            string path = node?.AssetPath;
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab")) return;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null) AssetDatabase.OpenAsset(prefab);
        }

        /// <summary>ソース位置を持つノード/エッジ（メソッド・フィールド・Udon Graph 要素）を開く。</summary>
        public static bool TryOpenSource(GraphNode node, GraphStore store)
        {
            if (node == null) return false;
            foreach (var e in store.GetInEdges(node.Id))
            {
                if (e.Kind == EdgeKind.UdonDeclaration && !string.IsNullOrEmpty(e.SourceFile))
                    return OpenSourceFile(e.SourceFile, e.SourceLine);
            }
            if (!string.IsNullOrEmpty(node.AssetPath))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(node.AssetPath);
                if (asset != null)
                {
                    AssetDatabase.OpenAsset(asset);
                    return true;
                }
            }
            return false;
        }

        public static bool OpenSourceFile(string path, int line)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
            {
                if (!File.Exists(path)) return false;
                EditorUtility.OpenWithDefaultApp(path);
                return true;
            }
            return AssetDatabase.OpenAsset(asset, Mathf.Max(line, 1));
        }

        static void ShowNotification(string message)
        {
            var window = EditorWindow.focusedWindow;
            if (window != null) window.ShowNotification(new GUIContent(message), 3.0);
            else Debug.Log("[ObjectRelationshipExplorer] " + message);
        }
    }
}

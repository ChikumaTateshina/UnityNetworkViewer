using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityNetworkViewer.Editor
{
    public enum UnloadedSceneMode
    {
        /// <summary>テキストシリアライズの読み取りのみ（既定・安全）。</summary>
        TextIndexOnly,
        /// <summary>索引しない。</summary>
        Disabled,
    }

    /// <summary>
    /// プロジェクトごとのユーザー設定。UserSettings/ 以下に保存されリポジトリにはコミットされない想定。
    /// </summary>
    [FilePath("UserSettings/UnityNetworkViewer/Settings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class UnvSettings : ScriptableSingleton<UnvSettings>
    {
        [Tooltip("Packages/ 以下の Asset も索引対象にする（読み取り専用）。")]
        public bool includePackages;

        [Tooltip("変更を検知して自動で差分再解析する。")]
        public bool autoUpdate = true;

        [Tooltip("差分更新のデバウンス (ms)。")]
        public int debounceMilliseconds = 300;

        [Tooltip("1フレームあたりの解析時間予算 (ms)。")]
        public int frameBudgetMilliseconds = 12;

        public UnloadedSceneMode unloadedSceneMode = UnloadedSceneMode.TextIndexOnly;

        [Tooltip("索引をLibrary/以下にキャッシュし、再起動後に復元する。")]
        public bool useCache = true;

        [Tooltip("ウィンドウを開いたとき、プロジェクト全体の索引をバックグラウンドで開始する。")]
        public bool indexProjectOnOpen = true;

        [Tooltip("1 Component あたりに走査する SerializedProperty の上限。")]
        public int maxPropertiesPerObject = 20000;

        [Tooltip("初期表示の最大ノード数。")]
        public int maxDisplayNodes = 300;

        [Tooltip("SerializedReference として扱わないプロパティ名（propertyPath の末尾一致）。")]
        public List<string> excludedPropertyNames = new List<string>
        {
            "m_GameObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
            "m_CorrespondingSourceObject",
            "m_Father",
            "m_Script",
            // UdonBehaviour の内部表現（Udon Provider が変数名付きで解析する）
            "publicVariablesUnityEngineObjects",
            "serializedProgramAsset",
        };

        [Tooltip("UdonSharp ソースの静的制御解析を有効にする（VRChat SDK 導入時のみ）。")]
        public bool enableUdonSharpSourceAnalysis = true;

        [Tooltip("Scene View に関連オブジェクトの線と枠を描画する。")]
        public bool sceneViewOverlay;

        public void SaveSettings()
        {
            Save(true);
        }

        public bool IsExcludedProperty(string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath)) return false;
            foreach (var name in excludedPropertyNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (propertyPath == name ||
                    propertyPath.EndsWith("." + name, System.StringComparison.Ordinal) ||
                    propertyPath.StartsWith(name + ".", System.StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}

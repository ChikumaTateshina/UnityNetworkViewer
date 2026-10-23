using UnityNetworkViewer.Core;
using UnityEditor;
using UnityEngine;

namespace UnityNetworkViewer.Editor.UI
{
    /// <summary>
    /// 描画スタイル。色だけに依存しないよう、確度は線種（実線/破線/点線）、
    /// 関係種別は略号ラベル、方向は矢印で表す。
    /// </summary>
    public static class GraphStyles
    {
        public const float NodeWidth = 170f;
        public const float NodeHeight = 38f;

        public static bool Dark => EditorGUIUtility.isProSkin;

        public static Color Background => Dark ? new Color(0.13f, 0.13f, 0.14f) : new Color(0.86f, 0.86f, 0.87f);
        public static Color GridLine => Dark ? new Color(1f, 1f, 1f, 0.035f) : new Color(0f, 0f, 0f, 0.05f);
        public static Color Text => Dark ? new Color(0.92f, 0.92f, 0.92f) : new Color(0.08f, 0.08f, 0.08f);
        public static Color SubText => Dark ? new Color(0.65f, 0.65f, 0.68f) : new Color(0.3f, 0.3f, 0.32f);

        // ハイライト（仕様 3.1.4）: 選択=主色、参照元=橙、参照先=青緑、親子=中立、Prefab=紫
        public static readonly Color Selected = new Color(1f, 0.85f, 0.2f);
        public static readonly Color Incoming = new Color(0.95f, 0.55f, 0.16f);
        public static readonly Color Outgoing = new Color(0.12f, 0.72f, 0.66f);
        public static readonly Color Structural = new Color(0.62f, 0.62f, 0.66f);
        public static readonly Color Prefab = new Color(0.68f, 0.47f, 0.95f);
        public static readonly Color PathColor = new Color(1f, 0.3f, 0.45f);
        public static readonly Color Unresolved = new Color(0.9f, 0.3f, 0.3f);

        public static Color NodeFill(NodeKind kind)
        {
            float a = 1f;
            switch (kind)
            {
                case NodeKind.Scene: return Tint(0.25f, 0.33f, 0.45f, a);
                case NodeKind.GameObject: return Tint(0.27f, 0.30f, 0.36f, a);
                case NodeKind.Component: return Tint(0.22f, 0.32f, 0.30f, a);
                case NodeKind.PrefabAsset: return Tint(0.33f, 0.25f, 0.45f, a);
                case NodeKind.ScriptAsset: return Tint(0.25f, 0.30f, 0.22f, a);
                case NodeKind.UdonProgram: return Tint(0.20f, 0.36f, 0.25f, a);
                case NodeKind.UdonMethod:
                case NodeKind.UdonEvent: return Tint(0.30f, 0.38f, 0.20f, a);
                case NodeKind.UdonVariable: return Tint(0.24f, 0.34f, 0.18f, a);
                case NodeKind.AnimatorController:
                case NodeKind.AnimationClip:
                case NodeKind.AnimationPath: return Tint(0.42f, 0.25f, 0.33f, a);
                case NodeKind.UnresolvedReference: return Tint(0.45f, 0.18f, 0.18f, a);
                default: return Tint(0.30f, 0.30f, 0.30f, a);
            }
        }

        static Color Tint(float r, float g, float b, float a)
        {
            if (Dark) return new Color(r, g, b, a);
            // ライトスキンでは明るくする
            return new Color(0.55f + r * 0.8f, 0.55f + g * 0.8f, 0.55f + b * 0.8f, a);
        }

        public static string KindBadge(NodeKind kind)
        {
            switch (kind)
            {
                case NodeKind.Scene: return "SCENE";
                case NodeKind.GameObject: return "GO";
                case NodeKind.Component: return "COMP";
                case NodeKind.PrefabAsset: return "PREFAB";
                case NodeKind.ScriptAsset: return "SCRIPT";
                case NodeKind.UdonProgram: return "UDON";
                case NodeKind.UdonMethod: return "METHOD";
                case NodeKind.UdonEvent: return "EVENT";
                case NodeKind.UdonVariable: return "VAR";
                case NodeKind.AnimatorController: return "CTRL";
                case NodeKind.AnimationClip: return "CLIP";
                case NodeKind.AnimationPath: return "PATH";
                case NodeKind.UnresolvedReference: return "?";
                default: return "ASSET";
            }
        }

        /// <summary>関係種別の略号（エッジラベル・凡例）。</summary>
        public static string EdgeAbbrev(EdgeKind kind)
        {
            switch (kind)
            {
                case EdgeKind.ParentChild: return "child";
                case EdgeKind.OwnsComponent: return "comp";
                case EdgeKind.SerializedReference: return "ref";
                case EdgeKind.ScriptUsage: return "script";
                case EdgeKind.PrefabSource: return "prefab";
                case EdgeKind.PrefabVariant: return "variant";
                case EdgeKind.UdonVariableReference: return "udon-var";
                case EdgeKind.UdonProgramBinding: return "program";
                case EdgeKind.UdonProxyBinding: return "proxy";
                case EdgeKind.UdonDeclaration: return "decl";
                case EdgeKind.MethodCall: return "call";
                case EdgeKind.MethodControl: return "control";
                case EdgeKind.EventDispatch: return "event";
                case EdgeKind.AnimationBinding: return "anim";
                case EdgeKind.AnimatorClip: return "clip";
                case EdgeKind.UnityEventCall: return "uevent";
                case EdgeKind.AssetDependency: return "dep";
                default: return kind.ToString();
            }
        }

        /// <summary>非ハイライト時のエッジ基本色（関係カテゴリ）。</summary>
        public static Color EdgeBase(EdgeKind kind)
        {
            if (EdgeKindInfo.IsStructural(kind)) return Structural;
            if (EdgeKindInfo.IsPrefabRelation(kind)) return Prefab;
            switch (kind)
            {
                case EdgeKind.SerializedReference: return new Color(0.45f, 0.62f, 0.9f);
                case EdgeKind.UnityEventCall: return new Color(0.35f, 0.8f, 0.95f);
                case EdgeKind.ScriptUsage: return new Color(0.55f, 0.65f, 0.45f);
                case EdgeKind.AnimationBinding:
                case EdgeKind.AnimatorClip: return new Color(0.9f, 0.5f, 0.7f);
                case EdgeKind.AssetDependency: return new Color(0.5f, 0.5f, 0.5f);
                case EdgeKind.MethodControl:
                case EdgeKind.EventDispatch:
                case EdgeKind.MethodCall: return new Color(0.85f, 0.75f, 0.3f);
                default: return new Color(0.45f, 0.8f, 0.45f);
            }
        }

        /// <summary>確度ごとの線種（ダッシュ長, 間隔）。実線は (0,0)。</summary>
        public static Vector2 DashPattern(EvidenceLevel evidence)
        {
            switch (evidence)
            {
                case EvidenceLevel.Inferred: return new Vector2(9f, 5f);
                case EvidenceLevel.Unresolved: return new Vector2(2.5f, 4f);
                default: return Vector2.zero;
            }
        }

        public static string EvidenceLabel(EvidenceLevel e)
        {
            switch (e)
            {
                case EvidenceLevel.Confirmed: return "確定";
                case EvidenceLevel.Inferred: return "推定";
                default: return "未解決";
            }
        }

        public static string ToHtml(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);
    }
}

using System;

namespace ObjectRelationshipExplorer.Core
{
    /// <summary>ノードの種類。表示・フィルタ・レイアウトのグルーピングに使用する。</summary>
    public enum NodeKind
    {
        Scene,
        GameObject,
        Component,
        PrefabAsset,
        ScriptAsset,
        UdonProgram,
        UdonMethod,
        UdonEvent,
        UdonVariable,
        AnimatorController,
        AnimationClip,
        AnimationPath,
        OtherAsset,
        UnresolvedReference,
    }

    /// <summary>
    /// 関係の意味。方向は常に Source → Target。
    /// 確度は <see cref="EvidenceLevel"/> で別に表し、Kind には確度を含めない。
    /// </summary>
    public enum EdgeKind
    {
        /// <summary>Parent GameObject → Child GameObject（Scene → Root GameObject を含む）</summary>
        ParentChild,
        /// <summary>GameObject → Component</summary>
        OwnsComponent,
        /// <summary>参照元 Component → 参照先 Object（SerializedProperty.propertyPath を根拠とする）</summary>
        SerializedReference,
        /// <summary>MonoBehaviour → MonoScript</summary>
        ScriptUsage,
        /// <summary>Prefab Instance (Object) → Source Asset / Source Object</summary>
        PrefabSource,
        /// <summary>Prefab Variant → Base Prefab</summary>
        PrefabVariant,
        /// <summary>UdonBehaviour → Object（シリアライズ済み public variable）</summary>
        UdonVariableReference,
        /// <summary>UdonBehaviour → Udon Program Asset、Program Asset → ソーススクリプト</summary>
        UdonProgramBinding,
        /// <summary>UdonSharpBehaviour (Proxy) → UdonBehaviour</summary>
        UdonProxyBinding,
        /// <summary>Script/Program → Method/Event/Variable の宣言</summary>
        UdonDeclaration,
        /// <summary>Method → Method の静的呼出し</summary>
        MethodCall,
        /// <summary>Method/Behaviour → 操作対象（SetActive 等の操作APIの静的検出）</summary>
        MethodControl,
        /// <summary>Sender → Receiver/Event（SendCustomEvent 等）</summary>
        EventDispatch,
        /// <summary>AnimationClip → Path、Animator → 対象 Object</summary>
        AnimationBinding,
        /// <summary>AnimatorController/Animator → AnimationClip</summary>
        AnimatorClip,
        /// <summary>UnityEvent の発信元 Component → 対象 Object（PersistentCall）</summary>
        UnityEventCall,
        /// <summary>AssetDatabase 依存情報のみから得た粗い依存（バイナリScene等）</summary>
        AssetDependency,
    }

    /// <summary>
    /// 対象解決の確度。Confirmed は「参照が存在する」ことを表し、実行されることは意味しない。
    /// </summary>
    public enum EvidenceLevel
    {
        Confirmed,
        Inferred,
        Unresolved,
    }

    [Flags]
    public enum NodeFlags
    {
        None = 0,
        Inactive = 1 << 0,
        EditorOnly = 1 << 1,
        Hidden = 1 << 2,
        /// <summary>未保存などで session-scoped ID を使用している</summary>
        Transient = 1 << 3,
        /// <summary>未ロード Scene の索引（テキスト解析）から得たノード</summary>
        Unloaded = 1 << 4,
        /// <summary>参照先として仮に作られたノード（詳細は所有 Provider が補完する）</summary>
        Placeholder = 1 << 5,
        MissingScript = 1 << 6,
        PrefabInstance = 1 << 7,
        PrefabInstanceRoot = 1 << 8,
        AddedByOverride = 1 << 9,
        ExternalPackage = 1 << 10,
        Missing = 1 << 11,
        PrefabVariant = 1 << 12,
        UdonSharpProxy = 1 << 13,
        PrefabAssetContent = 1 << 14,
    }

    public enum RelationDirection
    {
        Outgoing,
        Incoming,
        Both,
    }

    public enum SelectionSetMode
    {
        Union,
        Intersection,
    }

    public enum DiagnosticSeverity
    {
        Info,
        Warning,
        Error,
    }

    public static class EdgeKindInfo
    {
        public static readonly EdgeKind[] All = (EdgeKind[])Enum.GetValues(typeof(EdgeKind));
        public static readonly NodeKind[] AllNodeKinds = (NodeKind[])Enum.GetValues(typeof(NodeKind));

        /// <summary>構造（所属・親子）を表す関係か。循環診断などで除外する。</summary>
        public static bool IsStructural(EdgeKind kind)
        {
            return kind == EdgeKind.ParentChild || kind == EdgeKind.OwnsComponent || kind == EdgeKind.UdonDeclaration;
        }

        public static bool IsPrefabRelation(EdgeKind kind)
        {
            return kind == EdgeKind.PrefabSource || kind == EdgeKind.PrefabVariant;
        }

        public static bool IsUdonRelation(EdgeKind kind)
        {
            switch (kind)
            {
                case EdgeKind.UdonVariableReference:
                case EdgeKind.UdonProgramBinding:
                case EdgeKind.UdonProxyBinding:
                case EdgeKind.UdonDeclaration:
                case EdgeKind.MethodCall:
                case EdgeKind.MethodControl:
                case EdgeKind.EventDispatch:
                    return true;
                default:
                    return false;
            }
        }
    }
}

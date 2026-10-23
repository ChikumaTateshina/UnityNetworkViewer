using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ObjectRelationshipExplorer.Core;
using UnityEditor;
using Object = UnityEngine.Object;

namespace ObjectRelationshipExplorer.Editor
{
    public struct SerializedReferenceInfo
    {
        public string PropertyPath;
        public Object Target;
        public bool IsMissing;
        public int MissingInstanceId;
        public bool IsPrefabOverride;
        /// <summary>UnityEvent の PersistentCall の対象（m_Target）なら true。</summary>
        public bool IsUnityEventTarget;
        public string UnityEventMethod;
        /// <summary>UnityEvent の呼出し引数（m_ObjectArgument）。</summary>
        public bool IsUnityEventArgument;
        public bool IsArrayElement;
        public bool IsManagedReference;
    }

    /// <summary>
    /// SerializedObject / SerializedProperty を走査して ObjectReference を列挙する。
    /// ApplyModifiedProperties 等は呼ばないため、対象を Dirty にしない。
    /// 数値配列（メッシュ頂点等）には入らない。
    /// </summary>
    public static class SerializedReferenceScanner
    {
        static readonly HashSet<string> PrimitiveElementTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "float", "double", "int", "unsigned int", "SInt8", "UInt8", "SInt16", "UInt16", "SInt32", "UInt32",
            "SInt64", "UInt64", "bool", "char", "string", "Vector2f", "Vector3f", "Vector4f", "Quaternionf",
            "ColorRGBA", "Matrix4x4f", "Rectf", "AABB", "BoneWeights4", "BitField", "Keyframe", "int2_storage",
            "int3_storage", "float2", "float3", "float4", "Vector2", "Vector3", "Vector4", "Color", "Color32",
        };

        static readonly Regex PersistentCallTarget = new Regex(@"^(.*m_PersistentCalls\.m_Calls\.Array\.data\[\d+\]\.)m_Target$", RegexOptions.Compiled);
        static readonly Regex PersistentCallArgument = new Regex(@"m_PersistentCalls\.m_Calls\.Array\.data\[\d+\]\.m_Arguments\.m_ObjectArgument$", RegexOptions.Compiled);

        /// <summary>ターゲット側に参照を持たない（構造情報として別 Provider が扱う）型。</summary>
        public static bool ShouldSkipObject(Object obj)
        {
            return obj is UnityEngine.Transform;
        }

        public static int Scan(Object source, OreSettings settings, List<SerializedReferenceInfo> output, out bool truncated)
        {
            truncated = false;
            if (source == null) return 0;
            int found = 0;
            int limit = Math.Max(1000, settings.maxPropertiesPerObject);
            using (var so = new SerializedObject(source))
            {
                var it = so.GetIterator();
                bool enter = true;
                int visited = 0;
                while (it.Next(enter))
                {
                    if (++visited > limit)
                    {
                        truncated = true;
                        break;
                    }
                    enter = ShouldEnter(it);
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;

                    string path = it.propertyPath;
                    if (settings.IsExcludedProperty(path)) continue;

                    var value = it.objectReferenceValue;
                    int instanceId = it.objectReferenceInstanceIDValue;
                    bool missing = value == null && instanceId != 0;
                    if (value == null && !missing) continue;

                    var info = new SerializedReferenceInfo
                    {
                        PropertyPath = path,
                        Target = value,
                        IsMissing = missing,
                        MissingInstanceId = instanceId,
                        IsPrefabOverride = SafePrefabOverride(it),
                        IsArrayElement = path.EndsWith("]", StringComparison.Ordinal),
                        IsManagedReference = path.Contains("managedReferences") || path.Contains("references.RefIds"),
                    };

                    var m = PersistentCallTarget.Match(path);
                    if (m.Success)
                    {
                        info.IsUnityEventTarget = true;
                        var methodProp = so.FindProperty(m.Groups[1].Value + "m_MethodName");
                        info.UnityEventMethod = methodProp != null ? methodProp.stringValue : null;
                    }
                    else if (PersistentCallArgument.IsMatch(path))
                    {
                        info.IsUnityEventArgument = true;
                    }

                    output.Add(info);
                    found++;
                }
            }
            return found;
        }

        static bool SafePrefabOverride(SerializedProperty p)
        {
            try
            {
                return p.prefabOverride;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static bool ShouldEnter(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.String:
                case SerializedPropertyType.ObjectReference:
                case SerializedPropertyType.Vector2:
                case SerializedPropertyType.Vector3:
                case SerializedPropertyType.Vector4:
                case SerializedPropertyType.Quaternion:
                case SerializedPropertyType.Rect:
                case SerializedPropertyType.Bounds:
                case SerializedPropertyType.Color:
                case SerializedPropertyType.AnimationCurve:
                case SerializedPropertyType.Gradient:
                case SerializedPropertyType.Vector2Int:
                case SerializedPropertyType.Vector3Int:
                case SerializedPropertyType.RectInt:
                case SerializedPropertyType.BoundsInt:
                case SerializedPropertyType.Hash128:
                    return false;
                case SerializedPropertyType.ManagedReference:
                    return p.hasChildren;
            }
            if (!p.hasChildren) return false;
            if (p.isArray)
            {
                string element = p.arrayElementType;
                if (element == null) return true;
                if (element.StartsWith("PPtr<", StringComparison.Ordinal)) return true;
                if (PrimitiveElementTypes.Contains(element)) return false;
                // 巨大なジェネリック配列は打ち切り（UI データ等）
                return p.arraySize <= 20000;
            }
            return true;
        }

        /// <summary>走査結果を AnalysisResult に変換する共通処理。</summary>
        public static void EmitEdges(AnalysisResult result, ObjectTable table, Object source, string sourceId,
            List<SerializedReferenceInfo> refs, string providerId)
        {
            foreach (var r in refs)
            {
                if (r.IsMissing)
                {
                    string missingId = NodeIds.Unresolved(sourceId, "missing:" + r.PropertyPath);
                    result.AddNode(new GraphNode
                    {
                        Id = missingId,
                        Kind = NodeKind.UnresolvedReference,
                        Name = "Missing (" + LastSegment(r.PropertyPath) + ")",
                        Flags = NodeFlags.Missing,
                        SceneGuid = table.SceneGuid,
                        AssetGuid = table.AssetGuid,
                        AssetPath = table.AssetPath,
                        ProviderId = providerId,
                    });
                    var me = result.AddEdge(new GraphEdge
                    {
                        SourceId = sourceId,
                        TargetId = missingId,
                        Kind = r.IsUnityEventTarget ? EdgeKind.UnityEventCall : EdgeKind.SerializedReference,
                        Evidence = EvidenceLevel.Unresolved,
                        PropertyPath = r.PropertyPath,
                        MemberName = r.UnityEventMethod,
                        ProvenanceSceneGuid = table.SceneGuid,
                        ProvenanceAssetGuid = table.AssetGuid,
                        Explanation = "参照先が存在しません（Missing Reference）",
                        AnalyzedAtUtcTicks = DateTime.UtcNow.Ticks,
                    });
                    if (r.IsPrefabOverride) me.AddQualifier("prefabOverride");
                    result.AddDiagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.MissingReference,
                        $"Missing Reference: {table.PathOf((source as UnityEngine.Component)?.gameObject)} {source.GetType().Name}.{r.PropertyPath}",
                        sourceId, table.AssetPath ?? table.Target.Path, r.PropertyPath);
                    continue;
                }

                string targetId = table.IdOf(r.Target);
                if (targetId == null) continue;
                // 自己参照は UnityEvent（自身のメソッド呼出し）のみ残す
                if (targetId == sourceId && !r.IsUnityEventTarget) continue;
                if (!result.ContainsNode(targetId)) result.AddNode(table.CreateNodeFor(r.Target, providerId));

                var edge = new GraphEdge
                {
                    SourceId = sourceId,
                    TargetId = targetId,
                    Kind = r.IsUnityEventTarget ? EdgeKind.UnityEventCall : EdgeKind.SerializedReference,
                    Evidence = EvidenceLevel.Confirmed,
                    PropertyPath = r.PropertyPath,
                    MemberName = r.IsUnityEventTarget ? r.UnityEventMethod : null,
                    ProvenanceSceneGuid = table.SceneGuid,
                    ProvenanceAssetGuid = table.AssetGuid,
                    AnalyzedAtUtcTicks = DateTime.UtcNow.Ticks,
                };
                if (r.IsUnityEventTarget)
                    edge.Explanation = string.IsNullOrEmpty(r.UnityEventMethod)
                        ? "UnityEvent PersistentCall（メソッド未設定）"
                        : "UnityEvent PersistentCall → " + r.UnityEventMethod + "（登録の存在を示し、発火は保証しない）";
                if (r.IsPrefabOverride) edge.AddQualifier("prefabOverride");
                if (r.IsArrayElement) edge.AddQualifier("arrayElement");
                if (r.IsUnityEventArgument) edge.AddQualifier("unityEventArgument");
                if (r.IsManagedReference) edge.AddQualifier("managedReference");
                result.AddEdge(edge);
            }
        }

        static string LastSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return "?";
            int i = path.LastIndexOf('.');
            return i < 0 ? path : path.Substring(i + 1);
        }
    }
}

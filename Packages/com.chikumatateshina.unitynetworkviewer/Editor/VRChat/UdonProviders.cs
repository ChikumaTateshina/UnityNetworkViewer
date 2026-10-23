using System;
using System.Collections.Generic;
using System.Reflection;
using UnityNetworkViewer.Core;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.Udon;
using Object = UnityEngine.Object;

namespace UnityNetworkViewer.Editor.VRChat
{
    internal static class UdonProviderIds
    {
        public const string Behaviour = "vrchat.udonBehaviour";
        public const string Program = "vrchat.udonProgram";
        public const string ControlBinding = "vrchat.udonControl";
    }

    internal static class UdonUtil
    {
        public static bool IsHierarchyTarget(AnalysisTarget t)
            => t.Kind == AnalysisTargetKind.LoadedScene ||
               (t.Kind == AnalysisTargetKind.Asset && t.Path != null && t.Path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));

        /// <summary>UdonSharp の内部管理用シンボルは参照として扱わない。</summary>
        public static bool IsInternalSymbol(string symbol) => symbol == null || symbol.StartsWith("___", StringComparison.Ordinal) || symbol.StartsWith("__", StringComparison.Ordinal);

        /// <summary>public variable の値を Object の列に展開する（配列は要素ごと）。</summary>
        public static IEnumerable<(Object obj, int index, bool missing)> ExpandObjects(object value)
        {
            if (value is Object single)
            {
                yield return (single, -1, single == null && !ReferenceEquals(single, null));
                yield break;
            }
            if (value is Array array && typeof(Object).IsAssignableFrom(array.GetType().GetElementType()))
            {
                for (int i = 0; i < array.Length; i++)
                {
                    var o = array.GetValue(i) as Object;
                    if (ReferenceEquals(o, null)) continue;
                    yield return (o, i, o == null);
                }
            }
        }

        /// <summary>Proxy (UdonSharpBehaviour) の場合は対応する UdonBehaviour に読み替える。</summary>
        public static Object NormalizeBehaviour(Object o)
        {
            if (o is UdonSharpBehaviour usb)
            {
                var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(usb);
                if (backing != null) return backing;
            }
            return o;
        }

        public static string GuidOf(Object asset)
        {
            if (asset == null) return null;
            string path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path)) return null;
            string guid = AssetDatabase.AssetPathToGUID(path);
            return string.IsNullOrEmpty(guid) ? null : guid;
        }
    }

    /// <summary>
    /// UdonBehaviour → Program Asset、UdonSharp Proxy ↔ UdonBehaviour、シリアライズ済み public variables の参照。
    /// UdonSharp の Proxy 側フィールド値と UdonBehaviour 側の値が食い違う場合は診断する。
    /// </summary>
    public sealed class UdonBehaviourProvider : IAnalysisProvider
    {
        public string Id => UdonProviderIds.Behaviour;
        public int Version => 1;
        public int Order => 50;

        public bool CanAnalyze(AnalysisTarget target) => UdonUtil.IsHierarchyTarget(target);

        public AnalysisResult Analyze(AnalysisTarget target, AnalysisContext context)
        {
            var result = new AnalysisResult(Id);
            var table = context.GetTable(target);
            if (!table.IsValid) return result;

            foreach (var c in table.Components)
            {
                if (!(c is UdonBehaviour ub)) continue;
                string ubId = table.IdOf(ub);
                if (!result.ContainsNode(ubId)) result.AddNode(table.CreateComponentNode(ub, Id));

                // Program
                var program = ub.programSource;
                if (program != null)
                {
                    string programId = ObjectIdUtility.GetAssetObjectId(program);
                    if (!result.ContainsNode(programId)) result.AddNode(ObjectIdUtility.CreatePlaceholder(program, programId));
                    result.AddEdge(new GraphEdge
                    {
                        SourceId = ubId, TargetId = programId, Kind = EdgeKind.UdonProgramBinding, Evidence = EvidenceLevel.Confirmed,
                        PropertyPath = "programSource", ProvenanceSceneGuid = table.SceneGuid, ProvenanceAssetGuid = table.AssetGuid,
                    });
                    string pg = UdonUtil.GuidOf(program);
                    if (pg != null) result.DependencyKeys.Add(pg);
                }
                else
                {
                    result.AddDiagnostic(DiagnosticSeverity.Info, DiagnosticCodes.UnresolvedTarget, $"UdonBehaviour に Program が設定されていません: {table.PathOf(ub.gameObject)}", ubId, target.Path);
                }

                // UdonSharp Proxy
                UdonSharpBehaviour proxy = null;
                try
                {
                    proxy = UdonSharpEditorUtility.GetProxyBehaviour(ub);
                }
                catch (Exception ex)
                {
                    result.AddDiagnostic(DiagnosticSeverity.Info, DiagnosticCodes.ProviderError, "UdonSharp Proxy を取得できません: " + ex.Message, ubId, target.Path);
                }
                if (proxy != null)
                {
                    string proxyId = table.IdOf(proxy);
                    var proxyNode = result.GetNode(proxyId) ?? result.AddNode(table.CreateNodeFor(proxy, Id));
                    proxyNode.Flags |= NodeFlags.UdonSharpProxy;
                    result.AddEdge(new GraphEdge
                    {
                        SourceId = proxyId, TargetId = ubId, Kind = EdgeKind.UdonProxyBinding, Evidence = EvidenceLevel.Confirmed,
                        Explanation = "UdonSharpEditorUtility.GetProxyBehaviour による対応", ProvenanceSceneGuid = table.SceneGuid,
                    });
                }

                // public variables
                var symbols = ub.publicVariables?.VariableSymbols;
                if (symbols == null) continue;
                foreach (var symbol in symbols)
                {
                    if (UdonUtil.IsInternalSymbol(symbol)) continue;
                    if (!ub.publicVariables.TryGetVariableValue(symbol, out object value) || value == null) continue;
                    foreach (var (obj, index, missing) in UdonUtil.ExpandObjects(value))
                    {
                        string path = "publicVariables." + symbol + (index >= 0 ? "[" + index + "]" : "");
                        if (missing)
                        {
                            string missingId = NodeIds.Unresolved(ubId, "missing:" + path);
                            result.AddNode(new GraphNode { Id = missingId, Kind = NodeKind.UnresolvedReference, Name = "Missing (" + symbol + ")", Flags = NodeFlags.Missing, SceneGuid = table.SceneGuid, ProviderId = Id });
                            result.AddEdge(new GraphEdge { SourceId = ubId, TargetId = missingId, Kind = EdgeKind.UdonVariableReference, Evidence = EvidenceLevel.Unresolved, PropertyPath = path, MemberName = symbol, Explanation = "Udon 変数の参照先が存在しません" });
                            result.AddDiagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.MissingReference, $"Udon 変数 {symbol} が Missing: {table.PathOf(ub.gameObject)}", ubId, target.Path, path);
                            continue;
                        }
                        var normalized = obj;
                        string targetId = table.IdOf(normalized);
                        if (targetId == null || targetId == ubId) continue;
                        if (!result.ContainsNode(targetId)) result.AddNode(table.CreateNodeFor(normalized, Id));
                        var e = result.AddEdge(new GraphEdge
                        {
                            SourceId = ubId, TargetId = targetId, Kind = EdgeKind.UdonVariableReference, Evidence = EvidenceLevel.Confirmed,
                            PropertyPath = path, MemberName = symbol, ProvenanceSceneGuid = table.SceneGuid, ProvenanceAssetGuid = table.AssetGuid,
                            Explanation = "UdonBehaviour.publicVariables のシリアライズ値",
                        });
                        if (index >= 0) e.AddQualifier("arrayElement");
                        string assetGuid = NodeIds.TryGetAssetGuid(targetId);
                        if (assetGuid != null) result.DependencyKeys.Add(assetGuid);
                    }
                }

                if (proxy != null) CheckProxyConsistency(result, table, ub, ubId, proxy, target);
            }
            return result;
        }

        /// <summary>Proxy の C# フィールド値と UdonBehaviour の変数値を比較し、食い違いを診断する（読み取りのみ）。</summary>
        static void CheckProxyConsistency(AnalysisResult result, ObjectTable table, UdonBehaviour ub, string ubId, UdonSharpBehaviour proxy, AnalysisTarget target)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var type = proxy.GetType(); type != null && type != typeof(UdonSharpBehaviour); type = type.BaseType)
            {
                foreach (var field in type.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(Object).IsAssignableFrom(field.FieldType)) continue;
                    if (!field.IsPublic && field.GetCustomAttribute<SerializeField>() == null) continue;
                    if (!ub.publicVariables.TryGetVariableValue(field.Name, out object udonValue)) continue;
                    var proxyValue = field.GetValue(proxy) as Object;
                    var uv = udonValue as Object;
                    bool proxyNull = proxyValue == null, udonNull = uv == null;
                    if (proxyNull && udonNull) continue;
                    var nu = UdonUtil.NormalizeBehaviour(uv);
                    var np = UdonUtil.NormalizeBehaviour(proxyValue);
                    if (proxyNull != udonNull || nu != np)
                    {
                        result.AddDiagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.ValueMismatch,
                            $"UdonSharp Proxy と UdonBehaviour の値が不一致: {table.PathOf(ub.gameObject)}.{field.Name}（Proxy: {(proxyNull ? "null" : proxyValue.name)} / Udon: {(udonNull ? "null" : uv.name)}）。実行時は UdonBehaviour 側の値が使われます",
                            ubId, target.Path, "publicVariables." + field.Name);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Udon Program Asset（UdonSharp / Udon Graph）。UdonSharp はソーススクリプトとの対応、
    /// Udon Graph はイベント・変数の宣言と、イベントから到達可能な操作（推定）をプログラム単位で表す。
    /// </summary>
    public sealed class UdonProgramProvider : IAnalysisProvider, IAssetDiscoveryProvider
    {
        public string Id => UdonProviderIds.Program;
        public int Version => 1;
        public int Order => 55;
        public IEnumerable<string> AssetSearchFilters => new[] { "t:UdonSharpProgramAsset", "t:UdonGraphProgramAsset" };

        public bool CanAnalyze(AnalysisTarget target)
        {
            if (target.Kind != AnalysisTargetKind.Asset || target.Path == null || !target.Path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) return false;
            var type = AssetDatabase.GetMainAssetTypeAtPath(target.Path);
            return type != null && typeof(AbstractUdonProgramSource).IsAssignableFrom(type);
        }

        public AnalysisResult Analyze(AnalysisTarget target, AnalysisContext context)
        {
            var result = new AnalysisResult(Id);
            var program = AssetDatabase.LoadMainAssetAtPath(target.Path) as AbstractUdonProgramSource;
            if (program == null) return result;
            string programId = NodeIds.Asset(target.Guid);
            result.AddNode(new GraphNode
            {
                Id = programId, Kind = NodeKind.UdonProgram, Name = program.name, TypeName = program.GetType().Name,
                AssetGuid = target.Guid, AssetPath = target.Path, ProviderId = Id,
                Flags = ObjectIdUtility.IsPackagePath(target.Path) ? NodeFlags.ExternalPackage : NodeFlags.None,
            });

            if (program is UdonSharpProgramAsset sharp)
            {
                if (sharp.sourceCsScript != null)
                {
                    string scriptId = ObjectIdUtility.GetAssetObjectId(sharp.sourceCsScript);
                    if (!result.ContainsNode(scriptId)) result.AddNode(ObjectIdUtility.CreatePlaceholder(sharp.sourceCsScript, scriptId));
                    result.AddEdge(new GraphEdge
                    {
                        SourceId = programId, TargetId = scriptId, Kind = EdgeKind.UdonProgramBinding, Evidence = EvidenceLevel.Confirmed,
                        PropertyPath = "sourceCsScript", ProvenanceAssetGuid = target.Guid, Explanation = "UdonSharpProgramAsset のソーススクリプト",
                    });
                    string sg = NodeIds.TryGetAssetGuid(scriptId);
                    if (sg != null) result.DependencyKeys.Add(sg);
                }
                else
                {
                    result.AddDiagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.UnresolvedTarget, "UdonSharpProgramAsset にソーススクリプトがありません", programId, target.Path);
                }
                // ソース解析（メソッド・フィールド・制御）は UdonSharpSourceProvider（Roslyn）が担当
                if (!ProgramModelCache.SharpAnalyzerAvailable)
                    result.AddDiagnostic(DiagnosticSeverity.Info, DiagnosticCodes.Unsupported, "UdonSharp ソース解析（Roslyn）が利用できないため、制御関係は解析していません。参照解析は利用できます。", programId, target.Path);
                return result;
            }

            var model = ProgramModelCache.Get(program);
            if (model != null) ProgramModelEmitter.Emit(result, model, programId, Id);
            return result;
        }
    }

    /// <summary>ProgramModel をプログラム単位のグラフ（宣言・呼出し・操作）として出力する共通処理。</summary>
    public static class ProgramModelEmitter
    {
        public static void Emit(AnalysisResult result, ProgramModel model, string ownerNodeId, string providerId)
        {
            foreach (var d in model.Declarations)
            {
                var kind = d.Kind == DeclarationKind.Method ? NodeKind.UdonMethod
                         : d.Kind == DeclarationKind.Event ? NodeKind.UdonEvent
                         : NodeKind.UdonVariable;
                result.AddNode(new GraphNode
                {
                    Id = d.Id,
                    Kind = kind,
                    Name = d.Kind == DeclarationKind.Method ? d.Name + "()" : d.Name,
                    TypeName = (d.IsPublic ? "public " : "") + (d.TypeName ?? d.Kind.ToString()),
                    AssetGuid = model.ScriptGuid ?? model.ProgramGuid,
                    AssetPath = model.ScriptPath ?? model.ProgramPath,
                    HierarchyPath = (model.ClassName ?? "") + "." + d.Name,
                    ProviderId = providerId,
                });
                result.AddEdge(new GraphEdge
                {
                    SourceId = ownerNodeId, TargetId = d.Id, Kind = EdgeKind.UdonDeclaration, Evidence = EvidenceLevel.Confirmed,
                    MemberName = d.Name, SourceFile = model.ScriptPath ?? model.ProgramPath, SourceLine = d.Line, ProviderId = providerId,
                });
            }

            foreach (var call in model.Calls)
            {
                if (!result.ContainsNode(call.FromId) || !result.ContainsNode(call.ToId)) continue;
                var e = result.AddEdge(new GraphEdge
                {
                    SourceId = call.FromId, TargetId = call.ToId, Kind = EdgeKind.MethodCall, Evidence = EvidenceLevel.Inferred,
                    MemberName = call.ToName, SourceFile = model.ScriptPath, SourceLine = call.Line, ProviderId = providerId,
                    Explanation = "同一クラス内のメソッド呼出し（静的解析）",
                });
                if (call.Conditional) e.AddQualifier("mayEdge");
            }

            foreach (var f in model.Facts)
            {
                if (f.EntryId == null || !result.ContainsNode(f.EntryId)) continue;
                string targetId;
                EvidenceLevel evidence = EvidenceLevel.Inferred;
                if (f.Receiver == ReceiverKind.Variable && f.VariableId != null)
                {
                    targetId = f.VariableId;
                    if (!result.ContainsNode(targetId))
                        result.AddNode(new GraphNode { Id = targetId, Kind = NodeKind.UdonVariable, Name = f.Variable, AssetGuid = model.ScriptGuid ?? model.ProgramGuid, ProviderId = providerId, Flags = NodeFlags.Placeholder });
                }
                else if (f.Receiver == ReceiverKind.Self)
                {
                    targetId = ownerNodeId;
                }
                else
                {
                    targetId = NodeIds.Unresolved(f.EntryId, "recv:" + f.Api + ":" + f.Line + ":" + f.GraphNodeUid);
                    result.AddNode(new GraphNode { Id = targetId, Kind = NodeKind.UnresolvedReference, Name = "(動的対象) " + f.Api, ProviderId = providerId });
                    evidence = EvidenceLevel.Unresolved;
                }
                if (f.IsDispatch && f.EventName == null) evidence = EvidenceLevel.Unresolved;

                var e = result.AddEdge(new GraphEdge
                {
                    SourceId = f.EntryId, TargetId = targetId,
                    Kind = f.IsDispatch ? EdgeKind.EventDispatch : EdgeKind.MethodControl,
                    Evidence = evidence,
                    PropertyPath = f.Variable,
                    MemberName = f.IsDispatch ? $"{f.Api}(\"{f.EventName ?? "?"}\")" : f.Api,
                    SourceFile = f.SourceFile, SourceLine = f.Line, ProviderId = providerId,
                    Explanation = DescribeFact(f, model),
                });
                ApplyQualifiers(e, f);
            }

            foreach (var d in model.Diagnostics)
            {
                var copy = d.Clone();
                copy.ProviderId = providerId;
                if (copy.NodeId == null) copy.NodeId = ownerNodeId;
                result.Diagnostics.Add(copy);
            }
        }

        public static void ApplyQualifiers(GraphEdge e, OperationFact f)
        {
            e.AddQualifier("op=" + f.Category);
            if (f.Conditional) e.AddQualifier("mayEdge");
            if (f.ArrayElement) e.AddQualifier("arrayCandidate");
            if (f.IsNetwork) e.AddQualifier("network" + (string.IsNullOrEmpty(f.NetworkTarget) ? "" : "=" + f.NetworkTarget));
            if (!string.IsNullOrEmpty(f.Via)) e.AddQualifier("via=" + f.Via);
            if (f.GraphNodeUid != null) e.AddQualifier("graphNode=" + f.GraphNodeUid);
        }

        public static string DescribeFact(OperationFact f, ProgramModel model)
        {
            var parts = new List<string>
            {
                model.Mode == "udon-graph" ? "Udon Graph の静的解析による推定" : "UdonSharp ソースの静的解析（構文ベース）による推定",
                "実行されることは保証しない",
            };
            if (f.Conditional) parts.Add("条件分岐/ループ内（may-edge）");
            if (f.ArrayElement) parts.Add("配列要素（どの要素かは実行時に決まる）");
            if (f.IsNetwork) parts.Add("ネットワークイベント（遠隔実行を確定扱いしない）");
            if (!string.IsNullOrEmpty(f.Via)) parts.Add("経由: " + f.Via);
            if (f.ReachableFrom.Count > 0) parts.Add("到達元: " + string.Join(", ", f.ReachableFrom));
            if (!string.IsNullOrEmpty(f.UnresolvedReason)) parts.Add(f.UnresolvedReason);
            return string.Join(" / ", parts);
        }
    }

    /// <summary>
    /// プログラム単位の操作（ProgramModel）を UdonBehaviour インスタンスの変数値と結合し、
    /// 「Behaviour → 操作対象 Object」の推定エッジを作る。SendCustomEvent は受信側のイベント有無も検査する。
    /// </summary>
    public sealed class UdonControlBindingProvider : IAnalysisProvider
    {
        public string Id => UdonProviderIds.ControlBinding;
        public int Version => 1;
        public int Order => 60;

        public bool CanAnalyze(AnalysisTarget target) => UdonUtil.IsHierarchyTarget(target);

        public AnalysisResult Analyze(AnalysisTarget target, AnalysisContext context)
        {
            var result = new AnalysisResult(Id);
            var table = context.GetTable(target);
            if (!table.IsValid) return result;

            foreach (var c in table.Components)
            {
                if (!(c is UdonBehaviour ub) || ub.programSource == null) continue;
                var model = ProgramModelCache.Get(ub.programSource);
                if (model == null || model.Facts.Count == 0) continue;
                if (model.ProgramGuid != null) result.DependencyKeys.Add(model.ProgramGuid);
                if (model.ScriptGuid != null) result.DependencyKeys.Add(model.ScriptGuid);

                string ubId = table.IdOf(ub);
                if (!result.ContainsNode(ubId)) result.AddNode(table.CreateComponentNode(ub, Id));

                foreach (var f in model.Facts)
                {
                    foreach (var (obj, index, unresolvedReason) in ResolveTargets(ub, f))
                    {
                        string targetId;
                        EvidenceLevel evidence = EvidenceLevel.Inferred;
                        string extra = null;
                        if (obj == null)
                        {
                            targetId = NodeIds.Unresolved(ubId, "ctl:" + f.Api + ":" + f.Variable + ":" + f.Line + ":" + f.GraphNodeUid);
                            if (!result.ContainsNode(targetId))
                                result.AddNode(new GraphNode { Id = targetId, Kind = NodeKind.UnresolvedReference, Name = "(未解決) " + (f.Variable ?? f.Api), SceneGuid = table.SceneGuid, ProviderId = Id });
                            evidence = EvidenceLevel.Unresolved;
                            extra = unresolvedReason;
                        }
                        else
                        {
                            targetId = table.IdOf(obj);
                            if (targetId == null) continue;
                            if (!result.ContainsNode(targetId)) result.AddNode(table.CreateNodeFor(obj, Id));
                        }

                        if (f.IsDispatch)
                        {
                            if (f.EventName == null)
                            {
                                evidence = EvidenceLevel.Unresolved;
                            }
                            else if (obj is UdonBehaviour receiver)
                            {
                                var receiverModel = ProgramModelCache.Get(receiver.programSource);
                                if (receiverModel != null && receiverModel.Declarations.Count > 0 && !receiverModel.DeclaresEvent(f.EventName))
                                {
                                    evidence = EvidenceLevel.Unresolved;
                                    extra = $"受信側プログラムにイベント「{f.EventName}」が見つかりません";
                                }
                            }
                        }

                        var e = result.AddEdge(new GraphEdge
                        {
                            SourceId = ubId,
                            TargetId = targetId,
                            Kind = f.IsDispatch ? EdgeKind.EventDispatch : EdgeKind.MethodControl,
                            Evidence = evidence,
                            PropertyPath = f.Variable == null ? null : f.Variable + (index >= 0 ? "[" + index + "]" : ""),
                            MemberName = $"{f.Entry}: " + (f.IsDispatch ? $"{f.Api}(\"{f.EventName ?? "?"}\")" : f.Api),
                            SourceFile = f.SourceFile,
                            SourceLine = f.Line,
                            ProvenanceSceneGuid = table.SceneGuid,
                            ProvenanceAssetGuid = model.ProgramGuid,
                            Explanation = ProgramModelEmitter.DescribeFact(f, model) + (extra != null ? " / " + extra : ""),
                        });
                        ProgramModelEmitter.ApplyQualifiers(e, f);
                        if (index >= 0) e.AddQualifier("arrayElement");
                    }
                }
            }
            return result;
        }

        /// <summary>操作対象の候補。null は未解決（理由付き）。</summary>
        static IEnumerable<(Object obj, int index, string reason)> ResolveTargets(UdonBehaviour ub, OperationFact f)
        {
            switch (f.Receiver)
            {
                case ReceiverKind.Self:
                    yield return (SelfTarget(ub, f), -1, null);
                    yield break;
                case ReceiverKind.Variable:
                {
                    if (!ub.publicVariables.TryGetVariableValue(f.Variable, out object value) || value == null)
                    {
                        yield return (null, -1, $"変数「{f.Variable}」に値が設定されていない（または非公開変数で実行時に代入される）");
                        yield break;
                    }
                    bool any = false;
                    foreach (var (obj, index, missing) in UdonUtil.ExpandObjects(value))
                    {
                        if (missing) continue;
                        any = true;
                        var target = f.IsDispatch ? UdonUtil.NormalizeBehaviour(obj) : AdjustForOperation(obj, f);
                        yield return (target, index, null);
                    }
                    if (!any) yield return (null, -1, $"変数「{f.Variable}」の値が空です");
                    yield break;
                }
                default:
                    yield return (null, -1, f.UnresolvedReason ?? "静的に対象を特定できません");
                    yield break;
            }
        }

        static Object SelfTarget(UdonBehaviour ub, OperationFact f)
        {
            if (f.IsDispatch || f.Category == OperationRules.ProgramVariable) return ub;
            return AdjustForOperation(ub.gameObject, f);
        }

        /// <summary>操作の型に合わせて対象を Component へ寄せる（Animator.SetTrigger なら Animator）。</summary>
        static Object AdjustForOperation(Object obj, OperationFact f)
        {
            GameObject go = obj as GameObject ?? (obj as Component)?.gameObject;
            if (go == null || string.IsNullOrEmpty(f.ReceiverType)) return obj;
            if (f.ReceiverType == "GameObject") return go;
            if (f.ReceiverType == "Transform") return go.transform;
            if (obj is Component comp && comp.GetType().Name == f.ReceiverType) return obj;
            foreach (var cc in go.GetComponents<Component>())
                if (cc != null && cc.GetType().Name == f.ReceiverType) return cc;
            return obj;
        }
    }
}

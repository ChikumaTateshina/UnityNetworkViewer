using System;
using System.Collections.Generic;
using System.Linq;
using UnityNetworkViewer.Core;
using VRC.Udon.Editor.ProgramSources.UdonGraphProgram;
using VRC.Udon.Graph;

namespace UnityNetworkViewer.Editor.VRChat
{
    /// <summary>
    /// Udon Graph の静的解析。SDK が公開する UdonGraphData（nodes / fullName / nodeUIDs / flowUIDs / nodeValues）のみを読む。
    /// イベントノードからフローを辿り、到達可能な操作ノードの対象（変数・自身）を推定する。
    /// グラフ形式は SDK のバージョンに依存するため、想定外のノードは集計して診断に出す。
    /// </summary>
    public static class UdonGraphAnalyzer
    {
        const int MaxVisitedPerEvent = 5000;

        public static ProgramModel Analyze(UdonGraphProgramAsset asset, string guid, string path)
        {
            var model = new ProgramModel { ProgramGuid = guid, ProgramPath = path, Mode = "udon-graph", ClassName = asset.name };
            var data = asset.GetGraphData();
            if (data == null || data.nodes == null)
            {
                model.Diagnostics.Add(new AnalysisDiagnostic { Severity = DiagnosticSeverity.Info, Code = DiagnosticCodes.Unsupported, Message = "Udon Graph データがありません", AssetPath = path });
                return model;
            }

            var byUid = new Dictionary<string, UdonNodeData>(StringComparer.Ordinal);
            foreach (var n in data.nodes)
                if (n != null && !string.IsNullOrEmpty(n.uid)) byUid[n.uid] = n;

            // 変数
            var varByUid = new Dictionary<string, Declaration>(StringComparer.Ordinal);
            foreach (var n in data.nodes)
            {
                if (n?.fullName == null || !n.fullName.StartsWith("Variable_", StringComparison.Ordinal)) continue;
                string name = Value(n, 1) as string;
                if (string.IsNullOrEmpty(name)) continue;
                var decl = new Declaration
                {
                    Kind = DeclarationKind.Variable,
                    Name = name,
                    Id = NodeIds.UdonVariable(guid, name),
                    TypeName = OperationRules.FromUdonTypeToken(n.fullName.Substring("Variable_".Length)),
                    IsPublic = Value(n, 2) is bool b && b,
                    IsArray = n.fullName.EndsWith("Array", StringComparison.Ordinal),
                };
                model.Declarations.Add(decl);
                varByUid[n.uid] = decl;
            }

            var unclassified = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var ev in data.nodes)
            {
                if (ev?.fullName == null || !ev.fullName.StartsWith("Event_", StringComparison.Ordinal)) continue;
                string eventName = ev.fullName == "Event_Custom" ? Value(ev, 0) as string : ev.fullName.Substring("Event_".Length);
                if (string.IsNullOrEmpty(eventName)) continue;
                var evDecl = new Declaration { Kind = DeclarationKind.Event, Name = eventName, Id = NodeIds.UdonEvent(guid, eventName), IsPublic = true };
                if (model.FindDeclaration(eventName, DeclarationKind.Event) == null) model.Declarations.Add(evDecl);
                model.AddEvent(eventName);

                // フロー探索（分岐・ループ以降は条件付き）
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var queue = new Queue<(UdonNodeData node, bool conditional)>();
                foreach (var next in FlowTargets(ev, byUid)) queue.Enqueue((next, false));
                while (queue.Count > 0 && visited.Count < MaxVisitedPerEvent)
                {
                    var (node, conditional) = queue.Dequeue();
                    if (!visited.Add(node.uid)) continue;
                    bool branch = node.fullName == "Branch" || node.fullName == "For" || node.fullName == "While" || node.fullName.StartsWith("Switch", StringComparison.Ordinal);
                    AnalyzeNode(model, guid, path, eventName, evDecl.Id, node, conditional, byUid, varByUid, unclassified);
                    foreach (var next in FlowTargets(node, byUid)) queue.Enqueue((next, conditional || branch));
                }
            }

            if (unclassified.Count > 0)
            {
                var top = unclassified.OrderByDescending(kv => kv.Value).Take(5).Select(kv => kv.Key);
                model.Diagnostics.Add(new AnalysisDiagnostic
                {
                    Severity = DiagnosticSeverity.Info,
                    Code = DiagnosticCodes.Unsupported,
                    Message = $"操作ルール未対応の Udon ノード {unclassified.Count} 種（例: {string.Join(", ", top)}）は制御関係として扱っていません",
                    AssetPath = path,
                });
            }
            return model;
        }

        static IEnumerable<UdonNodeData> FlowTargets(UdonNodeData n, Dictionary<string, UdonNodeData> byUid)
        {
            if (n.flowUIDs == null) yield break;
            foreach (var f in n.flowUIDs)
            {
                if (string.IsNullOrEmpty(f)) continue;
                string uid = f.Split('|')[0];
                if (byUid.TryGetValue(uid, out var t)) yield return t;
            }
        }

        static void AnalyzeNode(ProgramModel model, string guid, string path, string eventName, string eventId, UdonNodeData node, bool conditional,
            Dictionary<string, UdonNodeData> byUid, Dictionary<string, Declaration> varByUid, Dictionary<string, int> unclassified)
        {
            string full = node.fullName;
            int sep = full.IndexOf(".__", StringComparison.Ordinal);
            if (sep <= 0) return; // 制御フロー・変数アクセス等
            string typeToken = full.Substring(0, sep);
            string rest = full.Substring(sep + 3);
            int end = rest.IndexOf("__", StringComparison.Ordinal);
            string method = end > 0 ? rest.Substring(0, end) : rest;
            string type = OperationRules.FromUdonTypeToken(typeToken);

            var rule = OperationRules.ClassifyStatic(type, method);
            bool isStatic = rule != null;
            if (rule == null) rule = OperationRules.Classify(type, method);
            if (rule == null)
            {
                if (!method.StartsWith("get_", StringComparison.Ordinal) && !method.StartsWith("op_", StringComparison.Ordinal))
                {
                    unclassified.TryGetValue(type + "." + method, out int c);
                    unclassified[type + "." + method] = c + 1;
                }
                return;
            }

            int targetInput = isStatic ? rule.TargetArgument : 0;
            var fact = new OperationFact
            {
                Entry = eventName,
                EntryId = eventId,
                ReceiverType = type,
                Category = rule.Category,
                Api = rule.IsDispatch ? type + "." + method : rule.Api,
                IsDispatch = rule.IsDispatch,
                IsNetwork = rule.IsNetwork,
                Conditional = conditional,
                GraphNodeUid = node.uid,
                SourceFile = path,
            };
            ResolveInput(node, targetInput, !isStatic, byUid, varByUid, fact, 0);

            if (rule.IsDispatch)
            {
                int eventInput = rule.EventNameArgument + 1;
                fact.EventName = ResolveString(node, eventInput, byUid);
                if (fact.EventName == null) fact.UnresolvedReason = "イベント名が動的に決まるため特定できません";
                if (rule.IsNetwork) fact.NetworkTarget = Value(node, 1)?.ToString();
            }
            model.Facts.Add(fact);
        }

        static void ResolveInput(UdonNodeData node, int index, bool isInstance, Dictionary<string, UdonNodeData> byUid,
            Dictionary<string, Declaration> varByUid, OperationFact fact, int depth)
        {
            string link = node.nodeUIDs != null && index < node.nodeUIDs.Length ? node.nodeUIDs[index] : null;
            if (string.IsNullOrEmpty(link))
            {
                if (isInstance)
                {
                    // 未接続のインスタンス入力は自身（this）として扱われる
                    fact.Receiver = ReceiverKind.Self;
                }
                else
                {
                    fact.Receiver = ReceiverKind.Unresolved;
                    fact.UnresolvedReason = "対象の入力が未接続です";
                }
                return;
            }
            if (!byUid.TryGetValue(link.Split('|')[0], out var src))
            {
                fact.Receiver = ReceiverKind.Unresolved;
                fact.UnresolvedReason = "接続先ノードが見つかりません";
                return;
            }
            if (src.fullName == "Get_Variable")
            {
                string varUid = Value(src, 0) as string;
                if (varUid != null && varByUid.TryGetValue(varUid, out var decl))
                {
                    fact.Receiver = ReceiverKind.Variable;
                    fact.Variable = decl.Name;
                    fact.VariableId = decl.Id;
                    fact.ArrayElement |= decl.IsArray;
                    return;
                }
                fact.Receiver = ReceiverKind.Unresolved;
                fact.UnresolvedReason = "参照する変数が見つかりません";
                return;
            }
            if (src.fullName.IndexOf("This", StringComparison.Ordinal) >= 0 && src.fullName.StartsWith("Const_", StringComparison.Ordinal))
            {
                fact.Receiver = ReceiverKind.Self;
                return;
            }
            // gameObject / transform / GetComponent / 配列要素の取得は入力側を辿る
            int sep = src.fullName.IndexOf(".__", StringComparison.Ordinal);
            if (sep > 0 && depth < 4)
            {
                string rest = src.fullName.Substring(sep + 3);
                if (rest.StartsWith("get_gameObject", StringComparison.Ordinal) || rest.StartsWith("get_transform", StringComparison.Ordinal) ||
                    rest.StartsWith("GetComponent", StringComparison.Ordinal) || rest.StartsWith("Get__", StringComparison.Ordinal) || rest.StartsWith("Get_", StringComparison.Ordinal))
                {
                    if (rest.StartsWith("GetComponent", StringComparison.Ordinal)) fact.Via = "GetComponent";
                    if (rest.StartsWith("Get", StringComparison.Ordinal) && src.fullName.Contains("Array")) fact.ArrayElement = true;
                    ResolveInput(src, 0, true, byUid, varByUid, fact, depth + 1);
                    return;
                }
            }
            fact.Receiver = ReceiverKind.Unresolved;
            fact.UnresolvedReason = "計算結果・動的探索（" + src.fullName + "）のため対象を特定できません";
        }

        static string ResolveString(UdonNodeData node, int index, Dictionary<string, UdonNodeData> byUid)
        {
            string link = node.nodeUIDs != null && index < node.nodeUIDs.Length ? node.nodeUIDs[index] : null;
            if (string.IsNullOrEmpty(link)) return Value(node, index) as string;
            if (byUid.TryGetValue(link.Split('|')[0], out var src) && src.fullName != null && src.fullName.StartsWith("Const_SystemString", StringComparison.Ordinal))
                return Value(src, 0) as string;
            return null;
        }

        static object Value(UdonNodeData n, int i)
        {
            if (n.nodeValues == null || i < 0 || i >= n.nodeValues.Length || n.nodeValues[i] == null) return null;
            try
            {
                return n.nodeValues[i].Deserialize();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

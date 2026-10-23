using System;
using System.Collections.Generic;
using UnityNetworkViewer.Core;
using UdonSharp;
using UnityEditor;
using VRC.Udon;
using VRC.Udon.Editor.ProgramSources.UdonGraphProgram;

namespace UnityNetworkViewer.Editor.VRChat
{
    public enum ReceiverKind
    {
        /// <summary>自身（this / 未接続のインスタンス入力）。</summary>
        Self,
        /// <summary>フィールド / Udon 変数。</summary>
        Variable,
        /// <summary>動的探索・引数・計算結果など、静的に特定できない。</summary>
        Unresolved,
    }

    /// <summary>プログラム単位の静的解析で得た「操作」。インスタンスの変数値と結合して対象が決まる。</summary>
    public sealed class OperationFact
    {
        public string Entry;
        public string EntryId;
        public ReceiverKind Receiver;
        public string Variable;
        public string VariableId;
        public bool ArrayElement;
        public string ReceiverType;
        public string Category;
        public string Api;
        public bool IsDispatch;
        public string EventName;
        public bool IsNetwork;
        public string NetworkTarget;
        /// <summary>条件分岐・ループ内（may-edge）。</summary>
        public bool Conditional;
        /// <summary>経由した API（GetComponent&lt;T&gt; など）。</summary>
        public string Via;
        public string SourceFile;
        public int Line;
        public string GraphNodeUid;
        public string UnresolvedReason;
        public readonly List<string> ReachableFrom = new List<string>();
    }

    public enum DeclarationKind
    {
        Method,
        Field,
        Event,
        Variable,
    }

    public sealed class Declaration
    {
        public DeclarationKind Kind;
        public string Name;
        public string Id;
        public string TypeName;
        public bool IsPublic;
        public bool IsArray;
        public int Line;
    }

    public sealed class CallFact
    {
        public string FromId;
        public string ToId;
        public string FromName;
        public string ToName;
        public int Line;
        public bool Conditional;
    }

    public sealed class ProgramModel
    {
        public string ProgramGuid;
        public string ProgramPath;
        public string ScriptGuid;
        public string ScriptPath;
        public string ClassName;
        /// <summary>"udon-graph" / "udonsharp-syntax" など解析モード。</summary>
        public string Mode;
        public readonly List<Declaration> Declarations = new List<Declaration>();
        public readonly List<OperationFact> Facts = new List<OperationFact>();
        public readonly List<CallFact> Calls = new List<CallFact>();
        public readonly List<AnalysisDiagnostic> Diagnostics = new List<AnalysisDiagnostic>();
        readonly HashSet<string> _events = new HashSet<string>(StringComparer.Ordinal);

        public void AddEvent(string name)
        {
            if (!string.IsNullOrEmpty(name)) _events.Add(OperationRules.NormalizeEventName(name));
        }

        /// <summary>SendCustomEvent の受信候補として、このプログラムがイベント（public メソッド）を持つか。</summary>
        public bool DeclaresEvent(string name) => !string.IsNullOrEmpty(name) && _events.Contains(OperationRules.NormalizeEventName(name));

        public Declaration FindDeclaration(string name, DeclarationKind kind)
        {
            foreach (var d in Declarations) if (d.Kind == kind && d.Name == name) return d;
            return null;
        }
    }

    /// <summary>
    /// プログラム解析結果のキャッシュ。Udon Graph は本アセンブリ、UdonSharp ソースは Roslyn を使う別アセンブリが
    /// <see cref="SharpAnalyzer"/> を登録する（Roslyn が使えない環境でも参照解析は動作する）。
    /// </summary>
    public static class ProgramModelCache
    {
        public static Func<UdonSharpProgramAsset, ProgramModel> SharpAnalyzer;
        public static bool SharpAnalyzerAvailable => SharpAnalyzer != null;

        static readonly Dictionary<string, (string hash, ProgramModel model)> Cache = new Dictionary<string, (string, ProgramModel)>(StringComparer.Ordinal);

        public static void Clear() => Cache.Clear();

        public static ProgramModel Get(AbstractUdonProgramSource program)
        {
            if (program == null) return null;
            string path = AssetDatabase.GetAssetPath(program);
            string guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) return null;

            string hash = ObjectIdUtility.GetDependencyHash(path);
            if (program is UdonSharpProgramAsset sharp && sharp.sourceCsScript != null)
                hash += "|" + ObjectIdUtility.GetDependencyHash(AssetDatabase.GetAssetPath(sharp.sourceCsScript));
            if (Cache.TryGetValue(guid, out var cached) && cached.hash == hash) return cached.model;

            ProgramModel model = null;
            try
            {
                if (program is UdonGraphProgramAsset graph) model = UdonGraphAnalyzer.Analyze(graph, guid, path);
                else if (program is UdonSharpProgramAsset us && SharpAnalyzer != null && UnvSettings.instance.enableUdonSharpSourceAnalysis) model = SharpAnalyzer(us);
            }
            catch (Exception ex)
            {
                model = new ProgramModel { ProgramGuid = guid, ProgramPath = path, Mode = "error" };
                model.Diagnostics.Add(new AnalysisDiagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Code = DiagnosticCodes.ProviderError,
                    Message = "プログラム解析に失敗: " + ex.Message,
                    AssetPath = path,
                });
            }
            Cache[guid] = (hash, model);
            return model;
        }
    }
}

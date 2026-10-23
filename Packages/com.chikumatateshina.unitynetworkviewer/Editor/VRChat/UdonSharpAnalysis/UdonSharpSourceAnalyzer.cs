using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnityNetworkViewer.Core;
using UdonSharp;
using UnityEditor;
using DiagnosticSeverity = UnityNetworkViewer.Core.DiagnosticSeverity;

namespace UnityNetworkViewer.Editor.VRChat.UdonSharpAnalysis
{
    [InitializeOnLoad]
    internal static class UdonSharpAnalyzerRegistration
    {
        static UdonSharpAnalyzerRegistration()
        {
            ProgramModelCache.SharpAnalyzer = UdonSharpSourceAnalyzer.Analyze;
        }
    }

    /// <summary>
    /// UdonSharp ソースの静的解析（Roslyn の SyntaxTree を使用する構文ベースの劣化モード）。
    /// 意味解析（SemanticModel）は Unity/VRChat 環境での参照解決の検証が必要なため現段階では使わず、
    /// フィールド宣言の型・ローカル別名・配列要素・GetComponent 程度を構文から推定する。
    /// GameObject.Find / 引数 / 計算結果 / 動的文字列イベント名などは Unresolved とする。
    /// </summary>
    public static class UdonSharpSourceAnalyzer
    {
        sealed class Receiver
        {
            public ReceiverKind Kind;
            public Declaration Field;
            public bool ArrayElement;
            public string DeclaredType;
            public string Via;
            public string Reason;

            public static Receiver Unresolved(string reason) => new Receiver { Kind = ReceiverKind.Unresolved, Reason = reason };
            public Receiver With(string type, string via = null)
                => new Receiver { Kind = Kind, Field = Field, ArrayElement = ArrayElement, DeclaredType = type ?? DeclaredType, Via = via ?? Via, Reason = Reason };
        }

        static readonly HashSet<string> StaticTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Networking", "Object", "UnityEngine.Object", "VRCInstantiate", "GameObject", "Utilities", "Debug", "Mathf", "Time", "Random", "Physics",
        };

        static HashSet<string> _udonSharpTypes;

        static HashSet<string> UdonSharpTypes
        {
            get
            {
                if (_udonSharpTypes != null) return _udonSharpTypes;
                _udonSharpTypes = new HashSet<string>(StringComparer.Ordinal) { "UdonSharpBehaviour" };
                foreach (var t in TypeCache.GetTypesDerivedFrom<UdonSharpBehaviour>()) _udonSharpTypes.Add(t.Name);
                return _udonSharpTypes;
            }
        }

        public static ProgramModel Analyze(UdonSharpProgramAsset asset)
        {
            var script = asset.sourceCsScript;
            if (script == null) return null;
            string programPath = AssetDatabase.GetAssetPath(asset);
            string scriptPath = AssetDatabase.GetAssetPath(script);
            var model = new ProgramModel
            {
                ProgramGuid = AssetDatabase.AssetPathToGUID(programPath),
                ProgramPath = programPath,
                ScriptGuid = AssetDatabase.AssetPathToGUID(scriptPath),
                ScriptPath = scriptPath,
                Mode = "udonsharp-syntax",
            };

            string text = File.Exists(scriptPath) ? File.ReadAllText(scriptPath) : script.text;
            string className = script.GetClass()?.Name ?? Path.GetFileNameWithoutExtension(scriptPath);
            AnalyzeSourceInto(model, text, className, UdonSharpTypes);
            return model;
        }

        /// <summary>ソース文字列を解析する（テスト・外部ツール用）。</summary>
        public static ProgramModel AnalyzeSource(string text, string scriptPath, string scriptGuid, string className, IEnumerable<string> udonSharpTypes = null)
        {
            var model = new ProgramModel { ScriptGuid = scriptGuid, ScriptPath = scriptPath, Mode = "udonsharp-syntax" };
            var types = new HashSet<string>(udonSharpTypes ?? Enumerable.Empty<string>(), StringComparer.Ordinal) { "UdonSharpBehaviour" };
            AnalyzeSourceInto(model, text, className, types);
            return model;
        }

        static void AnalyzeSourceInto(ProgramModel model, string text, string className, HashSet<string> udonTypes)
        {
            var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest), model.ScriptPath ?? "");
            var root = tree.GetCompilationUnitRoot();
            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
            var cls = classes.FirstOrDefault(c => c.Identifier.Text == className)
                      ?? classes.FirstOrDefault(c => c.BaseList != null && c.BaseList.Types.Any(t => t.Type.ToString().EndsWith("UdonSharpBehaviour", StringComparison.Ordinal)));
            if (cls == null)
            {
                model.Diagnostics.Add(new AnalysisDiagnostic { Severity = DiagnosticSeverity.Info, Code = DiagnosticCodes.Unsupported, Message = "UdonSharpBehaviour クラスが見つかりません", AssetPath = model.ScriptPath });
                return;
            }
            model.ClassName = cls.Identifier.Text;
            new Walker(model, tree, cls, model.ClassName, udonTypes).Run();
        }

        sealed class Walker
        {
            readonly ProgramModel _m;
            readonly SyntaxTree _tree;
            readonly ClassDeclarationSyntax _cls;
            readonly string _className;
            readonly HashSet<string> _udonTypes;
            readonly Dictionary<string, Declaration> _fields = new Dictionary<string, Declaration>(StringComparer.Ordinal);
            readonly Dictionary<string, Declaration> _methods = new Dictionary<string, Declaration>(StringComparer.Ordinal);
            readonly Dictionary<string, string> _constStrings = new Dictionary<string, string>(StringComparer.Ordinal);

            public Walker(ProgramModel m, SyntaxTree tree, ClassDeclarationSyntax cls, string className, HashSet<string> udonTypes)
            {
                _m = m;
                _tree = tree;
                _cls = cls;
                _className = className;
                _udonTypes = udonTypes;
            }

            int Line(SyntaxNode n) => _tree.GetLineSpan(n.Span).StartLinePosition.Line + 1;

            public void Run()
            {
                CollectDeclarations();
                foreach (var method in _cls.Members.OfType<MethodDeclarationSyntax>())
                {
                    if (!_methods.TryGetValue(method.Identifier.Text, out var decl)) continue;
                    AnalyzeMethod(method, decl);
                }
                ComputeReachability();
            }

            void CollectDeclarations()
            {
                foreach (var f in _cls.Members.OfType<FieldDeclarationSyntax>())
                {
                    bool isConst = f.Modifiers.Any(SyntaxKind.ConstKeyword);
                    bool isStatic = f.Modifiers.Any(SyntaxKind.StaticKeyword);
                    var type = f.Declaration.Type;
                    bool isArray = type is ArrayTypeSyntax;
                    string typeName = type is ArrayTypeSyntax at ? at.ElementType.ToString() : type.ToString();
                    bool serialized = f.Modifiers.Any(SyntaxKind.PublicKeyword) ||
                                      f.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString().Contains("SerializeField"));
                    foreach (var v in f.Declaration.Variables)
                    {
                        string name = v.Identifier.Text;
                        if (isConst && v.Initializer?.Value is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                            _constStrings[name] = lit.Token.ValueText;
                        if (isConst || isStatic) continue;
                        var decl = new Declaration
                        {
                            Kind = DeclarationKind.Field,
                            Name = name,
                            Id = NodeIds.Field(_m.ScriptGuid, _className + "." + name),
                            TypeName = typeName + (isArray ? "[]" : ""),
                            IsPublic = serialized,
                            IsArray = isArray,
                            Line = Line(v),
                        };
                        _fields[name] = decl;
                        _m.Declarations.Add(decl);
                    }
                }

                foreach (var method in _cls.Members.OfType<MethodDeclarationSyntax>())
                {
                    string name = method.Identifier.Text;
                    if (_methods.ContainsKey(name)) continue; // オーバーロードは最初の宣言にまとめる（UdonSharp はイベント名で区別）
                    bool isPublic = method.Modifiers.Any(SyntaxKind.PublicKeyword);
                    var decl = new Declaration
                    {
                        Kind = DeclarationKind.Method,
                        Name = name,
                        Id = NodeIds.Method(_m.ScriptGuid, _className + "." + name),
                        TypeName = method.ReturnType.ToString(),
                        IsPublic = isPublic,
                        Line = Line(method),
                    };
                    _methods[name] = decl;
                    _m.Declarations.Add(decl);
                    if (isPublic || OperationRules.BuiltinEvents.Contains(name)) _m.AddEvent(name);
                }
            }

            void AnalyzeMethod(MethodDeclarationSyntax method, Declaration decl)
            {
                SyntaxNode body = (SyntaxNode)method.Body ?? method.ExpressionBody;
                if (body == null) return;
                var parameters = new HashSet<string>(method.ParameterList.Parameters.Select(p => p.Identifier.Text), StringComparer.Ordinal);
                var locals = new Dictionary<string, Receiver>(StringComparer.Ordinal);

                foreach (var node in body.DescendantNodes())
                {
                    switch (node)
                    {
                        case VariableDeclaratorSyntax v when v.Initializer != null && v.Parent is VariableDeclarationSyntax vd:
                        {
                            var r = ResolveReceiver(v.Initializer.Value, locals, parameters);
                            string declared = vd.Type.IsVar ? r.DeclaredType : vd.Type.ToString();
                            locals[v.Identifier.Text] = r.With(declared);
                            break;
                        }
                        case ForEachStatementSyntax fe:
                        {
                            var r = ResolveReceiver(fe.Expression, locals, parameters);
                            if (r.Kind == ReceiverKind.Variable) r = new Receiver { Kind = ReceiverKind.Variable, Field = r.Field, ArrayElement = true, DeclaredType = fe.Type.IsVar ? ElementType(r.DeclaredType) : fe.Type.ToString() };
                            locals[fe.Identifier.Text] = r;
                            break;
                        }
                        case AssignmentExpressionSyntax assign:
                        {
                            if (assign.Left is IdentifierNameSyntax lid && locals.ContainsKey(lid.Identifier.Text))
                            {
                                locals[lid.Identifier.Text] = ResolveReceiver(assign.Right, locals, parameters).With(locals[lid.Identifier.Text].DeclaredType);
                            }
                            else if (assign.Left is MemberAccessExpressionSyntax lma)
                            {
                                var recv = ResolveReceiver(lma.Expression, locals, parameters);
                                string member = "set_" + lma.Name.Identifier.Text;
                                var rule = OperationRules.Classify(recv.DeclaredType, member);
                                if (rule != null) AddFact(decl, assign, recv, rule, member, null);
                                else if (recv.Kind == ReceiverKind.Variable && IsUdonSharpType(recv.DeclaredType))
                                    AddFact(decl, assign, recv, new OperationRules.Rule { Category = OperationRules.ProgramVariable, Api = OperationRules.ShortTypeName(recv.DeclaredType) + "." + lma.Name.Identifier.Text + " =（SetProgramVariable）" }, member, null);
                            }
                            break;
                        }
                        case InvocationExpressionSyntax inv:
                            HandleInvocation(decl, inv, locals, parameters);
                            break;
                    }
                }
            }

            void HandleInvocation(Declaration from, InvocationExpressionSyntax inv, Dictionary<string, Receiver> locals, HashSet<string> parameters)
            {
                var args = inv.ArgumentList.Arguments;
                string member;
                ExpressionSyntax receiverExpr = null;
                switch (inv.Expression)
                {
                    case MemberAccessExpressionSyntax ma:
                        member = ma.Name.Identifier.Text;
                        receiverExpr = ma.Expression is ThisExpressionSyntax ? null : ma.Expression;
                        break;
                    case IdentifierNameSyntax id:
                        member = id.Identifier.Text;
                        break;
                    case GenericNameSyntax gn:
                        member = gn.Identifier.Text;
                        break;
                    default:
                        return;
                }

                if (receiverExpr == null)
                {
                    // this.Method() / Method()
                    if (_methods.TryGetValue(member, out var target))
                    {
                        _m.Calls.Add(new CallFact { FromId = from.Id, ToId = target.Id, FromName = from.Name, ToName = target.Name, Line = Line(inv), Conditional = IsConditional(inv) });
                        return;
                    }
                    var selfRule = OperationRules.ClassifyStatic("Object", member) ?? OperationRules.Classify("UdonSharpBehaviour", member);
                    if (selfRule == null) return;
                    if (selfRule.TargetArgument >= 0)
                    {
                        if (selfRule.TargetArgument < args.Count) AddFact(from, inv, ResolveReceiver(args[selfRule.TargetArgument].Expression, locals, parameters), selfRule, member, args);
                        return;
                    }
                    AddFact(from, inv, new Receiver { Kind = ReceiverKind.Self, DeclaredType = "UdonBehaviour" }, selfRule, member, args);
                    return;
                }

                // 静的呼出し（Networking.SetOwner 等）
                if (receiverExpr is IdentifierNameSyntax typeId && !locals.ContainsKey(typeId.Identifier.Text) && !_fields.ContainsKey(typeId.Identifier.Text) && !parameters.Contains(typeId.Identifier.Text))
                {
                    string typeName = typeId.Identifier.Text;
                    var staticRule = OperationRules.ClassifyStatic(typeName, member);
                    if (staticRule != null)
                    {
                        if (staticRule.TargetArgument < args.Count) AddFact(from, inv, ResolveReceiver(args[staticRule.TargetArgument].Expression, locals, parameters), staticRule, member, args);
                        return;
                    }
                    if (StaticTypes.Contains(typeName)) return;
                }

                var recv = ResolveReceiver(receiverExpr, locals, parameters);
                var rule = OperationRules.Classify(recv.DeclaredType, member);
                if (rule == null)
                {
                    // UdonSharp 間のメソッド呼出しは SendCustomEvent 相当
                    if (recv.Kind != ReceiverKind.Unresolved && IsUdonSharpType(recv.DeclaredType) && !member.StartsWith("Get", StringComparison.Ordinal))
                    {
                        var r = new OperationRules.Rule { Category = OperationRules.Event, Api = OperationRules.ShortTypeName(recv.DeclaredType) + "." + member + "()", IsDispatch = true };
                        AddFact(from, inv, recv, r, member, null, member);
                    }
                    return;
                }
                AddFact(from, inv, recv, rule, member, args);
            }

            void AddFact(Declaration from, SyntaxNode at, Receiver recv, OperationRules.Rule rule, string member, SeparatedSyntaxList<ArgumentSyntax>? args, string eventName = null)
            {
                var fact = new OperationFact
                {
                    Entry = from.Name,
                    EntryId = from.Id,
                    Receiver = recv.Kind,
                    Variable = recv.Field?.Name,
                    VariableId = recv.Field?.Id,
                    ArrayElement = recv.ArrayElement,
                    ReceiverType = OperationRules.ShortTypeName(recv.DeclaredType),
                    Category = rule.Category,
                    Api = rule.IsDispatch && eventName == null ? (OperationRules.ShortTypeName(recv.DeclaredType) ?? "UdonBehaviour") + "." + member : rule.Api,
                    IsDispatch = rule.IsDispatch,
                    IsNetwork = rule.IsNetwork,
                    Conditional = IsConditional(at),
                    Via = recv.Via,
                    SourceFile = _m.ScriptPath,
                    Line = Line(at),
                    UnresolvedReason = recv.Reason,
                };
                if (rule.IsDispatch)
                {
                    fact.EventName = eventName;
                    if (fact.EventName == null && args.HasValue && rule.EventNameArgument >= 0 && rule.EventNameArgument < args.Value.Count)
                        fact.EventName = ConstString(args.Value[rule.EventNameArgument].Expression);
                    if (fact.EventName == null) fact.UnresolvedReason = "イベント名が動的に決まるため特定できません";
                    if (rule.IsNetwork && args.HasValue && args.Value.Count > 0) fact.NetworkTarget = args.Value[0].Expression.ToString().Split('.').Last();
                }
                _m.Facts.Add(fact);
            }

            bool IsUdonSharpType(string type)
            {
                string t = OperationRules.ShortTypeName(type);
                return t != null && (_udonTypes.Contains(t) || t == _className);
            }

            static string ElementType(string t) => t != null && t.EndsWith("[]", StringComparison.Ordinal) ? t.Substring(0, t.Length - 2) : t;

            string ConstString(ExpressionSyntax e)
            {
                switch (e)
                {
                    case LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression):
                        return lit.Token.ValueText;
                    case InvocationExpressionSyntax inv when inv.Expression is IdentifierNameSyntax n && n.Identifier.Text == "nameof" && inv.ArgumentList.Arguments.Count == 1:
                    {
                        string s = inv.ArgumentList.Arguments[0].Expression.ToString();
                        int dot = s.LastIndexOf('.');
                        return dot >= 0 ? s.Substring(dot + 1) : s;
                    }
                    case IdentifierNameSyntax id when _constStrings.TryGetValue(id.Identifier.Text, out var c):
                        return c;
                    default:
                        return null;
                }
            }

            Receiver ResolveReceiver(ExpressionSyntax e, Dictionary<string, Receiver> locals, HashSet<string> parameters, int depth = 0)
            {
                if (e == null || depth > 8) return Receiver.Unresolved("式が複雑なため特定できません");
                switch (e)
                {
                    case ParenthesizedExpressionSyntax p:
                        return ResolveReceiver(p.Expression, locals, parameters, depth + 1);
                    case CastExpressionSyntax c:
                        return ResolveReceiver(c.Expression, locals, parameters, depth + 1).With(c.Type.ToString());
                    case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AsExpression):
                        return ResolveReceiver(b.Left, locals, parameters, depth + 1).With(b.Right.ToString());
                    case ThisExpressionSyntax _:
                        return new Receiver { Kind = ReceiverKind.Self, DeclaredType = _className };
                    case IdentifierNameSyntax id:
                    {
                        string name = id.Identifier.Text;
                        if (locals.TryGetValue(name, out var local)) return local;
                        if (_fields.TryGetValue(name, out var field))
                            return new Receiver { Kind = ReceiverKind.Variable, Field = field, DeclaredType = field.TypeName };
                        if (name == "gameObject") return new Receiver { Kind = ReceiverKind.Self, DeclaredType = "GameObject" };
                        if (name == "transform") return new Receiver { Kind = ReceiverKind.Self, DeclaredType = "Transform" };
                        if (parameters.Contains(name)) return Receiver.Unresolved($"メソッド引数「{name}」は呼出し側で決まります");
                        return Receiver.Unresolved($"識別子「{name}」を解決できません（基底クラス/プロパティの可能性）");
                    }
                    case MemberAccessExpressionSyntax ma:
                    {
                        if (ma.Expression is ThisExpressionSyntax)
                            return ResolveReceiver(SyntaxFactory.IdentifierName(ma.Name.Identifier.Text), locals, parameters, depth + 1);
                        string name = ma.Name.Identifier.Text;
                        var inner = ResolveReceiver(ma.Expression, locals, parameters, depth + 1);
                        if (name == "gameObject") return inner.With("GameObject");
                        if (name == "transform") return inner.With("Transform");
                        if (inner.Kind == ReceiverKind.Unresolved) return inner;
                        return Receiver.Unresolved($"メンバーチェーン「{ma}」は静的に追跡しません");
                    }
                    case ElementAccessExpressionSyntax ea:
                    {
                        var inner = ResolveReceiver(ea.Expression, locals, parameters, depth + 1);
                        if (inner.Kind == ReceiverKind.Variable)
                            return new Receiver { Kind = ReceiverKind.Variable, Field = inner.Field, ArrayElement = true, DeclaredType = ElementType(inner.DeclaredType), Via = inner.Via };
                        return inner;
                    }
                    case InvocationExpressionSyntax inv:
                    {
                        string method = null;
                        ExpressionSyntax target = null;
                        TypeArgumentListSyntax typeArgs = null;
                        if (inv.Expression is MemberAccessExpressionSyntax ima)
                        {
                            method = ima.Name.Identifier.Text;
                            target = ima.Expression;
                            typeArgs = (ima.Name as GenericNameSyntax)?.TypeArgumentList;
                        }
                        else if (inv.Expression is GenericNameSyntax g)
                        {
                            method = g.Identifier.Text;
                            typeArgs = g.TypeArgumentList;
                        }
                        else if (inv.Expression is IdentifierNameSyntax iid)
                        {
                            method = iid.Identifier.Text;
                        }
                        if (method == null) break;
                        if (method.StartsWith("Find", StringComparison.Ordinal))
                            return Receiver.Unresolved($"動的探索（{method}）のため対象を特定できません");
                        if (method.StartsWith("GetComponent", StringComparison.Ordinal))
                        {
                            string t = typeArgs != null && typeArgs.Arguments.Count == 1 ? typeArgs.Arguments[0].ToString() : null;
                            if (t == null && inv.ArgumentList.Arguments.Count == 1 && inv.ArgumentList.Arguments[0].Expression is TypeOfExpressionSyntax tof) t = tof.Type.ToString();
                            var inner = target == null || target is ThisExpressionSyntax
                                ? new Receiver { Kind = ReceiverKind.Self, DeclaredType = _className }
                                : ResolveReceiver(target, locals, parameters, depth + 1);
                            if (inner.Kind == ReceiverKind.Unresolved) return inner;
                            bool children = method != "GetComponent";
                            if (children) return Receiver.Unresolved($"{method} は階層探索のため候補を特定できません");
                            return inner.With(t, method + (t != null ? "<" + t + ">" : ""));
                        }
                        return Receiver.Unresolved($"メソッド呼出し「{method}()」の戻り値は静的に追跡しません");
                    }
                    case ConditionalExpressionSyntax _:
                        return Receiver.Unresolved("条件式の結果は静的に追跡しません");
                }
                return Receiver.Unresolved("式「" + Truncate(e.ToString(), 40) + "」の結果は静的に追跡しません");
            }

            static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

            static bool IsConditional(SyntaxNode node)
            {
                for (var p = node.Parent; p != null; p = p.Parent)
                {
                    switch (p)
                    {
                        case MethodDeclarationSyntax _:
                            return false;
                        case IfStatementSyntax _:
                        case ElseClauseSyntax _:
                        case SwitchSectionSyntax _:
                        case ConditionalExpressionSyntax _:
                        case ForStatementSyntax _:
                        case ForEachStatementSyntax _:
                        case WhileStatementSyntax _:
                        case DoStatementSyntax _:
                        case CatchClauseSyntax _:
                            return true;
                        case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression) || b.IsKind(SyntaxKind.CoalesceExpression):
                            return true;
                    }
                }
                return false;
            }

            /// <summary>イベント入口（組込みイベント・public メソッド）からの到達元を各操作に付与する。</summary>
            void ComputeReachability()
            {
                var callees = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var c in _m.Calls)
                {
                    if (!callees.TryGetValue(c.FromId, out var list)) callees[c.FromId] = list = new List<string>();
                    list.Add(c.ToId);
                }
                var reachedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var m in _methods.Values)
                {
                    bool entry = m.IsPublic || OperationRules.BuiltinEvents.Contains(m.Name);
                    if (!entry) continue;
                    var seen = new HashSet<string>(StringComparer.Ordinal) { m.Id };
                    var queue = new Queue<string>();
                    queue.Enqueue(m.Id);
                    while (queue.Count > 0)
                    {
                        string cur = queue.Dequeue();
                        if (!reachedBy.TryGetValue(cur, out var by)) reachedBy[cur] = by = new List<string>();
                        if (by.Count < 6 && !by.Contains(m.Name)) by.Add(m.Name);
                        if (!callees.TryGetValue(cur, out var next)) continue;
                        foreach (var n in next) if (seen.Add(n)) queue.Enqueue(n);
                    }
                }
                foreach (var f in _m.Facts)
                {
                    if (reachedBy.TryGetValue(f.EntryId, out var by))
                    {
                        foreach (var b in by) if (b != f.Entry) f.ReachableFrom.Add(b);
                    }
                    else
                    {
                        f.UnresolvedReason = (f.UnresolvedReason == null ? "" : f.UnresolvedReason + " / ") + "イベント入口から到達しない可能性があります";
                    }
                }
            }
        }
    }

    /// <summary>UdonSharp ソースのメソッド・フィールド・呼出し・操作をプログラム単位で出力する。</summary>
    public sealed class UdonSharpSourceProvider : IAnalysisProvider
    {
        public string Id => "vrchat.udonSharpSource";
        public int Version => 1;
        public int Order => 56;

        public bool CanAnalyze(AnalysisTarget target)
        {
            if (!UnvSettings.instance.enableUdonSharpSourceAnalysis) return false;
            if (target.Kind != AnalysisTargetKind.Asset || target.Path == null || !target.Path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) return false;
            var type = AssetDatabase.GetMainAssetTypeAtPath(target.Path);
            return type != null && typeof(UdonSharpProgramAsset).IsAssignableFrom(type);
        }

        public AnalysisResult Analyze(AnalysisTarget target, AnalysisContext context)
        {
            var result = new AnalysisResult(Id);
            var asset = AssetDatabase.LoadMainAssetAtPath(target.Path) as UdonSharpProgramAsset;
            if (asset == null || asset.sourceCsScript == null) return result;
            var model = ProgramModelCache.Get(asset);
            if (model == null) return result;
            string scriptId = ObjectIdUtility.GetAssetObjectId(asset.sourceCsScript);
            if (!result.ContainsNode(scriptId)) result.AddNode(ObjectIdUtility.CreatePlaceholder(asset.sourceCsScript, scriptId));
            if (model.ScriptGuid != null) result.DependencyKeys.Add(model.ScriptGuid);
            ProgramModelEmitter.Emit(result, model, scriptId, Id);
            return result;
        }
    }
}

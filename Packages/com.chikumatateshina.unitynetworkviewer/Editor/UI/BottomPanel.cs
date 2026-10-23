using System;
using System.Collections.Generic;
using System.Linq;
using UnityNetworkViewer.Core;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityNetworkViewer.Editor.UI
{
    /// <summary>下パネル: 診断（Missing・未解決・Provider エラー・循環・孤立）と解析ログ。</summary>
    public sealed class BottomPanel : VisualElement
    {
        GraphViewModel _vm;
        readonly ListView _list;
        readonly List<AnalysisDiagnostic> _items = new List<AnalysisDiagnostic>();
        readonly List<string> _logItems = new List<string>();
        readonly ToolbarToggle _diagTab;
        readonly ToolbarToggle _logTab;
        readonly ToolbarToggle _warnOnly;
        readonly Label _summary;
        GraphDiagnosticReport _graphReport;
        bool _showLog;
        long _builtRevision = -1;

        public Action<string> FocusNodeRequested;

        public BottomPanel()
        {
            style.flexGrow = 1;
            var bar = new Toolbar();
            _diagTab = new ToolbarToggle { text = "診断", value = true };
            _logTab = new ToolbarToggle { text = "ログ" };
            _diagTab.RegisterValueChangedCallback(e => { if (e.newValue) SetTab(false); else if (!_showLog) _diagTab.SetValueWithoutNotify(true); });
            _logTab.RegisterValueChangedCallback(e => { if (e.newValue) SetTab(true); else if (_showLog) _logTab.SetValueWithoutNotify(true); });
            bar.Add(_diagTab);
            bar.Add(_logTab);
            _warnOnly = new ToolbarToggle { text = "警告以上のみ" };
            _warnOnly.RegisterValueChangedCallback(_ => Rebuild(true));
            bar.Add(_warnOnly);
            bar.Add(new ToolbarButton(RunGraphDiagnostics) { text = "グラフ診断を実行（循環・孤立・未解決）", tooltip = "索引全体を検査します" });
            _summary = new Label();
            _summary.style.unityTextAlign = TextAnchor.MiddleLeft;
            _summary.style.marginLeft = 8;
            bar.Add(_summary);
            Add(bar);

            _list = new ListView
            {
                fixedItemHeight = 18,
                makeItem = () =>
                {
                    var l = new Label { enableRichText = true };
                    l.style.unityTextAlign = TextAnchor.MiddleLeft;
                    l.style.whiteSpace = WhiteSpace.NoWrap;
                    l.style.overflow = Overflow.Hidden;
                    return l;
                },
                bindItem = (e, i) =>
                {
                    var l = (Label)e;
                    if (_showLog)
                    {
                        l.text = Esc(_logItems[i]);
                        l.tooltip = _logItems[i];
                        return;
                    }
                    var d = _items[i];
                    string color = d.Severity == DiagnosticSeverity.Error ? "#ff6060" : d.Severity == DiagnosticSeverity.Warning ? "#f0c040" : "#9aa";
                    l.text = $"<color={color}>[{d.Severity}]</color> <b>{d.Code}</b> {Esc(d.Message)}" +
                             (string.IsNullOrEmpty(d.AssetPath) ? "" : $" <color=#888>({Esc(d.AssetPath)}{(d.SourceLine > 0 ? ":" + d.SourceLine : "")})</color>");
                    l.tooltip = d.Message;
                },
                selectionType = SelectionType.Single,
            };
            _list.style.flexGrow = 1;
            _list.itemsChosen += items =>
            {
                if (_showLog) return;
                var d = items.FirstOrDefault() as AnalysisDiagnostic;
                if (d?.NodeId != null) FocusNodeRequested?.Invoke(d.NodeId);
            };
            Add(_list);
        }

        public void Bind(GraphViewModel vm)
        {
            _vm = vm;
            Rebuild(true);
        }

        void SetTab(bool log)
        {
            _showLog = log;
            _diagTab.SetValueWithoutNotify(!log);
            _logTab.SetValueWithoutNotify(log);
            Rebuild(true);
        }

        void RunGraphDiagnostics()
        {
            if (_vm == null) return;
            _graphReport = GraphDiagnosticsAnalyzer.Analyze(_vm.Store);
            SetTab(false);
        }

        public void Rebuild(bool force = false)
        {
            if (_vm == null) return;
            if (_showLog)
            {
                _logItems.Clear();
                _logItems.AddRange(_vm.Service.LogLines.Reverse());
                _list.itemsSource = _logItems;
                _list.Rebuild();
                _summary.text = $"{_logItems.Count} 行";
                return;
            }
            if (!force && _builtRevision == _vm.Store.Revision) return;
            _builtRevision = _vm.Store.Revision;
            _items.Clear();
            bool warnOnly = _warnOnly.value;
            foreach (var d in _vm.Store.AllDiagnostics())
                if (!warnOnly || d.Severity != DiagnosticSeverity.Info) _items.Add(d);
            if (_graphReport != null)
                foreach (var d in _graphReport.Items)
                    if (!warnOnly || d.Severity != DiagnosticSeverity.Info) _items.Add(d);
            _items.Sort((a, b) => b.Severity.CompareTo(a.Severity));
            _list.itemsSource = _items;
            _list.Rebuild();
            int errors = _items.Count(d => d.Severity == DiagnosticSeverity.Error);
            int warnings = _items.Count(d => d.Severity == DiagnosticSeverity.Warning);
            _summary.text = $"エラー {errors} / 警告 {warnings} / 合計 {_items.Count}" +
                            (_graphReport != null ? $"  （循環 {_graphReport.CycleCount}・孤立 {_graphReport.OrphanCount}・Missing {_graphReport.MissingReferenceCount}・未解決 {_graphReport.UnresolvedEdgeCount}）" : "");
        }

        public void OnLogAdded()
        {
            if (_showLog) Rebuild(true);
        }

        static string Esc(string s) => string.IsNullOrEmpty(s) ? s : s.Replace("<", "<​");
    }
}

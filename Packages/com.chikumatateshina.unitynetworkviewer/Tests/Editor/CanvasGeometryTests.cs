using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityNetworkViewer.Editor.UI;
using UnityEngine;

namespace UnityNetworkViewer.Tests
{
    /// <summary>キャンバスの破線生成が、極端な座標でも有限時間・有限個で終わること。</summary>
    public class CanvasGeometryTests
    {
        delegate void DashFn(List<Vector2> pts, float dash, float gap, List<List<Vector2>> output, Rect? clip);

        static readonly DashFn Dash = (DashFn)typeof(GraphCanvas)
            .GetMethod("DashPolyline", BindingFlags.NonPublic | BindingFlags.Static)
            .CreateDelegate(typeof(DashFn));

        static List<List<Vector2>> Run(Vector2 a, Vector2 b, Rect? clip, out long ms)
        {
            var output = new List<List<Vector2>>();
            var sw = Stopwatch.StartNew();
            Dash(new List<Vector2> { a, b }, 6f, 4f, output, clip);
            ms = sw.ElapsedMilliseconds;
            return output;
        }

        [Test]
        public void VeryLongLine_IsClippedToVisibleArea()
        {
            // 拡大時の非常に長いエッジ（画面座標で 10 億）
            var output = Run(new Vector2(-1e9f, 50f), new Vector2(1e9f, 50f), new Rect(0, 0, 1000, 800), out long ms);
            Assert.Less(ms, 200);
            Assert.LessOrEqual(output.Count, 200, "表示範囲（幅 1000）の分だけ区切る");
            // 座標 10 億付近の float の精度（約 64）を考慮して判定する
            Assert.IsTrue(output.SelectMany(p => p).All(p => p.x >= -256f && p.x <= 1256f));
        }

        [Test]
        public void LargeCoordinates_WithoutClip_Terminate()
        {
            // 座標が大きく浮動小数点の精度が落ちても止まる（以前は進み幅 0 で無限ループしメモリを使い切った）
            var output = Run(new Vector2(1e7f, 1e7f), new Vector2(1e7f + 5e5f, 1e7f), null, out long ms);
            Assert.Less(ms, 500);
            Assert.LessOrEqual(output.Count, 2001, "区切りの数に上限がある");
        }

        [Test]
        public void NonFiniteCoordinates_AreSkipped()
        {
            var output = Run(new Vector2(float.NaN, 0f), new Vector2(100f, 0f), null, out _);
            Assert.AreEqual(0, output.Count);
            output = Run(new Vector2(0f, 0f), new Vector2(float.PositiveInfinity, 0f), new Rect(0, 0, 100, 100), out _);
            Assert.AreEqual(0, output.Count);
        }

        [Test]
        public void NormalLine_ProducesDashes()
        {
            var output = Run(new Vector2(0f, 0f), new Vector2(100f, 0f), null, out _);
            Assert.AreEqual(10, output.Count, "長さ 100・周期 10 で 10 本");
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityNetworkViewer.Editor
{
    /// <summary>
    /// Component の表示名。既定は型名だが、型名だけでは区別できない Component（UdonBehaviour など）は
    /// 連携アセンブリが登録した解決関数で名前を決める（例: UdonSharp のクラス名、Udon Graph のプログラム名）。
    /// </summary>
    public static class ComponentNaming
    {
        static readonly List<Func<Component, string>> Resolvers = new List<Func<Component, string>>();

        /// <summary>表示名の解決関数を登録する。対象外の Component には null を返すこと。</summary>
        public static void Register(Func<Component, string> resolver)
        {
            if (resolver != null && !Resolvers.Contains(resolver)) Resolvers.Add(resolver);
        }

        public static string DisplayName(Component c)
        {
            if (c == null) return null;
            foreach (var r in Resolvers)
            {
                try
                {
                    string name = r(c);
                    if (!string.IsNullOrEmpty(name)) return name;
                }
                catch (Exception)
                {
                    // 名前の解決に失敗しても型名で表示する
                }
            }
            return c.GetType().Name;
        }
    }
}

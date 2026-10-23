using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace UnityNetworkViewer.Core.Yaml
{
    public struct UnityYamlReference
    {
        /// <summary>SerializedProperty.propertyPath に近い形式（"list.Array.data[2].field"）。</summary>
        public string Path;
        public long FileId;
        public string Guid;
        public int Type;
        public int Line;

        public bool IsNull => FileId == 0 && string.IsNullOrEmpty(Guid);
        public bool IsLocal => string.IsNullOrEmpty(Guid) && FileId != 0;
    }

    public sealed class UnityYamlModification
    {
        public UnityYamlReference Target;
        public string PropertyPath;
        public string Value;
        public UnityYamlReference ObjectReference;
        public int Line;
    }

    public sealed class UnityYamlObject
    {
        public int ClassId;
        public long FileId;
        public bool Stripped;
        /// <summary>ドキュメント先頭のキー（"GameObject", "MonoBehaviour" など）。</summary>
        public string TypeName;
        public int Line;
        /// <summary>トップレベルのスカラー値（m_Name, m_IsActive など）。</summary>
        public readonly Dictionary<string, string> Scalars = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>任意深さの m_MethodName 等、選択されたスカラーをフルパスで保持する。</summary>
        public readonly Dictionary<string, string> PathScalars = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly List<UnityYamlReference> References = new List<UnityYamlReference>();
        public readonly List<UnityYamlModification> Modifications = new List<UnityYamlModification>();

        public string GetScalar(string key) => Scalars.TryGetValue(key, out var v) ? v : null;

        public bool TryGetReference(string path, out UnityYamlReference reference)
        {
            foreach (var r in References)
            {
                if (r.Path == path)
                {
                    reference = r;
                    return true;
                }
            }
            reference = default;
            return false;
        }

        public long GetLocalReference(string path) => TryGetReference(path, out var r) ? r.FileId : 0;

        /// <summary>"m_Children.Array.data[n]" のような配列要素の参照を順に返す。</summary>
        public IEnumerable<UnityYamlReference> GetArrayReferences(string arrayPath)
        {
            string prefix = arrayPath + ".Array.data[";
            foreach (var r in References)
                if (r.Path != null && r.Path.StartsWith(prefix, StringComparison.Ordinal)) yield return r;
        }
    }

    public sealed class UnityYamlDocument
    {
        public readonly List<UnityYamlObject> Objects = new List<UnityYamlObject>();
        public readonly Dictionary<long, UnityYamlObject> ByFileId = new Dictionary<long, UnityYamlObject>();
        public bool IsText = true;
        public string Error;
        public int LineCount;

        public UnityYamlObject Get(long fileId) => fileId != 0 && ByFileId.TryGetValue(fileId, out var o) ? o : null;
    }

    /// <summary>
    /// Unity のテキストシリアライズ（Force Text）された .unity / .prefab を軽量に読む行ベースのパーサ。
    /// 汎用 YAML パーサではなく、Unity が出力する形式（ブロック形式 + 参照のフローマッピング）のみを対象とする。
    /// バイナリ形式や想定外の構文は <see cref="UnityYamlDocument.IsText"/> / Error で報告し、完全性は保証しない。
    /// </summary>
    public static class UnityYamlParser
    {
        static readonly HashSet<string> PathScalarKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_MethodName", "m_TargetAssemblyTypeName", "m_CallState",
        };

        public static UnityYamlDocument ParseFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return Parse(stream);
            }
        }

        public static UnityYamlDocument Parse(Stream stream)
        {
            // 先頭バイトでテキスト判定（バイナリシリアライズは "%YAML" で始まらない）
            var head = new byte[5];
            int read = stream.Read(head, 0, head.Length);
            if (read < 5 || Encoding.ASCII.GetString(head, 0, 5) != "%YAML")
            {
                return new UnityYamlDocument { IsText = false, Error = "not a text-serialized Unity file" };
            }
            stream.Seek(0, SeekOrigin.Begin);
            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 16, true))
            {
                return Parse(reader);
            }
        }

        public static UnityYamlDocument ParseText(string text)
        {
            if (text == null || !text.StartsWith("%YAML", StringComparison.Ordinal))
                return new UnityYamlDocument { IsText = false, Error = "not a text-serialized Unity file" };
            using (var reader = new StringReader(text))
            {
                return Parse(reader);
            }
        }

        sealed class Frame
        {
            public int Indent;
            public string Segment;
            public bool IsItem;
            public int ItemCount = -1; // キーフレームの配列要素数
        }

        public static UnityYamlDocument Parse(TextReader reader)
        {
            var doc = new UnityYamlDocument();
            UnityYamlObject current = null;
            var frames = new List<Frame>();
            int lineNo = 0;
            string line;
            char multilineQuote = '\0';
            UnityYamlModification currentMod = null;
            int currentModIndex = -1;

            while ((line = reader.ReadLine()) != null)
            {
                lineNo++;
                if (multilineQuote != '\0')
                {
                    string t = line.TrimEnd();
                    if (t.Length > 0 && t[t.Length - 1] == multilineQuote && !(multilineQuote == '\'' && t.EndsWith("''", StringComparison.Ordinal)))
                        multilineQuote = '\0';
                    continue;
                }
                if (line.Length == 0) continue;
                if (line[0] == '%') continue;
                if (line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    current = ParseHeader(line, lineNo);
                    frames.Clear();
                    currentMod = null;
                    currentModIndex = -1;
                    if (current != null)
                    {
                        doc.Objects.Add(current);
                        doc.ByFileId[current.FileId] = current;
                    }
                    continue;
                }
                if (current == null) continue;

                int indent = 0;
                while (indent < line.Length && line[indent] == ' ') indent++;
                if (indent >= line.Length) continue;

                if (indent == 0)
                {
                    // ドキュメントの型名 "GameObject:"
                    int c = line.IndexOf(':');
                    if (c > 0 && current.TypeName == null) current.TypeName = line.Substring(0, c);
                    continue;
                }

                int pos = indent;
                // 配列要素 "- ..."（Unity はキーと同じインデントで要素を書く）
                if (line[pos] == '-' && (pos + 1 == line.Length || line[pos + 1] == ' '))
                {
                    while (frames.Count > 0 && (frames[frames.Count - 1].Indent > indent || (frames[frames.Count - 1].Indent == indent && frames[frames.Count - 1].IsItem)))
                        frames.RemoveAt(frames.Count - 1);
                    Frame owner = frames.Count > 0 ? frames[frames.Count - 1] : null;
                    int itemIndex = 0;
                    if (owner != null && !owner.IsItem)
                    {
                        owner.ItemCount++;
                        itemIndex = owner.ItemCount;
                    }
                    var item = new Frame { Indent = indent + 1, IsItem = true, Segment = "Array.data[" + itemIndex.ToString(CultureInfo.InvariantCulture) + "]" };
                    frames.Add(item);

                    pos += 1;
                    while (pos < line.Length && line[pos] == ' ') pos++;
                    if (pos >= line.Length) continue;
                    // 要素内容: "key: value" か スカラー/参照
                    if (TrySplitKey(line, pos, out string itemKey, out string itemValue))
                    {
                        HandleKey(current, frames, indent + 2, itemKey, itemValue, lineNo, ref multilineQuote, ref currentMod, ref currentModIndex);
                    }
                    else
                    {
                        string path = BuildPath(frames, null);
                        ScanReferences(current, path, line, pos, lineNo);
                    }
                    continue;
                }

                if (TrySplitKey(line, pos, out string key, out string value))
                {
                    while (frames.Count > 0 && frames[frames.Count - 1].Indent >= indent)
                        frames.RemoveAt(frames.Count - 1);
                    HandleKey(current, frames, indent, key, value, lineNo, ref multilineQuote, ref currentMod, ref currentModIndex);
                }
                // それ以外（複数行プレーンスカラーの継続行など）は無視する
            }
            doc.LineCount = lineNo;
            return doc;
        }

        static void HandleKey(UnityYamlObject current, List<Frame> frames, int indent, string key, string value, int lineNo,
            ref char multilineQuote, ref UnityYamlModification currentMod, ref int currentModIndex)
        {
            if (string.IsNullOrEmpty(value))
            {
                frames.Add(new Frame { Indent = indent, Segment = key });
                return;
            }

            string parentPath = BuildPath(frames, null);
            string path = string.IsNullOrEmpty(parentPath) ? key : parentPath + "." + key;

            char q = value[0];
            if ((q == '"' || q == '\'') && (value.Length == 1 || value[value.Length - 1] != q))
                multilineQuote = q;

            // PrefabInstance の m_Modifications
            const string modPrefix = "m_Modification.m_Modifications.Array.data[";
            if (parentPath.StartsWith(modPrefix, StringComparison.Ordinal))
            {
                int close = parentPath.IndexOf(']', modPrefix.Length);
                int idx = close > 0 && int.TryParse(parentPath.Substring(modPrefix.Length, close - modPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;
                if (idx >= 0 && close == parentPath.Length - 1)
                {
                    if (idx != currentModIndex || currentMod == null)
                    {
                        currentMod = new UnityYamlModification { Line = lineNo };
                        currentModIndex = idx;
                        current.Modifications.Add(currentMod);
                    }
                    switch (key)
                    {
                        case "target":
                            if (TryParseReference(value, 0, out var tr)) { tr.Path = path; tr.Line = lineNo; currentMod.Target = tr; }
                            return;
                        case "propertyPath":
                            currentMod.PropertyPath = Unquote(value);
                            return;
                        case "value":
                            currentMod.Value = Unquote(value);
                            return;
                        case "objectReference":
                            if (TryParseReference(value, 0, out var orf)) { orf.Path = path; orf.Line = lineNo; currentMod.ObjectReference = orf; }
                            return;
                    }
                }
            }

            if (frames.Count == 0) current.Scalars[key] = Unquote(value);
            else if (PathScalarKeys.Contains(key)) current.PathScalars[path] = Unquote(value);

            ScanReferences(current, path, value, 0, lineNo);
        }

        static string BuildPath(List<Frame> frames, string leaf)
        {
            if (frames.Count == 0) return leaf ?? string.Empty;
            var sb = new StringBuilder();
            foreach (var f in frames)
            {
                if (sb.Length > 0) sb.Append('.');
                sb.Append(f.Segment);
            }
            if (!string.IsNullOrEmpty(leaf)) sb.Append('.').Append(leaf);
            return sb.ToString();
        }

        static bool TrySplitKey(string line, int start, out string key, out string value)
        {
            key = null;
            value = null;
            if (start >= line.Length) return false;
            char first = line[start];
            if (first == '{' || first == '[' || first == '"' || first == '\'' || first == '-') return false;
            int colon = -1;
            for (int i = start; i < line.Length; i++)
            {
                char c = line[i];
                if (c == ':')
                {
                    if (i + 1 == line.Length || line[i + 1] == ' ') { colon = i; break; }
                }
                else if (c == '{' || c == '[' || c == '"' || c == '\'' || c == '#') return false;
            }
            if (colon <= start) return false;
            key = line.Substring(start, colon - start);
            value = colon + 2 <= line.Length ? line.Substring(Math.Min(colon + 2, line.Length)).Trim() : string.Empty;
            return true;
        }

        static UnityYamlObject ParseHeader(string line, int lineNo)
        {
            // --- !u!114 &1234567890 stripped
            int u = line.IndexOf("!u!", StringComparison.Ordinal);
            int amp = line.IndexOf('&');
            if (u < 0 || amp < 0) return null;
            int classEnd = line.IndexOf(' ', u + 3);
            if (classEnd < 0) return null;
            if (!int.TryParse(line.Substring(u + 3, classEnd - (u + 3)), NumberStyles.Integer, CultureInfo.InvariantCulture, out int classId)) return null;
            int idEnd = line.IndexOf(' ', amp + 1);
            string idText = idEnd < 0 ? line.Substring(amp + 1) : line.Substring(amp + 1, idEnd - amp - 1);
            if (!long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long fileId)) return null;
            return new UnityYamlObject
            {
                ClassId = classId,
                FileId = fileId,
                Stripped = idEnd >= 0 && line.IndexOf("stripped", idEnd, StringComparison.Ordinal) >= 0,
                Line = lineNo,
            };
        }

        static void ScanReferences(UnityYamlObject obj, string path, string text, int start, int lineNo)
        {
            int i = start;
            while (true)
            {
                int idx = text.IndexOf("{fileID:", i, StringComparison.Ordinal);
                if (idx < 0) return;
                if (TryParseReference(text, idx, out var r))
                {
                    r.Path = path;
                    r.Line = lineNo;
                    obj.References.Add(r);
                }
                i = idx + 8;
            }
        }

        /// <summary>"{fileID: 123, guid: abc, type: 3}" を解析する。</summary>
        public static bool TryParseReference(string text, int start, out UnityYamlReference reference)
        {
            reference = default;
            int open = text.IndexOf("{fileID:", start, StringComparison.Ordinal);
            if (open < 0) return false;
            int close = text.IndexOf('}', open);
            if (close < 0) return false;
            string body = text.Substring(open + 1, close - open - 1);
            foreach (var part in body.Split(','))
            {
                int c = part.IndexOf(':');
                if (c < 0) continue;
                string k = part.Substring(0, c).Trim();
                string v = part.Substring(c + 1).Trim();
                switch (k)
                {
                    case "fileID":
                        long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out reference.FileId);
                        break;
                    case "guid":
                        reference.Guid = v;
                        break;
                    case "type":
                        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out reference.Type);
                        break;
                }
            }
            return true;
        }

        static string Unquote(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 2) return value;
            char q = value[0];
            if ((q == '"' || q == '\'') && value[value.Length - 1] == q)
            {
                string inner = value.Substring(1, value.Length - 2);
                return q == '\'' ? inner.Replace("''", "'") : UnescapeDoubleQuoted(inner);
            }
            return value;
        }

        static string UnescapeDoubleQuoted(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'u':
                        if (i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString();
        }
    }
}

# Testing

| テスト | 場所 | 実行条件 |
|---|---|---|
| Core 単体（Store / Query / Layout / Cache / Export / ID / YAML パーサ）| `Tests/Editor/CoreGraphTests.cs`, `UnityYamlParserTests.cs` | 常時（Core は Unity 非依存のため dotnet + NUnit でも実行可）|
| Unity 統合（実 Scene/Prefab を一時フォルダに作成）| `Tests/Editor/ProviderIntegrationTests.cs` | 常時 |
| VRChat（UdonSharp 構文解析・Udon Graph・UdonBehaviour 結合）| `Tests/VRChat/UdonAnalysisTests.cs` | `com.vrchat.worlds` 3.4.0 以上 |

統合テストで確認していること:
- 親子・Component 所属・SerializedReference（配列要素の propertyPath を含む）・UnityEvent・ScriptUsage・逆引き
- Missing Reference が Unresolved + 診断になる
- 保存済み Scene のロード済み解析と未ロード（テキスト）解析で GameObject ID と参照エッジが一致する
- Prefab Instance のルート/内部オブジェクトの ID が未ロード索引と一致し、Override（名前・参照）が反映される
- Prefab Variant → Base
- 解析で Scene が Dirty にならない、未ロード Scene のノードは勝手に開かない

## 手動確認

Samples の Fixture Generator で `Assets/OREFixtures` を生成し、以下を確認する。
- Hierarchy / Scene View / Project の選択でハイライトが追従する。Follow/Focus OFF で表示・視点が固定される
- Graph のクリックで Unity の選択が変わり、選択通知がループしない
- `Sub.unity` を閉じたまま `prefab:Door` で検索すると Variant の Instance が見つかる
- Undo/Redo、Asset 再インポート、スクリプト再コンパイル（Domain Reload）後も索引が整合し、イベントが重複購読されない

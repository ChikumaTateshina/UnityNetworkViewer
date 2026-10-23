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

Samples の Fixture Generator で `Assets/UNVFixtures` を生成し、以下を確認する。
- Hierarchy / Scene View / Project の選択でハイライトが追従する。Follow/Focus OFF で表示・視点が固定される
- Graph のクリックで Unity の選択が変わり、選択通知がループしない
- `Sub.unity` を閉じたまま `prefab:Door` で検索すると Variant の Instance が見つかる
- Undo/Redo、Asset 再インポート、スクリプト再コンパイル（Domain Reload）後も索引が整合し、イベントが重複購読されない

## 大規模グラフの最適化検証（2026-10-24）

Core の既存テストと追加した回帰テストを .NET 8 / Release で実行し、48 件成功。
追加ケースは 4,000 ノードの独立した親子ペア、20,000 ノードの深い連鎖、
グリッドを使う力学レイアウトの決定性とピン位置の維持。

階層レイアウトの変更前（v0.3.0）と変更後を同一プロセスで比較した参考値:

| ノード数（独立した親子ペア） | 変更前 | 変更後 | 変更前の割り当て | 変更後の割り当て |
|---|---:|---:|---:|---:|
| 1,000 | 18.0 ms | 1.5 ms | 32.01 MB | 0.76 MB |
| 4,000 | 112.9 ms | 2.8 ms | 494.14 MB | 3.04 MB |
| 10,000 | 612.0 ms | 12.9 ms | 3,066.56 MB | 7.64 MB |

時間は単発測定であり、JIT・実行環境に依存する。割り当て量は
`GC.GetAllocatedBytesForCurrentThread` の差分（計算中の累積値で、常駐メモリではない）。
このケースで繰り返されていた成分ごとの全ノード走査・逆隣接構築を除去した。
Unity Editor / Mono 上の実プロジェクトの解析・描画速度を保証する値ではない。

Unity Editor では次を確認する:
- 大きな Scene の索引中に選択を連続変更し、古いレイアウトで表示が上書きされない。
- 階層・放射・力学レイアウトで選択、ピン留め、グループ表示が正常に動く。
- 診断が 5,000 件以上ある場合も重要度順と合計件数が維持される。
- Profiler で索引中と選択変更時の Editor メインスレッド時間・GC Alloc を比較する。

初回は .NET のみで検証。下記の追加最適化時に Unity 2022.3.22f1 の実行環境を確認し、統合テストも実行した。GUI の手動操作と実ワールドでの Profiler 比較は未実施。

## 索引・検索・表示の追加最適化（2026-10-24）

- 階層・SerializedReference・Prefab・未ロード Scene の主要 Provider を段階実行化。
  オブジェクト表生成と ID 取得も分割し、コンテキストをフレーム間・Provider 間で共有。
- Asset 探索はフィルタごとにフレームを分け、候補を時間予算内で処理。
- Prefab の依存ハッシュ検証を解析コンテキスト内で共有し、キャッシュ失効時は再検証。
- kind / scene / provider 検索は二次索引で候補を絞る。
  単一の索引集合はコピーせず、広い複数集合は全件走査へフォールバック。
- 表示キャッシュは無関係なストア更新で再生成せず、以前表示に寄与した owner の
  削除・差し替えも追跡して古いエッジが残らないようにする。
- 詳細一覧は方向ごとに 300 件までの制限を保ち、前後のページへ移動できる。

検証結果:
- .NET 8 の Core テスト: 49 件成功。
- Unity 2022.3.22f1 の一時プロジェクト: Core / YAML / Provider 統合 / チャンク解析の計 64 件成功。
  オフライン検証用にソースと Fixture をコピーし、NUnit の Test / SetUp / TearDown を
  バッチモードのカスタムランナーから実行。Unity Test Runner パッケージと VRChat SDK は使用していない。
- 追加回帰テストでは索引検索の部分一致・否定・owner 差し替え、
  時間分割解析と同期解析の出力一致、Prefab 検証キャッシュ失効、
  無関係な更新での表示キャッシュ維持、エッジ削除の反映、
  オブジェクト表の途中再開、750 エッジのページ切り替えを確認。

残る制約:
- FindAssets / LoadAssetAtPath / SerializedObject の個々の同期 API と、owner 単位のコミットは中断できない。
- Animation / VRChat など今回段階実行に移行していない Provider は従来の同期解析を継続する。
- 実際の大規模ワールドにおける操作感とフレーム時間は未測定。

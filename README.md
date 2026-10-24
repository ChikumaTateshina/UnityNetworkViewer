# UnityNetworkViewer

Unity / VRChat ワールドプロジェクトの Scene・GameObject・Component・Prefab・Asset・UdonBehaviour・UdonSharp・Udon Graph の関係を
**型付き有向マルチグラフ**として索引化し、Unity Editor 上で検索・可視化・追跡する Editor 専用拡張です。
Hierarchy / Scene View / Project の選択と連動して、参照元・参照先・Prefab 関係・Udon の操作対象を強調表示します。

**導入ページ: https://chikumatateshina.github.io/UnityNetworkViewer/**

## 特長

- **選択連動のグラフ表示**: Unity で選んだオブジェクトの参照元・参照先を、ホップ数（0〜5）・方向・関係種類で絞って表示
- **幅広い関係の索引**: 親子、Component、シリアライズ参照（配列要素まで）、UnityEvent、Prefab Instance / Variant / Nested / Override、ScriptableObject、Animator / AnimationClip
- **未ロード Scene も横断**: 開いていない Scene もテキストシリアライズから索引し、Scene をまたいで検索
- **VRChat 対応**: UdonBehaviour の変数参照、UdonSharp の Proxy、Udon Graph と UdonSharp ソースの静的解析から、`SetActive` や `SendCustomEvent` などの操作対象を推定
- **根拠と確度の表示**: エッジごとに propertyPath・ソース位置・推定理由と、確定 / 推定 / 未解決の確度を表示
- **診断**: Missing Reference、未解決参照、循環、孤立ノード
- **読み取り専用**: 解析で Scene / Prefab を Dirty にせず、保存せず、勝手に開かない
- **ビルドに含まれない**: Editor 専用アセンブリのみで構成され、ランタイムビルドや VRChat のアップロード成果物には入りません
- JSON / GraphML / SVG エクスポート

## 動作環境

| 項目 | 対応 |
|---|---|
| Unity | 2022.3（2022.3.22f1 で確認） |
| VRChat SDK - Worlds | 任意。3.4.0 以上で VRChat 連携が有効（3.10.5 で確認） |

VRChat SDK が無いプロジェクトでも、Unity 標準の解析は動作します。

## インストール

### VCC / ALCOM（VRChat プロジェクト）

1. [導入ページ](https://chikumatateshina.github.io/UnityNetworkViewer/) の **Add to VCC** を押す
   （または VCC の Settings → Packages → Add Repository に `https://chikumatateshina.github.io/UnityNetworkViewer/index.json` を登録）
2. プロジェクトの Manage Project から **UnityNetworkViewer** を追加

### Unity Package Manager（Git URL）

Window → Package Manager → 「+」→ **Add package from git URL…** に次を入力します。

```
https://github.com/ChikumaTateshina/UnityNetworkViewer.git?path=/Packages/com.chikumatateshina.unitynetworkviewer
```

特定の版に固定する場合は、末尾に `#v0.5.0` のようにタグを付けます。

### 手動

[Releases](https://github.com/ChikumaTateshina/UnityNetworkViewer/releases) の zip を、プロジェクトの
`Packages/com.chikumatateshina.unitynetworkviewer/` に展開します。

## 使い方

`Window > Analysis > UnityNetworkViewer` を開きます。ロード済み Scene から解析を始め、続けてプロジェクト全体をバックグラウンドで索引します。
あとは Hierarchy や Project でオブジェクトを選ぶと、関係がグラフに表示されます。

画面構成、検索構文、関係の種類と確度、既知の制約は [パッケージの README](Packages/com.chikumatateshina.unitynetworkviewer/README.md) を参照してください。

| 文書 | 内容 |
|---|---|
| [README](Packages/com.chikumatateshina.unitynetworkviewer/README.md) | 使い方・関係の種類・既知の制約 |
| [CHANGELOG](Packages/com.chikumatateshina.unitynetworkviewer/CHANGELOG.md) | 変更履歴 |
| [architecture.md](Packages/com.chikumatateshina.unitynetworkviewer/Documentation~/architecture.md) | 内部構成 |
| [provider-contract.md](Packages/com.chikumatateshina.unitynetworkviewer/Documentation~/provider-contract.md) | 解析 Provider の追加方法 |
| [testing.md](Packages/com.chikumatateshina.unitynetworkviewer/Documentation~/testing.md) | テストの実行方法 |

## リリース手順（メンテナー向け）

1. `package.json` の `version` と `CHANGELOG.md` を更新して main に push
2. `v<version>` タグを push（例: `git tag v0.2.1 && git push origin v0.2.1`）
3. GitHub Actions が zip を Release に添付し、VPM listing と導入ページを GitHub Pages に再公開します

## ライセンス

[MIT License](LICENSE)

UdonSharp ソース解析は、VRChat SDK に同梱されている Roslyn（Microsoft.CodeAnalysis）を実行時に参照します。本パッケージは Roslyn を再配布していません。

本プロジェクトは VRChat Inc. および Unity Technologies とは関係のない非公式ツールです。VRChat・Unity は各社の商標です。

---

## English

An editor-only Unity extension that indexes relationships between scenes, GameObjects, components, prefabs, assets, and VRChat Udon programs (UdonBehaviour, UdonSharp, Udon Graph) as a typed directed multigraph. It visualizes them with highlighting that follows the Unity selection.

- Install with VCC / ALCOM from the [listing page](https://chikumatateshina.github.io/UnityNetworkViewer/), or with the Git URL above in the Unity Package Manager.
- Open it from `Window > Analysis > UnityNetworkViewer`.
- It requires Unity 2022.3. VRChat SDK - Worlds 3.4.0 or later is optional and enables the Udon analysis.
- Analysis is read-only. It never marks scenes dirty, never saves them, and never opens them on its own.
- It is licensed under MIT. It is not affiliated with VRChat Inc. or Unity Technologies.

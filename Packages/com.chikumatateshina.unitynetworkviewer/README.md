# UnityNetworkViewer

Unity プロジェクト内の Scene / GameObject / Component / Prefab / Asset / UdonBehaviour / UdonSharp / Udon Graph の関係を
**型付き有向マルチグラフ**として索引化し、Editor 上で検索・可視化・追跡する Editor 専用拡張です。
Hierarchy / Scene View / Project の選択と双方向に連動し、関連ノード・エッジを強調表示します。

- Editor 専用（ランタイムビルド・VRChat アップロード成果物にコードを含めません）
- 解析は読み取り専用（Scene / Prefab を Dirty にしない・保存しない・勝手に開かない）
- VRChat SDK が無いプロジェクトでも Unity 標準の解析は動作します

## 動作確認環境

| 項目 | バージョン | 確認内容 |
|---|---|---|
| Unity | 2022.3.22f1 | コンパイル・EditMode テスト（Core + 実 Scene/Prefab 統合テスト）|
| VRChat SDK - Worlds | 3.10.5（UdonSharp 同梱） | コンパイル・EditMode テスト（Udon Graph / UdonBehaviour / UdonSharp 解析）|

VRChat 連携アセンブリは `com.vrchat.worlds` 3.4.0 以上で自動的に有効になります（`versionDefines`）。それ以外の SDK 版は未検証です。

## インストール

導入ページ: https://chikumatateshina.github.io/UnityNetworkViewer/

いずれかの方法で導入します。

1. **VCC / ALCOM**（VRChat プロジェクト）: 導入ページの「Add to VCC」、または VCC の Settings → Packages → Add Repository に
   `https://chikumatateshina.github.io/UnityNetworkViewer/index.json` を登録し、Manage Project から追加
2. **Git URL**: Package Manager の「+」→ Add package from git URL… に次を入力
   ```
   https://github.com/ChikumaTateshina/UnityNetworkViewer.git?path=/Packages/com.chikumatateshina.unitynetworkviewer
   ```
   （特定の版に固定する場合は末尾に `#v0.7.0` などを付けます）
3. **手動**: Release の zip をプロジェクトの `Packages/com.chikumatateshina.unitynetworkviewer/` に展開

## 使い方

`Window > Analysis > UnityNetworkViewer`（または `Tools > UnityNetworkViewer > Open Window`）。

初回はロード済み Scene を優先解析し、続けてプロジェクト全体（Scene / Prefab / Controller / Clip / ScriptableObject / Udon Program）を
バックグラウンドで索引します（フレームあたりの時間予算内で処理。Unity の Progress ウィンドウからキャンセル可能）。

### 画面構成

| 位置 | 内容 |
|---|---|
| 上 | Rebuild All（全体再解析）/ Loaded Scenes / Index Project / Cancel、検索、Layout、View、Export、状態表示 |
| 左 | 選択同期（Follow / Focus / Graph→Unity / 複数選択の和集合・共通部分）、方向・ホップ数（0〜5）・最大表示数、関係種類・ノード種類・確度フィルタ、保存ビュー、索引設定 |
| 中央 | グラフ（パン: ドラッグ / ズーム: ホイール / ノードのドラッグでピン留め）、凡例、ミニマップ |
| 右 | 選択ノードの詳細、参照元・参照先一覧、エッジの根拠（propertyPath・ソース位置・推定理由・解析時刻）|
| 下 | 診断（Missing・未解決・Provider エラー・循環・孤立）、解析ログ |

### 選択連動

- Hierarchy / Scene View / Project で選択すると、選択ノードを太枠、参照元を橙、参照先を青緑、親子を中立色、Prefab 関係を紫で表示します。
  2 ホップ目以降は段階的に薄くなります。色だけでなく **線種（確度）・矢印（方向）・略号ラベル** でも区別できます。
- GameObject（または Component）を選ぶと、深さの設定とは別に「選択の文脈」も表示します:
  親から Scene ルートまでの祖先、含まれている Prefab（Instance のルートと元の Prefab Asset）、
  その GameObject と Component（Transform など）を参照しているオブジェクトとその GameObject。
  左パネルの「選択の文脈も表示」で切り替えられます。選択時に解析中だった周辺は、解析が終わると表示に加わります。
- UdonBehaviour は型名ではなく Udon の名前（UdonSharp のクラス名 / Udon Graph のプログラム名）で表示し、2 行目に付いている GameObject 名を出します。
  GameObject のノードには、付いている Udon の名前を `[DoorController, …]` のように併記します（未ロード Scene・Prefab 内も同様）。
- **Follow selection** を OFF にすると表示を固定します。**Focus selection** が ON の時だけ自動でパンします（ズームは維持）。
- グラフ上のクリックは Unity の Selection に反映されます（ループしないよう再入を抑止）。ダブルクリックで Ping / Frame。
  未ロード Scene のノードは確認ダイアログで「追加で開く / 置き換えて開く」を選んだ場合のみ開きます。
- 選択対象が索引に無い場合は理由（解析中・フィルタ・未ロード等）をバナーに表示し、所属 Scene / Asset を優先解析します。

### 検索

空白区切りで AND、先頭 `-` で否定。

```
door                       名前・型・パスの部分一致
type:Animator scene:Main   型と Scene で絞り込み
prefab:Lamp                Prefab（Instance の Source を含む）
evidence:Inferred          推定エッジを持つノード
method:SetActive           操作 API / UnityEvent メソッド
flag:Missing  edge:UnityEventCall  kind:UdonProgram  path:Assets/World
```

Enter で検索結果をすべてグラフに表示、クリックでそのノードを起点に表示します。

### ノードの右クリック

Unity で選択 / 移動・開く / Ping / Prefab Mode / Scene を開く / ソースを開く / 起点にする / 隣接を展開 / 子の折り畳み /
ピン留め / **経路を表示**（グラフで選択中のノードからの最短経路）/ 表示から外す / ID コピー。

キー: `F` 選択へフレーム、`A` 全体表示、`Delete` 表示から外す、`Esc` 選択と経路表示の解除。

### エクスポート

JSON / GraphML（表示中の部分グラフ・全索引）、SVG（現在の表示）。出力前に Asset パス・Hierarchy パス・ソース位置・推定理由・診断を含めるか選択できます。

## 関係の種類と確度

| 種別 | 方向 | 根拠 |
|---|---|---|
| ParentChild | Parent → Child（Scene → Root）| Transform |
| OwnsComponent | GameObject → Component | GetComponents |
| SerializedReference | 参照元 Component → 参照先 Object | SerializedProperty.propertyPath |
| ScriptUsage | MonoBehaviour → MonoScript | m_Script |
| UnityEventCall | 発信元 Component → 対象（メソッド名付き）| PersistentCall |
| PrefabSource | Instance → Source Prefab / Source Object | PrefabUtility |
| PrefabVariant | Variant → Base Prefab | PrefabUtility |
| AnimatorClip / AnimationBinding | Controller → Clip、Clip → パス、Animator → 解決した対象 | AnimationUtility |
| UdonProgramBinding | UdonBehaviour → Program、Program → ソース | programSource / sourceCsScript |
| UdonProxyBinding | UdonSharpBehaviour → UdonBehaviour | UdonSharpEditorUtility |
| UdonVariableReference | UdonBehaviour → Object | publicVariables |
| UdonDeclaration / MethodCall | Script/Program → Method/Event/Variable、Method → Method | ソース / Udon Graph |
| MethodControl / EventDispatch | Method・Behaviour → 操作対象 / 受信側 | 操作 API ルール（SetActive, Transform, Animator, SendCustomEvent 等）|
| AssetDependency | Scene → Asset | AssetDatabase 依存（バイナリ Scene のみ）|

- **Confirmed**: シリアライズ値等から参照先を特定できる（参照の存在を示し、実行されることは意味しない）
- **Inferred**: 静的解析による推定（条件分岐内は `mayEdge`、配列は `arrayCandidate` 属性）
- **Unresolved**: Missing・動的探索（`GameObject.Find` 等）・引数・動的なイベント名など、対象を特定できない

## アーキテクチャ

```
UI (EditorWindow / GraphCanvas / Panels / SelectionBridge)
  └ GraphViewModel ─ GraphQueryService ─ GraphStore ← AnalysisScheduler ← IAnalysisProvider 群
                                              ↑               ↑
                                     GraphCacheStorage   ChangeTracker（変更検知・デバウンス）
```

| アセンブリ | 内容 | 依存 |
|---|---|---|
| `UnityNetworkViewer.Core` | ノード/エッジ/ID、GraphStore、Query、Layout（4 種）、Cache、Export、Unity YAML パーサ | なし（`noEngineReferences`）|
| `UnityNetworkViewer.Editor` | Unity 解析 Provider、Scheduler、ChangeTracker、Cache 保存、UI | Core |
| `UnityNetworkViewer.Editor.VRChat` | UdonBehaviour / Udon Program / Udon Graph / インスタンス結合 | Core, Editor, VRChat SDK（条件付き）|
| `UnityNetworkViewer.Editor.UdonSharpAnalysis` | UdonSharp ソース静的解析（Roslyn SyntaxTree）| 上記 + SDK 同梱の Roslyn（条件付き）|

Provider の追加方法は [Documentation~/provider-contract.md](Documentation~/provider-contract.md) を参照してください。

## 保存先

| ファイル | 内容 |
|---|---|
| `Library/UnityNetworkViewer/index.bin` | 索引キャッシュ（削除しても再構築されます）|
| `UserSettings/UnityNetworkViewer/Settings.asset` | 索引設定 |
| `UserSettings/UnityNetworkViewer/Views.json` | 保存ビュー |

## 既知の制約

- **未ロード Scene** はテキストシリアライズ（Force Text）の読み取りで索引します。Scene を開くとロード済み解析の結果に置き換わります（ノード ID は GlobalObjectId と同じ値を使うため一致します）。
  バイナリ形式の Scene は AssetDatabase の依存情報のみです。Additive ロードによる精密解析モードは未実装です。
- 未ロード Scene 内の Prefab Instance は Prefab Asset の値 + Override から再構成します。入れ子 Prefab の内側への Override 参照は解決しません（診断に表示）。
- **UdonSharp 解析は構文ベース**（Roslyn SyntaxTree）です。意味解析（SemanticModel）は未使用のため、基底クラスのフィールド、プロパティ経由のチェーン、
  メソッド戻り値、オーバーロードの区別は追跡しません（Unresolved として表示）。
- Udon Graph は SDK が公開する `UdonGraphData` を読みます。操作ルールに無いノードは集計して診断に出します。
- Animator のバインディングは Controller が使用する Clip のパスを Animator 階層で解決した**推定**です（状態遷移の到達性は考慮しません）。
- Prefab Stage（Prefab Mode）内のオブジェクトは Prefab Asset 側の対応オブジェクトとして扱います（未保存の編集内容は保存後に反映）。
- PNG エクスポート、Batch Mode 別プロセスによるオフライン索引は未実装です。
- 大きな Scene は GameObject 64 個程度の「チャンク」に分けてフレームごとの時間予算内で解析し、変更のあったチャンクだけを再解析します
  （3 万 GameObject・約 11 万ノード / 29 万エッジの Scene で、値の変更は数 ms、1 フレームの処理は最大 50ms 程度）。
  初回の全体解析は数秒〜数十秒かけて裏で進みます。1 つの Component の走査や 1 つの Prefab の読み込みはそれ以上分割できないため、
  非常に重い Component・Prefab ではその分だけ 1 フレームが長くなります。
- 作成直後（未保存）のオブジェクトは保存されるまで一時 ID で索引します（保存後に永続 ID へ置き換わります）。

## テスト

`Packages/manifest.json` の `testables` に本パッケージを追加し、Test Runner の EditMode で実行します。
VRChat 用テスト（`Tests/VRChat`）は SDK 導入プロジェクトでのみコンパイルされます。

手動確認用 Fixture: Package Manager の Samples から **Fixture Generator** をインポートし、
`Tools > UnityNetworkViewer > Generate Fixtures` を実行します（複数 Scene / Variant / Nested / Override / Missing / 配列 / UnityEvent / Animator）。

## ライセンス

[MIT License](LICENSE.md)

UdonSharp ソース解析は VRChat SDK 同梱の Roslyn を実行時に参照します（本パッケージは再配布していません）。
本パッケージは VRChat Inc. / Unity Technologies とは関係のない非公式ツールです。

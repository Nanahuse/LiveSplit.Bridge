# LiveSplit.Bridge

LiveSplit.Bridgeは、外部アプリケーションとLiveSplitを接続するためのLiveSplit
コンポーネントです。対応する外部アプリケーションから、タイマーの状態確認や操作、
ゲーム内時間の制御ができるようになります。

このコンポーネント単体では画面表示や自動Splitを行いません。LiveSplit.Bridgeに対応した
外部アプリケーションと組み合わせて使用してください。

## 対応環境

- Windows
- LiveSplit 1.8.37

## できること

対応する外部アプリケーションから、次の機能を利用できます。

- LiveSplitの現在のタイマー状態を確認する
- タイマーの開始、Split、Skip、Undo、Reset、Pause、Resumeを操作する
- ゲーム内時間を設定、Pause、Resumeする
- LiveSplit側で行われたタイマー操作や設定変更を受け取る

## インストール

1. [最新のReleases](https://github.com/Nanahuse/LiveSplit.Bridge/releases/latest)から
   `LiveSplit.Bridge-v*.zip`をダウンロードします。
2. LiveSplitを終了します。
3. ZIPの内容をLiveSplitの`Components`フォルダーへ展開します。
4. LiveSplitを起動します。
5. レイアウトを右クリックして`Edit Layout...`を開きます。
6. `+`から`Control` > `LiveSplit Bridge`を追加します。

`LiveSplit Bridge`はレイアウト上には表示されません。レイアウトに追加されている間、
対応する外部アプリケーションから接続できます。

## 設定

レイアウト編集画面で`LiveSplit Bridge`の設定を開くと、接続に使用するRPCポートと
イベントポートを変更できます。既定値は次のとおりです。

| 項目 | 既定値 |
|---|---:|
| RPCポート | `54000` |
| イベントポート | `54001` |

通常は既定値のまま使用できます。外部アプリケーション側で接続先を指定する場合は、
ここで設定したポートと同じ値を指定してください。設定内容はLiveSplitのレイアウトに
保存されます。

## 更新

1. LiveSplitを終了します。
2. 新しいリリースのZIPをダウンロードします。
3. ZIPの内容を既存のLiveSplitの`Components`フォルダーへ上書きします。
4. LiveSplitを起動します。

## アンインストール

1. レイアウト編集画面から`LiveSplit Bridge`を削除します。
2. LiveSplitを終了します。
3. `Components`フォルダーからLiveSplit.Bridgeのファイルを削除します。

## トラブルシューティング

- 外部アプリケーションが接続できない場合は、現在のレイアウトに
  `LiveSplit Bridge`が追加されていることを確認してください。
- LiveSplitを複数起動する場合は、ポートが重複しないよう、それぞれのレイアウトで
  異なるRPCポートとイベントポートを設定してください。
- Bridgeは同じPCからの接続だけを受け付けます。別のPCからは接続できません。
- 更新後に問題が起きた場合は、LiveSplitを終了してからファイルを上書きしたか確認して
  ください。

## 開発者の方へ

クライアント実装、通信プロトコル、開発環境、ビルド、テスト、リリースについては
[`DEVELOPMENT.md`](DEVELOPMENT.md)を参照してください。

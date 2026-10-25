# Codex の作業指示

## 日本語の文章

日本語のドキュメントや PR 説明文を作成・推敲するときは、
[yomiyasu](https://github.com/nanaism/yomiyasu) スキルを使用してください。
`.agents/skills/yomiyasu/SKILL.md` を読み、その手順に従ってください。

## スキルの導入

スキル本体は各作業環境にインストールし、Git では管理しません。
未導入の場合は、リポジトリのルートで次を実行してください。

```sh
npx --yes skills add nanaism/yomiyasu --agent codex --skill yomiyasu --copy --yes
```

Windows PowerShell で `npx.ps1` の実行が制限される場合は、`npx.cmd` を使用してください。
検査スクリプトの実行には Python 3 が必要です。

導入済みかどうかは `npx --yes skills list --agent codex` で確認できます。

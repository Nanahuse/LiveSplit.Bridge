# JSON v4 Schema tools

C# DTOから公開JSON Schemaを生成し、実際のC#シリアライズ結果と固定ケースで検証します。

必要なツール、生成・同期・検証コマンド、生成器の補正、SchemaとSerializerの差は
[Schemaの生成・検証](../../docs/schema-generation.md)を参照してください。
生成結果の`schemas/v4/`は手作業で編集しないでください。

| ファイル | 用途 |
| --- | --- |
| `Program.cs` | DTOから3つのSchemaを生成する |
| `sync_schemas.py` | 公開Schemaへコピーする。`--check`ではバイト単位で比較する |
| `ContractSamples/` | Runtime DTOのシリアライズ例を出力する |
| `samples/validation-cases.json` | Protocolの許容・拒否条件を検査する固定ケース |
| `validate_schemas.py` | jsonschemaでDraft 2020-12のSchemaと各メッセージを検証する |
| `pyproject.toml`・`uv.lock` | Python依存を固定する。`uv sync --locked`で復元する |

通信仕様は[Protocol Reference](../../docs/protocol.md)、クライアントからのSchema利用は
[クライアント開発ガイド](../../docs/client-guide.md#6-schemaの利用)を参照してください。

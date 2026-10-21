# JSON v4 既存テストの整理結果

2026-10-21。基準は `0966623`、作業ブランチは `codex/simplify-v4-tests`。

テストの目的を通信・状態管理の保証へ絞り、テスト専用の公開APIとPythonのSchema相当検証を削減した。JSON v4 DTO、serializer options、チェックイン済み3 Schemaは変更していない。生成方式の案Aへの全面移行は別作業とする。

## 変更内容

- Schema生成コマンドは生成だけを行う。C#の実通信サンプル出力を `ContractSamples.cs` と明示的な `--samples` コマンドへ分離した。既存33メッセージを維持し、26個のserializer境界チェックと `runtime-validation.txt` 出力を削除した。
- AjvはSchemaをcompileし、C#実出力33件と契約固有の拒否例21件を検査する。compileと重複する明示的なmeta-schema検証を削除した。SchemaのDraft宣言を含めた再現性は既存の `Sync-Schemas.ps1 -Check` で確認する。
- Pythonは応答ID照合、Session、成功/error排他、エラー表示、Controlのnull成功、Queryのobjectを確認する。Eventでは表示と欠落警告に使うtype/Session/sequenceだけを確認する。Run/Attempt/Context/Timerの全フィールド、未使用画像やmetadata、enum候補一覧の検証を削除した。
- CLIの通常表示で必要なデータがなければ表示処理の例外を `BridgeClientError` に変換する。`--json` はpayloadを変換せず出力する。phase文字列の大文字小文字は検査しない。未知のerror codeもmessageとともに表示する。
- timeoutの可否を調べるためだけの一時socketを削除した。CLIに必要な有限・正値・ms丸めは維持し、実接続で生じる値/範囲エラーを接続失敗として報告する。10進秒→ticksの正確な変換は維持した。
- MSBuild XML検査5件を削除した。製品の配置対象は `$(TargetPath)` 一つとし、配布リスト用のproperty/itemを削除した。`package-release.ps1` はそのRelease出力を直接ZIP化し、実ZIPが非空の `LiveSplit.Bridge.dll` 一つだけであることを検査する。可変allowlist、staging directory、staging検査、再帰削除が不要になった。checksum出力とRelease時のPDB無効設定は維持した。
- Runtimeテストは具体的な応答/Eventの派生型、null sentinelの参照、重複するJSON断片の検査から、応答値と通知の観測へ変更した。公開状態はGetRun/GetContext Query経由で確認する。`SessionId` / `PublishedRun` / `PublishedContext` のテスト専用getterを削除した。
- Event Sequenceの内部加算テストは既存の全callbackテストへ統合し、実際の出力が `uint.MaxValue, 1, 2, ...` になることを確認する。加算ヘルパーは本体へインライン化した。40億回のcallback実行を避ける境界設定にだけprivate fieldのreflectionを使う。この一箇所は意図した内部依存であり、テスト専用の製品APIは増やさない。
- 購読テストは送信payload、送信中のsocket処理の完了、購読解除、late Bindのsocket閉鎖を確認する。Abort/Disposeの回数、SenderTask状態、Enqueue戻り値の全列挙を削除し、製品の `SenderTask` getterも削除した。二重停止防止の内部ガードは並行処理に必要なので維持した。
- 設定の既定値復帰を一つのTheoryに統合し、同じ失敗経路の解釈不能Version例を削除した。旧形式の無視、Version 1復元、保存は維持した。
- Snapshot/adapterの参照同一性要求を削除し、旧公開値と更新値で確認する。画像比較の独立Factは同値比較と既存の画像変更2ケースに重複するため削除した。

## 維持した状態管理

Runの24変更ケースは維持する。対象は名前、カテゴリ、offset、パス、metadata、RunId、platform/region/emulator、変数、comparisonの値/順序、segmentの増減/順序/各値、時刻、画像である。これは標準ライブラリの型検査ではなく、Bridgeが自前で実装する `ContentEquals` の比較漏れを防ぐ。比較漏れはRunChangedの欠落となる。実Runtimeでの通知抑制・通知時点のQuery・Resetと候補完了の競合も別途維持した。

スナップショットのcopy方法、画像bufferの参照構造は要求しない。既存の画像bufferは読取専用共有という所有契約を維持し、可変metadata、比較リスト、segment、時刻を書き換えても旧公開スナップショットが壊れないことを確認する。

Control直列化、Query独立性、古い候補の破棄、Resetの最新タイミング保護、破棄後の候補/通知抑止、遅い購読者の隔離、Origin権限、切断・再接続・ポート解放、Applyと起動失敗からの復旧、Python実通信E2Eは維持した。

## 規模比較

件数はパラメータ展開後の実行件数。Schema内の集約チェックと通信サンプルはテストフレームワークの件数に加算しない。

| 対象 | 前 | 後 | 説明 |
| --- | ---: | ---: | --- |
| .NET xUnit | 96 | 87 | MSBuild 5、Runtime 2、Snapshot 1、設定の冗長入力1を削減 |
| Python CLI単体 | 62 | 31 | validator専用25、socket専用1、重複入力5を削減 |
| Python E2E | 12 | 12 | 既存テストを変更せず維持 |
| Schema C#境界チェック | 26 | 0 | 独立したserializer自己検査を削除 |
| Schema固定fixture | 80 | 21 | 正常例重複、標準型範囲/変換、同制約の反復を削減 |
| C#実シリアライズ試料 | 33 | 33 | 全5 Query、7 Timer/4 Game Time操作、8 Response試料、9 Event試料 |

ソース量はUTF-8・LFへ正規化して比較する。改行コードによる減少は除外し、コメント/空行も含める。新しい `ContractSamples.cs` を含み、レポート文書は除外する。JSON fixtureは従来と同じ1例1行の整形を維持した。

| 対象 | 行数 前→後 | UTF-8 bytes 前→後 |
| --- | ---: | ---: |
| C#テスト・補助コード（Tests project） | 2,169 → 1,978 | 97,148 → 89,887 |
| CLI製品コード | 754 → 533 | 27,842 → 19,483 |
| CLIテスト（E2E含む） | 856 → 685 | 27,541 → 21,966 |
| Schema生成・試料出力・Ajvコード | 362 → 267 | 22,085 → 12,128 |
| Schema固定fixture | 82 → 23 | 16,178 → 3,524 |
| Bridge変更対象2 C#ファイル＋csproj | 988 → 969 | 43,667 → 42,957 |
| 配布スクリプト | 103 → 52 | 3,960 → 1,914 |

上記対象の合計は 5,314 → 4,507 行（807 行減）。C#のFact/Theory定義は62→51、Pythonのtest関数は24→20。Theoryのデータ行とpytest fixture `test_host` は関数数と区別する。

## CIの責務と実行結果

既存の2ジョブを維持する。Schema jobはLiveSplit本体に依存せず生成の再現性と通信契約を確認し、Windows jobは実Bridge/CLIの動作と実配布ZIPを確認する。目的が異なるためジョブを統合しない。Schema jobに明示的な試料出力stepを加えた。調査用の大量mutation/生成方式比較は製品CIへ追加していない。

以下はこのWindows作業環境で実行したCI相当の結果。GitHub Actionsの実行は行っていない。

| 検証 | 結果 |
| --- | --- |
| 整理前 `.NET dotnet test` | 96/96合格、skip 0 |
| 整理前 CLI単体 pytest | 62/62合格 |
| 整理後 Release全solution build | 成功、error 0 |
| 整理後 `.NET dotnet test` | 87/87合格、skip 0 |
| 整理後 CLI全pytest | 43/43合格（単体31＋実TestHost E2E12） |
| ruff check / format --check | 合格、5ファイル整形済み |
| Schema tool Release build | 成功、warning/error 0 |
| Sync-Schemas -Check | Request/Response/Eventすべてバイト一致 |
| Ajv契約検証 | 21/21固定例、33/33 C#実出力、3 Schema compile成功 |
| package-release.ps1 | 非空のBridge DLL一つだけのZIP、SHA256SUMS生成成功 |
| git diff --check | 合格 |

SDK 10.0.302、.NET Framework 4.8.1、Python 3.14.6、Ajv 8.17.1を使用。依存バージョン/lock fileは変更していない。初回のPython全テストはTestHost未ビルドのため失敗し、CIと同じ全solution buildを実行してから全43件を再実行して合格した。

再実行は `.github/workflows/ci.yml` および `tools/schema/README.md` のコマンドを使用する。Windows側は全solutionのRelease build後にdotnet testとCLI pytestを実行する。実行ログとTRX/JUnitはローカルのignored `.tmp/test-cleanup-before/`、`.tmp/test-cleanup-after/` に保存した。

## 残した制約・別作業

- CS8632（既存nullable context）とxUnit1030（既存ConfigureAwait使用）のwarningは残る。今回のテスト整理に伴う新しい失敗はない。
- 過去の生成方式調査は `0966623` / `investigation/schema-generation` を基準とする記録として維持する。113件入力を前提とした過去の集計・調査ハーネスはその版で再現する。削減後の54件を過去の113件と同じ母集団として扱わない。
- Schemaの整数1.0、JavaScript int64丸め、重複キー、配列/辞書内null、Base64 annotationとserializerの差異は今回修正していない。CLIは完全なSchema validatorではない。
- Schema管理方式の全面移行や既存WebSocket/Runtimeの動作仕様変更、v4 merge、Release/タグ発行は行っていない。次に案Aを採用する場合も生成と実出力契約検証の責務分離を維持し、旧DTO具象型に依存するテストを戻さない。

## 各テストの棚卸し

以下は変更前の名前を基準に、各Fact/Theory・pytest test関数の保証、重複/依存、判断を記録したもの。パラメータ化されたテストは行内でケース方針を示す。

### BridgeSettingsTests.cs

| テスト | 保証・必要性 | 重複/依存の評価 | 判断 |
| --- | --- | --- | --- |
| `ReadFrom_LoadsVersion1Port` | 既存Version 1設定のポート復元。保存済み設定との互換性。 | 保存テストと対象が異なる。XMLは実際の永続化形式。 | 維持 |
| `ReadFrom_MissingVersionUsesDefault` | Versionなし設定から既定値へ復帰。 | 初期化・実行・期待値が他の復帰テストと共通。 | UnsupportedSettingsRestoreDefaultへ統合 |
| `ReadFrom_UnparseableVersionUsesDefault` | 解釈不能Versionから既定値へ復帰。 | Versionなしと同じ読込失敗経路。入力表現の追加例。 | 削除 |
| `ReadFrom_DifferentVersionUsesDefault` | 未対応Versionを読み込まない。 | 他の復帰テストと共通。 | UnsupportedSettingsRestoreDefaultへ統合 |
| `ReadFrom_InvalidVersion1PortUsesDefault` | 使用不能ポートから既定値へ復帰。 | Version失敗とは異なる設定値エラー。同じ手順を共用。 | UnsupportedSettingsRestoreDefaultへ統合 |
| `ReadFrom_LegacySettingsWithoutVersionAreIgnored` | 旧RpcPort/EventPortを採用しない。実際の設定移行互換性。 | 旧形式固有の条件を独立維持。 | 維持 |
| `WriteTo_SavesVersionAndPort` | 保存したVersionとポートを復元可能な形式にする。 | 設定フォーマット自体は契約。MSBuild XMLとは異なる。 | 維持 |

### ComponentLifecycleTests.cs

| テスト | 保証・必要性 | 重複/依存の評価 | 判断 |
| --- | --- | --- | --- |
| `LayoutSwitchReleasesPreviousRuntimeAndStartsNextRuntimeOnItsOwnPorts` | Layout切替時に旧ポートを解放して新ホストを起動。 | Componentの組合せライフサイクル。 | 維持 |
| `SetSettingsDoesNotBindEndpointUntilUpdate` | 設定読込だけで接続を開始しない。 | Componentの起動タイミング固有。 | 維持 |
| `ComponentPublishesJsonV4RpcEndpoint` | Componentの起動から実RPC公開まで接続できる。 | RPC内容は他と重なるがComponent配線の疎通確認。 | 維持 |
| `DisposeReleasesEndpointForReuse` | Component破棄後のポート再使用。 | 接続クライアントなしのライフサイクル。 | 維持 |
| `ApplyRestartsRuntimeOnNewPortAndReleasesOldOne` | 設定UIのApplyで再起動・ポート移動・状態表示。 | UI操作はこの統合経路に必要。 | 維持 |
| `BindFailureMarksStatusFailedAndRetriesAfterDelay` | 占有ポートで失敗表示し、解放後に再試行で復旧。 | 起動失敗の実動作。再試行期限は必要。 | 維持 |

### DeploymentProjectTests.cs

| テスト | 保証・必要性 | 重複/依存の評価 | 判断 |
| --- | --- | --- | --- |
| `DeploymentIsConfinedToLiveSplitComponentsDirectory` | 配置先指定を確認。 | MSBuild Target/変数名への依存。実ファイルを観測しない。 | 削除。実ZIP検証へ責務集約 |
| `DeploymentDoesNotReplaceLiveSplitRuntimeConfiguration` | host設定を上書きしないことを意図。 | プロジェクトXML文字列の不在だけを確認。 | 削除。配置はTargetPath一つに限定 |
| `DistributionAllowListContainsOnlyExpectedBridgeFiles` | 配布リストの値を固定。 | MSBuildの表現を検査。実ZIPの検査と重複。 | 削除 |
| `DeploymentUsesDistributionAllowListInsteadOfReferenceCopyLocalPaths` | 配置の内部的なファイル選択方法を固定。 | 項目名・変換構文に依存。 | 削除 |
| `ReleaseBuildDoesNotGenerateDebugSymbols` | ReleaseのPDB設定を確認。 | XML属性の検査。配布物への混入はZIPで確認。 | 削除。既存Release設定は維持 |

### JsonBridgeRuntimeTests.cs

| テスト | 保証・必要性 | 重複/依存の評価 | 判断 |
| --- | --- | --- | --- |
| `MapsQueriesAndControlResponsesWithSessionAndRequestIds` | 同じruntimeの応答ID対応とSession一貫性、Control実行。 | 具体的DTO派生型・Result参照同一性・null文字列検査を除去。 | JSON上のIDと操作回数へ簡素化 |
| `DeniesControlBeforeParamsValidationAndMapsOperationErrors` | 許可前に操作しない。処理失敗と不正引数を対応するErrorへ写像。 | 実行回数は権限判定順序の観測に必要。派生型要求は不要。 | Error codeを観測して維持 |
| `QueryExceptionReturnsInternalError` | Query例外をRPCエラーに変換しIDを維持。 | 具象ErrorResponse要求を除去。 | 維持・簡素化 |
| `QueryDoesNotWaitForControlOperationGate` | 停止中のControlがQueryを塞がない。 | ブロックするadapterは並行性を再現する必要なseam。 | 維持。応答はwire内容で観測 |
| `ControlOperationsAreSerialized` | 複数Controlが同時にadapterへ入らない。 | ゲートと同期信号はBridge固有の競合再現に必要。 | 維持。応答派生型要求を除去 |
| `TimerCallbacksEnqueueJsonEventsInOrderAndResetAllowsNullState` | 状態取得失敗時でもResetを順序通りnull stateで通知。 | 型要求と文字列Containsを除去。通常Resetは別の実通信テスト。 | 通知のtype/sequence/stateを観測して維持 |
| `JsonAdapterControlsUseLiveSplitTimerAndGameTimeOperations` | Pause/ResumeとGame Time制御がLiveSplitへ反映される。 | DTOの一般変換ではなく実LiveSplit操作。 | 維持 |
| `TimerEventsMapAllStateCallbacksToJsonTypesInSequence` | 全Timer callbackの通知種類と順序。 | 具象Event型を要求しない。wrapの境界設定のみreflection。 | wrapも統合して維持 |
| `TimerEventSequenceWrapsFromMaximumToOne` | 非ゼロsequenceの折返し。 | 内部加算ヘルパー呼出だけでは実際の通知を保証しない。 | 独立テストを削除。callbackから出力されるsequenceで確認 |
| `ContextChangesPublishAnImmutableSnapshotAndNullStateEvent` | Contextの変更時通知・同内容の通知抑制・公開済み値の独立性。 | singleton参照と重複null文字列検査を除去。旧応答を後で読み直す。 | Queryと通知で観測して維持 |
| `RunChangedIsQueuedAfterTheNewSnapshotIsPublished` | 通知受信時点で新RunをQueryできる。 | PublishedRun getterとnull sentinel検査を除去。 | Query経由で維持 |
| `ResetPublishesTimingAndRunIdChangesBeforeRunChanged` | Reset直後のRunId/タイミングと通知順。 | 内部公開getterを除去。 | Query経由で維持 |
| `DisposingRuntimeSuppressesEventsFromLaterStateChanges` | 破棄後にcallbackから通知しない。 | 重要なイベント解除の観測。 | 維持 |
| `NewerRunCandidateWinsWhenAnOlderBuildFinishesLater` | 古い非同期候補が新しいRunを上書きしない。 | 候補実行制御seamは決定的な競合再現に必要。 | Query/通知で維持 |
| `CandidateCompletingAfterResetKeepsTheResetTimingSnapshot` | 遅い静的候補がResetの最新タイミング/RunIdを戻さない。 | 通常の新旧候補競合とは異なる世代の競合。 | Query/通知で維持 |
| `DisposedRuntimeDoesNotPublishACompletedBackgroundCandidate` | 破棄後に完了した候補から通知も状態更新もしない。 | イベント解除テストと異なる非同期完了経路。 | Query/通知で維持 |
| `RuntimeResponsesSerializeToTheResponseSchemaShape` | Response/Error/Reset/ContextのJSON断片を確認。 | SchemaのC#実出力検証とWebSocket通信に重複。 | 削除 |

### JsonEventSubscriptionTests.cs

| テスト | 保証・必要性 | 重複/依存の評価 | 判断 |
| --- | --- | --- | --- |
| `FullQueueDisconnectsOnlySlowSubscriberAndKeepsFastSubscriberOrdered` | 遅い購読者の送信停滞を隔離し正常購読者の順序を保護。 | Abort回数・SenderTask・全Enqueue戻り値の検査を除去。 | 送信payload、切断通知、blocked sendの解放で維持 |
| `DisposeAbortsBlockedSenderOnceAndDoesNotWaitForItSynchronously` | 破棄が送信停滞を解放し同期的に待ち続けない。 | Abort/Dispose回数と呼出順序要求を除去。 | DisposeReleasesBlockedSendWithoutWaitingSynchronouslyへ簡素化 |
| `OverflowBeforeSocketBindCannotBeRevivedByLateBind` | 切断した購読を遅いBindで復活させない。 | Task生成状態・Abort/Dispose回数を除去。 | late socketがOpenでなく送信されないことで維持 |

### JsonLiveSplitAdapterTests.cs

| テスト | 保証・必要性 | 重複/依存の評価 | 判断 |
| --- | --- | --- | --- |
| `MapsTimerAttemptCompletedCountAndContext` | LiveSplitの各状態値を通信モデルへ写像。 | 実ドメイン状態を使用。serializer自己検査と異なる。 | 維持 |
| `CapturesRunStateAndDetachedPngImages` | 元画像破棄後もキャプチャしたRun/PNGを利用可能。 | PNG signatureは画像変換結果の契約。Base64標準変換テストとは異なる。 | 維持 |
| `TimingUpdateCopiesPublishedSnapshotAndRefreshesRunIdAndTimes` | 更新後も旧公開Runが壊れず、構造不整合は更新しない。 | 参照Same/NotSameを除去。前後の値で確認。 | 維持・簡素化 |
| `TreatsEmptyLiveSplitRunIdAsUnset` | LiveSplitの空RunIdを未設定へ正規化。 | Bridge固有の意味変換。 | 維持 |
| `MergeRunTimingsPreservesCandidateStaticDataAndRejectsStructureMismatch` | 候補静的情報を保ちながらタイミング更新、異なる構造は拒否。 | 参照同一性を除去。旧値と更新値で確認。 | 維持・簡素化 |

### JsonWebSocketEventTests.cs

| テスト | 保証・必要性 | 重複/依存の評価 | 判断 |
| --- | --- | --- | --- |
| `TimerControlProducesOrderedTextEventWithMatchingSession` | 複数購読者への通知・RPCとのSession対応・Run差替え。 | 単なるSchema形状ではなく実通信の結合。 | 維持 |
| `RemainingTimerEventsAndQueriesShareTheWebSocketContract` | 全callback制御とQueryが同じ状態を返す。 | runtime単体だけではtransport/実adapter連携を保証しない。 | 維持 |
| `ContextChangesArePublishedAsNullStateTextEvents` | Context変更を実通信で固定nullのstateとともに配信。 | Schema試料とは別の通知経路確認。 | 維持 |

### JsonWebSocketLifecycleTests.cs

| テスト | 保証・必要性 | 重複/依存の評価 | 判断 |
| --- | --- | --- | --- |
| `DisposeWithConnectedClientReleasesPortForReconnect` | 接続済みクライアントがいても停止・同ポート再接続可能。 | 接続なしのComponent破棄とは異なる。 | 維持 |

### JsonWebSocketRpcTests.cs

| テスト | 保証・必要性 | 重複/依存の評価 | 判断 |
| --- | --- | --- | --- |
| `V4QueryAndIdentifiableInvalidRequestsUseTextResponses` | 全Query/Controlの入口と識別可能な不正要求へのエラー返信。 | wire境界の責務。一般型変換の単体検査を置かない。 | 維持 |
| `UnidentifiableOrMalformedEnvelopeIsDropped` | 識別不能要求を操作せず捨て、次の正常要求を受信可能。 | 入力例はdropとreplyの分岐を守るため。 | 維持 |
| `DuplicatePropertiesAreRejectedWithKnownRequestId` | 曖昧な操作要求を実行せず既知IDへエラー返信。 | Bridgeの独自重複キー検出と入口の契約。 | 維持 |
| `BinaryMessageIsDroppedAndConnectionRemainsUsable` | 非text入力を処理せず、接続を利用し続けられる。 | フレーム境界固有。 | 維持 |
| `RpcEnforces64KibibyteLimitAcrossFragments` | 断片化に関係なく要求サイズ制限を適用。 | Bridgeの上限処理。ライブラリ一般挙動ではない。 | 維持 |
| `LargeRunWithPngIconCanBeReturnedWithoutRequestLimit` | 大きな画像応答に要求側のサイズ制限を誤適用しない。 | 実Runと画像の応答方向の確認。 | 維持 |
| `ExternalOriginRetainsQueryButDeniesControl` | 外部OriginのQueryを維持しControlを拒否。 | Originの詳細行列とは別のread/control分離を確認。 | 維持 |

### SnapshotOperationsTests.cs

| テスト | 保証・必要性 | 重複/依存の評価 | 判断 |
| --- | --- | --- | --- |
| `RunContentEquals_RecognizesEqualDistinctSnapshotsAndDictionaryOrder` | 同じ値から不要通知を起こさない。辞書順序や画像配列の同一性に依存しない。 | root参照NotSameを除去。画像同値テストをここへ集約。 | 維持・簡素化 |
| `RunContentEquals_DetectsChangesToEveryPublishedField` | 24種類の公開データ変更を比較漏れで捨てない。 | 自前ContentEqualsは通知を抑止する条件。全フィールド確認に意味がある。 | 24ケースを維持 |
| `RunContentEquals_ComparesImageBytesRatherThanArrayIdentity` | 画像値の一致/差異を比較。 | 同値は前のFact、画像変更2例は24ケースTheoryで既に確認。 | 削除 |
| `ContextContentEquals_UsesValuesAndIgnoresDictionaryInsertionOrder` | Contextの値差分は通知、辞書順序だけなら抑制。 | 自前の比較関数が通知に直接影響する。 | 維持 |
| `CopyForTimingUpdate_IsolatesMutableStateAndPreservesImageBytes` | コピー更新で旧公開Runの可変データを壊さず画像値を維持。 | NotSame要求を除去。具体的コピー方式を指定しない。画像は既存の読取専用所有契約。 | 維持・簡素化 |

### WebSocketOriginTests.cs

| テスト | 保証・必要性 | 重複/依存の評価 | 判断 |
| --- | --- | --- | --- |
| `ValidatorAllowsLoopbackAndMissingOrigins` | Originなし/IPv4/IPv6/localhost/HTTP(S)の制御許可。 | 独自権限ポリシー。単なるURLパーサ再テストではない。 | 維持 |
| `ValidatorRejectsOtherOrigins` | 外部host、紛らわしいhost、非HTTPを許可しない。 | 権限の取り違えを防ぐ具体例。 | 維持 |
| `JsonV4RpcAndEventsPreserveOriginPermissions` | 接続時Originが制御許可へ反映されEventsは利用可能。 | 単体ポリシーと異なる接続配線確認。 | 維持 |

### test_bridge_e2e.py

| テスト | 保証・必要性 | 重複/依存の評価 | 判断 |
| --- | --- | --- | --- |
| `test_queries_return_v4_state_and_complete_json_envelopes` | CLIで全Queryを取得・表示しraw JSONを保つ。 | 実TestHostとの統合。validator専用正常例をここへ集約。 | 維持 |
| `test_timer_and_game_time_controls_change_observed_state` | CLI操作結果を再QueryしてLiveSplitへの反映を確認。 | 単体の要求JSON確認と異なる実操作。 | 維持 |
| `test_events_cli_receives_ordered_v4_events` | CLIが実通知の種類・順序を出力。 | fake Event型検査とは異なる実購読。 | 維持 |
| `test_events_json_outputs_complete_event_and_preserves_null` | Eventのraw JSONがenvelopeとnullを保持。 | 通常表示との機能差。 | 維持 |
| `test_bridge_invalid_request_error_is_reported_by_client` | 実Bridgeが返すinvalid_requestを例外として報告。 | 単体CLI stderr確認と別の実通信。 | 維持 |
| `test_connection_failure_returns_nonzero_and_error` | 接続できない場合に非ゼロ終了と説明を出す。 | OS例外の分類表を独自に追加しない。 | 維持 |
| `test_testhost_rejects_port_occupied_by_another_process` | 使用中ポートでTestHostが正常起動したと誤報しない。 | 実プロセスの競合。 | 維持 |
| `test_testhost_shutdown_releases_rpc_port` | プロセス停止後のポート解放。 | Component単体とは異なる起動/停止全体。 | 維持 |
| `test_event_cli_reports_component_shutdown_as_connection_error` | 通知購読中のBridge終了を説明付きエラーで処理。 | 単なる接続前エラーとは別経路。 | 維持 |
| `test_explicit_port_overrides_invalid_environment` | 無効な環境変数があっても明示ポートで実接続。 | 単体の優先順位設定確認と少し重複するが、実プロセス設定が目的。 | 維持 |
| `test_testhost_rejects_invalid_port` | TestHostの未使用/範囲外ポートを起動前に拒否。 | CLI側とは異なるTestHostの入口。 | 2ケース維持 |

### test_cli.py

| テスト | 保証・必要性 | 重複/依存の評価 | 判断 |
| --- | --- | --- | --- |
| `test_format_ticks` | CLI固有の時刻表示と負の時刻を保持。 | 標準整数変換ではなく表示ルール。 | 維持 |
| `test_rpc_error_is_reported_and_returns_nonzero` | Control失敗をstderrと終了値へ反映し正しい操作要求を送る。 | fake通信はCLI表示の検証に使用。 | 維持 |
| `test_invalid_arguments_fail_before_connecting` | 操作パラメータ不備、tick範囲外、無効timeoutで操作前に終了。 | nan/inf等の同経路の表現違い、巨大timeoutでsocket限界を探る例を削除。 | 14→9ケースへ整理 |
| `test_port_environment_parsing_and_cli_precedence` | 既定/環境/明示引数のポート選択と無効設定の報告。 | CLI固有の設定優先順位。 | 維持 |
| `test_socket_supported_timeout_is_passed_to_rpc` | socketが長いtimeoutを扱えることと値の受渡し。 | socket自己検査と専用probeを要求する。 | 削除 |
| `test_decimal_ticks_are_sent_exactly_in_json_rpc` | 10進秒→100ns ticksとties-to-even、int64端値を正確に送る。 | Bridge CLI固有の単位変換。標準整数範囲の再テストとは異なる。 | 8ケース維持 |
| `test_rpc_response_rejects_zero_or_non_integer_session_id` | 応答Sessionを安全に識別する。 | Session非ゼロは通信契約。判定補助関数の単体テストは作らない。 | 4ケース維持 |
| `test_json_output_preserves_complete_response` | raw JSON出力でenvelopeとnullable値を落とさない。 | CLIの公開機能。 | 維持 |
| `test_rpc_rejects_malformed_query_results` | 独自payload validatorの全構造/型/範囲検査。 | SchemaをPythonに複製した処理の専用テスト。 | 9ケースと専用helper/fixtureを削除 |
| `test_rpc_accepts_complete_query_results` | 独自validatorが5種類の完全なpayloadを受理。 | 実通信Query E2EとSchemaのC#実出力検証に重複。 | 5ケースを削除 |
| `test_invalid_query_response_is_reported_without_traceback` | 表示に必要な情報不足でも未処理例外を出さない。 | validator名/field pathへの依存を除去。 | 表示失敗のstderrと終了値で維持 |
| `test_event_payload_validation` | Event種類と全payloadを独自Schema検証。 | RUNNINGの表記差を含む11例はCLIが必要としない構造制限。 | 11ケースとvalidatorを削除 |
| `test_invalid_event_is_reported_without_traceback` | Eventの表示対象データ不足を説明付きエラーにする。 | 独自Schema validatorのエラー文字列を要求しない。 | 表示失敗のstderrと終了値で維持 |

### Program.csの集約C#チェック26項目

すべて削除。以下の通信上の責務だけを残し、独立したserializer自己検査は代替追加しない。

| 旧チェック | 削除理由・残る責務 |
| --- | --- |
| five query methods deserialize to their declared derived types | 派生型の一般的な解決。全Queryの実RPCとC#実出力を維持。 |
| all seven timer operation parameter types survive round-trip | 標準多態round-trip。全7操作の試料と実RPCを維持。 |
| all four game-time operation parameter types and set ticks survive round-trip | 標準多態round-trip。4操作の試料、実操作E2Eとticks要求を維持。 |
| missing operation discriminator is rejected | 固定fixtureのoperation必須制約に集約。 |
| game_time_operation set requires ticks | 固定fixtureと実RPCのset missing ticksエラーに集約。 |
| game_time_operation set accepts signed ticks before its discriminator | プロパティ順序のserializer対応確認。独立保証不要。 |
| unknown timer or game-time operation discriminator is rejected | 固定fixtureと実通信の不正操作拒否へ集約。 |
| null or wrong-typed required game-time ticks are rejected | 標準の型/null変換の反復。set必須の契約例を維持。 |
| non-set game-time operation rejects ticks as unmapped | 非setにticksを許さない固定fixtureへ集約。 |
| unknown request method is rejected | 固定fixtureと実RPCのunknown methodエラーへ集約。 |
| query method rejects operation params | 固定fixtureのmethod/params相関に集約。 |
| missing operation params is rejected | 固定fixtureへ集約。 |
| request_id before method is accepted when out-of-order metadata is enabled | プロパティ順序のserializer対応確認。独立保証不要。 |
| invalid enum text and numeric enum values are rejected | 一般enum変換確認。protocol discriminatorはfixture/実RPCで維持。 |
| shared explicit-null converter emits and accepts null only | converter自己検査。C#Control実出力とnull制約fixtureへ集約。 |
| success and error response DTOs serialize mutually exclusive members | 全Response実出力のAjv検証とresult/error排他fixtureへ集約。 |
| required nullable TimeValue fields reject omission but allow null | 標準required/nullableの読み書き。nullを含む実出力は維持。 |
| RunState byte arrays serialize as Base64 strings and nullable fields as null | 標準byte[]変換。空/実RunのC#実出力は維持。 |
| System.Text.Json rejects invalid Base64 while Ajv annotation validation is optional | 標準Base64 decoderとannotationの機能差。既知差異として文書化。 |
| all eight event types serialize and deserialize with type discriminator | 標準多態round-trip。全Event実出力と実購読テストへ集約。 |
| timer_reset requires timer_state and accepts object or null | Resetのobject/null実出力、欠落fixture、Runtime失敗時null通知を維持。 |
| non-reset timer events reject null timer_state | 固定fixtureの非Reset null拒否へ集約。 |
| run/context changed events emit and accept only explicit null timer_state | 変更Eventの実出力、固定null fixture、実購読へ集約。 |
| unknown properties are rejected on requests and state DTOs | 追加field拒否fixtureと実RPCのinvalid_requestへ集約。 |
| request and response request_id round-trip zero, one, and uint32 maximum | 標準uint round-trip。実RPCのID0とRuntimeの最大ID照合を維持。 |
| request_id rejects overflow, negative, null, and omission in Request and Response | 標準uint変換・required。実RPCのdrop/reply判定と必須fixtureを維持。 |

### 固定fixture 80項目

C#実シリアライズ試料33件はすべて維持。下表は手書きfixtureだけの判断。

| Schema | 旧fixture | 判断・理由 |
| --- | --- | --- |
| request | get_timer_state query | 削除。C#の実出力と重複する手書き正常例。 |
| request | request id zero | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| request | request id uint32 maximum | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| request | get_run query | 削除。C#の実出力と重複する手書き正常例。 |
| request | get_attempt query | 削除。C#の実出力と重複する手書き正常例。 |
| request | get_context_state query | 削除。C#の実出力と重複する手書き正常例。 |
| request | get_completed_count query | 削除。C#の実出力と重複する手書き正常例。 |
| request | timer start | 削除。C#の実出力と重複する手書き正常例。 |
| request | timer split | 削除。C#の実出力と重複する手書き正常例。 |
| request | timer skip | 削除。C#の実出力と重複する手書き正常例。 |
| request | timer undo | 削除。C#の実出力と重複する手書き正常例。 |
| request | timer reset | 削除。C#の実出力と重複する手書き正常例。 |
| request | timer pause | 削除。C#の実出力と重複する手書き正常例。 |
| request | timer resume | 削除。C#の実出力と重複する手書き正常例。 |
| request | game time initialize | 削除。C#の実出力と重複する手書き正常例。 |
| request | game time set with signed ticks | 削除。C#の実出力と重複する手書き正常例。 |
| request | game time set with reordered ticks | 削除。serializerのプロパティ順序対応。 |
| request | game time pause | 削除。C#の実出力と重複する手書き正常例。 |
| request | game time resume | 削除。C#の実出力と重複する手書き正常例。 |
| request | unknown method | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| request | method and params mismatch | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| request | missing timer operation params | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| request | missing operation discriminator | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| request | invalid operation enum | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| request | game time set missing ticks | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| request | game time non-set has unnecessary ticks | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| request | request id above uint32 | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| request | request id negative | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| request | request id null | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| request | request id missing | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| response | response request id zero | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| response | response request id uint32 maximum and session id maximum | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| response | timer state success with unset game time | 削除。C#の実出力と重複する手書き正常例。 |
| response | run state with absent metadata and image and empty collections | 削除。C#の実出力と重複する手書き正常例。 |
| response | run state with metadata, comparisons, segment, and Base64 PNG bytes | 削除。C#の実出力と重複する手書き正常例。 |
| response | contentEncoding annotation does not assert Base64 by default | 削除。AjvのcontentEncoding annotationの一般挙動。 |
| response | attempt response and custom variables | 削除。C#の実出力と重複する手書き正常例。 |
| response | context response with Custom Variables | 削除。C#の実出力と重複する手書き正常例。 |
| response | completed count response | 削除。C#の実出力と重複する手書き正常例。 |
| response | control success null | 削除。C#の実出力と重複する手書き正常例。 |
| response | error response | 削除。C#の実出力と重複する手書き正常例。 |
| response | result and error coexist | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| response | result and error both missing | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| response | control result must be null | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| response | session id zero | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| response | session id above uint32 | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| response | response request id above uint32 | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| response | response request id negative | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| response | response request id null | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| response | response request id missing | 削除。共通必須ID制約の反復。要求側の必須例と実出力を維持。 |
| response | invalid nullable ticks value | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| response | unknown response property | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| response | invalid timer phase enum | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| response | error must not be null | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| response | invalid bridge error enum | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| response | uint32 attempt count overflow | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| response | signed 32-bit split index overflow | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| response | signed 64-bit ticks gross overflow | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| event | timer_started event | 削除。C#の実出力と重複する手書き正常例。 |
| event | timer_split event | 削除。C#の実出力と重複する手書き正常例。 |
| event | timer_skipped event | 削除。C#の実出力と重複する手書き正常例。 |
| event | timer_undo event | 削除。C#の実出力と重複する手書き正常例。 |
| event | timer_reset event | 削除。C#の実出力と重複する手書き正常例。 |
| event | timer_reset with null timer state | 削除。C#の実出力と重複する手書き正常例。 |
| event | timer_phase_changed event | 削除。C#の実出力と重複する手書き正常例。 |
| event | run_changed with explicit null timer state | 削除。C#の実出力と重複する手書き正常例。 |
| event | context_changed with explicit null timer state | 削除。C#の実出力と重複する手書き正常例。 |
| event | run_changed rejects timer state object | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| event | context_changed rejects timer state object | 削除。同じ固定null制約のrun_changed例と実Context通知に集約。 |
| event | timer_reset requires timer_state property | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| event | timer_started rejects null timer state | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| event | unknown event type | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| event | timer event missing state | 削除。必須timer_stateはReset例で確認、全Event実出力を維持。 |
| event | event sequence zero | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| event | event sequence uint32 maximum | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| event | event sequence above uint32 | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| event | event sequence negative | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| event | event sequence null | 削除。標準型範囲/型変換・一般enum変換の反復。 |
| event | event sequence missing | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |
| event | unknown event property | 維持。method/params、排他、固定null、非ゼロID、必須情報、未知field/typeの契約制約。 |

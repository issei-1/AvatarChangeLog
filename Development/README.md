# Avatar Change Log v0.1.1 開発用検証

このフォルダはソース版専用です。製品unitypackageには含まれません。

- Run-Checks.ps1: Unity 2022.3.22f1の参照で製品・CoreTests・統合テストをコンパイルし、Unity非依存のテストを実行します。
- Run-Checks.ps1 -RunUnity: 専用の検証プロジェクトを作成してUnity統合テストを実行します。利用可能なUnityライセンスが必要です。
- CoreTests.cs: 差分、記録タイミング、集約、検索、出力、保存上限などを検証します。
- Validation.cs / Fixture.cs: Unity上での取得・保存・自動記録・復帰・UI更新などを検証します。
- Run-Benchmarks.ps1 / Benchmarks.cs: 比較用ソースを指定して差分処理等を測定する開発用ツールです。

最新の自動テスト結果は親フォルダのvalidation-results.txtを参照してください。Unity Editor上での統合テスト実行と実操作確認の実施状況は、コンパイル成功とは区別して記載しています。

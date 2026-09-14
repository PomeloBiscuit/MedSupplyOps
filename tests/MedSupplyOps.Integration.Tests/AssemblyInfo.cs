using Xunit;

[assembly: TestCaseOrderer("MedSupplyOps.Integration.Tests.MutexTestCaseOrderer", "MedSupplyOps.Integration.Tests")]

// ★ 整合測試不平行執行。
//
// 這個組件裡的每一條測試都打同一個 Oracle 容器、同一個 schema。
// xUnit 預設會平行執行不同的測試類別，於是 A 類別建立的資料會出現在
// B 類別的查詢結果裡 —— 而症狀是「單獨跑某個類別是綠的、整包跑就紅」，
// 最難重現也最容易被歸咎成「環境問題」的那一種。
//
// 實際發生過：發料測試建立的批次出現在「N 天內到期」查詢的結果中，
// 讓一條原本會過的測試變紅。
//
// 注意這只關閉「測試類別之間」的平行，**不影響測試內部自己開的並發**——
// FR-402 的並發測試在單一條測試裡用 Task.WhenAll 開多條連線，不受此影響。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

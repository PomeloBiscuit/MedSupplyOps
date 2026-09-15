const test = require("node:test");
const assert = require("node:assert/strict");
const {
  createLatestRequestCoordinator,
  createAvailabilityPresentation,
} = require("../../src/MedSupplyOps.Web/wwwroot/js/requisition-create.js");

test("後發的可用量請求會取消並淘汰先發請求", () => {
  const coordinator = createLatestRequestCoordinator();
  const itemSelect = {};

  const first = coordinator.begin(itemSelect);
  const second = coordinator.begin(itemSelect);
  let displayedItem = null;

  // 模擬第二個請求先回來、第一個請求稍後才回來。
  if (coordinator.isCurrent(itemSelect, second)) displayedItem = "後選品項";
  if (coordinator.isCurrent(itemSelect, first)) displayedItem = "先選品項";

  assert.equal(first.controller.signal.aborted, true);
  assert.equal(coordinator.isCurrent(itemSelect, first), false);
  assert.equal(coordinator.isCurrent(itemSelect, second), true);
  assert.equal(displayedItem, "後選品項");
});

test("不足時就地顯示警示但只回傳呈現狀態，不形成送出封鎖", () => {
  const result = createAvailabilityPresentation(3, 5, "2030-01-01", {
    unselected: "尚未選擇品項。",
    insufficient: "庫存不足：需要 {0}，目前可用 {1}。仍可送出。",
    available: "可用量 {0}；最早效期 {1}",
    noLot: "無可用批次",
  });

  assert.deepEqual(result, {
    kind: "insufficient",
    text: "庫存不足：需要 5，目前可用 3。仍可送出。",
    isInsufficient: true,
  });
});

const test = require("node:test");
const assert = require("node:assert/strict");
const { shouldCommitSidebarDrag, DRAG_THRESHOLD } = require("../../src/MedSupplyOps.Web/wwwroot/js/sidebar-drag.js");

test("展開時往左拖過門檻才收合；往右拖或拖不夠都不動", () => {
  assert.equal(shouldCommitSidebarDrag("compact", 180, 180 - DRAG_THRESHOLD), true);
  assert.equal(shouldCommitSidebarDrag("compact", 180, 180 - DRAG_THRESHOLD + 1), false);
  assert.equal(shouldCommitSidebarDrag("compact", 180, 260), false);
});

test("圖示列時往右拖過門檻才展開；往左拖不動", () => {
  assert.equal(shouldCommitSidebarDrag("expanded", 64, 64 + DRAG_THRESHOLD), true);
  assert.equal(shouldCommitSidebarDrag("expanded", 64, 64 + DRAG_THRESHOLD - 1), false);
  assert.equal(shouldCommitSidebarDrag("expanded", 64, 10), false);
});

test("offcanvas 往左拖才關閉", () => {
  assert.equal(shouldCommitSidebarDrag("close", 180, 100), true);
  assert.equal(shouldCommitSidebarDrag("close", 180, 200), false);
});

test("不認得的目標或非數值座標一律不動作", () => {
  assert.equal(shouldCommitSidebarDrag("hidden", 180, 0), false);
  assert.equal(shouldCommitSidebarDrag("", 180, 0), false);
  assert.equal(shouldCommitSidebarDrag("compact", Number.NaN, 0), false);
  assert.equal(shouldCommitSidebarDrag("expanded", 0, Number.POSITIVE_INFINITY), false);
});

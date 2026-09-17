const test = require("node:test");
const assert = require("node:assert/strict");
const { applyBarcodeLookup } = require("../../src/MedSupplyOps.Web/wwwroot/js/receiving-barcode.js");

function originBadge() {
  return {
    visible: false,
    classList: {
      remove(name) {
        if (name === "d-none") this.owner.visible = true;
      },
    },
  };
}

test("掃描結果只帶入品項與 GS1 欄位，不清空已填的數量與儲位", () => {
  const lotOrigin = originBadge();
  lotOrigin.classList.owner = lotOrigin;
  const expiryOrigin = originBadge();
  expiryOrigin.classList.owner = expiryOrigin;
  const controls = {
    item: { value: "7" },
    lot: { value: "MANUAL-LOT" },
    expiry: { value: "2030-01-01" },
    quantity: { value: "23" },
    storage: { value: "ROOM-A-3" },
    lotOrigin,
    expiryOrigin,
  };

  applyBarcodeLookup(
    { itemId: 42, itemLabel: "MD-0042", lotNumber: "GS1-LOT", expiryDate: "2049-12-31" },
    controls,
  );

  assert.equal(controls.item.value, "42");
  assert.equal(controls.lot.value, "GS1-LOT");
  assert.equal(controls.expiry.value, "2049-12-31");
  assert.equal(controls.quantity.value, "23");
  assert.equal(controls.storage.value, "ROOM-A-3");
  assert.equal(lotOrigin.visible, true);
  assert.equal(expiryOrigin.visible, true);
});

test("一般條碼沒有 GS1 欄位時保留既有批號與效期", () => {
  const lotOrigin = originBadge();
  lotOrigin.classList.owner = lotOrigin;
  const expiryOrigin = originBadge();
  expiryOrigin.classList.owner = expiryOrigin;
  const controls = {
    item: { value: "7" },
    lot: { value: "MANUAL-LOT" },
    expiry: { value: "2030-01-01" },
    lotOrigin,
    expiryOrigin,
  };

  applyBarcodeLookup({ itemId: 42, itemLabel: "MD-0042", lotNumber: null, expiryDate: null }, controls);

  assert.equal(controls.item.value, "42");
  assert.equal(controls.lot.value, "MANUAL-LOT");
  assert.equal(controls.expiry.value, "2030-01-01");
});

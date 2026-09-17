(() => {
  "use strict";

  function applyBarcodeLookup(lookup, controls) {
    controls.item.value = lookup.itemId.toString();
    if (lookup.lotNumber) {
      controls.lot.value = lookup.lotNumber;
      controls.lotOrigin.classList.remove("d-none");
    }
    if (lookup.expiryDate) {
      controls.expiry.value = lookup.expiryDate;
      controls.expiryOrigin.classList.remove("d-none");
    }
  }

  if (typeof module !== "undefined" && module.exports) {
    module.exports = { applyBarcodeLookup };
  }

  if (typeof document === "undefined") {
    return;
  }

  const scan = document.getElementById("barcode-scan");
  const result = document.getElementById("barcode-scan-result");
  const controls = {
    item: document.getElementById("ItemId"),
    lot: document.getElementById("LotNumber"),
    expiry: document.getElementById("ExpiryDate"),
    lotOrigin: document.getElementById("lot-number-barcode-origin"),
    expiryOrigin: document.getElementById("expiry-date-barcode-origin"),
  };

  if (!scan || !result || Object.values(controls).some((control) => !control)) {
    return;
  }

  function hideOriginWhenOverwritten(input, origin) {
    input.addEventListener("input", () => origin.classList.add("d-none"));
  }

  hideOriginWhenOverwritten(controls.lot, controls.lotOrigin);
  hideOriginWhenOverwritten(controls.expiry, controls.expiryOrigin);

  scan.addEventListener("keydown", async (event) => {
    if (event.key !== "Enter") {
      return;
    }

    event.preventDefault();
    const scannedValue = scan.value.trim();
    result.classList.remove("text-danger", "text-success");
    result.textContent = scan.dataset.textLookingUp;

    try {
      const url = `${scan.dataset.lookupUrl}?value=${encodeURIComponent(scannedValue)}`;
      const response = await fetch(url, { headers: { Accept: "application/json" } });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || scan.dataset.textQueryFailed);
      }

      applyBarcodeLookup(payload, controls);
      result.classList.add("text-success");
      result.textContent = scan.dataset.textFound.replace("{0}", payload.itemLabel);
    } catch (error) {
      result.classList.add("text-danger");
      result.textContent = error.message || scan.dataset.textQueryFailed;
    }
  });
})();

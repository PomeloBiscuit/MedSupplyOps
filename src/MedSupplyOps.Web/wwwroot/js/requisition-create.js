(() => {
  "use strict";

  const form = document.querySelector("#requisition-create-form");
  const lines = document.querySelector("#requisition-lines");
  const template = document.querySelector("#requisition-line-template");
  const addButton = document.querySelector("#add-requisition-line");
  const validationMessage = document.querySelector("#client-validation-message");

  if (!form || !lines || !template || !addButton || !validationMessage) {
    return;
  }

  const asOf = form.querySelector("input[name='AsOf']").value;
  const apiTemplate = form.dataset.apiTemplate;

  function showClientError(message) {
    validationMessage.textContent = message;
    validationMessage.classList.remove("d-none");
  }

  function clearClientError() {
    validationMessage.textContent = "";
    validationMessage.classList.add("d-none");
  }

  function reindexLines() {
    lines.querySelectorAll(".requisition-line").forEach((row, index) => {
      const item = row.querySelector(".item-select");
      const quantity = row.querySelector(".quantity-input");
      item.name = `Lines[${index}].ItemId`;
      item.id = `Lines_${index}__ItemId`;
      quantity.name = `Lines[${index}].Quantity`;
      quantity.id = `Lines_${index}__Quantity`;
    });
  }

  async function updateAvailability(select) {
    const output = select.closest(".requisition-line").querySelector(".availability-output");
    if (!select.value || select.value === "0") {
      output.textContent = "尚未選擇品項。";
      return;
    }

    output.textContent = "查詢中…";
    const url = `${apiTemplate.replace("{itemId}", encodeURIComponent(select.value))}?asOf=${encodeURIComponent(asOf)}`;
    try {
      const response = await fetch(url, { headers: { Accept: "application/json" } });
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const availability = await response.json();
      const expiry = availability.earliestUsableExpiry ?? "無可用批次";
      output.textContent = `可用量 ${availability.availableQuantity}；最早效期 ${expiry}`;
    } catch {
      output.textContent = "可用量查詢失敗，請稍後重試。";
    }
  }

  addButton.addEventListener("click", () => {
    const index = lines.querySelectorAll(".requisition-line").length;
    const html = template.innerHTML.replaceAll("__index__", index.toString());
    lines.insertAdjacentHTML("beforeend", html);
    clearClientError();
  });

  lines.addEventListener("click", (event) => {
    const removeButton = event.target.closest(".remove-requisition-line");
    if (!removeButton) {
      return;
    }

    removeButton.closest(".requisition-line").remove();
    reindexLines();
  });

  lines.addEventListener("change", (event) => {
    if (event.target.matches(".item-select")) {
      updateAvailability(event.target);
    }
  });

  form.addEventListener("submit", (event) => {
    clearClientError();
    const rows = [...lines.querySelectorAll(".requisition-line")];
    if (rows.length === 0) {
      event.preventDefault();
      showClientError("請領單至少需要一筆明細。");
      return;
    }

    const selectedItemIds = rows
      .map((row) => row.querySelector(".item-select").value)
      .filter((itemId) => itemId && itemId !== "0");
    if (new Set(selectedItemIds).size !== selectedItemIds.length) {
      event.preventDefault();
      showClientError("同一張請領單不可重複加入相同品項。");
    }
  });

  lines.querySelectorAll(".item-select").forEach((select) => {
    if (select.value && select.value !== "0") {
      updateAvailability(select);
    }
  });
})();

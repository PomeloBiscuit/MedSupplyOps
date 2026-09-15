(() => {
  "use strict";

  function createLatestRequestCoordinator() {
    let requestSequence = 0;
    const activeRequests = new WeakMap();

    return {
      begin(target) {
        activeRequests.get(target)?.controller.abort();
        const request = { sequence: ++requestSequence, controller: new AbortController() };
        activeRequests.set(target, request);
        return request;
      },
      cancel(target) {
        activeRequests.get(target)?.controller.abort();
        activeRequests.delete(target);
      },
      isCurrent(target, request) {
        return activeRequests.get(target) === request && !request.controller.signal.aborted;
      },
    };
  }

  function format(templateText, ...values) {
    return values.reduce((text, value, index) => text.replaceAll(`{${index}}`, value), templateText);
  }

  function createAvailabilityPresentation(available, quantity, expiry, texts) {
    if (Number.isNaN(available)) {
      return { kind: "unselected", text: texts.unselected, isInsufficient: false };
    }

    if (!Number.isNaN(quantity) && quantity > available) {
      return {
        kind: "insufficient",
        text: format(texts.insufficient, quantity, available),
        isInsufficient: true,
      };
    }

    return {
      kind: "available",
      text: format(texts.available, available, expiry || texts.noLot),
      isInsufficient: false,
    };
  }

  if (typeof module !== "undefined" && module.exports) {
    module.exports = { createLatestRequestCoordinator, createAvailabilityPresentation };
  }

  if (typeof document === "undefined") {
    return;
  }

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
  const latestRequest = createLatestRequestCoordinator();

  function renderAvailability(row) {
    const output = row.querySelector(".availability-output");
    const available = Number.parseInt(row.dataset.availableQuantity ?? "", 10);
    const quantity = Number.parseInt(row.querySelector(".quantity-input").value, 10);
    const presentation = createAvailabilityPresentation(
      available,
      quantity,
      row.dataset.earliestExpiry,
      {
        unselected: form.dataset.textUnselected,
        insufficient: form.dataset.textInsufficient,
        available: form.dataset.textAvailable,
        noLot: form.dataset.textNoLot,
      });

    output.classList.remove("text-muted", "text-success", "text-danger", "fw-semibold");
    output.textContent = presentation.text;
    if (presentation.kind === "unselected") {
      output.classList.add("text-muted");
      delete output.dataset.insufficient;
      return;
    }

    if (presentation.isInsufficient) {
      output.classList.add("text-danger", "fw-semibold");
      output.dataset.insufficient = "true";
      return;
    }

    output.classList.add("text-success");
    delete output.dataset.insufficient;
  }

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
    const row = select.closest(".requisition-line");
    const output = row.querySelector(".availability-output");
    if (!select.value || select.value === "0") {
      latestRequest.cancel(select);
      delete row.dataset.availableQuantity;
      delete row.dataset.earliestExpiry;
      renderAvailability(row);
      return;
    }

    const requestedItemId = select.value;
    const request = latestRequest.begin(select);
    delete row.dataset.availableQuantity;
    delete row.dataset.earliestExpiry;
    output.classList.remove("text-success", "text-danger", "fw-semibold");
    output.classList.add("text-muted");
    output.textContent = form.dataset.textLoading;
    const url = `${apiTemplate.replace("{itemId}", encodeURIComponent(requestedItemId))}?asOf=${encodeURIComponent(asOf)}`;
    try {
      const response = await fetch(url, {
        headers: { Accept: "application/json" },
        signal: request.controller.signal,
      });
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const availability = await response.json();
      if (!latestRequest.isCurrent(select, request) || select.value !== requestedItemId) {
        return;
      }

      row.dataset.availableQuantity = availability.availableQuantity.toString();
      row.dataset.earliestExpiry = availability.earliestUsableExpiry ?? "";
      renderAvailability(row);
    } catch (error) {
      if (error.name === "AbortError" || !latestRequest.isCurrent(select, request)) {
        return;
      }

      delete row.dataset.availableQuantity;
      delete row.dataset.earliestExpiry;
      output.classList.remove("text-success", "text-danger", "fw-semibold");
      output.classList.add("text-muted");
      output.textContent = form.dataset.textQueryFailed;
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

  lines.addEventListener("input", (event) => {
    if (event.target.matches(".quantity-input")) {
      renderAvailability(event.target.closest(".requisition-line"));
    }
  });

  form.addEventListener("submit", (event) => {
    clearClientError();
    const rows = [...lines.querySelectorAll(".requisition-line")];
    if (rows.length === 0) {
      event.preventDefault();
      showClientError(form.dataset.textNoLines);
      return;
    }

    const selectedItemIds = rows
      .map((row) => row.querySelector(".item-select").value)
      .filter((itemId) => itemId && itemId !== "0");
    if (new Set(selectedItemIds).size !== selectedItemIds.length) {
      event.preventDefault();
      showClientError(form.dataset.textDuplicate);
    }
  });

  lines.querySelectorAll(".item-select").forEach((select) => {
    if (select.value && select.value !== "0") {
      updateAvailability(select);
    }
  });
})();

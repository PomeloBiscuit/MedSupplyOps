document.addEventListener("DOMContentLoaded", () => {
  const modalElement = document.querySelector("[data-mso-requisition-modal]");
  const dialog = modalElement?.querySelector("[data-mso-requisition-dialog]");
  if (!(modalElement instanceof HTMLElement) || !(dialog instanceof HTMLElement)) return;

  const modal = bootstrap.Modal.getOrCreateInstance(modalElement);
  let lastTrigger = null;
  let activeRequest = null;

  const message = (name) => modalElement.dataset[name] || "";

  function renderState(text, role, alertClass) {
    const content = document.createElement("div");
    content.className = "modal-content";

    const header = document.createElement("div");
    header.className = "modal-header";
    const title = document.createElement("h2");
    title.className = "modal-title fs-5";
    title.id = "requisition-details-modal-title";
    title.textContent = text;
    const dismiss = document.createElement("button");
    dismiss.type = "button";
    dismiss.className = "btn-close";
    dismiss.dataset.bsDismiss = "modal";
    dismiss.setAttribute("aria-label", message("closeLabel"));
    header.append(title, dismiss);

    const body = document.createElement("div");
    body.className = "modal-body";
    const status = document.createElement("div");
    status.className = alertClass;
    status.setAttribute("role", role);
    status.textContent = text;
    body.append(status);
    content.append(header, body);

    if (role === "alert") {
      const footer = document.createElement("div");
      footer.className = "modal-footer";
      const close = document.createElement("button");
      close.type = "button";
      close.className = "btn btn-secondary";
      close.dataset.bsDismiss = "modal";
      close.textContent = message("closeLabel");
      footer.append(close);
      content.append(footer);
    }

    dialog.replaceChildren(content);
  }

  function failureMessage(response) {
    if (response.status === 401 || (response.redirected && response.url.includes("/Account/Login"))) {
      return message("unauthorizedMessage");
    }
    if (response.status === 403 || (response.redirected && response.url.includes("/Account/AccessDenied"))) {
      return message("forbiddenMessage");
    }
    if (response.status === 404) return message("notFoundMessage");
    return message("serverErrorMessage");
  }

  document.querySelectorAll("[data-mso-requisition-details]").forEach((link) => {
    link.addEventListener("click", async (event) => {
      if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey ||
          event.shiftKey || event.altKey || link.target || link.hasAttribute("download")) {
        return;
      }

      const panelUrl = link.dataset.panelUrl;
      if (!panelUrl) return;

      event.preventDefault();
      lastTrigger = link;
      activeRequest?.abort();
      activeRequest = new AbortController();
      renderState(message("loadingMessage"), "status", "text-center text-muted py-4");
      modal.show(link);

      const url = new URL(panelUrl, window.location.href);
      url.searchParams.set("returnUrl", `${window.location.pathname}${window.location.search}`);

      try {
        const response = await fetch(url, {
          credentials: "same-origin",
          headers: { "X-Requested-With": "XMLHttpRequest" },
          signal: activeRequest.signal,
        });
        if (!response.ok || response.redirected) {
          renderState(failureMessage(response), "alert", "alert alert-danger mb-0");
          return;
        }

        dialog.innerHTML = await response.text();
      } catch (error) {
        if (error.name !== "AbortError") {
          renderState(message("networkErrorMessage"), "alert", "alert alert-danger mb-0");
        }
      }
    });
  });

  modalElement.addEventListener("hidden.bs.modal", () => {
    activeRequest?.abort();
    activeRequest = null;
    lastTrigger?.focus();
    lastTrigger = null;
  });
});

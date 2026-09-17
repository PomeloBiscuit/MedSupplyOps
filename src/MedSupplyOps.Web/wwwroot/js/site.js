// 導覽偏好只存版面資訊；Secure 依目前協定設定，避免 HTTP 開發站無法寫入 Cookie。
function setMsoPreference(name, value) {
  const secure = window.location.protocol === "https:" ? "; Secure" : "";
  document.cookie = `${name}=${encodeURIComponent(value)}; Path=/; Max-Age=31536000; SameSite=Lax${secure}`;
}

// 左側欄依序切換三段，重新載入後由 Razor 直接輸出目標版面而不發生前端換版閃爍。
function cycleMsoSidebar() {
  const current = document.documentElement.dataset.msoSidebarState || "expanded";
  const next = current === "expanded" ? "compact" : current === "compact" ? "hidden" : "expanded";
  setMsoPreference("mso-sidebar-state", next);
  window.location.reload();
}

document.addEventListener("DOMContentLoaded", () => {
  document.querySelectorAll("[data-bs-toggle='tooltip']").forEach((element) => new bootstrap.Tooltip(element));
  document.querySelectorAll("[data-mso-sidebar-toggle]").forEach((button) => button.addEventListener("click", cycleMsoSidebar));
  initializeSidebarResizer();
  document.querySelectorAll("[data-mso-preference]").forEach((input) => input.addEventListener("change", () => {
    if (!input.checked) return;
    setMsoPreference(input.dataset.msoPreference, input.value);
    window.location.reload();
  }));
  document.querySelectorAll("[data-mso-contrast]").forEach((input) => input.addEventListener("change", () => {
    setMsoPreference("mso-theme", input.checked ? "contrast" : "light");
    window.location.reload();
  }));
  document.querySelectorAll("[data-mso-demo-close]").forEach((button) => button.addEventListener("click", () => {
    button.closest("[data-mso-demo-card]")?.setAttribute("hidden", "");
  }));
  document.querySelectorAll("[data-mso-fill-email]").forEach((button) => button.addEventListener("click", () => {
    const email = document.getElementById("Email");
    if (!(email instanceof HTMLInputElement)) return;
    email.value = button.value;
    email.dispatchEvent(new Event("input", { bubbles: true }));
    email.dispatchEvent(new Event("change", { bubbles: true }));
    email.focus();
  }));

  const accountModal = document.getElementById("mso-account-modal");
  const accountDialog = accountModal?.querySelector("[data-mso-account-modal-dialog]");
  accountModal?.addEventListener("show.bs.modal", async (event) => {
    const trigger = event.relatedTarget;
    const url = trigger instanceof HTMLElement ? trigger.dataset.msoAccountUrl : null;
    if (!url || !(accountDialog instanceof HTMLElement)) return;

    accountDialog.innerHTML = '<div class="modal-content"><div class="modal-body text-center text-muted py-5" role="status">載入中…</div></div>';
    const response = await fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" } });
    if (!response.ok) {
      accountDialog.innerHTML = '<div class="modal-content"><div class="modal-body"><div class="alert alert-danger mb-0">無法載入帳號設定，請重新整理後再試。</div></div></div>';
      return;
    }

    accountDialog.innerHTML = await response.text();
    initializeAccountDialog(accountDialog);
  });

  accountDialog?.addEventListener("submit", async (event) => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement) || !form.matches("[data-mso-account-form]")) return;
    event.preventDefault();

    const submit = form.querySelector("button[type='submit']");
    if (submit instanceof HTMLButtonElement) submit.disabled = true;
    const response = await fetch(form.action, {
      method: "POST",
      body: new FormData(form),
      headers: { "X-Requested-With": "XMLHttpRequest" },
    });

    const contentType = response.headers.get("content-type") || "";
    if (response.ok && contentType.includes("application/json")) {
      const result = await response.json();
      accountDialog.innerHTML = '<div class="modal-content"><div class="modal-header"><h2 class="modal-title fs-5">變更密碼</h2><button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="關閉"></button></div><div class="modal-body"><div class="alert alert-success mb-0" data-mso-account-success></div></div><div class="modal-footer"><button type="button" class="btn btn-primary" data-bs-dismiss="modal">完成</button></div></div>';
      accountDialog.querySelector("[data-mso-account-success]").textContent = result.message;
      return;
    }

    accountDialog.innerHTML = await response.text();
    initializeAccountDialog(accountDialog);
  });
});

function initializeSidebarResizer() {
  const handle = document.querySelector("[data-mso-sidebar-resizer]");
  const sidebar = handle?.closest(".mso-sidebar-expanded");
  if (!(handle instanceof HTMLElement) || !(sidebar instanceof HTMLElement)) return;

  const minimum = Number(handle.getAttribute("aria-valuemin"));
  const maximum = Number(handle.getAttribute("aria-valuemax"));
  const defaultWidth = Number(handle.dataset.defaultWidth);
  const isWideViewport = () => window.matchMedia("(min-width: 992px)").matches;
  const clamp = (width) => Math.min(maximum, Math.max(minimum, Math.round(width)));
  const currentWidth = () => clamp(Number(handle.getAttribute("aria-valuenow")) || defaultWidth);
  const applyWidth = (width) => {
    const next = clamp(width);
    sidebar.style.setProperty("--mso-sidebar-width", `${next}px`);
    sidebar.dataset.sidebarWidth = String(next);
    handle.setAttribute("aria-valuenow", String(next));
    return next;
  };
  const saveWidth = (width) => setMsoPreference("mso-sidebar-width", String(width));

  let pointerId = null;
  let pointerStartX = 0;
  let widthAtPointerStart = currentWidth();

  handle.addEventListener("pointerdown", (event) => {
    if (!isWideViewport() || event.button !== 0) return;
    event.preventDefault();
    pointerId = event.pointerId;
    pointerStartX = event.clientX;
    widthAtPointerStart = currentWidth();
    handle.setPointerCapture(pointerId);
    handle.classList.add("is-dragging");
    document.body.classList.add("mso-sidebar-resizing");
  });

  handle.addEventListener("pointermove", (event) => {
    if (event.pointerId !== pointerId) return;
    applyWidth(widthAtPointerStart + event.clientX - pointerStartX);
  });

  handle.addEventListener("pointerup", (event) => {
    if (event.pointerId !== pointerId) return;
    const width = applyWidth(widthAtPointerStart + event.clientX - pointerStartX);
    handle.releasePointerCapture(pointerId);
    pointerId = null;
    handle.classList.remove("is-dragging");
    document.body.classList.remove("mso-sidebar-resizing");
    saveWidth(width);
  });

  handle.addEventListener("pointercancel", (event) => {
    if (event.pointerId !== pointerId) return;
    applyWidth(widthAtPointerStart);
    pointerId = null;
    handle.classList.remove("is-dragging");
    document.body.classList.remove("mso-sidebar-resizing");
  });

  handle.addEventListener("keydown", (event) => {
    if (!isWideViewport() || (event.key !== "ArrowLeft" && event.key !== "ArrowRight")) return;
    event.preventDefault();
    const delta = event.key === "ArrowLeft" ? -16 : 16;
    saveWidth(applyWidth(currentWidth() + delta));
  });

  handle.addEventListener("dblclick", (event) => {
    if (!isWideViewport()) return;
    event.preventDefault();
    saveWidth(applyWidth(defaultWidth));
  });
}

function initializeAccountDialog(root) {
  root.querySelectorAll("[data-mso-password-toggle]").forEach((button) => button.addEventListener("click", () => {
    const input = root.querySelector(`#${CSS.escape(button.dataset.msoPasswordToggle)}`);
    if (!(input instanceof HTMLInputElement)) return;
    input.type = input.type === "password" ? "text" : "password";
    button.textContent = input.type === "password" ? "顯示" : "隱藏";
  }));

  const password = root.querySelector("[data-mso-password-input]");
  if (!(password instanceof HTMLInputElement)) return;
  const evaluate = () => {
    const value = password.value;
    const checks = {
      length: value.length >= Number(password.dataset.requiredLength),
      unique: new Set(value).size >= Number(password.dataset.requiredUnique),
      digit: /[0-9]/.test(value),
      lowercase: /[a-z]/.test(value),
      uppercase: /[A-Z]/.test(value),
      symbol: /[^a-zA-Z0-9]/.test(value),
    };
    root.querySelectorAll("[data-mso-password-rule]").forEach((rule) => {
      const passed = checks[rule.dataset.msoPasswordRule];
      rule.classList.toggle("is-valid", Boolean(passed));
      const marker = rule.querySelector("span");
      if (marker) marker.textContent = passed ? "✓" : "·";
    });
  };
  password.addEventListener("input", evaluate);
  evaluate();
}

// [ 是側欄三段循環快捷鍵；在輸入、選取與可編輯區域輸入文字時不得攔截。
document.addEventListener("keydown", (event) => {
  const target = event.target;
  const isTyping = target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement ||
    target instanceof HTMLSelectElement || target?.isContentEditable;
  if (event.key === "[" && !event.ctrlKey && !event.altKey && !event.metaKey && !isTyping &&
      document.documentElement.dataset.msoNavigation === "sidebar") {
    event.preventDefault();
    cycleMsoSidebar();
  }
});

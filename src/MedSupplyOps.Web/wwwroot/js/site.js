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
  document.querySelectorAll("[data-mso-navigation-toggle]").forEach((button) => button.addEventListener("click", () => {
    setMsoPreference("mso-navigation-layout", "top");
    window.location.reload();
  }));
});

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

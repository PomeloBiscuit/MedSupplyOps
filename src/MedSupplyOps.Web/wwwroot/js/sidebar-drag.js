// 側欄右緣的拖曳手勢：展開時往左拖 → 收合成圖示列；圖示列時往右拖 → 展開；
// offcanvas 往左拖 → 關閉。側欄寬度本身固定，這裡不做連續調寬。
//
// ★ 第一版把參考介面上的「Drag to resize」照字面做成連續調寬（還存寬度 cookie），
//   但使用者要的是「用拖的來收合／展開」。判斷邏輯獨立成純函式，才能在 Node 裡測。
(() => {
  // 拖過這個距離（px）才算數；太短的移動多半是誤觸或單純點擊。
  const DRAG_THRESHOLD = 40;

  // target：這條把手要切換到的狀態（"compact"、"expanded" 或 "close"）。
  // 回傳「往正確方向拖了多遠」：展開要往右（正值），收合與關閉要往左（取負號後為正值）。
  function dragProgress(target, startX, currentX) {
    const direction = target === "expanded" ? 1 : -1;
    return (currentX - startX) * direction;
  }

  function shouldCommitSidebarDrag(target, startX, endX, threshold = DRAG_THRESHOLD) {
    if (target !== "compact" && target !== "expanded" && target !== "close") return false;
    if (!Number.isFinite(startX) || !Number.isFinite(endX)) return false;
    return dragProgress(target, startX, endX) >= threshold;
  }

  if (typeof module !== "undefined" && module.exports) {
    module.exports = { shouldCommitSidebarDrag, dragProgress, DRAG_THRESHOLD };
  }

  if (typeof document === "undefined") {
    return;
  }

  function commit(handle, target) {
    if (target === "close") {
      const offcanvas = handle.closest(".offcanvas");
      if (offcanvas && window.bootstrap) {
        window.bootstrap.Offcanvas.getOrCreateInstance(offcanvas).hide();
      }
      return;
    }

    // 收合與展開是伺服器端依 cookie 渲染的兩種不同標記（圖示列不渲染文字、改掛 tooltip），
    // 所以跟既有的切換鈕一樣：寫 cookie 後重新載入。
    setMsoPreference("mso-sidebar-state", target);
    window.location.reload();
  }

  function initialize(handle) {
    const target = handle.dataset.msoSidebarTarget;
    let pointerId = null;
    let startX = 0;

    const finish = (event, cancelled) => {
      if (event.pointerId !== pointerId) return;
      try { handle.releasePointerCapture(pointerId); } catch { /* 指標可能已被瀏覽器釋放 */ }
      pointerId = null;
      handle.classList.remove("is-dragging", "is-armed");
      document.body.classList.remove("mso-sidebar-dragging");
      if (!cancelled && shouldCommitSidebarDrag(target, startX, event.clientX)) {
        commit(handle, target);
      }
    };

    handle.addEventListener("pointerdown", (event) => {
      if (event.button !== 0) return;
      event.preventDefault();
      pointerId = event.pointerId;
      startX = event.clientX;
      try { handle.setPointerCapture(pointerId); } catch { /* 合成事件沒有真的指標 */ }
      handle.classList.add("is-dragging");
      document.body.classList.add("mso-sidebar-dragging");
    });

    handle.addEventListener("pointermove", (event) => {
      if (event.pointerId !== pointerId) return;
      handle.classList.toggle("is-armed", shouldCommitSidebarDrag(target, startX, event.clientX));
    });

    handle.addEventListener("pointerup", (event) => finish(event, false));
    handle.addEventListener("pointercancel", (event) => finish(event, true));

    // 鍵盤：往把手指向的方向按方向鍵，或按 Enter，都等同拖過門檻。
    handle.addEventListener("keydown", (event) => {
      const directionKey = target === "expanded" ? "ArrowRight" : "ArrowLeft";
      if (event.key !== directionKey && event.key !== "Enter") return;
      event.preventDefault();
      commit(handle, target);
    });
  }

  document.addEventListener("DOMContentLoaded", () => {
    document.querySelectorAll("[data-mso-sidebar-drag]").forEach(initialize);
  });
})();

(() => {
    "use strict";

    const itemSelect = document.getElementById("availability-item");
    const asOfInput = document.getElementById("inventory-as-of");
    const result = document.getElementById("availability-result");

    if (!itemSelect || !asOfInput || !result) {
        return;
    }

    // 每一個選擇都遞增。只有最後一個請求可寫入畫面，避免舊回應覆蓋新選項。
    let latestAvailabilityRequest = 0;

    itemSelect.addEventListener("change", async () => {
        const itemId = itemSelect.value;
        const requestNumber = ++latestAvailabilityRequest;

        if (!itemId) {
            result.textContent = "尚未選擇品項。";
            return;
        }

        result.textContent = "查詢中…";
        const endpoint = itemSelect.dataset.apiTemplate.replace("{itemId}", encodeURIComponent(itemId));
        const url = `${endpoint}?asOf=${encodeURIComponent(asOfInput.value)}`;

        try {
            const response = await fetch(url, { headers: { Accept: "application/json" } });
            const availability = await response.json();

            if (requestNumber !== latestAvailabilityRequest) {
                return;
            }

            if (!response.ok) {
                throw new Error("無法取得可用量。");
            }

            const earliestExpiry = availability.earliestUsableExpiry ?? "無可用批次";
            result.textContent = `可用量：${availability.availableQuantity}；最早可用效期：${earliestExpiry}`;
        } catch (error) {
            if (requestNumber === latestAvailabilityRequest) {
                result.textContent = "查詢失敗，請稍後再試。";
            }
        }
    });
})();

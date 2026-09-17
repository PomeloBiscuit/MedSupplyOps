"use strict";

document.addEventListener("click", function (event) {
  const button = event.target.closest("[data-mso-print-barcode]");
  if (!button) {
    return;
  }

  window.print();
});

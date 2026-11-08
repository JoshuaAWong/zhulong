"use strict";
// 页面内托盘演示：主题沿用根节点，导航复用已有按钮，不接入系统托盘。
const trayPreview = document.querySelector("#trayPreview");
trayPreview.addEventListener("click", event => {
  const button = event.target.closest("[data-tray-page]");
  if (!button) return;
  const navigation = document.querySelector('.tab[data-page="' + button.dataset.trayPage + '"]');
  trayPreview.hidePopover();
  navigation.click();
  navigation.focus({ preventScroll:true });
  document.querySelector("#prototype").scrollIntoView({ block:"start", behavior:"instant" });
});
document.addEventListener("visibilitychange", () => {
  if (document.hidden && trayPreview.matches(":popover-open")) trayPreview.hidePopover();
});
window.addEventListener("pagehide", () => {
  if (trayPreview.matches(":popover-open")) trayPreview.hidePopover();
});

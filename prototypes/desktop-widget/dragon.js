"use strict";

const desk = document.querySelector(".dragon-desk");
const dock = desk.querySelector(".dragon-dock");
const handle = dock.querySelector(".dragon-handle");
const panel = dock.querySelector(".dragon-panel");
const pinButton = panel.querySelector(".pin-flyout");
const themeButtons = document.querySelectorAll("[data-theme-option]");
const edgeButtons = document.querySelectorAll("[data-edge-option]");
const viewButtons = panel.querySelectorAll("[data-view]");
const views = panel.querySelectorAll("[data-panel]");
const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
let timer;
let pinned = false;
let drag;
let suppressClick = false;

function showPanel(visible) {
  panel.hidden = !visible;
  handle.setAttribute("aria-expanded", String(visible));
}

function setPinned(value) {
  pinned = value;
  pinButton.setAttribute("aria-pressed", String(value));
  pinButton.textContent = value ? "取消固定" : "固定";
}

function setEdge(edge) {
  desk.dataset.edge = edge;
  for (const button of edgeButtons) button.setAttribute("aria-pressed", String(button.dataset.edgeOption === edge));
}

function dismiss() {
  clearTimeout(timer);
  setPinned(false);
  showPanel(false);
}

function closeAndFocus() {
  dismiss();
  handle.focus({ preventScroll: true });
}

for (const button of themeButtons) {
  button.addEventListener("click", () => {
    document.documentElement.dataset.theme = button.dataset.themeOption;
    for (const option of themeButtons) option.setAttribute("aria-pressed", String(option === button));
  });
}
for (const button of edgeButtons) button.addEventListener("click", () => setEdge(button.dataset.edgeOption));
for (const button of viewButtons) {
  button.addEventListener("click", () => {
    for (const option of viewButtons) option.setAttribute("aria-pressed", String(option === button));
    for (const view of views) view.hidden = view.dataset.panel !== button.dataset.view;
  });
}

dock.addEventListener("pointerenter", event => {
  if (event.pointerType !== "mouse" || drag?.moved) return;
  clearTimeout(timer);
  if (panel.hidden) timer = setTimeout(() => showPanel(true), reduceMotion.matches ? 0 : 65);
});
dock.addEventListener("pointerleave", event => {
  if (event.pointerType !== "mouse" || drag?.moved) return;
  clearTimeout(timer);
  if (!pinned) timer = setTimeout(() => {
    const focus = document.activeElement;
    if (!(panel.contains(focus) && focus.matches(":focus-visible"))) showPanel(false);
  }, 165);
});
dock.addEventListener("focusout", event => {
  if (dock.contains(event.relatedTarget) || pinned || dock.matches(":hover")) return;
  clearTimeout(timer);
  showPanel(false);
});
dock.addEventListener("keydown", event => {
  if (event.key !== "Escape" || panel.hidden) return;
  event.preventDefault();
  closeAndFocus();
});
panel.querySelector(".close-flyout").addEventListener("click", closeAndFocus);
pinButton.addEventListener("click", () => setPinned(!pinned));
handle.addEventListener("click", event => {
  if (event.detail && suppressClick) return;
  if (pinned) dismiss();
  else {
    clearTimeout(timer);
    setPinned(true);
    showPanel(true);
  }
});

handle.addEventListener("pointerdown", event => {
  if (!event.isPrimary || event.button !== 0) return;
  suppressClick = false;
  const bounds = desk.getBoundingClientRect();
  const rect = handle.getBoundingClientRect();
  drag = { id: event.pointerId, x: event.clientX, offset: event.clientX - rect.left, bounds, width: rect.width, moved: false };
  document.addEventListener("pointermove", moveDrag);
  document.addEventListener("pointerup", endDrag);
  document.addEventListener("pointercancel", endDrag);
});

function moveDrag(event) {
  if (drag?.id !== event.pointerId) return;
  if (!drag.moved) {
    if (Math.abs(event.clientX - drag.x) < 7) return;
    drag.moved = true;
    dismiss();
    desk.dataset.dragging = "";
  }
  const x = event.clientX - drag.bounds.left - drag.offset;
  desk.style.setProperty("--drag-left", `${Math.min(Math.max(x, 3), drag.bounds.width - drag.width - 3)}px`);
}

function endDrag(event) {
  if (drag?.id !== event.pointerId) return;
  if (drag.moved) {
    suppressClick = true;
    if (event.type === "pointerup") setEdge(event.clientX - drag.bounds.left < drag.bounds.width / 2 ? "left" : "right");
    delete desk.dataset.dragging;
    desk.style.removeProperty("--drag-left");
  }
  drag = undefined;
  document.removeEventListener("pointermove", moveDrag);
  document.removeEventListener("pointerup", endDrag);
  document.removeEventListener("pointercancel", endDrag);
}

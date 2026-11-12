"use strict";

const root = document.documentElement;
const desks = Array.from(document.querySelectorAll(".desk"));
const template = document.querySelector("#flyout-template");
const themeOptions = document.querySelectorAll("[data-theme-option]");
const edgeOptions = document.querySelectorAll("[data-edge-option]");
const dockToggle = document.querySelector("#dock-toggle");

function setOpen(desk, open) {
  desk.dataset.open = String(open);
  desk.querySelector(".widget-handle").setAttribute("aria-expanded", String(open));
  desk.querySelector(".flyout").hidden = !open;
}

function syncDockControls() {
  for (const button of edgeOptions) {
    const count = desks.filter(desk => desk.dataset.edge === button.dataset.edgeOption).length;
    button.setAttribute("aria-pressed", count === desks.length ? "true" : count ? "mixed" : "false");
  }
  const docked = desks.filter(desk => desk.dataset.docked === "true").length;
  dockToggle.checked = docked === desks.length;
  dockToggle.indeterminate = docked > 0 && docked < desks.length;
}

for (const button of themeOptions) {
  button.addEventListener("click", () => {
    root.dataset.theme = button.dataset.themeOption;
    for (const option of themeOptions) option.setAttribute("aria-pressed", String(option === button));
  });
}

for (const button of edgeOptions) {
  button.addEventListener("click", () => {
    for (const desk of desks) desk.dataset.edge = button.dataset.edgeOption;
    syncDockControls();
  });
}

dockToggle.addEventListener("change", () => {
  for (const desk of desks) {
    desk.dataset.docked = String(dockToggle.checked);
    if (dockToggle.checked) setOpen(desk, false);
  }
  syncDockControls();
});

for (const desk of desks) {
  const handle = desk.querySelector(".widget-handle");
  const flyout = desk.querySelector(".flyout");
  flyout.append(template.content.cloneNode(true));
  const tabs = flyout.querySelectorAll("[data-view]");
  const panels = flyout.querySelectorAll("[data-panel]");
  let drag;
  let suppressClick = false;

  for (const tab of tabs) {
    tab.addEventListener("click", () => {
      for (const option of tabs) option.setAttribute("aria-pressed", String(option === tab));
      for (const panel of panels) panel.hidden = panel.dataset.panel !== tab.dataset.view;
    });
  }

  function closeAndFocus() {
    setOpen(desk, false);
    handle.focus({ preventScroll: true });
  }

  flyout.querySelector(".close-flyout").addEventListener("click", closeAndFocus);
  desk.addEventListener("keydown", event => {
    if (event.key !== "Escape" || desk.dataset.open !== "true") return;
    event.preventDefault();
    closeAndFocus();
  });

  handle.addEventListener("click", event => {
    if (event.detail && suppressClick) return;
    setOpen(desk, desk.dataset.open !== "true");
  });

  handle.addEventListener("pointerdown", event => {
    if (!event.isPrimary || event.button !== 0) return;
    suppressClick = false;
    const bounds = desk.getBoundingClientRect();
    const handleBounds = handle.getBoundingClientRect();
    drag = { id: event.pointerId, start: event.clientX, offset: event.clientX - handleBounds.left,
      bounds, width: handleBounds.width, moved: false };
    document.addEventListener("pointermove", moveDrag);
    document.addEventListener("pointerup", finishDrag);
    document.addEventListener("pointercancel", finishDrag);
  });

  function moveDrag(event) {
    if (drag?.id !== event.pointerId) return;
    if (!drag.moved && Math.abs(event.clientX - drag.start) < 8) return;
    drag.moved = true;
    const x = event.clientX - drag.bounds.left - drag.offset;
    const left = Math.min(Math.max(x, 0), drag.bounds.width - drag.width);
    desk.style.setProperty("--drag-left", `${left}px`);
    desk.dataset.dragging = "true";
  }

  function finishDrag(event) {
    if (drag?.id !== event.pointerId) return;
    if (event.type === "pointerup" && drag.moved) {
      desk.dataset.edge = event.clientX - drag.bounds.left < drag.bounds.width / 2 ? "left" : "right";
      desk.dataset.docked = "true";
      suppressClick = true;
      setOpen(desk, false);
      handle.blur();
      syncDockControls();
    }
    desk.removeAttribute("data-dragging");
    desk.style.removeProperty("--drag-left");
    drag = undefined;
    document.removeEventListener("pointermove", moveDrag);
    document.removeEventListener("pointerup", finishDrag);
    document.removeEventListener("pointercancel", finishDrag);
  }
}
syncDockControls();

"use strict";
// 太极颜色即昼夜入口；主题仍交给既有 themeToggle 同步各页与山水。
const mainDial = $(".instrument-dial");
mainDial.setAttribute("role", "group");
mainDial.setAttribute("aria-label", "提交水位 55%，上限余量 45%；太极可切换昼夜");
mainDial.setAttribute("title", "浅色切昼，深色切夜；悬停三倍速。当前提交水位 55%");
dialSvg.removeAttribute("aria-hidden");
dialSvg.setAttribute("role", "group");
dialSvg.setAttribute("aria-label", "太极昼夜切换");
const toneShapes = Array.from(dialSvg.querySelectorAll(".taiji-core > *"));
toneShapes.forEach((shape, index) => {
  const tone = shape.classList.contains("taiji-paper") ? "light" : "dark";
  shape.setAttribute("data-taiji-tone", tone);
  if (index < 2) {
    shape.setAttribute("role", "button");
    shape.setAttribute("tabindex", "0");
    shape.setAttribute("aria-label", tone === "light" ? "切换昼间" : "切换夜间");
  }
});
const taijiHint = document.createElement("span");
taijiHint.className = "taiji-hint";
taijiHint.textContent = "浅色为昼 · 深色为夜";
mainDial.append(taijiHint);
let themeReveal = null, applyingTone = false, baguaBurst = null;
function applyInkTone(tone) {
  if (root.dataset.theme === tone) return;
  applyingTone = true;
  $("#themeToggle").click();
  applyingTone = false;
}
function clearThemeReveal(commit = false) {
  if (!themeReveal) return;
  const previous = themeReveal;
  themeReveal = null;
  previous.layer.remove();
  previous.animation?.cancel();
  if (commit) applyInkTone(previous.tone);
}
function revealInkTheme(tone, x, y) {
  clearThemeReveal();
  if (root.dataset.theme === tone) return;
  if (!motionAllowed()) { applyInkTone(tone); return; }
  const radius = Math.hypot(Math.max(x, window.innerWidth - x), Math.max(y, window.innerHeight - y)) * 1.25;
  const layer = document.createElement("div"), wave = document.createElement("span");
  layer.className = "theme-reveal";
  layer.setAttribute("aria-hidden", "true");
  layer.dataset.tone = tone;
  wave.className = "theme-wave";
  Object.assign(wave.style, { width:2 * radius + "px", height:2 * radius + "px", left:x - radius + "px", top:y - radius + "px" });
  layer.append(wave);
  document.body.append(layer);
  const reveal = { tone, layer, animation:null };
  themeReveal = reveal;
  reveal.animation = wave.animate([{ transform:"scale(0)" }, { transform:"scale(1)" }], { duration:520, easing:"cubic-bezier(.2,.65,.3,1)", fill:"forwards" });
  const settle = () => { if (themeReveal === reveal) clearThemeReveal(true); };
  reveal.animation.oncancel = settle;
  reveal.animation.onfinish = () => {
    if (themeReveal !== reveal) return;
    reveal.animation.oncancel = null;
    reveal.animation.cancel();
    applyInkTone(tone);
    // 覆盖全视口后再显现目标配色，避免全页在扩散之前突然跳色。
    reveal.animation = layer.animate([{ opacity:1 }, { opacity:0 }], { duration:620, easing:"ease-out" });
    reveal.animation.onfinish = reveal.animation.oncancel = settle;
  };
}
function taijiCenter() {
  const rect = dialSvg.getBoundingClientRect();
  return { x:rect.left + rect.width / 2, y:rect.top + rect.height / 2 };
}
document.addEventListener("click", event => {
  const control = event.target.closest("[data-taiji-tone],[data-unified-tone],#themeToggle");
  if (!control || applyingTone) return;
  event.preventDefault();
  event.stopImmediatePropagation();
  const tone = control.dataset.taijiTone || control.dataset.unifiedTone || (root.dataset.theme === "light" ? "dark" : "light");
  const center = taijiCenter();
  const fromFish = control.hasAttribute("data-taiji-tone") && event.detail > 0;
  revealInkTheme(tone, fromFish ? event.clientX : center.x, fromFish ? event.clientY : center.y);
}, true);
dialSvg.addEventListener("keydown", event => {
  const control = event.target.closest("[data-taiji-tone][role='button']");
  if (!control || !["Enter", " "].includes(event.key)) return;
  event.preventDefault();
  const center = taijiCenter();
  revealInkTheme(control.dataset.taijiTone, center.x, center.y);
});
function clearBagua() {
  if (!baguaBurst) return;
  const previous = baguaBurst;
  baguaBurst = null;
  previous.field.remove();
  previous.animations.forEach(animation => animation.cancel());
}
function gatherBagua() {
  clearBagua();
  if (!motionAllowed() || !dialInView || $("#overview").hidden) return;
  const field = document.createElement("span");
  field.className = "bagua-field";
  field.setAttribute("aria-hidden", "true");
  mainDial.append(field);
  const burst = { field, animations:[] };
  baguaBurst = burst;
  const radius = Math.min(140, window.innerWidth * .14);
  // 对向符号同速同起点距离，不错开延迟，保持中心对称。
  Array.from("☰☱☲☳☷☶☵☴").forEach((symbol, index) => {
    const angle = (index * 45 - 90) * Math.PI / 180;
    const x = Math.cos(angle) * radius, y = Math.sin(angle) * radius;
    const glyph = document.createElement("span");
    glyph.className = "bagua-glyph";
    glyph.textContent = symbol;
    field.append(glyph);
    const animation = glyph.animate([
      { transform:"translate(" + x + "px," + y + "px) scale(1.3)", opacity:0 },
      { transform:"translate(" + x * .85 + "px," + y * .85 + "px) scale(1.05)", opacity:.64, offset:.18 },
      { transform:"translate(0,0) scale(.1)", opacity:0 }
    ], { duration:1150, easing:"cubic-bezier(.35,.1,.25,1)" });
    burst.animations.push(animation);
    animation.onfinish = animation.oncancel = () => {
      glyph.remove();
      if (baguaBurst === burst && !field.childElementCount) { field.remove(); baguaBurst = null; }
    };
  });
}
dialSvg.addEventListener("pointerenter", gatherBagua);
dialSvg.addEventListener("pointerleave", clearBagua);
document.addEventListener("taiji-motion-sync", () => {
  if (!motionAllowed()) clearThemeReveal(true);
  if (!motionAllowed() || !dialInView || $("#overview").hidden) clearBagua();
});
window.addEventListener("resize", () => clearThemeReveal(true));
window.addEventListener("pagehide", () => { clearThemeReveal(); clearBagua(); });

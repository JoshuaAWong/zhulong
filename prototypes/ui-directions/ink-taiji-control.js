"use strict";
// 与加速共用整块太极命中区域，切换时保留新旧界面的内容。
const mainDial = $(".instrument-dial");
mainDial.setAttribute("role", "group");
mainDial.setAttribute("aria-label", "提交水位 55%，上限余量 45%；太极可切换昼夜");
mainDial.setAttribute("title", "点击切换昼夜；悬停三倍速并持续聚卦。当前提交水位 55%");
[dialSvg, demoSvg].forEach((svg, index) => {
  svg.removeAttribute("aria-hidden");
  svg.setAttribute("data-taiji-toggle", "");
  svg.setAttribute("role", "button");
  svg.setAttribute("tabindex", "0");
  svg.setAttribute("aria-label", index ? "演示太极，点击切换昼夜" : "太极，点击切换昼夜");
  svg.setAttribute("id", index ? "taijiDemoToggle" : "taijiToggle");
});
const taijiHint = document.createElement("span");
taijiHint.className = "taiji-hint";
taijiHint.textContent = "点击换昼夜 · 悬停聚卦";
mainDial.append(taijiHint);
let themeReveal = null, applyingTone = false, baguaStream = null;
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
  previous.animation?.cancel();
  previous.transition?.skipTransition();
  root.style.removeProperty("--tide-x");
  root.style.removeProperty("--tide-y");
  if (commit) applyInkTone(previous.tone);
}
function revealInkTheme(tone, x, y) {
  clearThemeReveal();
  if (root.dataset.theme === tone) return;
  if (!motionAllowed() || !document.startViewTransition) { applyInkTone(tone); return; }
  const radius = Math.hypot(Math.max(x, window.innerWidth - x), Math.max(y, window.innerHeight - y));
  root.style.setProperty("--tide-x", x + "px");
  root.style.setProperty("--tide-y", y + "px");
  const reveal = { tone, transition:null, animation:null };
  themeReveal = reveal;
  reveal.transition = document.startViewTransition(() => {
    if (themeReveal === reveal) applyInkTone(tone);
  });
  const settle = () => { if (themeReveal === reveal) clearThemeReveal(true); };
  reveal.transition.ready.then(() => {
    if (themeReveal !== reveal) return;
    if (!motionAllowed()) { settle(); return; }
    // 新界面沿柔边波前铺开；旧界面完整保留在下面，没有纯色清屏阶段。
    reveal.animation = root.animate([
      { "--tide-radius":"0px" }, { "--tide-radius":radius + 160 + "px" }
    ], { duration:1100, easing:"cubic-bezier(.2,.65,.3,1)", fill:"forwards", pseudoElement:"::view-transition-new(root)" });
    reveal.animation.onfinish = reveal.animation.oncancel = settle;
  }).catch(settle);
  reveal.transition.finished.then(settle, settle);
}
function controlCenter(control) {
  const rect = control.getBoundingClientRect();
  return { x:rect.left + rect.width / 2, y:rect.top + rect.height / 2 };
}
function nextInkTone() {
  return (themeReveal?.tone || root.dataset.theme) === "light" ? "dark" : "light";
}
document.addEventListener("click", event => {
  const control = event.target.closest("[data-taiji-toggle],[data-unified-tone],#themeToggle");
  if (!control || applyingTone) return;
  event.preventDefault();
  event.stopImmediatePropagation();
  const tone = control.dataset.unifiedTone || nextInkTone();
  const center = controlCenter(control);
  revealInkTheme(tone, event.detail > 0 ? event.clientX : center.x, event.detail > 0 ? event.clientY : center.y);
}, true);
[dialSvg, demoSvg].forEach(svg => svg.addEventListener("keydown", event => {
  if (!["Enter", " "].includes(event.key)) return;
  event.preventDefault();
  const center = controlCenter(svg);
  revealInkTheme(nextInkTone(), center.x, center.y);
}));
function streamAllowed(svg) {
  return motionAllowed() && (svg === dialSvg ? dialHovered && dialInView && !$("#overview").hidden : demoHovered && demoInView);
}
function clearBagua() {
  if (!baguaStream) return;
  const previous = baguaStream;
  baguaStream = null;
  clearTimeout(previous.timer);
  previous.field.remove();
  previous.animations.forEach(animation => animation.cancel());
}
function emitBagua(stream) {
  if (baguaStream !== stream) return;
  if (!streamAllowed(stream.svg)) { clearBagua(); return; }
  // 每次随机独立发射；最多十枚，只有悬停期间保留一个待执行计时器。
  if (stream.animations.size < 10) {
    const glyph = document.createElement("span");
    glyph.className = "bagua-glyph";
    glyph.textContent = Array.from("☰☱☲☳☴☵☶☷")[Math.floor(Math.random() * 8)];
    stream.field.append(glyph);
    const angle = Math.random() * 360, turn = 18 + Math.random() * 42;
    const radius = Math.min(150, window.innerWidth * .18) * (.8 + Math.random() * .45);
    const size = .85 + Math.random() * .65, duration = 950 + Math.random() * 650;
    const frames = [[0,1,0],[.18,.84,.58],[.72,.24,.42],[1,0,0]].map(([offset, distance, opacity]) => {
      const degrees = angle + turn * offset, radians = degrees * Math.PI / 180;
      return { offset, opacity, transform:"translate(" + Math.cos(radians) * radius * distance + "px," + Math.sin(radians) * radius * distance + "px) rotate(" + (degrees + 90) + "deg) scale(" + (size * (1 - offset) + .08 * offset) + ")" };
    });
    const animation = glyph.animate(frames, { duration, easing:"linear" });
    stream.animations.add(animation);
    animation.onfinish = animation.oncancel = () => { glyph.remove(); stream.animations.delete(animation); };
  }
  stream.timer = setTimeout(() => emitBagua(stream), 170 + Math.random() * 220);
}
function gatherBagua(svg) {
  if (baguaStream?.svg === svg) return;
  clearBagua();
  if (!streamAllowed(svg)) return;
  const field = document.createElement("span");
  field.className = "bagua-field";
  field.setAttribute("aria-hidden", "true");
  svg.parentElement.append(field);
  baguaStream = { svg, field, timer:null, animations:new Set() };
  emitBagua(baguaStream);
}
[dialSvg, demoSvg].forEach(svg => {
  svg.addEventListener("pointerenter", () => gatherBagua(svg));
  svg.addEventListener("pointerleave", () => { if (baguaStream?.svg === svg) clearBagua(); });
});
document.addEventListener("taiji-motion-sync", () => {
  if (!motionAllowed()) clearThemeReveal(true);
  if (baguaStream && !streamAllowed(baguaStream.svg)) clearBagua();
  if (streamAllowed(dialSvg)) gatherBagua(dialSvg);
  else if (streamAllowed(demoSvg)) gatherBagua(demoSvg);
});
window.addEventListener("resize", () => clearThemeReveal(true));
window.addEventListener("pagehide", () => { clearThemeReveal(); clearBagua(); });

"use strict";
// 弧长表示真实占比；中心太极保持传统等分，只承担视觉识别。
function taijiMarkup(value, style) {
  const core = '<circle cx="60" cy="60" r="28" class="taiji-paper"/>' +
    '<path d="M60 32a28 28 0 0 1 0 56a14 14 0 0 1 0-28a14 14 0 0 0 0-28Z" class="taiji-ink"/>' +
    '<circle cx="60" cy="46" r="3.6" class="taiji-ink"/><circle cx="60" cy="74" r="3.6" class="taiji-paper"/>';
  const width = style === "sector" ? 19 : style === "fish" ? 12 : 5;
  const ring = `<circle cx="60" cy="60" r="47" class="ratio-track" stroke-width="${width}"/>` +
    `<circle cx="60" cy="60" r="47" class="ratio-value" stroke-width="${width}" pathLength="100" stroke-dasharray="${value} 100" transform="rotate(-90 60 60)"/>`;
  let accents = "";
  if (style === "ring") accents = `<circle cx="60" cy="60" r="47" class="ratio-halo" pathLength="100" stroke-dasharray="${value} 100" transform="rotate(-90 60 60)"/>`;
  if (style === "fish") {
    // 鱼眼只放在足够长的分弧内部；0% 与 100% 不画不存在的一侧。
    for (const [size, end, className] of [[value, value, "taiji-paper"], [100 - value, 100, "taiji-ink"]]) {
      if (size <= 5) continue;
      const angle = (end / 100 * 360 - 97) * Math.PI / 180;
      accents += `<circle cx="${(60 + 47 * Math.cos(angle)).toFixed(3)}" cy="${(60 + 47 * Math.sin(angle)).toFixed(3)}" r="2.4" class="${className}"/>`;
    }
  }
  if (style === "sector") accents = '<path d="M60 2v5m58 53h-5M60 118v-5M2 60h5" class="ratio-ticks"/>';
  return `<svg class="taiji-svg taiji-${style}" viewBox="0 0 120 120" aria-hidden="true">${ring}${accents}<g class="taiji-core">${core}</g></svg>`;
}
function renderTaiji() {
  const style = { ridge: "ring", breath: "fish", scroll: "sector" }[root.dataset.atmosphere];
  const dial = $(".instrument-dial");
  dial.removeAttribute("aria-hidden");
  dial.setAttribute("role", "img");
  dial.setAttribute("aria-label", "提交水位 55%，上限余量 45%；太极内核为装饰");
  dial.setAttribute("title", "提交 26.4 / 48.0 GB；外环表示 55%，中心为太极图案");
  dial.innerHTML = taijiMarkup(55, style) + '<div class="dial-caption"><strong>55<small>%</small></strong><span>提交水位</span></div>';
  for (const [name, value] of [["cpu", 14], ["gpu", 6]]) {
    const resource = $(`.resource[data-detail="${name}"]`);
    resource.querySelector("svg").outerHTML = taijiMarkup(value, style);
  }
}
document.addEventListener("click", event => {
  if (event.target.closest("[data-atmosphere-choice]")) renderTaiji();
});
renderTaiji();

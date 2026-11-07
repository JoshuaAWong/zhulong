"use strict";
// 组合稿复用主题、场景和动效开关；仅增加占比试验和局部墨晕。
chooseAtmosphere("breath");
renderTaiji();
function syncUnifiedLabel() {
  const tone = root.dataset.theme;
  markButtons("[data-unified-tone]", "unifiedTone", tone);
  $("#directionDescription").textContent = "A2 整页墨气 + A1 太极墨环，点击模块感受墨色晕开。";
  $("#directionFoot").textContent = "墨气与太极 · " + (tone === "light" ? "昼 · " : "夜 · ") + $("#sceneCaption").textContent;
}
function balanceCoreMarkup(value) {
  const r = 28, bottom = r * value / 100, top = r - bottom;
  const eye = Math.min(top, bottom) * .23;
  const shape = value === 0 ? "M60 32Z" : value === 100 ? "M60 32a28 28 0 1 1 0 56a28 28 0 1 1 0-56Z" :
    "M60 32a28 28 0 0 1 0 56a" + bottom + " " + bottom + " 0 0 1 0 " + (-2 * bottom) + "a" + top + " " + top + " 0 0 0 0 " + (-2 * top) + "Z";
  // 两个鱼眼等面积抵消；深墨面积占比为 bottom / r。
  return '<circle cx="60" cy="60" r="28" class="taiji-paper"/><path d="' + shape + '" class="taiji-ink"/><circle cx="60" cy="' + (32 + top) + '" r="' + eye + '" class="taiji-ink"/><circle cx="60" cy="' + (88 - bottom) + '" r="' + eye + '" class="taiji-paper"/>';
}
$("#ringDemo").innerHTML = taijiMarkup(55, "ring");
$("#balanceDemo").innerHTML = taijiMarkup(55, "ring");
function updateRatioPreview(value) {
  $("#ratioPreview").value = String(value);
  $("#ratioReadout").textContent = value + "%";
  $("#ratioRemainder").textContent = "占用 " + value + "% · 余量 " + (100 - value) + "%";
  markButtons("[data-ratio]", "ratio", String(value));
  all(".demo-meter").forEach(meter => {
    meter.setAttribute("role", "img");
    meter.setAttribute("aria-label", "占用 " + value + "%，余量 " + (100 - value) + "%");
    meter.querySelectorAll(".ratio-value,.ratio-halo").forEach(arc => { arc.style.strokeDasharray = value + " 100"; });
  });
  $("#balanceDemo .taiji-core").innerHTML = balanceCoreMarkup(value);
}
$("#ratioPreview").addEventListener("input", event => updateRatioPreview(Number(event.target.value)));
updateRatioPreview(55);
// 只转主水位的太极与装饰墨圈；计量弧和文字保持固定朝向。
const dialSvg = $(".instrument-dial>.taiji-svg");
const spirit = document.createElementNS("http://www.w3.org/2000/svg", "g");
spirit.setAttribute("class", "taiji-spirit");
spirit.innerHTML = '<path class="orbit-ink" d="M10 38A55 55 0 0 1 48 6M82 9A55 55 0 0 1 114 61M99 99A55 55 0 0 1 38 110"/>';
spirit.append(dialSvg.querySelector(".taiji-core"));
dialSvg.append(spirit);
let spiritAnimation = null, dialInView = false, activeBloom = null;
function motionAllowed() { return root.dataset.motion === "on" && !reduced.matches && !document.hidden; }
function clearBloom() {
  if (!activeBloom) return;
  const previous = activeBloom;
  activeBloom = null;
  previous.node.remove();
  previous.animation.cancel();
}
function syncUnifiedMotion() {
  const allowed = motionAllowed();
  if (!allowed) clearBloom();
  if (spiritAnimation?.playState === "idle") spiritAnimation = null;
  if (!allowed || !dialInView || $("#overview").hidden) {
    spiritAnimation?.cancel();
    spiritAnimation = null;
    return;
  }
  if (!spiritAnimation) spiritAnimation = spirit.animate([{ transform:"rotate(0deg)" }, { transform:"rotate(360deg)" }], { duration:60000, iterations:Infinity, easing:"linear" });
}
const dialObserver = new IntersectionObserver(entries => {
  dialInView = entries[0].isIntersecting;
  syncUnifiedMotion();
});
dialObserver.observe($(".instrument-dial"));
function bloomInk(target) {
  clearBloom();
  if (!motionAllowed()) return;
  const node = document.createElement("span");
  node.className = "ink-bloom";
  node.setAttribute("aria-hidden", "true");
  target.append(node);
  const animation = node.animate([
    { opacity:0, transform:"scale(.48,.66)" },
    { opacity:.26, transform:"scale(.8,.88)", offset:.32 },
    { opacity:0, transform:"scale(1.12,1.06)" }
  ], { duration:900, easing:"cubic-bezier(.2,.65,.3,1)" });
  activeBloom = { node, animation };
  animation.onfinish = animation.oncancel = () => {
    node.remove();
    if (activeBloom?.node === node) activeBloom = null;
  };
}
function cancelSharedFade() {
  all(".brand,#prototype,.page,#chartContainer,#detailDialog").forEach(element => element.getAnimations().forEach(animation => animation.cancel()));
}
document.addEventListener("click", event => {
  const button = event.target.closest("button");
  if (!button) return;
  if (button.dataset.unifiedTone) {
    if (button.dataset.unifiedTone !== root.dataset.theme) $("#themeToggle").click();
    return;
  }
  if (button.dataset.ratio !== undefined) { updateRatioPreview(Number(button.dataset.ratio)); return; }
  cancelSharedFade();
  syncUnifiedLabel();
  syncUnifiedMotion();
  if (button.id === "motionToggle") return;
  const target = button.closest("[data-module]") ||
    (button.dataset.detail ? $("#detailDialog") :
    button.matches("#themeToggle,#sceneToggle") ? $(".scene-header") :
    button.matches("[data-page],[data-go],#replayMotion,[data-filter]") ? $(".page:not([hidden])") : null);
  if (target) bloomInk(target);
});
$(".brand").addEventListener("click", () => { cancelSharedFade(); syncUnifiedMotion(); bloomInk($("#overview")); });
document.addEventListener("visibilitychange", syncUnifiedMotion);
reduced.addEventListener("change", syncUnifiedMotion);
window.addEventListener("pagehide", () => { clearBloom(); spiritAnimation?.cancel(); spiritAnimation = null; });
window.addEventListener("pageshow", syncUnifiedMotion);
cancelSharedFade();
syncUnifiedLabel();

"use strict";
// 固定用户选定的 A 布局，三案仅比较整页水墨的空间关系。
const atmospheres = {
  ridge: { name: "A1 · 云岚入境 / 太极墨环", description: "山势沿侧边延伸，云气接入内容，远岸收在页尾。" },
  breath: { name: "A2 · 墨气相连 / 双鱼分弧", description: "柔墨穿过分区间隙，选中模块融入同一片墨色。" },
  scroll: { name: "A3 · 山河长卷 / 太极扇盘", description: "从上方云山到下方远岸，整页展开一幅山水长卷。" }
};
function syncAtmosphere() {
  const choice = atmospheres[root.dataset.atmosphere];
  // 原景龙身更大，取右下及底部地貌，避免在内容区重复出现龙首。
  $(".ink-mountain").setAttribute("viewBox", inkSceneIndex === 0 ? "1450 0 722 724" : "1850 480 322 244");
  $(".ink-shore").setAttribute("viewBox", inkSceneIndex === 0 ? "0 400 2172 324" : "0 600 2172 124");
  $("#directionDescription").textContent = choice.description;
  $("#directionFoot").textContent = `${choice.name} · ${root.dataset.theme === "light" ? "日间" : "夜间"}`;
  all(".page-ink image").forEach(image => {
    const source = image.dataset.tone === "light" ? ".scene-day" : ".scene-night";
    image.setAttribute("href", $(source).getAttribute("src"));
  });
}
function chooseAtmosphere(name) {
  root.dataset.atmosphere = name;
  markButtons("[data-atmosphere-choice]", "atmosphereChoice", name);
  $("#preferenceStatus").textContent = "";
  syncAtmosphere();
  animateChange($(".brand"));
}
document.addEventListener("click", event => {
  const button = event.target.closest("[data-atmosphere-choice]");
  if (button) chooseAtmosphere(button.dataset.atmosphereChoice);
});
$("#themeToggle").addEventListener("click", syncAtmosphere);
$("#sceneToggle").addEventListener("click", syncAtmosphere);
chooseLayout("mist");
const requestedAtmosphere = new URLSearchParams(location.search).get("atmosphere");
chooseAtmosphere(Object.hasOwn(atmospheres, requestedAtmosphere) ? requestedAtmosphere : "ridge");

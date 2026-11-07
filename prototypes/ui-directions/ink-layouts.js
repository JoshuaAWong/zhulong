"use strict";
// 仅管理候选布局与关注状态；导航、样本、主题和详情仍由既有脚本维护。
const layoutChoices = {
  mist: { name: "A · 云岫", description: "横向展开，淡墨托住当前选择；适合一眼看全。" },
  inkstone: { name: "B · 砚池", description: "左侧导航，趋势居中，事件在侧；适合持续观察。" },
  gallery: { name: "C · 游廊", description: "窄栏导航，宽幅主图，柔光回应选择；适合专注查看。" }
};
function syncLayoutLabel() {
  const layout = layoutChoices[root.dataset.layout];
  $("#directionFoot").textContent = `${layout.name} · ${root.dataset.theme === "light" ? "日间" : "夜间"}`;
}
function chooseLayout(name) {
  root.dataset.layout = name;
  markButtons("[data-layout-choice]", "layoutChoice", name);
  $("#directionDescription").textContent = layoutChoices[name].description;
  $("#preferenceStatus").textContent = "";
  syncLayoutLabel();
  animateChange($("#prototype"));
}
function selectModule(name) {
  all("[data-module]").forEach(panel => {
    const selected = panel.dataset.module === name;
    panel.classList.toggle("is-selected", selected);
    const button = panel.querySelector(".module-pick");
    button.setAttribute("aria-pressed", String(selected));
    button.textContent = selected ? "已关注" : "关注";
  });
  $("#selectionStatus").textContent = `当前关注：${name}`;
}
document.addEventListener("click", event => {
  const button = event.target.closest("button");
  if (button?.dataset.layoutChoice) chooseLayout(button.dataset.layoutChoice);
  const panel = event.target.closest("[data-module]");
  if (panel && (button || event.target.matches("input"))) selectModule(panel.dataset.module);
  if (button?.matches(".process,.resource")) {
    all(button.matches(".process") ? ".process" : ".resource").forEach(item => item.classList.toggle("is-inspected", item === button));
  }
  if (button?.dataset.page || button?.dataset.go) $("#selectionStatus").textContent = "可点选模块的“关注”，比较选中效果";
});
$("#themeToggle").addEventListener("click", syncLayoutLabel);
$("#sceneToggle").addEventListener("click", syncLayoutLabel);
const requestedLayout = new URLSearchParams(location.search).get("layout");
chooseLayout(Object.hasOwn(layoutChoices, requestedLayout) ? requestedLayout : "mist");
selectModule("资源趋势");

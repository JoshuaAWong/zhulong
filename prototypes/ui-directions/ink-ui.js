"use strict";
// 复用 app.js 的导航、样本与详情；这里只维护跨页水墨主景的标题和显示偏好。
const scenePages = {
  overview: ["本机观察", "系统总览", "先看水位，再看资源用在哪里。"],
  usage: ["本地用量", "AI 账本", "用量、费用与会话，放在同一本账里。"],
  events: ["观察记录", "事件记录", "按时间回看提醒，区分变化与实际处置。"]
};
function syncInkScene(page) {
  const copy = scenePages[page];
  $("#sceneIndex").textContent = copy[0];
  $("#sceneTitle").textContent = copy[1];
  $("#sceneDescription").textContent = copy[2];
}
function syncInkTone() {
  const light = root.dataset.theme === "light";
  $("#sceneCaption").textContent = light ? "墨隐山川" : "寒江月照";
  $("#directionFoot").textContent = `水墨工作台 · ${light ? "日间" : "夜间"}`;
}
document.addEventListener("click", event => {
  const button = event.target.closest("button");
  const page = button?.dataset.page || button?.dataset.go;
  if (page) syncInkScene(page);
});
$(".brand").addEventListener("click", () => syncInkScene("overview"));
$("#themeToggle").addEventListener("click", syncInkTone);
$("#ornamentToggle").addEventListener("click", () => {
  const enabled = root.dataset.ornament !== "on";
  root.dataset.ornament = enabled ? "on" : "off";
  $("#ornamentToggle").setAttribute("aria-pressed", String(enabled));
  $("#ornamentLabel").textContent = enabled ? "显" : "隐";
});
root.dataset.theme = new URLSearchParams(location.search).get("tone") === "dark" ? "dark" : "light";
$("#directionDescription").textContent = "B 布局 · 柔墨细景 · 昼夜各有山水";
updateThemeLabel();
syncInkTone();
syncInkScene("overview");

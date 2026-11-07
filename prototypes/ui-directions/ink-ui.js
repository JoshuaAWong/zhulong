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
const inkScenes = [
  { day: "ink-day-a-v4.png", night: "ink-night-a-v4.png", light: "松崖长游", dark: "江湾回环" },
  { day: "ink-day-b-v4.png", night: "ink-night-b-v4.png", light: "云岫盘龙", dark: "寒汀远行" }
];
let inkSceneIndex = 0;
function syncInkTone() {
  const light = root.dataset.theme === "light";
  const scene = inkScenes[inkSceneIndex];
  $(".scene-day").src = "assets/" + scene.day;
  $(".scene-night").src = "assets/" + scene.night;
  $("#sceneCaption").textContent = light ? scene.light : scene.dark;
  $("#sceneLabel").textContent = `${inkSceneIndex + 1} / ${inkScenes.length}`;
  $("#directionFoot").textContent = `水墨工作台 · ${light ? "日间" : "夜间"}`;
}
document.addEventListener("click", event => {
  const button = event.target.closest("button");
  const page = button?.dataset.page || button?.dataset.go;
  if (page) syncInkScene(page);
});
$(".brand").addEventListener("click", () => syncInkScene("overview"));
$("#themeToggle").addEventListener("click", syncInkTone);
$("#sceneToggle").addEventListener("click", () => {
  inkSceneIndex = (inkSceneIndex + 1) % inkScenes.length;
  syncInkTone();
});
$("#ornamentToggle").addEventListener("click", () => {
  const enabled = root.dataset.ornament !== "on";
  root.dataset.ornament = enabled ? "on" : "off";
  $("#ornamentToggle").setAttribute("aria-pressed", String(enabled));
  $("#ornamentLabel").textContent = enabled ? "显" : "隐";
});
root.dataset.theme = new URLSearchParams(location.search).get("tone") === "dark" ? "dark" : "light";
$("#directionDescription").textContent = "B 布局 · 笔墨游龙 · 两组昼夜山水";
updateThemeLabel();
syncInkTone();
syncInkScene("overview");

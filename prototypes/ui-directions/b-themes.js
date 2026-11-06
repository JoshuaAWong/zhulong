"use strict";
// B 版共用原型交互；这里只拥有主题选择、装饰与选型说明。
const themes = {
  abyss: {
    name: "B1 / 玄渊龙章",
    description: "玄黑古金与赤色龙纹，取古典器物的沉静。浅色换为素绢与墨金。",
    motto: "烛照九阴 · 观测万象",
    title: "烛照此间，诸象平稳。",
    world: "钟山之下 · 昼夜有序",
    seal: "烛阴",
    note: "龙章与器物纹理承接烛龙意象，界面以墨金分出主次。"
  },
  astral: {
    name: "B2 / 太虚灵枢",
    description: "深靛冷青、星轨与阵纹。传统神话与精密仪表交织，浅色转为青瓷月白。",
    motto: "观星测象 · 烛照微尘",
    title: "灵枢有序，诸象平稳。",
    world: "太虚观象 · 日月同枢",
    seal: "观象",
    note: "以星轨和阵纹表达采集与观测；赛博修真是视觉创作，指标口径不变。"
  },
  moon: {
    name: "B3 / 月白山海",
    description: "月白山海：圆润分区、青玉游龙与云山留白，深青夜色和月白日色相伴。",
    motto: "山海入卷 · 明晦有度",
    title: "山海无扰，诸象平稳。",
    world: "山海入卷 · 昼夜相守",
    seal: "山海",
    note: "云山在远，游龙在侧。日月云纹相伴，昼夜各有清宁。"
  }
};
function syncMythTheme() {
  const light = root.dataset.theme === "light";
  $("#themeLabel").textContent = light ? "昼 · 浅色" : "夜 · 深色";
  $("#themeToggle").setAttribute("aria-label", light ? "切换为深色夜景" : "切换为浅色日景");
  $("#mythPhase").textContent = light ? "视为昼" : "瞑为夜";
  $("#directionFoot").textContent = `${themes[root.dataset.skin].name} · ${light ? "浅色" : "深色"}`;
}
function selectMythTheme(name) {
  const theme = themes[name];
  root.dataset.skin = name;
  root.dataset.design = "instrument";
  markButtons("[data-skin-choice]", "skinChoice", name);
  $("#directionDescription").textContent = theme.description;
  $(".brand p").textContent = theme.motto;
  $(".verdict h2").textContent = theme.title;
  $("#worldCaption").textContent = theme.world;
  $("#worldSeal").textContent = theme.seal;
  $("#worldNote").textContent = theme.note;
  syncMythTheme();
  animateChange($("#prototype"));
}
all("[data-skin-choice]").forEach(button => button.addEventListener("click", () => selectMythTheme(button.dataset.skinChoice)));
$("#themeToggle").addEventListener("click", syncMythTheme);
$("#ornamentToggle").addEventListener("click", () => {
  const enabled = root.dataset.ornament !== "on";
  root.dataset.ornament = enabled ? "on" : "off";
  $("#ornamentToggle").setAttribute("aria-pressed", String(enabled));
  $("#ornamentLabel").textContent = enabled ? "显" : "隐";
  animateChange($(".hero"));
});
const skin = new URLSearchParams(location.search).get("skin");
const tone = new URLSearchParams(location.search).get("tone");
root.dataset.theme = tone === "light" ? "light" : "dark";
selectMythTheme(Object.hasOwn(themes, skin) ? skin : "moon");

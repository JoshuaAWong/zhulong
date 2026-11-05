"use strict";
// 固定演示样本；不读取本机、不发请求、不创建刷新循环。
const root = document.documentElement;
const directions = {
  scroll: { name: "A / 山海手卷", theme: "light", description: "山海手卷：沿用国风三栏，用墨线、朱批和留白整理系统信息。" },
  instrument: { name: "B / 玄枢仪", theme: "dark", description: "玄枢仪：以水位仪表和紧凑分区组织信息，适合同时观察多项指标。" },
  jade: { name: "C / 青玉书斋", theme: "light", description: "青玉书斋：更柔和的分区与留白，让系统观察和 AI 账本适合长时间阅读。" }
};
const processes = [
  { name: "Chrome", icon: "C", memory: 2.84, cpu: 4.2, note: "浏览器示例进程，物理内存占用最高。多进程用量在此按应用展示。" },
  { name: "Code", icon: "V", memory: 1.26, cpu: 2.1, note: "代码编辑器示例进程，当前没有持续增长的判断依据。" },
  { name: "Explorer", icon: "E", memory: 0.42, cpu: 0.8, note: "Windows 桌面与文件管理示例。观察版只记录，不执行结束操作。" },
  { name: "Discord", icon: "D", memory: 0.38, cpu: 2.8, note: "通信应用示例，CPU 排名与内存排名不同。" },
  { name: "Zhulong", icon: "烛", memory: 0.06, cpu: 0.2, note: "此数值仅用于排版演示，不是新版实测开销或资源预算。" }
];
const events = [
  { id: "waterline", time: "14:02", level: "warning", title: "内存水位短时升高", text: "提交水位达到 76%，持续约 2 分钟，当前已回落。未执行任何处置。" },
  { id: "import", time: "13:48", level: "info", title: "AI 用量已更新", text: "读取到 3 条新的演示用量记录，归入今日账本。" },
  { id: "start", time: "09:00", level: "info", title: "开始今日观察", text: "监控、历史与事件记录就绪。此页全部使用演示数据。" }
];
const metrics = {
  commit: { title: "提交内存", meaning: "系统承诺提供的内存，不等于物理内存", value: "26.4", unit: "GB", limit: "上限 48.0 GB · 当前 55%", legend: "已提交", end: 55, base: 49, insight: "14:02 水位曾达 76%，当前已回落；单次峰值不能证明进程泄漏。" },
  physical: { title: "物理内存", meaning: "当前驻留在物理内存中的数据，与提交内存分开观察", value: "19.2", unit: "GB", limit: "总量 32.0 GB · 当前 60%", legend: "物理内存已用", end: 60, base: 54, insight: "物理内存占用约 60%。回收工作集不等于减少提交内存。" },
  cpu: { title: "处理器", meaning: "整机 CPU 占用，按全部逻辑核心归一化", value: "14", unit: "%", limit: "固定量程 0–100% · 单核峰值 32%", legend: "CPU 总占用", end: 14, base: 20, insight: "整机 CPU 当前 14%。平均值较低时，也需留意单核峰值和交互延迟。" }
};
const ranges = {
  "24h": { label: "过去 24 小时", axis: ["昨日 14:32", "20:32", "02:32", "08:32", "14:32"] },
  "7d": { label: "过去 7 天", axis: ["7 天前", "5 天前", "3 天前", "1 天前", "现在"] },
  "30d": { label: "过去 30 天", axis: ["30 天前", "23 天前", "15 天前", "7 天前", "现在"] }
};
let currentMetric = "commit", currentRange = "24h", currentRank = "memory";
const reduced = matchMedia("(prefers-reduced-motion: reduce)");
root.dataset.motion = reduced.matches ? "off" : "on";
const $ = selector => document.querySelector(selector);
const all = selector => Array.from(document.querySelectorAll(selector));
function markButtons(selector, attribute, value) {
  all(selector).forEach(button => {
    const active = button.dataset[attribute] === value;
    button.classList.toggle("active", active);
    button.setAttribute("aria-pressed", String(active));
  });
}
function animateChange(element) {
  if (root.dataset.motion !== "on" || reduced.matches || document.hidden) return;
  element.getAnimations().forEach(animation => animation.cancel());
  element.animate([{ opacity: .45, transform: "translateY(3px)" }, { opacity: 1, transform: "translateY(0)" }], { duration: 230, easing: "ease-out" });
}
function updateThemeLabel() {
  const light = root.dataset.theme === "light";
  $("#themeLabel").textContent = light ? "日间" : "夜间";
  $("#themeToggle").setAttribute("aria-label", light ? "切换为夜间" : "切换为日间");
}
function chooseDirection(name) {
  const direction = directions[name];
  root.dataset.design = name;
  root.dataset.theme = direction.theme;
  markButtons("[data-design-choice]", "designChoice", name);
  $("#directionDescription").textContent = direction.description;
  $("#directionFoot").textContent = direction.name;
  updateThemeLabel();
  animateChange($("#prototype"));
}
function renderProcesses() {
  const query = $("#processSearch").value.trim().toLowerCase();
  const selected = processes.filter(process => process.name.toLowerCase().includes(query)).sort((a, b) => b[currentRank] - a[currentRank]);
  $("#processList").innerHTML = selected.map(process => {
    const index = processes.indexOf(process);
    const amount = currentRank === "memory" ? process.memory.toFixed(2) : process.cpu.toFixed(1);
    const width = process[currentRank] / (currentRank === "memory" ? 3 : 5) * 100;
    return `<button class="process" data-process="${index}" aria-label="查看 ${process.name} 详情"><span class="process-top"><span class="process-icon">${process.icon}</span><span class="process-name">${process.name}</span><span class="process-value">${amount}<small>${currentRank === "memory" ? "GB" : "%"}</small></span></span><span class="process-meter"><i style="width:${width}%"></i></span></button>`;
  }).join("") || '<p class="fine-print" style="padding:24px 0">没有匹配的演示进程。</p>';
  $("#rankUnit").textContent = currentRank === "memory" ? "口径：物理内存占用" : "口径：整机 CPU 归一化占比";
}
function renderChart() {
  const metric = metrics[currentMetric], range = ranges[currentRange];
  $("#chartTitle").textContent = metric.title;
  $("#chartMeaning").textContent = metric.meaning;
  $("#chartValue").innerHTML = `${metric.value} <small>${metric.unit}</small>`;
  $("#chartLimit").textContent = metric.limit;
  $("#chartLegend").textContent = metric.legend;
  $("#chartInsight").textContent = metric.insight;
  $("#rangeSummary").textContent = range.label + " · 演示趋势";
  const count = 48, points = [];
  const offset = currentRange === "24h" ? 0 : currentRange === "7d" ? 2 : 4;
  for (let i = 0; i < count; i++) {
    let value = metric.base + Math.sin(i * .47 + offset) * 4 + Math.cos(i * 1.17) * 2;
    if (i > 30 && i < 37) value += currentMetric === "cpu" ? 30 : 13;
    if (currentMetric === "commit" && i === 46) value = 76;
    if (i === count - 1) value = metric.end;
    points.push([34 + i * 568 / (count - 1), 168 - value * 1.48]);
  }
  const curve = points.map((point, index) => `${index ? "L" : "M"}${point[0].toFixed(1)},${point[1].toFixed(1)}`).join(" ");
  const grid = [0, 25, 50, 75, 100].map(value => `<path class="grid" d="M34 ${168 - value * 1.48}H602"/><text x="25" y="${171 - value * 1.48}" text-anchor="end">${value}</text>`).join("");
  const thresholds = currentMetric === "commit" ? [70, 85].map(value => `<path class="threshold" d="M34 ${168 - value * 1.48}H602"/><text x="602" y="${164 - value * 1.48}" text-anchor="end">${value === 70 ? "慎" : "危"} ${value}%</text>`).join("") : "";
  const axis = range.axis.map((label, index) => `<text x="${34 + index * 142}" y="191" text-anchor="${index === 0 ? "start" : index === 4 ? "end" : "middle"}">${label}</text>`).join("");
  $("#chartContainer").innerHTML = `<svg viewBox="0 0 620 200" preserveAspectRatio="none" role="img" aria-label="${metric.title}，${range.label}演示曲线，固定百分比量程 0 至 100"><text x="25" y="10" text-anchor="end">%</text>${grid}${thresholds}<path class="area" d="${curve}L602,168L34,168Z"/><path class="curve" d="${curve}"/><circle class="end-dot" cx="602" cy="${points[count - 1][1]}" r="3.3"/>${axis}</svg>`;
  animateChange($("#chartContainer"));
}
function openPage(name) {
  all(".page").forEach(page => { page.hidden = page.id !== name; });
  markButtons(".tab", "page", name);
  animateChange($("#" + name));
}
function renderEvents(filter = "all") {
  $("#eventList").innerHTML = events.filter(event => filter === "all" || event.level === filter).map(event => `<article class="event-item"><time>${event.time}</time><span class="small-seal">${event.level === "warning" ? "慎" : "记"}</span><div><h3>${event.title}</h3><p>${event.text}</p></div><button class="text-button" data-detail="${event.id}">查看详情</button></article>`).join("");
}
const details = {
  waterline: { title: "一次水位变化", text: "14:02，提交水位达到 76%，约 2 分钟后回落。当前 26.4 / 48.0 GB，占 55%。", note: "这是演示事件。尚无持续增长证据，不能据此判断泄漏；观察版没有执行清理、结束进程或服务重启。" },
  cpu: { title: "处理器", text: "整机占用 14%，单核峰值 32%。历史图使用固定 0–100% 量程。", note: "单个核心繁忙与整体繁忙分开解释；此处仅展示演示样本。" },
  gpu: { title: "显卡", text: "GPU 利用率 6%，温度 43°C，显存使用 1.8 GB。", note: "这三项不是同一指标；设备不支持的字段应显示不可用，而非零。" },
  disk: { title: "磁盘读写", text: "当前合计吞吐 18.3 MB/s，最忙磁盘活动时间占比 8%。", note: "吞吐、活动时间与响应延迟分开观察；本页没有进行磁盘采样。" },
  network: { title: "网络", text: "下载 1.8 MB/s，上传 0.6 MB/s。", note: "速率不代表延迟表现；正式版本中延迟与抖动单独展示。" },
  import: { title: "AI 用量更新", text: "本次新增 3 条演示用量记录。今日总计 186.4k tokens，估算费用 ¥3.82。", note: "这里没有读取日志或导入历史。费用按演示口径展示，不能当作实际账单。" },
  start: { title: "开始今日观察", text: "09:00 开始记录系统指标与事件。", note: "此处是界面交互样例，不表示本机监控服务已启动或发生配置改变。" }
};
function showDetail(detail) {
  $("#detailTitle").textContent = detail.title;
  const content = $("#detailContent");
  content.replaceChildren();
  [detail.text, detail.note].forEach(text => { const paragraph = document.createElement("p"); paragraph.textContent = text; content.append(paragraph); });
  $("#detailDialog").showModal();
  animateChange($("#detailDialog"));
}
function setMotion(enabled) {
  root.dataset.motion = enabled ? "on" : "off";
  $("#motionToggle").setAttribute("aria-pressed", String(enabled));
  $("#motionLabel").textContent = enabled ? "开" : "关";
  $("#replayMotion").disabled = !enabled;
  if (!enabled) document.getAnimations().forEach(animation => animation.cancel());
}
document.addEventListener("click", event => {
  const button = event.target.closest("button");
  if (!button) return;
  if (button.dataset.designChoice) chooseDirection(button.dataset.designChoice);
  if (button.dataset.page || button.dataset.go) openPage(button.dataset.page || button.dataset.go);
  if (button.dataset.rank) { currentRank = button.dataset.rank; markButtons("[data-rank]", "rank", currentRank); renderProcesses(); }
  if (button.dataset.metric) { currentMetric = button.dataset.metric; markButtons("[data-metric]", "metric", currentMetric); renderChart(); }
  if (button.dataset.range) { currentRange = button.dataset.range; markButtons("[data-range]", "range", currentRange); renderChart(); }
  if (button.dataset.filter) { markButtons("[data-filter]", "filter", button.dataset.filter); renderEvents(button.dataset.filter); }
  if (button.dataset.detail) showDetail(details[button.dataset.detail]);
  if (button.dataset.process !== undefined) { const process = processes[Number(button.dataset.process)]; showDetail({ title: process.name, text: `物理内存 ${process.memory.toFixed(2)} GB；CPU 占用 ${process.cpu.toFixed(1)}%。`, note: process.note }); }
});
$("#processSearch").addEventListener("input", renderProcesses);
$(".brand").addEventListener("click", event => { event.preventDefault(); openPage("overview"); });
$("#themeToggle").addEventListener("click", () => { root.dataset.theme = root.dataset.theme === "light" ? "dark" : "light"; updateThemeLabel(); animateChange($(".brand")); });
$("#motionToggle").addEventListener("click", () => setMotion(root.dataset.motion !== "on" && !reduced.matches));
$("#replayMotion").addEventListener("click", () => animateChange($(".page:not([hidden])")));
$("#closeDetail").addEventListener("click", () => $("#detailDialog").close());
$("#ackDetail").addEventListener("click", () => $("#detailDialog").close());
$("#preferButton").addEventListener("click", () => { $("#preferenceStatus").textContent = `本页偏好：${directions[root.dataset.design].name}（回到对话告诉我即可）`; });
document.addEventListener("visibilitychange", () => { root.dataset.paused = String(document.hidden); if (document.hidden) document.getAnimations().forEach(animation => animation.cancel()); });
reduced.addEventListener("change", event => setMotion(!event.matches));
const requested = new URLSearchParams(location.search).get("design");
chooseDirection(Object.hasOwn(directions, requested) ? requested : "scroll");
setMotion(!reduced.matches);
renderProcesses();
renderChart();
renderEvents();

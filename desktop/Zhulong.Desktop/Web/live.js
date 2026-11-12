"use strict";
// 独立桌面数据层：只通过受限宿主桥读取新版数据，不连接旧面板。
(() => {
  const nativeBridge = window.chrome?.webview;
  const bridge = nativeBridge || window.zhulongPortalBridge;
  const windowControls = $("#windowControls");
  function setWindowMode(value) {
    const maximized = value === "maximized";
    root.dataset.windowState = maximized ? "maximized" : "normal";
    const button = windowControls?.querySelector("[data-window-command='toggle-maximize']");
    if (button) {
      const label = maximized ? "还原窗口" : "最大化";
      button.setAttribute("aria-label", label);
      button.title = label;
    }
  }
  function windowCommand(command) {
    if (nativeBridge && token) nativeBridge.postMessage({ kind: "window-command", token, command });
  }
  function initWindowControls() {
    if (!nativeBridge || !windowControls) return;
    const drag = $(".window-drag");
    windowControls.addEventListener("click", event => {
      const command = event.target.closest("[data-window-command]")?.dataset.windowCommand;
      if (command) windowCommand(command);
    });
    let start;
    drag.addEventListener("pointerdown", event => {
      if (root.dataset.nativeDrag !== "true" && event.isPrimary && event.button === 0) start = [event.clientX, event.clientY];
    });
    drag.addEventListener("pointermove", event => {
      if (!start || !(event.buttons & 1) || Math.hypot(event.clientX - start[0], event.clientY - start[1]) < 4) return;
      start = null;
      windowCommand("drag");
    });
    ["pointerup", "pointerleave", "pointercancel"].forEach(type => drag.addEventListener(type, () => start = null));
    drag.addEventListener("dblclick", () => { if (root.dataset.nativeDrag !== "true") windowCommand("toggle-maximize"); });
  }

  const pending = new Map(), histories = new Map();
  let token = null, sequence = 0, refreshTimer = null, quotaTimer = null;
  let latest = null, liveReadout = null, usage = null, lastUsageRead = 0, refreshing = false, usageBusy = false;
  let health = null, eventHours = 24, actionBusy = false;
  let quotaRows = [], chartCleanup = null;
  function clearChartHover() { chartCleanup?.(); chartCleanup = null; }
  const number = (value, digits = 1) => Number.isFinite(value) ? value.toLocaleString("zh-CN", { maximumFractionDigits: digits }) : "—";
  const percentText = value => Number.isFinite(value) ? number(value) + "%" : "未知";
  const date = value => value && Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleString("zh-CN", { hour12: false }) : "未知";
  const text = (selector, value) => { const node = $(selector); if (node) node.textContent = value; };
  function node(tag, value, className) {
    const result = document.createElement(tag);
    if (value !== undefined) result.textContent = value;
    if (className) result.className = className;
    return result;
  }
  function request(api, parameters = {}) {
    if (!bridge || !token) return Promise.reject(new Error("桌面数据桥尚未就绪"));
    const id = ++sequence;
    return new Promise((resolve, reject) => {
      const timer = api === "action" ? null : setTimeout(() => { pending.delete(id); reject(new Error("读取超时")); }, 20000);
      pending.set(id, { resolve, reject, timer });
      bridge.postMessage({ kind: "query", id, token, api, ...parameters });
    });
  }
  function cancelPending() {
    pending.forEach(item => { clearTimeout(item.timer); item.reject(new Error("页面已暂停")); });
    pending.clear();
  }
  function status(value) { text(".sample-time", value); $(".sample-time").title = value; }
  let sentTheme;
  function syncWindowTheme() {
    const theme = root.dataset.theme;
    if (!nativeBridge || !token || theme === sentTheme || !["light", "dark"].includes(theme)) return;
    nativeBridge.postMessage({ kind: "theme", token, theme });
    sentTheme = theme;
  }
  if (nativeBridge && typeof MutationObserver === "function") {
    new MutationObserver(syncWindowTheme).observe(root, { attributes: true, attributeFilter: ["data-theme"] });
  }
  const desktopFrameGap = "desktop_frame_gap_ms";
  const historyHours = { "1h": 1, "24h": 24, "7d": 168, "30d": 720 };
  const metricsByName = {
    [desktopFrameGap]: ["桌面帧间隔", "桌面连续提交时的最长间隔，数值越高表示提交停顿越久", "ms"],
    pagefile_used_gb: ["分页文件已用", "分页文件承载的内存，结合提交水位观察", "GB"],
    pagefile_total_gb: ["分页文件总量", "Windows 报告的分页文件容量", "GB"],
    cpu_max_core: ["单核峰值", "最繁忙逻辑核心，区别于整机平均占用", "%"],
    io_read_mb_s: ["磁盘读取", "全部磁盘读取吞吐，不代表活动时间占比", "MB/s"],
    io_write_mb_s: ["磁盘写入", "全部磁盘写入吞吐，不代表活动时间占比", "MB/s"],
    net_down_mb_s: ["网络下载", "网络下行速率", "MB/s"],
    net_up_mb_s: ["网络上传", "网络上行速率", "MB/s"],
    ping_gw_ms: ["网关延迟", "本机到网关的往返时间", "ms"],
    ping_net_ms: ["外网延迟", "配置目标的往返时间", "ms"],
    ping_jitter_ms: ["网络抖动", "连续探测的延迟变化", "ms"],
    gpu_temp: ["显卡温度", "当前显卡温度", "°C"],
    gpu_util: ["显卡利用率", "显卡计算负载", "%"],
    gpu_mem_used: ["显存已用", "当前显卡显存使用量", "GB"],
    gpu_mem_total: ["显存总量", "当前显卡显存容量", "GB"],
    disk_total_gb: ["磁盘总容量", "被采集磁盘的容量合计", "GB"],
    disk_free_gb: ["磁盘可用容量", "被采集磁盘剩余空间", "GB"],
    disk_temp_c: ["磁盘温度", "可读磁盘传感器的最高温度", "°C"],
    disk_used_percent: ["磁盘空间占比", "已用空间占比，不是磁盘活动时间", "%"]
  };
  const metric = name => latest?.metrics?.[name];
  const sampledAt = item => item?.sampledAt || latest?.timestamp;
  function sampleState(item) {
    const stamp = Date.parse(sampledAt(item));
    if (item?.state && item.state !== "ok") return item.state === "stale" ? "旧采样" : "暂无有效值";
    if (!Number.isFinite(item?.value)) return "未知";
    const interval = Number.isFinite(health?.sampleSeconds) && health.sampleSeconds > 0 ? health.sampleSeconds : 30;
    return Number.isFinite(stamp) && Date.now() - stamp > Math.max(90, interval * 3) * 1000 ? "旧采样" : "有效";
  }
  function metricText(name, digits = 1) {
    const item = metric(name), info = metricsByName[name];
    const suffix = sampleState(item) === "有效" ? "" : sampleState(item) === "旧采样" ? "（旧）" : "（未知）";
    const value = ["有效", "旧采样"].includes(sampleState(item)) ? item?.value : null;
    return number(value, digits) + " " + (item?.unit || info?.[2] || "") + suffix;
  }
  function newestReadout() {
    if (liveReadout && !(Date.parse(latest?.timestamp) > Date.parse(liveReadout.timestamp))) return liveReadout;
    return latest;
  }
  function describeMetric(name) {
    const readout = newestReadout();
    if (name === "commit") return { title: "提交内存", meaning: "系统承诺提供的内存，与物理内存分开观察", value: latest?.commitUsedGb, unit: "GB", plotUnit: "%", percent: latest?.commitPercent, at: latest?.timestamp };
    if (name === "physical") return { title: "物理内存", meaning: "当前驻留物理内存的占用", value: readout?.memoryUsedGb, unit: "GB", plotUnit: "%", percent: readout?.memoryPercent, at: readout?.timestamp };
    if (name === "cpu") return { title: "处理器", meaning: "整机 CPU，按所有逻辑核心归一为 0–100%", value: readout?.cpuPercent, unit: "%", plotUnit: "%", percent: readout?.cpuPercent, at: readout?.timestamp };
    const item = metric(name), info = metricsByName[name] || [name, "系统指标", ""];
    return { title: info[0], meaning: item?.label ? info[1] + " · " + item.label : info[1], value: ["有效", "旧采样"].includes(sampleState(item)) ? item?.value : null, unit: item?.unit || info[2], plotUnit: item?.unit || info[2], at: sampledAt(item), state: sampleState(item), peak: name === desktopFrameGap };
  }
  ranges["1h"] = { label: "过去 1 小时" };
  const frameHour = node("button", "1 小时");
  frameHour.type = "button"; frameHour.dataset.range = "1h"; frameHour.hidden = true;
  frameHour.setAttribute("aria-pressed", "false");
  $(".range-switch").prepend(frameHour);
  function syncHistoryRange() {
    frameHour.hidden = currentMetric !== desktopFrameGap;
    if (frameHour.hidden && currentRange === "1h") {
      currentRange = "24h"; markButtons("[data-range]", "range", currentRange);
    }
  }
  const moreMetrics = node("select", undefined, "live-metric-select");
  moreMetrics.setAttribute("aria-label", "选择更多历史指标");
  const defaultMetric = node("option", "更多指标"); defaultMetric.value = ""; moreMetrics.append(defaultMetric);
  Object.entries(metricsByName).forEach(([key, info]) => { const option = node("option", info[0]); option.value = key; moreMetrics.append(option); });
  $(".metric-tabs").after(moreMetrics);
  moreMetrics.addEventListener("change", () => {
    if (!moreMetrics.value) return;
    currentMetric = moreMetrics.value;
    if (currentMetric === desktopFrameGap) { currentRange = "1h"; markButtons("[data-range]", "range", currentRange); }
    markButtons("[data-metric]", "metric", currentMetric); renderChart();
    readHistory().catch(error => status("历史读取失败：" + error.message));
  });
  const rankGroup = $(".ranking [data-rank]").parentElement;
  rankGroup.querySelector("[data-rank='memory']").textContent = "提交";
  const ioRank = node("button", "I/O"); ioRank.dataset.rank = "io"; ioRank.setAttribute("aria-pressed", "false"); rankGroup.append(ioRank);
  const originalProcesses = renderProcesses;
  $("#processSearch").removeEventListener("input", originalProcesses);
  renderProcesses = function () {
    const search = $("#processSearch").value.trim().toLowerCase();
    const rows = processes.filter(item => item.name.toLowerCase().includes(search))
      .sort((a, b) => (Number.isFinite(b[currentRank]) ? b[currentRank] : -1) - (Number.isFinite(a[currentRank]) ? a[currentRank] : -1)).slice(0, 5);
    const list = $("#processList"); list.replaceChildren();
    const max = Math.max(...rows.map(row => Number.isFinite(row[currentRank]) ? row[currentRank] : 0), 1);
    rows.forEach(item => {
      const button = node("button", undefined, "process");
      button.dataset.process = String(processes.indexOf(item));
      const line = node("span", undefined, "process-top");
      line.append(node("span", item.name.slice(0, 1), "process-icon"), node("span", item.name, "process-name"));
      const amount = node("span", number(item[currentRank], currentRank === "memory" ? 2 : 1), "process-value");
      amount.append(node("small", currentRank === "memory" ? "GB" : currentRank === "io" ? "MB/s" : "%"));
      line.append(amount); button.append(line);
      const meter = node("span", undefined, "process-meter"), fill = node("i");
      fill.style.width = (Number.isFinite(item[currentRank]) ? Math.max(0, Math.min(100, item[currentRank] / max * 100)) : 0) + "%";
      meter.append(fill); button.append(meter);
      button.append(node("span", "PID " + item.pid + " · 工作集 " + number(item.workingSet, 2) + " GB · CPU " + percentText(item.cpu), "live-process-meta"));
      list.append(button);
    });
    if (!rows.length) list.append(node("p", latest ? "当前没有匹配进程" : "等待首次采集", "fine-print live-empty"));
    const meaning = { memory: "单进程提交内存，GB", cpu: "单进程整机归一化 CPU，0–100%", io: "单进程读写吞吐合计，MB/s" }[currentRank];
    text("#rankUnit", "口径：" + meaning + "；未知显示 —");
    text(".ranking .section-subtitle", "当前" + { memory: "提交内存", cpu: "CPU", io: "读写吞吐" }[currentRank] + "最高的5个单进程");
  };
  $("#processSearch").addEventListener("input", renderProcesses);
  document.addEventListener("click", event => {
    const button = event.target.closest("[data-process]");
    if (!button) return;
    event.stopImmediatePropagation();
    const item = processes[Number(button.dataset.process)];
    if (item) showDetail({ title: item.name,
      text: "PID " + item.pid + "；提交 " + number(item.memory, 2) + " GB；工作集 " + number(item.workingSet, 2) + " GB；整机 CPU " + number(item.cpu) + "%；读取 " + number(item.read) + " / 写入 " + number(item.write) + " MB/s。",
      note: (item.path ? "路径：" + item.path + "。" : "路径暂不可读。") + "采样于 " + date(latest?.timestamp) + "。进程处置需经过白名单和原生确认。" });
  }, true);
  renderEvents = function (filter = "all") {
    const list = $("#eventList"); list.replaceChildren();
    events.filter(item => filter === "all" || item.level === filter).forEach(item => {
      const article = node("article", undefined, "event-item");
      article.append(node("time", date(item.timestamp)), node("span", item.level === "error" ? "警" : item.level === "warning" ? "慎" : "记", "small-seal"));
      const copy = node("div"); copy.append(node("h3", item.title), node("p", item.text)); article.append(copy); list.append(article);
    });
    if (!list.children.length) list.append(node("p", "当前范围没有观察事件", "live-empty"));
  };
  function svgElement(tag, attributes) {
    const result = document.createElementNS("http://www.w3.org/2000/svg", tag);
    Object.entries(attributes).forEach(([key, value]) => result.setAttribute(key, String(value)));
    return result;
  }
  function bindChartHover(svg, box, points, description) {
    const cursor = svgElement("g", { class: "live-history-crosshair", visibility: "hidden" });
    const vertical = svgElement("path", {}), horizontal = svgElement("path", {}), dot = svgElement("circle", { r: 3 });
    cursor.append(vertical, horizontal, dot); svg.append(cursor);
    const tooltip = node("div", undefined, "live-history-tooltip"); tooltip.hidden = true; tooltip.setAttribute("role", "tooltip"); box.append(tooltip);
    let selected = null;
    function hide() { cursor.setAttribute("visibility", "hidden"); tooltip.hidden = true; selected = null; }
    function move(event) {
      const matrix = svg.getScreenCTM();
      if (!matrix || !points.length) { hide(); return; }
      const position = new DOMPoint(event.clientX, event.clientY).matrixTransform(matrix.inverse());
      if (!Number.isFinite(position.x) || !Number.isFinite(position.y) || position.x < 34 || position.x > 602 || position.y < 20 || position.y > 168) { hide(); return; }
      const hit = points.reduce((nearest, point) => Math.abs(point.x - position.x) < Math.abs(nearest.x - position.x) ? point : nearest);
      if (selected === hit && !tooltip.hidden) return;
      selected = hit; cursor.setAttribute("visibility", "visible");
      vertical.setAttribute("d", "M" + hit.x.toFixed(2) + " 20V168");
      horizontal.setAttribute("d", hit.y === null ? "" : "M34 " + hit.y.toFixed(2) + "H602");
      dot.setAttribute("visibility", hit.y === null ? "hidden" : "visible");
      if (hit.y !== null) { dot.setAttribute("cx", hit.x); dot.setAttribute("cy", hit.y); }
      tooltip.textContent = description.title + " · " + (Number.isFinite(hit.point.value) ? number(hit.point.value, 6) + " " + description.plotUnit : "暂无有效值") + "\n" + date(hit.point.timestamp) + "\n" + (description.peak ? "时间桶最高值，时间为该间隔开始时刻。" : "时间桶均值；时间为桶内最近记录，不作插值。");
      tooltip.hidden = false;
      const anchor = new DOMPoint(hit.x, hit.y ?? 20).matrixTransform(matrix), rect = box.getBoundingClientRect();
      tooltip.style.left = Math.max(8, Math.min(rect.width - tooltip.offsetWidth - 8, anchor.x - rect.left + 12)) + "px";
      tooltip.style.top = Math.max(8, Math.min(rect.height - tooltip.offsetHeight - 8, anchor.y - rect.top - tooltip.offsetHeight - 10)) + "px";
    }
    svg.addEventListener("pointermove", move); svg.addEventListener("pointerleave", hide); window.addEventListener("resize", hide);
    chartCleanup = () => {
      hide(); svg.removeEventListener("pointermove", move); svg.removeEventListener("pointerleave", hide); window.removeEventListener("resize", hide);
      cursor.remove(); tooltip.remove();
    };
  }
  renderChart = function () {
    syncHistoryRange();
    clearChartHover();
    const description = describeMetric(currentMetric);
    const points = (histories.get(currentMetric + ":" + currentRange) || []).slice(-288);
    const percent = description.plotUnit === "%";
    const values = points.map(point => point.value).filter(Number.isFinite);
    const highest = Math.max(...values, Number.isFinite(description.value) ? description.value : 0);
    const rawStep = highest > 0 ? highest * 1.08 / 4 : .25;
    const base = 10 ** Math.floor(Math.log10(rawStep));
    const top = percent ? 100 : Math.ceil(rawStep / base) * base * 4;
    text("#chartTitle", description.title); text("#chartMeaning", description.meaning);
    text("#chartValue", number(description.value, 2) + " " + description.unit);
    text("#chartLimit", (description.state ? description.state + " · " : "") + (Number.isFinite(description.percent) ? "当前占比 " + number(description.percent) + "% · " : "") + "量程 0–" + number(top, 2) + description.plotUnit);
    text("#chartLegend", description.title);
    text("#chartInsight", "采样于 " + date(description.at) + "。" + (description.state && description.state !== "有效" ? description.state + "，等待新样本。" : "") + (description.peak ? "空白表示桌面静止或采集不可用。将尖峰时间与事件记录对照。" : "空白表示缺少有效记录，零值按实际样本展示。"));
    text("#rangeSummary", ranges[currentRange].label + " · " + points.length + " 个历史点");
    const box = $("#chartContainer"); box.replaceChildren();
    if (!points.length) { box.append(node("p", "当前范围尚无该指标的历史记录", "live-empty")); return; }
    const svg = svgElement("svg", { viewBox: "0 0 620 200", role: "img", "aria-label": description.title + "真实趋势，单位" + description.plotUnit });
    [0, 1, 2, 3, 4].forEach(index => {
      const value = top * index / 4;
      svg.append(svgElement("path", { class: "grid", d: "M34 " + (168 - index * 37) + "H602" }));
      const label = svgElement("text", { x: 25, y: 171 - index * 37, "text-anchor": "end" }); label.textContent = number(value, 2); svg.append(label);
    });
    const hours = historyHours[currentRange];
    const end = Date.now(), start = end - hours * 3600000;
    const hoverPoints = [], segmentStarts = [];
    const maxGap = description.peak ? 2000 * Math.max(health?.sampleSeconds || 30, Math.ceil(hours * 3600 / 288)) : Infinity;
    let path = "", segment = false, lastPoint = null, lastStamp = null;
    points.forEach(point => {
      const stamp = Date.parse(point.timestamp);
      if (!Number.isFinite(stamp)) { segment = false; return; }
      const x = Math.max(34, Math.min(602, 34 + (stamp - start) / (end - start) * 568));
      const y = Number.isFinite(point.value) ? 168 - Math.max(0, Math.min(top, point.value)) / top * 148 : null;
      hoverPoints.push({ point, x, y });
      if (y === null) { segment = false; return; }
      if (lastStamp !== null && stamp - lastStamp > maxGap) segment = false;
      if (!segment && description.peak) segmentStarts.push([x, y]);
      path += (segment ? "L" : "M") + x.toFixed(2) + " " + y.toFixed(2) + " "; segment = true; lastPoint = [x, y]; lastStamp = stamp;
    });
    if (path) svg.append(svgElement("path", { class: "curve", d: path }));
    for (const [x, y] of segmentStarts) svg.append(svgElement("circle", { class: "end-dot", cx: x, cy: y, r: 2 }));
    if (lastPoint) svg.append(svgElement("circle", { class: "end-dot", cx: lastPoint[0], cy: lastPoint[1], r: 3.3 }));
    const startText = svgElement("text", { x: 34, y: 194 }), endText = svgElement("text", { x: 602, y: 194, "text-anchor": "end" });
    startText.textContent = hours === 1 ? new Date(start).toLocaleTimeString("zh-CN", { hour12: false, hour: "2-digit", minute: "2-digit" }) : new Date(start).toLocaleDateString("zh-CN");
    endText.textContent = "现在"; svg.append(startText, endText); box.append(svg);
    bindChartHover(svg, box, hoverPoints, description);
  };
  async function readHistory() {
    const metric = currentMetric, range = currentRange;
    const result = await request("history", { metric, hours: historyHours[range] });
    histories.set(metric + ":" + range, result?.points || []); renderChart();
  }
  function updateReadoutNumbers() {
    const sample = newestReadout();
    text(".resource[data-detail='cpu'] strong", percentText(sample?.cpuPercent));
    text("#liveMemoryPercent", percentText(sample?.memoryPercent));
    text("#liveMemoryBytes", number(sample?.memoryUsedGb) + " / " + number(sample?.memoryTotalGb) + " GB");
  }
  function applyLiveReadout(value) {
    if (!Number.isFinite(Date.parse(value?.timestamp)) || (liveReadout && Date.parse(value.timestamp) < Date.parse(liveReadout.timestamp))) return;
    liveReadout = value;
    if (document.hidden || $("#overview").hidden) return;
    updateReadoutNumbers();
    if (currentMetric === "cpu" || currentMetric === "physical") {
      const item = describeMetric(currentMetric);
      text("#chartValue", number(item.value, 2) + " " + item.unit);
      text("#chartLimit", "当前占比 " + percentText(item.percent) + " · 量程 0–100%");
    }
  }
  function applySnapshot(value) {
    latest = value;
    const rows = value?.processMetrics?.length ? value.processMetrics : value?.processes || [];
    processes.splice(0, processes.length, ...rows.map(item => ({ pid: item.pid, name: item.name || "未知进程",
      memory: item.commitGb, workingSet: item.memoryGb, cpu: item.cpuPercent, read: item.readMbS, write: item.writeMbS,
      io: Number.isFinite(item.readMbS) && Number.isFinite(item.writeMbS) ? item.readMbS + item.writeMbS : null,
      createdTicks: item.createdTicks, path: item.executablePath })));
    const percent = value?.commitPercent;
    const ratio = Number.isFinite(percent) ? Math.max(0, Math.min(100, percent)) : null;
    const stale = sampleState({ value: percent, sampledAt: value?.timestamp }) === "旧采样";
    const state = !Number.isFinite(percent) ? ["候", "等待采样"] : health?.paused ? ["候", "处置已暂停"] : stale ? ["候", "等待新样本"] : percent >= 85 ? ["慎", "提交水位偏高"] : ["安", "当前平稳"];
    text(".verdict-character", state[0]); text(".verdict h2", state[1]);
    text(".hero-description", value?.error || (health?.paused ? "自动处置已暂停，采样、告警与历史记录继续。" : "关注资源变化，处置结果会另行留档。"));
    const facts = all(".hero-facts strong");
    facts[0].textContent = number(value?.commitUsedGb) + " / " + number(value?.commitLimitGb) + " GB";
    const dial = $(".instrument-dial");
    dial.querySelectorAll(".ratio-value,.ratio-halo").forEach(arc => arc.setAttribute("stroke-dasharray", (ratio ?? 0) + " 100"));
    const inkRatio = ratio ?? 50;
    const core = dial.querySelector(".taiji-core");
    if (core && core.dataset.inkRatio !== String(inkRatio)) {
      core.innerHTML = balanceCoreMarkup(inkRatio);
      core.dataset.inkRatio = String(inkRatio);
    }
    const caption = dial.querySelector(".dial-caption strong"); if (caption) caption.textContent = ratio === null ? "—" : percentText(ratio);
    dial.setAttribute("aria-label", "提交内存占用 " + percentText(ratio) + (stale ? "，旧采样" : ""));
    dial.setAttribute("title", "提交 " + number(value?.commitUsedGb) + " / " + number(value?.commitLimitGb) + " GB，采样于 " + date(value?.timestamp));
    const showResource = (key, amount, note) => { text(".resource[data-detail='" + key + "'] strong", amount); text(".resource[data-detail='" + key + "'] span:last-child", note); };
    showResource("cpu", percentText(newestReadout()?.cpuPercent), "单核峰值 " + metricText("cpu_max_core"));
    updateReadoutNumbers();
    showResource("gpu", metricText("gpu_util"), metricText("gpu_temp") + " · 显存 " + metricText("gpu_mem_used"));
    const read = metric("io_read_mb_s")?.value, write = metric("io_write_mb_s")?.value;
    const throughput = Number.isFinite(read) && Number.isFinite(write) ? read + write : null;
    showResource("disk", number(throughput) + " " + (metric("io_read_mb_s")?.unit || "MB/s"), "盘温 " + metricText("disk_temp_c"));
    showResource("network", metricText("net_down_mb_s"), "上传 " + metricText("net_up_mb_s"));
    Object.assign(details, {
      waterline: { title: "提交内存", text: "当前 " + number(value?.commitUsedGb) + " / " + number(value?.commitLimitGb) + " GB，占 " + number(percent) + "%；物理内存 " + number(value?.memoryUsedGb) + " / " + number(value?.memoryTotalGb) + " GB。", note: "采样于 " + date(value?.timestamp) + "。单次高水位不能证明泄漏。" },
      cpu: { title: "处理器", text: "整机 CPU " + number(value?.cpuPercent) + "%，单核峰值 " + metricText("cpu_max_core") + "。", note: "整机占比按全部逻辑核心归一化。首次差分或采集失败时显示未知。" },
      gpu: { title: "显卡", text: "利用率 " + metricText("gpu_util") + "；温度 " + metricText("gpu_temp") + "；显存 " + metricText("gpu_mem_used") + " / " + metricText("gpu_mem_total") + "。", note: "设备：" + (metric("gpu_util")?.label || "未识别") + "。采样于 " + date(sampledAt(metric("gpu_util"))) },
      disk: { title: "磁盘", text: "读取 " + metricText("io_read_mb_s") + "；写入 " + metricText("io_write_mb_s") + "；空间已用 " + metricText("disk_used_percent") + "；剩余 " + metricText("disk_free_gb") + "；盘温 " + metricText("disk_temp_c") + "。", note: "温度取可读设备的最高值，缺少传感器数据时显示未知。" },
      network: { title: "网络", text: "下载 " + metricText("net_down_mb_s") + "；上传 " + metricText("net_up_mb_s") + "；网关 " + metricText("ping_gw_ms") + "；外网 " + metricText("ping_net_ms") + "；抖动 " + metricText("ping_jitter_ms") + "。", note: "零速率可以表示当前没有流量；延迟探测失败保留未知。" }
    });
    for (const button of all(".live-metric-strip [data-metric]")) {
      const key = button.dataset.metric;
      button.querySelector("strong").textContent = metricText(key);
      button.dataset.state = sampleState(metric(key));
      button.title = metricsByName[key][1] + "，采样于 " + date(sampledAt(metric(key)));
    }
    renderDeviceTemperatures();
    renderProcesses(); renderChart(); syncUnifiedMotion();
    status(value?.timestamp ? (stale ? "旧采样 " : "采样于 ") + date(value.timestamp) : "等待首次采样");
  }
  const metricStrip = node("section", undefined, "live-metric-strip");
  metricStrip.setAttribute("aria-label", "分页文件、网络延迟与磁盘容量");
  ["pagefile_used_gb", "ping_gw_ms", "ping_net_ms", "ping_jitter_ms", "disk_free_gb", "gpu_mem_used"].forEach(key => {
    const button = node("button"); button.dataset.metric = key;
    button.append(node("span", metricsByName[key][0]), node("strong", "—")); metricStrip.append(button);
  });
  $(".resource-strip").after(metricStrip);
  const devices = node("section", undefined, "live-device-sensors");
  devices.setAttribute("aria-label", "设备温度");
  $(".resource-strip").after(devices);
  function renderDeviceTemperatures() {
    const diskKeys = Object.keys(latest?.metrics || {}).filter(key => /^disk_\d+_temp_c$/.test(key))
      .sort((a, b) => Number(a.split("_")[1]) - Number(b.split("_")[1]));
    const gpu = metric("gpu_temp")?.label || metric("gpu_util")?.label || "显卡";
    const labels = [[gpu, "gpu_temp"], ...diskKeys.map(key => [metric(key)?.label || "磁盘 " + key.split("_")[1], key])];
    if (!diskKeys.length) labels.push(["磁盘", "disk_temp_c"]);
    devices.replaceChildren(...labels.map(([label, key]) => {
      const item = node("span", undefined, "live-device-temperature");
      item.append(node("span", label), node("strong", metricText(key)));
      return item;
    }));
  }
  const healthNode = node("p", "后台状态待同步", "live-runtime-health");
  healthNode.setAttribute("role", "status");
  const healthFailures = node("ul", undefined, "live-runtime-failures"); healthFailures.hidden = true;
  $(".navigation-row").after(healthNode, healthFailures);
  function applyHealth(value) {
    health = value;
    if (!value) { healthNode.textContent = "后台状态待同步"; return; }
    const failures = Array.isArray(value.failures) ? value.failures : value.failures && typeof value.failures === "object" ? Object.entries(value.failures).map(([key, failure]) => key + "：" + (failure?.message || failure?.error || failure)) : value.failures ? [String(value.failures)] : [];
    healthNode.textContent = "自动处置" + (value.paused ? "已暂停" : "未暂停") + "；临时静音" + (value.muted ? "生效" : "未启用") + "；采样周期 " + number(value.sampleSeconds, 0) + " 秒" + (failures.length ? "；有 " + failures.length + " 项异常" : "");
    healthNode.dataset.state = failures.length ? "error" : value.paused ? "paused" : "ok";
    healthFailures.replaceChildren(); healthFailures.hidden = !failures.length;
    failures.slice(0, 10).forEach(failure => healthFailures.append(node("li", typeof failure === "string" ? failure : failure.message || failure.error || "后台任务异常")));
  }
  const actionsSection = node("section", undefined, "live-actions");
  actionsSection.append(node("h3", "操作与待处置"));
  const actionsList = node("div", undefined, "live-actions-list"); actionsSection.append(actionsList);
  const actionStatus = node("p", undefined, "fine-print live-action-status"); actionStatus.setAttribute("role", "status"); actionsSection.append(actionStatus);
  $(".notice").after(actionsSection);
  function applyActions(value) {
    const rows = Array.isArray(value) ? value : value?.actions;
    actionsList.replaceChildren();
    if (!rows) { actionsList.append(node("p", "等待读取操作状态", "fine-print")); return; }
    rows.slice(0, 130).forEach(item => {
      const article = node("article", undefined, "live-action-item"), copy = node("div");
      copy.append(node("strong", item.title || item.action || "待处置"), node("p", item.text || item.target || "查看并确认后执行"));
      const button = node("button", "查看并确认", "text-button"); button.disabled = actionBusy;
      button.setAttribute("aria-label", (item.title || "待处置") + "，查看并确认");
      button.addEventListener("click", async () => {
        if (actionBusy || document.hidden) return;
        actionBusy = true; all(".live-actions button").forEach(control => { control.disabled = true; });
        actionStatus.textContent = "等待确认与执行结果";
        try {
          const result = await request("action", { metric: String(item.id) });
          const statuses = { killed: "进程已结束", trimmed: "工作集已整理", restarted: "服务已重启", throttled: "限流已应用", restored: "限流已还原", rejected: "动作已失效", ok: "执行完成", success: "执行完成", cancelled: "已取消", canceled: "已取消", refused: "未执行", denied: "未执行", skipped: "未执行", protected: "目标受保护", rate_limited: "操作冷却中", cooldown: "操作冷却中", not_found: "目标已退出", timeout: "操作超时", failed: "执行失败", error: "执行失败" };
          const parts = String(result?.status || "").split(":");
          const reasons = { protected_process: "目标受保护", not_whitelisted: "目标不在白名单", accounting_unavailable: "处置记账不可用", frequency_cap: "操作频次达到限制", process_gone: "目标已退出", commit_below_threshold: "提交内存已低于阈值", too_young: "进程运行时间不足", path_mismatch: "路径不符合白名单", pid_reused: "进程实例已变化", ws_below_threshold: "工作集已低于阈值", access_denied: "当前权限不足", accounting_failed: "处置结果记账失败", audit_failed: "操作审计记录失败", unknown_action: "该动作不可用", automatic_disabled: "自动处置未启用" };
          const resultText = statuses[result?.status] || reasons[parts[1]] || ({ blocked: "安全检查拒绝了操作", skipped: "本次未执行", degraded: "操作未完整完成" }[parts[0]]) || "操作已返回";
          actionStatus.textContent = resultText + (result?.detail ? "：" + result.detail : "");
          const state = await Promise.all([request("actions"), request("health"), request("events", { hours: eventHours })]);
          applyActions(state[0]); applyHealth(state[1]); applyEvents(state[2]);
        } catch (error) { actionStatus.textContent = "操作未完成：" + error.message; }
        finally { actionBusy = false; all(".live-actions button").forEach(control => { control.disabled = false; }); }
      });
      article.append(copy, button); actionsList.append(article);
    });
    if (!rows.length) actionsList.append(node("p", "当前没有待处置项目", "fine-print"));
  }
  const eventRanges = node("div", undefined, "live-event-ranges"); eventRanges.setAttribute("role", "group"); eventRanges.setAttribute("aria-label", "事件时间范围");
  [[24, "24 小时"], [168, "7 天"], [720, "30 天"]].forEach(([hours, label]) => {
    const button = node("button", label, "text-button"); button.dataset.eventHours = hours; button.setAttribute("aria-pressed", String(hours === eventHours));
    button.addEventListener("click", async () => {
      eventHours = hours; markButtons("[data-event-hours]", "eventHours", String(hours));
      try { applyEvents(await request("events", { hours })); } catch (error) { status("事件读取失败：" + error.message); }
    }); eventRanges.append(button);
  });
  $(".event-toolbar").prepend(eventRanges);
  const errorFilter = node("button", "异常"); errorFilter.dataset.filter = "error"; errorFilter.setAttribute("aria-pressed", "false");
  $(".event-toolbar [data-filter]").parentElement.append(errorFilter);
  function applyEvents(value) {
    events.splice(0, events.length, ...(value?.events || [])); renderEvents($("[data-filter].active")?.dataset.filter || "all");
    text(".tab-count", String(events.length)); text(".notes .section-subtitle", "提醒、操作与结果分别留档");
    const notice = $(".notice"); notice.replaceChildren();
    if (events.length) { notice.append(node("time", date(events[0].timestamp)), node("h3", events[0].title), node("p", events[0].text)); }
    else notice.append(node("h3", "暂无观察事件"), node("p", "新版开始运行后在此积累记录。"));
    $(".timeline").replaceChildren(...events.slice(1, 4).map(event => {
      const row = node("li");
      row.append(node("time", date(event.timestamp)), node("strong", event.title));
      return row;
    }));
  }
  const ledger = $(".ledger"), quotaSection = node("section", undefined, "panel live-quota");
  const usageSummary = $("#usage .usage-summary"), usageLayout = $("#usage .usage-layout");
  quotaSection.append(node("h2", "套餐额度与重置")); usageLayout.before(quotaSection);
  const toolNames = { codex: "Codex", kimi: "Kimi Code", zcode: "GLM（ZCode）", deepseek: "DeepSeek" };
  let selectedTool = "codex";
  const toolPicker = node("div", undefined, "usage-tool-picker");
  toolPicker.setAttribute("role", "group");
  toolPicker.setAttribute("aria-label", "AI 工具");
  for (const [tool, label] of Object.entries(toolNames)) {
    const button = node("button", label, "text-button");
    button.dataset.usageTool = tool;
    button.addEventListener("click", () => { selectedTool = tool; applyUsage(usage); });
    toolPicker.append(button);
  }
  const refreshButton = node("button", "刷新本机记录", "text-button live-refresh");
  const usageStatus = node("p", "等待读取本机记录", "fine-print live-usage-status");
  usageStatus.setAttribute("role", "status");
  const accountButton = node("button", "更新账户用量与额度", "text-button live-refresh");
  const providerSettings = node("button", "账户来源设置", "text-button live-refresh");
  providerSettings.addEventListener("click", () => request("show-ai-settings").catch(error => { usageStatus.textContent = error.message; }));
  const usageHeading = $("#usage .page-title>div");
  usageHeading.append(toolPicker);
  if (nativeBridge) usageHeading.append(refreshButton, accountButton, providerSettings);
  usageHeading.append(usageStatus);
  function sessionTable(rows, metadata) {
    const table = node("table"), head = node("thead"), header = node("tr"), body = node("tbody");
    ["会话", metadata ? "累计快照" : "已采集用量", metadata ? "来源" : "模型", "时间"].forEach(value => header.append(node("th", value)));
    head.append(header); table.append(head, body);
    rows.forEach(item => {
      const row = node("tr");
      [String(item.session_id || "未知").slice(0, 16), number(item.total_tokens, 0), metadata ? "状态库快照" : item.model || "未知", date(metadata ? item.observed_at : item.last_usage_at)].forEach(value => row.append(node("td", value)));
      body.append(row);
    });
    if (!rows.length) { const row = node("tr"), cell = node("td", "尚无可用记录"); cell.colSpan = 4; row.append(cell); body.append(row); }
    const wrap = node("div", undefined, "table-wrap live-session-table"); wrap.append(table); return wrap;
  }
  function countdown() {
    quotaRows.forEach(item => {
      const remaining = Math.max(0, Math.ceil((Date.parse(item.reset) - Date.now()) / 1000));
      if (!Number.isFinite(remaining)) { item.target.textContent = "重置时间未知"; return; }
      if (!remaining) {
        item.target.textContent = "已到记录的重置时间 · 待新快照确认";
        item.summary.textContent = item.label + " · 剩余未知";
        return;
      }
      const days = Math.floor(remaining / 86400), hours = Math.floor(remaining % 86400 / 3600), minutes = Math.floor(remaining % 3600 / 60), seconds = remaining % 60;
      item.target.textContent = "还需 " + (days ? days + "天 " : "") + hours + "小时 " + minutes + "分 " + seconds + "秒";
    });
  }
  function applyUsage(value) {
    const wrapper = value && Object.hasOwn(value, "report") ? value : null;
    usage = value; quotaRows = [];
    const walletOnly = selectedTool === "deepseek";
    refreshButton.hidden = usageSummary.hidden = usageLayout.hidden = walletOnly;
    accountButton.hidden = selectedTool !== "codex";
    providerSettings.hidden = selectedTool === "codex";
    for (const button of toolPicker.querySelectorAll("button")) button.setAttribute("aria-pressed", String(button.dataset.usageTool === selectedTool));
    if (selectedTool !== "codex") { renderProviderUsage(wrapper?.provider_reports?.find(provider => provider.tool === selectedTool)); return; }
    value = wrapper ? wrapper.report : value;
    const totals = value?.totals;
    const accountUsage = wrapper?.account_usage, accountQuota = wrapper?.account_quota;
    text(".ai-summary h2", "Codex 已采集用量"); text(".ai-summary p", number(totals?.total_tokens, 0) + " tokens · " + number(value?.metadata_session_count, 0) + " 个会话快照");
    text(".ai-price strong", "—"); text(".ai-price>span", "未估算费用");
    text("#usage .page-title h2", "Codex 会话账本"); text("#usage .page-title>div>p:nth-of-type(2)", "请求明细与会话累计快照分别展示，不相加。");
    const summary = all(".usage-summary>div");
    ["账户已消耗 Token", "本机请求累计 Token", "本机会话快照"].forEach((label, index) => summary[index].querySelector("span").textContent = label);
    [number(accountUsage?.lifetime_tokens, 0), number(totals?.total_tokens, 0), number(value?.metadata_session_count, 0)].forEach((amount, index) => summary[index].querySelector("strong").textContent = amount);
    ledger.replaceChildren(node("h2", "请求明细汇总"), sessionTable(value?.sessions || [], false), node("p", "每会话包含回放/继承历史，总览按唯一事件记账。最多展示100行。", "fine-print"), node("h2", "桌面会话累计快照"), sessionTable(value?.metadata_sessions || [], true), node("p", "累计快照没有输入输出拆分，不用于今日账单；不与请求明细相加。", "fine-print"));
    const breakdown = $(".token-breakdown"); breakdown.replaceChildren(node("h2", "已采集 Token 构成"));
    const dl = node("dl");
    [["输入（包含缓存）", totals?.input_tokens], ["其中缓存读取", totals?.cached_input_tokens], ["输出（包含推理）", totals?.output_tokens], ["其中推理", totals?.reasoning_tokens]].forEach(([name, amount]) => { const pair = node("div"); pair.append(node("dt", name), node("dd", number(amount, 0))); dl.append(pair); });
    breakdown.append(dl, node("p", "本机缓存与推理为子集。账户累计与本机请求累计分别统计。", "fine-print"));
    renderAccountUsage(breakdown, accountUsage);
    renderQuota(accountQuota, value?.quota);
    usageStatus.textContent = value?.generated_at ? "汇总生成于 " + date(value.generated_at) + " · " + (value.quality?.pending_sources ? "部分日志仍在分批读取" : "本地来源，覆盖范围有限") : "统计尚未就绪";
    if (wrapper?.last_success_at) usageStatus.textContent += " · 最近采集 " + date(wrapper.last_success_at);
    if (wrapper?.source_state === "not_observed") usageStatus.textContent = "尚未观察到 Codex 本地来源";
    if (wrapper?.metadata_pending) usageStatus.textContent += " · 会话快照仍在分批读取";
    if (wrapper?.scanning) usageStatus.textContent += " · 正在扫描本地来源";
    if (wrapper?.error) usageStatus.textContent += " · " + wrapper.error;
    if (value?.quality?.sources_without_token_usage) usageStatus.textContent += " · " + value.quality.sources_without_token_usage + " 个来源尚未观察到token明细";
    countdown();
  }
  function renderAccountUsage(container, account) {
    container.append(node("h3", "账户近期用量"));
    const days = account?.daily_usage_buckets;
    if (!Array.isArray(days) || !days.length) {
      container.append(node("p", "账户日用量未知。", "fine-print"));
    } else {
      const rows = node("dl", undefined, "live-account-days");
      [...days].sort((a, b) => String(b.start_date).localeCompare(String(a.start_date))).slice(0, 7).forEach(day => {
        const row = node("div");
        row.append(node("dt", day.start_date || "未知日期"), node("dd", number(day.tokens, 0)));
        rows.append(row);
      });
      container.append(rows);
    }
    if (account?.fetched_at) container.append(node("p", "Codex 官方接口 · 更新于 " + date(account.fetched_at), "fine-print"));
    if (account?.state === "stale") container.append(node("p", "当前显示上次成功读取的账户用量。", "fine-print"));
  }
  function quotaWindow(group, window) {
    const row = node("div", undefined, "live-quota-window"), clock = node("p", "等待计算", "live-countdown");
    const used = window.used_percent;
    const remaining = window.remaining_percent;
    const knownNames = { limit5h: "5 小时窗口", limit7d: "7 天窗口", monthTotal: "本月总额度", monthCode: "本月代码额度" };
    const duration = knownNames[window.name] || (window.window_minutes === 10080 ? "7 天窗口" : window.window_minutes === 300 ? "5 小时窗口" : window.window_minutes ? number(window.window_minutes, 0) + " 分钟窗口" : window.name || "额度窗口");
    const label = duration + " · 已用 " + percentText(used);
    const summary = node("strong", label + " · 剩余 " + percentText(remaining));
    row.append(summary);
    if (Number.isFinite(window.used_units) || Number.isFinite(window.total_units)) {
      const unit = window.unit ? " " + window.unit : "";
      row.append(node("p", "已用 " + number(window.used_units, 2) + " / 总额 " + number(window.total_units, 2) + unit));
    }
    row.append(node("p", "重置时间 " + date(window.resets_at)), clock);
    quotaRows.push({ target: clock, reset: window.resets_at, label, summary });
    group.append(row);
  }
  function renderQuota(account, snapshots) {
    quotaSection.replaceChildren(node("h2", "套餐额度与重置"));
    const official = account?.windows;
    if (Array.isArray(official) && official.some(limit => limit.primary || limit.secondary)) {
      const plan = account.plan_type ? String(account.plan_type) : "未知";
      quotaSection.append(node("p", "Codex 官方账户 · 套餐 " + plan + " · 更新于 " + date(account.fetched_at), "fine-print"));
      if (account.state === "stale") quotaSection.append(node("p", "当前显示上次成功读取的额度。", "fine-print"));
      for (const limit of official) {
        const group = node("article", undefined, "live-quota-group");
        group.append(node("h3", limit.limit_name || limit.limit_id || "Codex"));
        [limit.primary, limit.secondary].filter(Boolean).forEach(window => quotaWindow(group, window));
        quotaSection.append(group);
      }
      return;
    }
    const unavailable = { not_checked: nativeBridge ? "点击「更新账户用量与额度」读取 Codex 套餐。" : "请在桌面窗口读取 Codex 套餐。", cli_unavailable: "未找到 Codex CLI。",
      not_signed_in: "请先在 Codex 登录 ChatGPT 账户。", auth_unsupported: "当前认证方式没有 ChatGPT 套餐数据。",
      unsupported: "当前 Codex 接口未提供套餐额度。", cli_unsupported: "当前 Codex CLI 不支持账户接口。",
      timeout: "读取账户额度超时。", unavailable: "官方账户额度暂不可用。" };
    quotaSection.append(node("p", account?.refreshing ? "正在读取账户额度…" : unavailable[account?.state] || unavailable.unavailable, "fine-print"));
    if (!Array.isArray(snapshots) || !snapshots.length) {
      quotaSection.append(node("p", "本机也尚无额度快照。重置时间未知。", "fine-print"));
      return;
    }
    for (const snapshot of snapshots.slice(0, 5)) {
      const group = node("article", undefined, "live-quota-group");
      group.append(node("h3", snapshot.limit_id || "Codex"), node("p", "本机日志快照 · " + date(snapshot.observed_at) + " · 账户未确认", "fine-print"));
      [snapshot.primary, snapshot.secondary].filter(Boolean).forEach(window => quotaWindow(group, window));
      quotaSection.append(group);
    }
  }
  function providerState(state, hasData) {
    const labels = {
      not_configured: "尚未配置账户来源", not_checked: "尚未读取账户数据", not_observed: "尚未观察到本机来源",
      stale: "显示上次成功读取的数据", login_required: "账户需要登录", not_signed_in: "账户需要登录",
      auth_required: "需要验证账户来源", unsupported: "该来源未提供账户额度", unsupported_auth: "认证方式不支持额度读取",
      scanning: "正在补录本机历史", partial: "部分记录暂不可读", source_missing: "本机来源当前不可用", local_sources: "已读取本机记录",
      cli_unavailable: "未找到 Kimi Code CLI", cli_unsupported: "当前 Kimi CLI 不支持账户接口", auth_rejected: "Key 未通过账户接口验证",
      auth_expired: "登录已过期", duplicate_account: "两个来源登录了同一账户", duplicate_profile: "此账户来源目录已被使用",
      rate_limited: "达到读取频率限制", timeout: "读取超时", unavailable: "数据暂不可用", error: "读取失败"
    };
    return labels[state] || (hasData ? "已读取" : "暂未获取数据");
  }
  function renderWallet(card, balances, available) {
    for (const balance of balances) {
      const row = node("div", undefined, "live-quota-window");
      row.append(node("strong", balance.currency + " 总余额 " + number(balance.total_balance, 8)),
        node("p", "充值余额 " + number(balance.topped_up_balance, 8) + " · 赠金余额 " + number(balance.granted_balance, 8)));
      card.append(row);
    }
    if (available === false) card.append(node("p", "余额不足以调用 API。", "fine-print"));
  }
  function renderProviderUsage(provider) {
    const label = toolNames[selectedTool], report = provider?.report, totals = report?.totals;
    const accounts = Array.isArray(provider?.accounts) ? provider.accounts : [];
    const walletOnly = selectedTool === "deepseek";
    text("#usage .page-title h2", label + (walletOnly ? " API 钱包" : " 会话账本"));
    text("#usage .page-title>div>p:nth-of-type(2)", walletOnly ? "按币种查看开放平台余额。"
      : "账户数据按来源展示。共享本机记录列在待归属会话中。");
    if (!walletOnly) {
      const summary = all(".usage-summary>div");
      const count = report?.session_count ?? report?.sessions?.length;
      ["待归属 Token", Number.isFinite(report?.session_count) ? "待归属会话" : "已列出会话记录", "账户来源"].forEach((item, index) => summary[index].querySelector("span").textContent = item);
      [number(totals?.total_tokens, 0), number(count, 0), provider ? number(accounts.length, 0) : "—"].forEach((amount, index) => summary[index].querySelector("strong").textContent = amount);
      ledger.replaceChildren(node("h2", "本机待归属会话"), sessionTable(report?.sessions || [], false),
        node("p", "会话没有可确认的账户归属。最多显示 100 行。", "fine-print"));
      const breakdown = $(".token-breakdown");
      breakdown.replaceChildren(node("h2", "待归属 Token 构成"));
      const dl = node("dl");
      [["输入", totals?.input_tokens], ["其中缓存读取", totals?.cached_input_tokens], ["输出", totals?.output_tokens], ["其中推理", totals?.reasoning_tokens]].forEach(([name, amount]) => {
        const row = node("div"); row.append(node("dt", name), node("dd", number(amount, 0))); dl.append(row);
      });
      breakdown.append(dl);
    }
    quotaSection.replaceChildren(node("h2", walletOnly ? "钱包余额" : label + " 账户额度"));
    const cards = node("div", undefined, "provider-account-grid");
    for (const account of accounts) {
      const card = node("article", undefined, "provider-account");
      const head = node("div", undefined, "provider-account-head");
      head.append(node("h3", account.label || "账户"));
      if (nativeBridge && account.id) {
        const actionLabel = walletOnly ? "余额" : "额度";
        const button = node("button", "更新" + actionLabel, "text-button");
        button.dataset.providerRefresh = "true";
        button.disabled = usageBusy;
        button.setAttribute("aria-label", "更新 " + (account.label || "账户") + " 的" + actionLabel);
        button.addEventListener("click", () => readUsage("refresh-provider-account", { metric: account.id }));
        head.append(button);
      }
      const windows = Array.isArray(account.windows) ? account.windows : [];
      const balances = Array.isArray(account.balances) ? account.balances : [];
      const state = providerState(account.state, windows.length > 0 || balances.length > 0 || Number.isFinite(account.account_tokens));
      card.append(head, node("p", [state, account.plan_type, ({ kimi_local_api: "Kimi 本地接口", glm_official_plan: "GLM 官方套餐", deepseek_official_balance: "DeepSeek 官方接口" })[account.source], account.fetched_at && "更新于 " + date(account.fetched_at)].filter(Boolean).join(" · "), "fine-print"));
      if (walletOnly) {
        renderWallet(card, balances, account.is_available);
        cards.append(card);
        continue;
      }
      card.append(node("p", "账户累计 " + (Number.isFinite(account.account_tokens) ? number(account.account_tokens, 0) + " Token" : "未知"), "fine-print"));
      windows.forEach(window => quotaWindow(card, window));
      if (!windows.length) card.append(node("p", "剩余额度和重置时间未知。", "fine-print"));
      if (account.local_report) {
        const detail = node("details", undefined, "provider-local-sessions");
        detail.append(node("summary", "此来源本机累计 " + number(account.local_report.totals?.total_tokens, 0) + " Token"), sessionTable(account.local_report.sessions || [], false));
        card.append(detail);
      } else if (account.local_state) card.append(node("p", "本机来源 · " + providerState(account.local_state, false), "fine-print"));
      cards.append(card);
    }
    if (accounts.length) quotaSection.append(cards);
    else quotaSection.append(node("p", "尚无已配置账户来源。", "fine-print"));
    usageStatus.textContent = walletOnly ? "" : providerState(provider?.state, Boolean(report));
    if (report?.generated_at) usageStatus.textContent += " · 本机汇总于 " + date(report.generated_at);
    countdown();
  }
  function setUsageBusy(value) {
    usageBusy = value;
    refreshButton.disabled = accountButton.disabled = value;
    all("[data-provider-refresh]").forEach(button => button.disabled = value);
  }
  async function readUsage(api = "usage", parameters = {}) {
    if (usageBusy) return;
    setUsageBusy(true);
    if (api === "refresh-codex-account" || api === "refresh-provider-account") usageStatus.textContent = parameters.metric === "deepseek"
      ? "正在查询 DeepSeek API 余额…" : "正在读取账户用量与额度…";
    try { const value = await request(api, parameters); applyUsage(value); lastUsageRead = Date.now(); }
    catch (error) { usageStatus.textContent = "统计读取未完成：" + error.message; }
    finally { setUsageBusy(false); }
  }
  refreshButton.addEventListener("click", () => readUsage("refresh-usage"));
  accountButton.addEventListener("click", () => readUsage("refresh-codex-account"));
  async function refresh() {
    if (!token || document.hidden || refreshing) return;
    refreshing = true;
    const hours = eventHours;
    try {
      const values = await Promise.allSettled([request("snapshot"), request("events", { hours }), request("actions"), request("health")]);
      if (document.hidden) return;
      if (values[3].status === "fulfilled") applyHealth(values[3].value);
      if (values[1].status === "fulfilled" && hours === eventHours) applyEvents(values[1].value);
      if (values[2].status === "fulfilled") applyActions(values[2].value);
      if (values[0].status === "fulfilled") applySnapshot(values[0].value);
      const failed = values.filter(value => value.status === "rejected");
      if (failed.length) status("部分读取未完成：" + failed.map(value => value.reason.message).join("；"));
      await readHistory();
      if (!lastUsageRead || Date.now() - lastUsageRead >= 60000) await readUsage();
    } catch (error) { if (!document.hidden) status("读取暂不可用：" + error.message + (latest?.timestamp ? " · 保留 " + date(latest.timestamp) + " 样本" : "")); }
    finally { refreshing = false; }
  }
  function timers() {
    clearInterval(refreshTimer); clearInterval(quotaTimer); refreshTimer = quotaTimer = null;
    if (document.hidden || !token) return;
    refreshTimer = setInterval(refresh, 30000);
    quotaTimer = setInterval(() => { if (!$("#usage").hidden) countdown(); }, 1000);
  }
  document.addEventListener("click", event => {
    const button = event.target.closest("button");
    if (button?.dataset.metric || button?.dataset.range) { moreMetrics.value = Object.hasOwn(metricsByName, currentMetric) ? currentMetric : ""; readHistory().catch(error => status("历史读取失败：" + error.message)); }
    if (button?.dataset.page || button?.dataset.go) {
      if ((button.dataset.page || button.dataset.go) === "overview") { updateReadoutNumbers(); renderChart(); } else clearChartHover();
    }
    if (button?.dataset.page === "usage" || button?.dataset.go === "usage") { countdown(); if (!usage) readUsage(); }
  });
  document.addEventListener("visibilitychange", () => {
    timers();
    if (document.hidden) { clearChartHover(); cancelPending(); } else { updateReadoutNumbers(); renderChart(); refresh(); }
  });
  window.addEventListener("pagehide", () => { clearChartHover(); clearInterval(refreshTimer); clearInterval(quotaTimer); cancelPending(); });
  bridge?.addEventListener("message", event => {
    const message = event.data;
    if (message?.kind === "init") {
      token = message.token;
      if (nativeBridge) {
        setWindowMode(message.windowState);
        root.dataset.nativeDrag = String(message.nativeDrag === true);
        if (windowControls) windowControls.hidden = false;
      }
      sentTheme = null; syncWindowTheme(); timers(); refresh(); return;
    }
    if (nativeBridge && message?.kind === "window-state") { setWindowMode(message.state); return; }
    if (nativeBridge && message?.kind === "live-readout") { applyLiveReadout(message.value); return; }
    const item = pending.get(message?.id);
    if (!item) return;
    clearTimeout(item.timer); pending.delete(message.id);
    if (message.error) item.reject(new Error(message.error)); else item.resolve(message.result);
  });
  if(window.chrome?.webview){
    const settingsButton=node("button","设置","text-button");settingsButton.title="打开原生设置（Ctrl+,）；主窗口Ctrl+Q退出新版";
    settingsButton.addEventListener("click",()=>request("show-settings").catch(error=>status(error.message)));$(".app-footer").append(settingsButton);
  }
  root.dataset.live = "true";
  if (nativeBridge) { root.dataset.nativeChrome = "true"; initWindowControls(); }
  text(".brand p", "本地系统守护"); text(".device-kind", "Windows · 本机运行");
  text(".ranking .section-subtitle", "单进程排行 · 前五项");
  text(".app-footer>span:first-child", "烛龙 · 本机监控与受限处置");
  text("#events .page-title p:last-of-type", "按范围查看资源提醒、后台异常和操作结果。");
  text("#events .page-title h2", "事件与处置记录");
  text("#events .event-toolbar h3", "最近记录");
  $(".resource-strip").querySelectorAll("svg").forEach(element => element.remove());
  text("#detailDialog .overline", "观察详情");
  processes.splice(0); events.splice(0); applyUsage(null); applyEvents(null); applyActions(null); applyHealth(null); applySnapshot(null);
  status(bridge ? "正在连接新版数据" : "请在烛龙桌面程序中打开");
  bridge?.postMessage({ kind: "ready" });
})();

"use strict";
// 独立桌面数据层：只通过受限宿主桥读取新版数据，不连接旧面板。
(() => {
  const bridge = window.chrome?.webview;
  const pending = new Map(), histories = new Map();
  let token = null, sequence = 0, refreshTimer = null, quotaTimer = null;
  let latest = null, usage = null, lastUsageRead = 0, refreshing = false;
  let quotaRows = [];
  const number = (value, digits = 1) => Number.isFinite(value) ? value.toLocaleString("zh-CN", { maximumFractionDigits: digits }) : "—";
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
      const timer = setTimeout(() => { pending.delete(id); reject(new Error("读取超时")); }, 20000);
      pending.set(id, { resolve, reject, timer });
      bridge.postMessage({ kind: "query", id, token, api, ...parameters });
    });
  }
  function cancelPending() {
    pending.forEach(item => { clearTimeout(item.timer); item.reject(new Error("页面已暂停")); });
    pending.clear();
  }
  function status(value) { text(".sample-time", value); }
  const originalProcesses = renderProcesses;
  $("#processSearch").removeEventListener("input", originalProcesses);
  renderProcesses = function () {
    const search = $("#processSearch").value.trim().toLowerCase();
    const rows = processes.filter(item => item.name.toLowerCase().includes(search))
      .sort((a, b) => (Number.isFinite(b[currentRank]) ? b[currentRank] : -1) - (Number.isFinite(a[currentRank]) ? a[currentRank] : -1));
    const list = $("#processList");
    list.replaceChildren();
    rows.forEach(item => {
      const button = node("button", undefined, "process");
      button.dataset.process = String(processes.indexOf(item));
      const line = node("span", undefined, "process-top");
      line.append(node("span", item.name.slice(0, 1), "process-icon"), node("span", item.name, "process-name"));
      const amount = node("span", number(item[currentRank], currentRank === "memory" ? 2 : 1), "process-value");
      amount.append(node("small", currentRank === "memory" ? "GB" : "%"));
      line.append(amount); button.append(line);
      const meter = node("span", undefined, "process-meter"), fill = node("i");
      const max = Math.max(...rows.map(row => Number.isFinite(row[currentRank]) ? row[currentRank] : 0), 1);
      fill.style.width = (Number.isFinite(item[currentRank]) ? item[currentRank] / max * 100 : 0) + "%";
      meter.append(fill); button.append(meter); list.append(button);
    });
    if (!rows.length) list.append(node("p", latest ? "当前没有匹配进程" : "等待首次采集", "fine-print live-empty"));
    text("#rankUnit", currentRank === "memory" ? "口径：单进程工作集，GB" : "CPU 排名尚未接入，未知显示 —");
  };
  $("#processSearch").addEventListener("input", renderProcesses);
  document.addEventListener("click", event => {
    const button = event.target.closest("[data-process]");
    if (!button) return;
    event.stopImmediatePropagation();
    const item = processes[Number(button.dataset.process)];
    if (item) showDetail({ title: item.name, text: "PID " + item.pid + " · 工作集 " + number(item.memory, 2) + " GB · CPU " + number(item.cpu) + "%", note: "新版只读观察，不执行结束进程。CPU 未接入时显示未知。" });
  }, true);
  renderEvents = function (filter = "all") {
    const list = $("#eventList"); list.replaceChildren();
    events.filter(item => filter === "all" || item.level === filter).forEach(item => {
      const article = node("article", undefined, "event-item");
      article.append(node("time", date(item.timestamp)), node("span", item.level === "warning" ? "慎" : "记", "small-seal"));
      const copy = node("div"); copy.append(node("h3", item.title), node("p", item.text)); article.append(copy); list.append(article);
    });
    if (!list.children.length) list.append(node("p", "当前范围没有观察事件", "live-empty"));
  };
  function svgElement(tag, attributes) {
    const result = document.createElementNS("http://www.w3.org/2000/svg", tag);
    Object.entries(attributes).forEach(([key, value]) => result.setAttribute(key, String(value)));
    return result;
  }
  renderChart = function () {
    const description = {
      commit: ["提交内存", "系统承诺提供的内存，与物理内存分开观察", latest?.commitPercent, latest?.commitUsedGb, "GB"],
      physical: ["物理内存", "当前驻留物理内存的占用", latest?.memoryPercent, latest?.memoryUsedGb, "GB"],
      cpu: ["处理器", "整机 CPU 占用，首个差分样本前保持未知", latest?.cpuPercent, latest?.cpuPercent, "%"]
    }[currentMetric];
    text("#chartTitle", description[0]); text("#chartMeaning", description[1]);
    text("#chartValue", number(description[3]) + " " + description[4]);
    text("#chartLimit", "当前占比 " + number(description[2]) + "% · 量程 0–100%");
    text("#chartLegend", description[0]); text("#chartInsight", "仅展示新版开始记录后的真实样本；空白表示尚无数据。");
    const points = histories.get(currentMetric + ":" + currentRange) || [];
    text("#rangeSummary", ranges[currentRange].label + " · " + points.length + " 个真实样本");
    const box = $("#chartContainer"); box.replaceChildren();
    if (!points.length) { box.append(node("p", "等待采样积累历史", "live-empty")); return; }
    const svg = svgElement("svg", { viewBox: "0 0 620 200", role: "img", "aria-label": description[0] + "真实占比趋势，0到100%" });
    [0, 25, 50, 75, 100].forEach(value => {
      svg.append(svgElement("path", { class: "grid", d: "M34 " + (168 - value * 1.48) + "H602" }));
      const label = svgElement("text", { x: 25, y: 171 - value * 1.48, "text-anchor": "end" }); label.textContent = value; svg.append(label);
    });
    const hours = { "24h": 24, "7d": 168, "30d": 720 }[currentRange];
    const end = Date.now(), start = end - hours * 3600000;
    let path = "", segment = false, lastPoint = null;
    points.forEach(point => {
      const stamp = Date.parse(point.timestamp);
      if (!Number.isFinite(point.value) || !Number.isFinite(stamp)) { segment = false; return; }
      const x = Math.max(34, Math.min(602, 34 + (stamp - start) / (end - start) * 568));
      const y = 168 - Math.max(0, Math.min(100, point.value)) * 1.48;
      path += (segment ? "L" : "M") + x.toFixed(2) + " " + y.toFixed(2) + " "; segment = true; lastPoint = [x, y];
    });
    if (path) svg.append(svgElement("path", { class: "curve", d: path }));
    if (lastPoint) svg.append(svgElement("circle", { class: "end-dot", cx: lastPoint[0], cy: lastPoint[1], r: 3.3 }));
    const startText = svgElement("text", { x: 34, y: 194 }), endText = svgElement("text", { x: 602, y: 194, "text-anchor": "end" });
    startText.textContent = new Date(start).toLocaleDateString("zh-CN"); endText.textContent = "现在"; svg.append(startText, endText); box.append(svg);
  };
  async function readHistory() {
    const metric = currentMetric, range = currentRange;
    const result = await request("history", { metric, hours: { "24h": 24, "7d": 168, "30d": 720 }[range] });
    histories.set(metric + ":" + range, result?.points || []); renderChart();
  }
  function applySnapshot(value) {
    latest = value;
    processes.splice(0, processes.length, ...(value?.processes || []).map(item => ({ pid: item.pid, name: item.name, memory: item.memoryGb, cpu: item.cpuPercent, note: "新版只读采样" })));
    const percent = value?.commitPercent;
    const state = !Number.isFinite(percent) ? ["候", "等待采样"] : percent >= 85 ? ["慎", "提交水位偏高"] : ["安", "正在观察"];
    text(".verdict-character", state[0]); text(".verdict h2", state[1]); text(".hero-description", value?.error || "30 秒采样一次，数据与旧版独立保存。");
    const facts = all(".hero-facts strong");
    facts[0].textContent = number(value?.commitUsedGb) + " / " + number(value?.commitLimitGb) + " GB";
    facts[1].textContent = events.length + " 条新版记录";
    const dial = $(".instrument-dial");
    dial.querySelectorAll(".ratio-value,.ratio-halo").forEach(arc => arc.setAttribute("stroke-dasharray", (Number.isFinite(percent) ? percent : 0) + " 100"));
    const caption = dial.querySelector(".dial-caption strong"); if (caption) caption.textContent = number(percent) + "%";
    dial.setAttribute("aria-label", "提交内存占用 " + number(percent) + "%"); dial.setAttribute("title", "提交 " + number(value?.commitUsedGb) + " / " + number(value?.commitLimitGb) + " GB");
    text(".resource[data-detail='cpu'] strong", number(value?.cpuPercent) + "%");
    text(".resource[data-detail='cpu'] span:last-child", "整机 CPU，首次样本前未知");
    ["gpu", "disk", "network"].forEach(key => { text(".resource[data-detail='" + key + "'] strong", "—"); text(".resource[data-detail='" + key + "'] span:last-child", "尚未接入"); });
    Object.assign(details, {
      waterline: { title: "提交内存", text: "当前 " + number(value?.commitUsedGb) + " / " + number(value?.commitLimitGb) + " GB，占 " + number(percent) + "%", note: "真实单次样本不能证明泄漏；本版不执行进程处置。" },
      cpu: { title: "处理器", text: "整机 CPU " + number(value?.cpuPercent) + "%", note: "差分首次样本没有百分比；单核与进程CPU尚未接入。" }
    });
    ["gpu", "disk", "network"].forEach(key => { details[key] = { title: { gpu: "显卡", disk: "磁盘", network: "网络" }[key], text: "此指标尚未接入新版。", note: "缺失值保持未知，不展示排版样本。" }; });
    renderProcesses(); renderChart(); syncUnifiedMotion();
    status(value?.timestamp ? "采样于 " + date(value.timestamp) : "等待新版首次采样");
  }
  function applyEvents(value) {
    events.splice(0, events.length, ...(value?.events || [])); renderEvents($("[data-filter].active")?.dataset.filter || "all");
    text(".tab-count", String(events.length)); text(".notes .section-subtitle", "新版独立记录，提醒与实际处置分开");
    const notice = $(".notice"); notice.replaceChildren();
    if (events.length) { notice.append(node("time", date(events[0].timestamp)), node("h3", events[0].title), node("p", events[0].text)); }
    else notice.append(node("h3", "暂无观察事件"), node("p", "新版开始运行后在此积累记录。"));
    $(".timeline").replaceChildren();
    if (latest) all(".hero-facts strong")[1].textContent = events.length + " 条新版记录";
  }
  const ledger = $(".ledger"), quotaSection = node("section", undefined, "panel live-quota");
  quotaSection.append(node("h2", "套餐额度与重置")); $("#usage .usage-layout").after(quotaSection);
  const refreshButton = node("button", "刷新 Codex 统计", "text-button live-refresh");
  const usageStatus = node("p", "等待读取本地 Codex 统计", "fine-print live-usage-status");
  $("#usage .page-title>div").append(refreshButton, usageStatus);
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
      if (!remaining) { item.target.textContent = "已到记录的重置时间 · 待新快照确认"; return; }
      const days = Math.floor(remaining / 86400), hours = Math.floor(remaining % 86400 / 3600), minutes = Math.floor(remaining % 3600 / 60), seconds = remaining % 60;
      item.target.textContent = "还需 " + (days ? days + "天 " : "") + hours + "小时 " + minutes + "分 " + seconds + "秒";
    });
  }
  function applyUsage(value) {
    const wrapper = value && Object.hasOwn(value, "report") ? value : null;
    value = wrapper ? wrapper.report : value;
    usage = value; quotaRows = [];
    const totals = value?.totals;
    text(".ai-summary h2", "Codex 已采集用量"); text(".ai-summary p", number(totals?.total_tokens, 0) + " tokens · " + number(value?.metadata_session_count, 0) + " 个会话快照");
    text(".ai-price strong", "—"); text(".ai-price>span", "未估算费用");
    text("#usage .page-title h2", "Codex 会话账本"); text("#usage .page-title>div>p:nth-of-type(2)", "请求明细与会话累计快照分别展示，不相加。");
    const summary = all(".usage-summary>div");
    ["已采集请求用量", "费用估算", "累计快照会话"].forEach((label, index) => summary[index].querySelector("span").textContent = label);
    [number(totals?.total_tokens, 0) + " tokens", "未接入", number(value?.metadata_session_count, 0)].forEach((value, index) => summary[index].querySelector("strong").textContent = value);
    ledger.replaceChildren(node("h2", "请求明细汇总"), sessionTable(value?.sessions || [], false), node("p", "每会话包含回放/继承历史，总览按唯一事件记账。最多展示100行。", "fine-print"), node("h2", "桌面会话累计快照"), sessionTable(value?.metadata_sessions || [], true), node("p", "累计快照没有输入输出拆分，不用于今日账单；不与请求明细相加。", "fine-print"));
    const breakdown = $(".token-breakdown"); breakdown.replaceChildren(node("h2", "已采集 Token 构成"));
    const dl = node("dl");
    [["输入（包含缓存）", totals?.input_tokens], ["其中缓存读取", totals?.cached_input_tokens], ["输出（包含推理）", totals?.output_tokens], ["其中推理", totals?.reasoning_tokens]].forEach(([name, amount]) => { const pair = node("div"); pair.append(node("dt", name), node("dd", number(amount, 0))); dl.append(pair); });
    breakdown.append(dl, node("p", "缓存与推理是子集，不再次加到总量。费用未估算。", "fine-print"));
    quotaSection.replaceChildren(node("h2", "套餐额度与重置"), node("p", "最近5组来源会话快照分别展示，不代表已确认账户。", "fine-print"));
    (value?.quota || []).slice(0, 5).forEach(snapshot => {
      const group = node("article", undefined, "live-quota-group");
      group.append(node("h3", snapshot.limit_id || "Codex"), node("p", "本地日志快照 · " + date(snapshot.observed_at) + " · 账户身份未确认", "fine-print"));
      [snapshot.primary, snapshot.secondary].filter(Boolean).forEach(window => {
        const row = node("div", undefined, "live-quota-window"), clock = node("p", "等待计算", "live-countdown");
        row.append(node("strong", (window.window_minutes ? number(window.window_minutes, 0) + " 分钟窗口" : window.name || "额度窗口") + " · 已用 " + number(window.used_percent) + "%"), node("p", "记录的重置时间：" + date(window.resets_at)), clock);
        quotaRows.push({ target: clock, reset: window.resets_at }); group.append(row);
      }); quotaSection.append(group);
    });
    if (!(value?.quota || []).length) quotaSection.append(node("p", "尚未读取到额度快照，重置时间未知；不会按token推算套餐余额。", "fine-print"));
    usageStatus.textContent = value?.generated_at ? "读取于 " + date(value.generated_at) + " · " + (value.quality?.pending_sources ? "部分日志仍在分批读取" : "本地来源，覆盖范围有限") : "统计尚未就绪";
    if (wrapper?.scanning) usageStatus.textContent += " · 正在扫描本地来源";
    if (wrapper?.error) usageStatus.textContent += " · " + wrapper.error;
    if (value?.quality?.sources_without_token_usage) usageStatus.textContent += " · " + value.quality.sources_without_token_usage + " 个来源尚未观察到token明细";
    countdown();
  }
  async function readUsage(force = false) {
    refreshButton.disabled = true;
    try { const value = await request(force ? "refresh-usage" : "usage"); applyUsage(value); lastUsageRead = Date.now(); }
    catch (error) { usageStatus.textContent = "统计读取未完成：" + error.message; }
    finally { refreshButton.disabled = false; }
  }
  refreshButton.addEventListener("click", () => readUsage(true));
  async function refresh() {
    if (!token || document.hidden || refreshing) return;
    refreshing = true;
    try {
      const [snapshot, eventData] = await Promise.all([request("snapshot"), request("events")]);
      applyEvents(eventData); applySnapshot(snapshot); await readHistory();
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
    if (button?.dataset.metric || button?.dataset.range) readHistory().catch(error => status("历史读取失败：" + error.message));
    if (button?.dataset.page === "usage" || button?.dataset.go === "usage") { countdown(); if (!usage) readUsage(); }
  });
  document.addEventListener("visibilitychange", () => {
    timers();
    if (document.hidden) cancelPending(); else refresh();
  });
  window.addEventListener("pagehide", () => { clearInterval(refreshTimer); clearInterval(quotaTimer); cancelPending(); });
  bridge?.addEventListener("message", event => {
    const message = event.data;
    if (message?.kind === "init") { token = message.token; timers(); refresh(); return; }
    const item = pending.get(message?.id);
    if (!item) return;
    clearTimeout(item.timer); pending.delete(message.id);
    if (message.error) item.reject(new Error(message.error)); else item.resolve(message.result);
  });
  root.dataset.live = "true";
  text(".brand p", "独立系统观察"); text(".device-kind", "Windows · 新版观察版");
  text(".ranking .section-subtitle", "当前工作集最高的5个单进程");
  text(".app-footer>span:first-child", "新版观察版 · 仅记录与提醒");
  text("#events .page-title p:last-of-type", "新版独立记录，不读取旧烛龙数据。");
  text("#events .page-title h2", "观察事件");
  text("#events .event-toolbar h3", "最近记录");
  $(".resource-strip").querySelectorAll("svg").forEach(element => element.remove());
  processes.splice(0); events.splice(0); applyUsage(null); applyEvents(null); applySnapshot(null);
  status(bridge ? "正在连接新版数据" : "请在烛龙桌面程序中打开");
  bridge?.postMessage({ kind: "ready" });
})();

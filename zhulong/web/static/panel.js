"use strict";
/* 烛龙面板逻辑：数据拉取 / 区间切换 / 昼夜主题 / 事件语义标注 */
const $ = id => document.getElementById(id);

const THEMES = {
  dark:  { grid: "rgba(255,159,67,.07)", tick: "#6e6353", main: "#ff9f43", fill: "255,159,67",
           dimA: "#b98c5f", limit: "#5c5348",
           proc: ["#ff9800", "#03a9f4", "#e91e63", "#8bc34a", "#ab47bc"],
           ok: "#9cc79c", info: "#5c9dff", danger: "#ff5c5c", mut: "#8a7a63" },
  light: { grid: "rgba(90,70,40,.10)", tick: "#8a7a63", main: "#c87414", fill: "200,116,20",
           dimA: "#a05a2c", limit: "#9a8b72",
           proc: ["#c87414", "#2f6fb2", "#b03a2e", "#3e7d44", "#7d4fa0"],
           ok: "#3e7d44", info: "#2f6fb2", danger: "#c0392b", mut: "#7a6c56" }
};
const RANGES = {
  "24h": { hours: 24, bucket: 300, label: ts => ts.slice(11, 16), title: "24小时（5分钟桶）" },
  "7d":  { hours: 168, bucket: 1800, label: ts => ts.slice(5, 16), title: "7天（30分钟桶）" },
  "30d": { hours: 720, bucket: 7200, label: ts => ts.slice(5, 10), title: "30天（2小时桶）" }
};
const RULE_CN = { hyphelper_leak: "泄漏处置", commit_high: "高水位告警" };
const ACTION_CN = { kill_process: "自动结束", toast: "通知", muted: "已静音" };
const ADVICE = {
  "frequency_cap": "该进程 10 分钟内已自动处置 2 次（频次熔断保护），本次未自动结束。推荐：任务管理器手动结束该进程（重新打开对应启动器即重置）；若反复泄漏，升级该软件版本。",
  "access_denied": "权限不足未能自动结束。推荐：以管理员身份打开任务管理器手动结束该进程。"
};

let state = { range: "24h", charts: [] };
let META = { names: {}, program_path: "" };

async function jget(url) { return (await fetch(url)).json(); }
function th() { return THEMES[document.documentElement.dataset.theme]; }
function zh(key) { const n = META.names[key]; return n ? n[0] : key; }
function zhDesc(key) { const n = META.names[key]; return n ? n[1] : ""; }
function zoneOf(pct) {
  if (pct < 70) return ["绿区 · 健康", "var(--ok)"];
  if (pct < 85) return ["黄区 · 注意", "var(--acc2)"];
  return ["红区 · 危险", "var(--danger)"];
}
function destroyCharts() { state.charts.forEach(c => c.destroy()); state.charts = []; }

/* ── 当前值渲染 ── */
function renderCurrent(cur) {
  const pct = cur.commit_percent ?? 0;
  const [zt, zc] = zoneOf(pct);
  $("heroNum").innerHTML = `${pct.toFixed(1)}<small> %</small>`;
  $("heroNum").style.color = zc;
  $("heroCn").textContent = zh("commit_percent");
  $("heroDesc").textContent = zhDesc("commit_percent");
  const cards = [];
  const add = (key, val, unit) => cards.push(
    `<div class="subcard"><div class="l">${zh(key)} <small>${key}</small></div>
     <div class="v">${val} ${unit}</div><div class="d">${zhDesc(key)}</div></div>`);
  add("commit_used_gb", (cur.commit_used_gb ?? 0).toFixed(1), "GB");
  add("commit_limit_gb", (cur.commit_limit_gb ?? 0).toFixed(1), "GB");
  add("mem_used_gb", (cur.mem_used_gb ?? 0).toFixed(1), "GB");
  add("mem_total_gb", (cur.mem_total_gb ?? 0).toFixed(1), "GB");
  add("mem_percent", (cur.mem_percent ?? 0).toFixed(0), "%");
  add("pagefile_used_gb", (cur.pagefile_used_gb ?? 0).toFixed(1), "GB");
  add("pagefile_total_gb", (cur.pagefile_total_gb ?? 0).toFixed(1), "GB");
  for (const k of Object.keys(cur).filter(k => k.endsWith("_commit_gb"))) {
    cards.push(`<div class="subcard"><div class="l">${zh(k)} <small>${k.replace("_commit_gb", "")}.exe</small></div>
      <div class="v">${(cur[k] ?? 0).toFixed(1)} GB</div><div class="d">${zhDesc(k)}</div></div>`);
  }
  $("subcards").innerHTML = cards.join("");
}

/* ── 事件语义：标记点（仅 24h）与时间线 ── */
function evActionClass(action) {
  if (action.startsWith("degraded")) return "pend";
  if (action === "muted") return "muted-b";
  return "done";
}
function actionCn(action) {
  if (action.startsWith("degraded")) return "降级处理";
  return ACTION_CN[action] || action;
}
function parseStatus(detail) {
  try { return JSON.parse(detail || "{}").status || ""; } catch (e) { return ""; }
}
function buildMarkers(events, labels) {
  const groups = {};
  for (const [ts, rule, action, detail] of events) {
    const minute = ts.slice(0, 16);
    const key = rule + "|" + minute;
    const pri = action.startsWith("degraded") ? 4 : action === "kill_process" ? 3 : action === "muted" ? 2 : 1;
    if (!groups[key] || pri > groups[key].pri) groups[key] = { ts, rule, action, detail, pri };
  }
  const out = [];
  for (const g of Object.values(groups)) {
    const label = g.ts.slice(11, 16);
    const idx = labels.indexOf(label);
    if (idx < 0) continue;
    out.push({ idx, pri: g.pri,
               title: `${actionCn(g.action)} · ${RULE_CN[g.rule] || g.rule} · ${label}`,
               detail: (g.detail || "").slice(0, 80) });
  }
  return out;
}
function renderTimeline(events) {
  $("events").innerHTML = events.map(([ts, rule, action, detail]) => {
    const cls = evActionClass(action);
    const badge = cls === "pend" ? "待处理" : cls === "muted-b" ? "已静音" : "已处理";
    let tip = "";
    if (cls === "pend") {
      const st = parseStatus(detail);
      tip = `<span class="tip">推荐：${ADVICE[st.split(":")[1]] || "详见重点提醒区"}</span>`;
    }
    return `<div class="ev"><span class="t">${ts}</span><span class="rule">${RULE_CN[rule] || rule}</span>
      <span class="st"><span class="st-badge ${cls}">${badge}</span></span>
      <span class="detail">${(detail || "").slice(0, 110)}${tip}</span></div>`;
  }).join("") || `<div class="ev"><span class="detail">本区间无事件</span></div>`;
}
function renderAlert(events) {
  const pend = events.filter(([, , a]) => a.startsWith("degraded"));
  const handled = events.filter(([, , a]) => a === "kill_process" || a === "toast").length;
  if (pend.length) {
    const [ts, rule, , detail] = pend[0];
    const st = parseStatus(detail).split(":")[1] || "";
    $("alertTitle").textContent = `待处理 ${pend.length} 起 · 需要人工介入`;
    $("alertAdvice").innerHTML = `<b>${ts.slice(11)} ${RULE_CN[rule] || rule}降级</b><br><b>推荐处理思路</b>：${ADVICE[st] || "详见事件时间线"}`;
    $("alert").hidden = false;
  } else {
    $("alert").hidden = true;
  }
  if (handled) {
    $("handledText").textContent = `本区间其余 ${handled} 起处置已全部自动完成`;
    $("handledLine").hidden = false;
  } else {
    $("handledLine").hidden = true;
  }
}

/* ── 图表 ── */
function baseOpts(t, extra) {
  return Object.assign({
    responsive: true, maintainAspectRatio: false,
    scales: { x: { grid: { color: t.grid }, ticks: { color: t.tick, maxTicksLimit: 12 } },
              y: { grid: { color: t.grid }, ticks: { color: t.tick } } },
    plugins: { legend: { display: false } }
  }, extra || {});
}
function buildMain(labels, values, events) {
  const t = th();
  const ctx = $("mainChart").getContext("2d");
  const grad = ctx.createLinearGradient(0, 0, 0, 320);
  grad.addColorStop(0, `rgba(${t.fill},.26)`); grad.addColorStop(1, `rgba(${t.fill},0)`);
  const markers = state.range === "24h" ? buildMarkers(events, labels) : [];
  const mkColor = m => m.pri === 4 ? t.danger : m.pri === 3 ? t.ok : m.pri === 2 ? t.mut : t.info;
  const datasets = [
    { data: values, borderColor: t.main, backgroundColor: grad, fill: true, tension: .3, pointRadius: 0, borderWidth: 2 },
    { data: labels.map(() => 70), borderColor: t.mut, borderDash: [4, 5], pointRadius: 0, borderWidth: 1 },
    { data: labels.map(() => 85), borderColor: t.danger, borderDash: [4, 5], pointRadius: 0, borderWidth: 1 }
  ];
  if (markers.length) datasets.push({
    type: "scatter", data: markers.map(m => ({ x: labels[m.idx], y: values[m.idx] })),
    pointRadius: 7, pointStyle: "rectRot",
    pointBackgroundColor: markers.map(mkColor), pointBorderColor: "#00000000"
  });
  const opts = baseOpts(t, {
    scales: { x: { grid: { color: t.grid }, ticks: { color: t.tick, maxTicksLimit: 12 } },
              y: { grid: { color: t.grid }, ticks: { color: t.tick }, suggestedMax: 100 } },
    plugins: { legend: { display: false },
               tooltip: { filter: it => markers.length ? it.datasetIndex === 3 : it.datasetIndex === 0,
                          callbacks: { title: items => items.length ? markers[items[0].dataIndex].title : "",
                                       label: it => markers[it.dataIndex].detail } } }
  });
  state.charts.push(new Chart(ctx, { type: "line", data: { labels, datasets }, options: opts }));
  $("legendNote").hidden = markers.length === 0;
}
function buildDim1(labels, used, limit) {
  const t = th();
  state.charts.push(new Chart($("dim1"), {
    type: "line",
    data: { labels, datasets: [
      { label: "已用", data: used, borderColor: t.main, tension: .3, pointRadius: 0, borderWidth: 1.5 },
      { label: "上限", data: limit, borderColor: t.limit, borderDash: [5, 4], pointRadius: 0, borderWidth: 1.5 }
    ]},
    options: baseOpts(t, { scales: { x: { display: false }, y: { grid: { color: t.grid }, ticks: { color: t.tick } } },
                           plugins: { legend: { labels: { color: t.tick, boxWidth: 10 } } } })
  }));
}
function buildDim2(labels, mem) {
  const t = th();
  state.charts.push(new Chart($("dim2"), {
    type: "line",
    data: { labels, datasets: [{ data: mem, borderColor: t.dimA, tension: .3, pointRadius: 0, borderWidth: 1.5 }]},
    options: baseOpts(t, { scales: { x: { display: false }, y: { grid: { color: t.grid }, ticks: { color: t.tick }, suggestedMax: 100 } } })
  }));
}
async function buildProc(labels, cur, r) {
  const t = th();
  const keys = Object.keys(cur).filter(k => k.endsWith("_commit_gb"));
  const datasets = [];
  for (let i = 0; i < keys.length; i++) {
    const resp = await jget(`/api/history?collector=process&key=${keys[i]}&hours=${r.hours}&bucket=${r.bucket}`);
    const byLabel = {};
    resp.points.forEach(p => { byLabel[r.label(p[0])] = p[1]; });
    datasets.push({ label: zh(keys[i]), data: labels.map(l => byLabel[l] ?? null),
                    borderColor: t.proc[i % t.proc.length], tension: .25, pointRadius: 0, borderWidth: 1.5, spanGaps: true });
  }
  state.charts.push(new Chart($("procChart"), {
    type: "line",
    data: { labels, datasets },
    options: baseOpts(t, { scales: { x: { grid: { color: t.grid }, ticks: { color: t.tick, maxTicksLimit: 12 } },
                                     y: { grid: { color: t.grid }, ticks: { color: t.tick }, suggestedMin: 0 } },
                           plugins: { legend: { labels: { color: t.tick, boxWidth: 10 } } } })
  }));
}
function renderInsight(points, events, curPct) {
  let maxV = 0, maxT = "";
  for (const [ts, v] of points) if (v > maxV) { maxV = v; maxT = ts.slice(5, 16); }
  const counts = {};
  for (const [, , a] of events) counts[actionCn(a)] = (counts[actionCn(a)] || 0) + 1;
  const [zt, zc] = zoneOf(curPct);
  $("insight").innerHTML =
    `区间最高 <b>${maxV.toFixed(1)}%（${maxT}）</b> · 事件 <b>${events.length}</b> 起` +
    (events.length ? `（${Object.entries(counts).map(([k, n]) => `${k} ${n}`).join(" / ")}）` : "") +
    ` · 当前水位：<b style="color:${zc}">${zt}</b>`;
}

/* ── 主刷新 ── */
async function refresh() {
  const cur = await jget("/api/current");
  META = cur._meta || META;
  $("programPath").textContent = META.program_path || "";
  renderCurrent(cur);
  const r = RANGES[state.range];
  $("mainTitle").innerHTML = `提交内存水位 · ${r.title} <span class="mk">（虚线：70% 黄区 / 85% 红区阈值）</span>`;
  const [commitPts, usedPts, limitPts, memPts, eventsResp] = await Promise.all([
    jget(`/api/history?collector=memory&key=commit_percent&hours=${r.hours}&bucket=${r.bucket}`),
    jget(`/api/history?collector=memory&key=commit_used_gb&hours=${r.hours}&bucket=${r.bucket}`),
    jget(`/api/history?collector=memory&key=commit_limit_gb&hours=${r.hours}&bucket=${r.bucket}`),
    jget(`/api/history?collector=memory&key=mem_percent&hours=${r.hours}&bucket=${r.bucket}`),
    jget(`/api/events?hours=${r.hours}&limit=200`)
  ]);
  const labels = commitPts.points.map(p => r.label(p[0]));
  destroyCharts();
  buildMain(labels, commitPts.points.map(p => p[1]), eventsResp.events);
  buildDim1(labels, usedPts.points.map(p => p[1]), limitPts.points.map(p => p[1]));
  buildDim2(labels, memPts.points.map(p => p[1]));
  await buildProc(labels, cur, r);
  renderTimeline(eventsResp.events);
  renderAlert(eventsResp.events);
  renderInsight(commitPts.points, eventsResp.events, cur.commit_percent ?? 0);
}

/* ── 主题与区间 ── */
function applyTheme(mode) {
  document.documentElement.dataset.theme = mode;
  $("eyeOpen").style.display = mode === "light" ? "" : "none";
  $("eyeClosed").style.display = mode === "light" ? "none" : "";
  $("themeLabel").textContent = mode === "light" ? "昼" : "夜";
  try { localStorage.setItem("zhulong-theme", mode); } catch (e) { /* 忽略 */ }
}
$("themeBtn").addEventListener("click", async () => {
  applyTheme(document.documentElement.dataset.theme === "dark" ? "light" : "dark");
  await refresh();
});
$("rangePills").addEventListener("click", async e => {
  const pill = e.target.closest(".pill");
  if (!pill) return;
  document.querySelectorAll("#rangePills .pill").forEach(p => p.classList.remove("on"));
  pill.classList.add("on");
  state.range = pill.dataset.range;
  await refresh();
});

let savedTheme = "dark";
try { savedTheme = localStorage.getItem("zhulong-theme") || "dark"; } catch (e) { /* 忽略 */ }
applyTheme(savedTheme);
refresh();
setInterval(refresh, 30000);

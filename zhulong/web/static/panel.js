"use strict";
/* 烛龙面板逻辑：真实数据拉取 / 国风昼夜 / 区间切换 / 动作通道（二次确认+令牌） */
const $ = id => document.getElementById(id);

const RANGES = {
  d1:  { hours: 24, bucket: 300, label: ts => ts.slice(11, 16), name: "一昼夜" },
  d7:  { hours: 168, bucket: 1800, label: ts => ts.slice(5, 16), name: "七日" },
  d30: { hours: 720, bucket: 7200, label: ts => ts.slice(5, 10), name: "一月" }
};
let curRange = "d1";
let state = { charts: {}, needRebuild: true, _lastSyncIdx: null, _syncRaf: null };
let META = { names: {}, labels: {}, program_path: "" };
let TOKEN = "";

const RULE_CN = { hyphelper_leak: "泄漏处置", commit_high: "高水位告警", throttle: "限流", panel_action: "面板操作" };
const ADVICE = {
  "frequency_cap": "该进程 10 分钟内已自动处置 2 次（熔断保护），未自动终结。可点「手动终结」直接处置，或升级对应软件版本。",
  "access_denied": "权限不足未能自动终结。建议：以管理员身份手动结束该进程。"
};

async function jget(url) { return (await fetch(url)).json(); }
const CS = getComputedStyle(document.documentElement);
const cv = k => CS.getPropertyValue(k).trim();
function theme() {
  return { main: cv("--chart-main"), fill: cv("--chart-fill"), grid: cv("--chart-grid"),
           mut: cv("--mut"), cinn: cv("--cinn"), ochre: cv("--ochre"), brush: cv("--brush"),
           leaf: cv("--leaf") };
}
function zh(key) { const n = META.names[key]; return n ? n[0] : key; }
function zhDesc(key) { const n = META.names[key]; return n ? n[1] : ""; }
function f1(v) { return (v ?? 0).toFixed(1); }

/* ── 干支四季（永不写死） ── */
(function season() {
  const stems = "甲乙丙丁戊己庚辛壬癸", branches = "子丑寅卯辰巳午未申酉戌亥";
  const d = new Date(), y = d.getFullYear(), m = d.getMonth() + 1;
  const gz = stems[(y - 4) % 10] + branches[(y - 4) % 12];
  const sn = m <= 2 || m === 12 ? "冬" : m <= 5 ? "春" : m <= 8 ? "夏" : "秋";
  $("seasonText").textContent = `岁在${gz} · ${sn}月系统监察录`;
  $("sideNote").textContent = `${sn}月监察 · 烛龙司夜`;
})();

/* ── 十字线 + 联动（A1 节流） + 就地更新（A2） ── */
const crosshairPlugin = {
  id: "crosshair",
  afterDraw(chart) {
    const act = chart.getActiveElements();
    if (!act.length || !chart.chartArea) return;
    const x = act[0].element.x, y = act[0].element.y;
    const { ctx, chartArea } = chart;
    ctx.save();
    ctx.strokeStyle = theme().mut; ctx.lineWidth = 1; ctx.setLineDash([4, 4]);
    ctx.beginPath(); ctx.moveTo(x, chartArea.top); ctx.lineTo(x, chartArea.bottom); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(chartArea.left, y); ctx.lineTo(chartArea.right, y); ctx.stroke();
    ctx.restore();
  }
};
function syncOthers(srcChart, actEls) {
  const idx = actEls.length ? actEls[0].index : null;
  if (idx === state._lastSyncIdx) return;
  state._lastSyncIdx = idx;
  if (state._syncRaf) return;
  state._syncRaf = requestAnimationFrame(() => {
    state._syncRaf = null;
    for (const c of Object.values(state.charts)) {
      if (c === srcChart) continue;
      const els = idx === null ? [] : [{ datasetIndex: 0, index: Math.min(idx, c.data.labels.length - 1) }];
      c.setActiveElements(els);
      if (c.tooltip) c.tooltip.setActiveElements(els, { x: 0, y: 0 });
      c.update("none");
    }
  });
}
function upsert(key, canvasId, config) {
  const existing = state.charts[key];
  if (existing && !state.needRebuild) {
    existing.data = config.data;
    existing.update("none");
    return existing;
  }
  if (existing) existing.destroy();
  state.charts[key] = new Chart($(canvasId), config);
  return state.charts[key];
}
function mk(id, datasets, unit, yMax) {
  const t = theme(), r = RANGES[curRange];
  const ctx = $(id).getContext("2d");
  const grad = ctx.createLinearGradient(0, 0, 0, 200);
  grad.addColorStop(0, t.fill); grad.addColorStop(1, "rgba(0,0,0,0)");
  datasets[0].backgroundColor = grad; datasets[0].fill = true;
  upsert(id, id, {
    type: "line",
    data: { labels: mk._labels, datasets },
    options: { responsive: true, maintainAspectRatio: false,
               interaction: { mode: "index", intersect: false },
               onHover: (evt, actEls, chart) => syncOthers(chart, actEls),
               scales: { x: { grid: { color: t.grid }, ticks: { color: t.mut, maxTicksLimit: 9 } },
                          y: { grid: { color: t.grid }, ticks: { color: t.mut, callback: v => (+v).toFixed(1) + unit }, min: 0, max: yMax } },
               plugins: { legend: { display: false }, tooltip: { callbacks: { label: it => (it.dataset.label ? it.dataset.label + " " : "") + it.parsed.y.toFixed(1) + unit } } }
    },
    plugins: [crosshairPlugin]
  });
}
async function hist(collector, key) {
  const r = RANGES[curRange];
  const resp = await jget(`/api/history?collector=${collector}&key=${key}&hours=${r.hours}&bucket=${r.bucket}`);
  return resp.points.map(p => p[1]);
}

/* ── 判词 ── */
function zoneAn(text) { return `<span class="an">${text}</span>`; }
function zoneShen(text) { return `<span class="shen">${text}</span>`; }
function zoneWei(text) { return `<span class="wei">${text}</span>`; }
function setPlain(id, html, cls) {
  $(id).innerHTML = html;
  const z = $(id).closest(".zone, .rail");
  if (z) { z.classList.remove("z-shen", "z-wei"); if (cls) z.classList.add(cls); }
}
function omen(k, vHtml, barPct, barCls, d) {
  const bar = barPct == null ? "" : `<div class="bar"><i${barCls ? ` class="${barCls}"` : ""} style="width:${Math.max(0, Math.min(100, barPct)).toFixed(1)}%"></i></div>`;
  return `<div class="omen"><div class="k">${k}</div><div class="v">${vHtml}</div>${bar}<div class="d">${d || ""}</div></div>`;
}

/* ── 各分区渲染 ── */
async function renderZ1(cur) {
  const pct = cur.commit_percent ?? 0;
  const verdict = pct < 70 ? zoneAn("安 · 内存很够用，不用担心") : pct < 85 ? zoneShen("慎 · 水位偏高，留意大户") : zoneWei("危 · 逼近上限，请即处置");
  setPlain("p1", verdict, pct < 70 ? "" : pct < 85 ? "z-shen" : "z-wei");
  const t = theme();
  const pts = await hist("memory", "commit_percent");
  mk("c1", [
    { data: pts, borderColor: t.main, tension: .3, pointRadius: 0, borderWidth: 1.8 },
    { data: pts.map(() => 70), borderColor: t.ochre, borderDash: [3, 5], pointRadius: 0, borderWidth: 1 },
    { data: pts.map(() => 85), borderColor: t.cinn, borderDash: [3, 5], pointRadius: 0, borderWidth: 1 }
  ], "%", 100);
  $("o1").innerHTML =
    omen("已用提交内存", `${f1(cur.commit_used_gb)} <small>GB</small>`, cur.commit_used_gb / (cur.commit_limit_gb || 1) * 100, "", zhDesc("commit_used_gb")) +
    omen("提交内存上限", `${f1(cur.commit_limit_gb)} <small>GB</small>`, 100, "", zhDesc("commit_limit_gb")) +
    omen("物理内存", `${f1(cur.mem_used_gb)} / ${f1(cur.mem_total_gb)} <small>GB</small>`, cur.mem_percent ?? 0, (cur.mem_percent ?? 0) > 85 ? "danger" : "", zhDesc("mem_percent"));
}
async function renderZ2(cur) {
  const mc = cur.cpu_max_core ?? 0;
  const verdict = mc > 80 ? zoneShen("慎 · 单核偏高") : zoneAn("安 · 算力充裕");
  setPlain("p2", verdict, mc > 80 ? "z-shen" : "");
  const t = theme();
  mk("c2", [
    { label: "总占用", data: await hist("cpu", "cpu_percent"), borderColor: t.main, tension: .3, pointRadius: 0, borderWidth: 1.8 },
    { label: "单核峰值", data: await hist("cpu", "cpu_max_core"), borderColor: t.cinn, borderDash: [4, 4], tension: .3, pointRadius: 0, borderWidth: 1.3 }
  ], "%", 100);
  const top = META.labels.cpu_top_pct || "";
  $("o2").innerHTML =
    omen("总占用", `${f1(cur.cpu_percent)} <small>%</small>`, cur.cpu_percent ?? 0, "", zhDesc("cpu_percent")) +
    omen("单核峰值", `${f1(cur.cpu_max_core)} <small>%</small>`, cur.cpu_max_core ?? 0, mc > 80 ? "warn" : "", zhDesc("cpu_max_core")) +
    omen("CPU 大户", top ? `${top} <small>${f1(cur.cpu_top_pct)}%</small>` : "–", cur.cpu_top_pct ?? 0, (cur.cpu_top_pct ?? 0) > 50 ? "warn" : "", zhDesc("cpu_top_pct"));
}
async function renderZ3(cur) {
  const mx = Math.max(cur.io_read_mb_s ?? 0, cur.io_write_mb_s ?? 0);
  const verdict = mx > 20 ? zoneShen("慎 · 读写繁忙") : zoneAn("安 · 平稳，无有扫描");
  setPlain("p3", verdict, mx > 20 ? "z-shen" : "");
  const t = theme();
  mk("c3", [
    { label: "读取", data: await hist("diskio", "io_read_mb_s"), borderColor: t.main, tension: .3, pointRadius: 0, borderWidth: 1.8 },
    { label: "写入", data: await hist("diskio", "io_write_mb_s"), borderColor: t.brush, tension: .3, pointRadius: 0, borderWidth: 1.6 }
  ], " MB/s", 5);
  const top = META.labels.io_top_mb || "";
  $("o3").innerHTML =
    omen("读取", `${f1(cur.io_read_mb_s)} <small>MB/s</small>`, (cur.io_read_mb_s ?? 0) / 5 * 100, "", zhDesc("io_read_mb_s")) +
    omen("写入", `${f1(cur.io_write_mb_s)} <small>MB/s</small>`, (cur.io_write_mb_s ?? 0) / 5 * 100, "", zhDesc("io_write_mb_s")) +
    omen("IO 大户", top ? `${top} <small>${f1(cur.io_top_mb)}MB</small>` : "无", null, "", zhDesc("io_top_mb"));
}
async function renderZ4(cur) {
  const jit = cur.ping_jitter_ms ?? 0;
  const verdict = jit > 10 ? zoneShen("慎 · 抖动偏大") : zoneAn("安 · 平稳");
  setPlain("p4", verdict, jit > 10 ? "z-shen" : "");
  const t = theme();
  mk("c4", [
    { label: "下载", data: await hist("net", "net_down_mb_s"), borderColor: t.main, tension: .3, pointRadius: 0, borderWidth: 1.8 },
    { label: "上传", data: await hist("net", "net_up_mb_s"), borderColor: t.brush, tension: .3, pointRadius: 0, borderWidth: 1.6 }
  ], " MB/s", 10);
  $("o4").innerHTML =
    omen("下载", `${f1(cur.net_down_mb_s)} <small>MB/s</small>`, (cur.net_down_mb_s ?? 0) / 10 * 100, "", zhDesc("net_down_mb_s")) +
    omen("上传", `${f1(cur.net_up_mb_s)} <small>MB/s</small>`, (cur.net_up_mb_s ?? 0) / 10 * 100, "", zhDesc("net_up_mb_s")) +
    omen("延迟 / 抖动", `${f1(cur.ping_net_ms)} / ${f1(jit)} <small>ms</small>`, Math.min(100, jit * 10), jit > 10 ? "warn" : "", zhDesc("ping_jitter_ms"));
}
async function renderZ5(cur) {
  const temp = cur.gpu_temp ?? 0, util = cur.gpu_util ?? 0;
  const verdict = temp > 83 ? zoneShen("慎 · 温度偏高") : util > 80 ? zoneShen("慎 · 高负载运转") : zoneAn("安 · 闲置，温度正常");
  setPlain("p5", verdict, (temp > 83 || util > 80) ? "z-shen" : "");
  const t = theme();
  mk("c5", [
    { label: "利用率", data: await hist("gpu", "gpu_util"), borderColor: t.main, tension: .3, pointRadius: 0, borderWidth: 1.8 }
  ], "%", 100);
  $("o5").innerHTML =
    omen("温度", `${f1(cur.gpu_temp)} <small>°C</small>`, temp / 90 * 100, temp > 83 ? "warn" : "", zhDesc("gpu_temp")) +
    omen("利用率", `${f1(cur.gpu_util)} <small>%</small>`, util, util > 80 ? "warn" : "", zhDesc("gpu_util")) +
    omen("显存", `${f1(cur.gpu_mem_used)} / ${f1(cur.gpu_mem_total)} <small>GB</small>`, cur.gpu_mem_used / (cur.gpu_mem_total || 1) * 100, "", zhDesc("gpu_mem_used"));
}
async function renderZ6(cur) {
  const total = cur.pagefile_total_gb || 62;
  const ratio = (cur.pagefile_used_gb ?? 0) / total * 100;
  const verdict = ratio > 60 ? zoneShen("慎 · 物理内存吃紧") : zoneAn("安 · 占用甚微");
  setPlain("p6", verdict, ratio > 60 ? "z-shen" : "");
  const t = theme();
  mk("c6", [
    { label: "已用", data: await hist("memory", "pagefile_used_gb"), borderColor: t.main, tension: .3, pointRadius: 0, borderWidth: 1.8 }
  ], " GB", Math.ceil(total * 1.05));
  $("o6").innerHTML =
    omen("已用", `${f1(cur.pagefile_used_gb)} <small>GB</small>`, ratio, ratio > 60 ? "warn" : "", zhDesc("pagefile_used_gb")) +
    omen("总量", `${f1(cur.pagefile_total_gb)} <small>GB</small>`, 100, "", zhDesc("pagefile_total_gb")) +
    omen("已用占比", `${f1(ratio)} <small>%</small>`, ratio, ratio > 60 ? "warn" : "", "物理内存尚且够用");
}

/* ── 主数字与速读 ── */
function renderHero(cur) {
  const pct = cur.commit_percent ?? 0;
  const [zt, zc, zcls] = pct < 70 ? ["安 · 绿区健康，无需处置", "an", ""] : pct < 85 ? ["慎 · 水位偏高，留意大户", "shen", ""] : ["危 · 逼近上限，请即处置", "wei", ""];
  $("heroNum").innerHTML = `${f1(pct)}<small> %</small>`;
  $("heroNum").style.color = pct < 70 ? "var(--ink)" : pct < 85 ? "var(--ochre)" : "var(--cinn)";
  const top = META.labels.cpu_top_pct ? `CPU：大户 ${META.labels.cpu_top_pct} 占 ${f1(cur.cpu_top_pct)}%` : "CPU：平稳";
  $("quickread").innerHTML =
    `${zh("commit_percent")} <span class="${zcls || "an"}">${zt}</span><br>` +
    `内存：已用 ${f1(cur.commit_used_gb)} / 上限 ${f1(cur.commit_limit_gb)} GB ｜ ${top} ｜ 磁盘网络：平稳 ｜ 显卡：${f1(cur.gpu_temp)}°C ${(cur.gpu_util ?? 0) > 50 ? "高负载" : "闲置"}`;
}

/* ── 大户榜 / 设置 / 事件 / 待办 ── */
function renderRanks(cur) {
  const items = [];
  for (let i = 1; i <= 5; i++) {
    const raw = META.labels[`proctop${i}`];
    const gb = cur[`proctop${i}`];
    if (raw) {
      const [name, path] = raw.split("|");
      items.push(`<div class="rank"><span>${"①②③④⑤"[i - 1]} ${name}</span><span class="vv">${f1(gb)} GB</span></div>` +
        (path ? `<div class="rank-path" title="${path}">${path}</div>` : ""));
    }
  }
  $("ranks").innerHTML = items.join("") || `<div class="rank"><span>暂无数据（大户扫描每两分钟一轮）</span></div>`;
  const top1 = META.labels.proctop1;
  $("rankPlain").textContent = top1 ? `榜首 ${top1}` : "";
}
async function renderThrottle() {
  const th = await jget("/api/throttle");
  const enabled = th.enabled === "default" ? "出厂默认开" : th.enabled === "1" ? "开" : "关";
  $("thState").textContent = enabled;
  const rows = th.rules.map(r =>
    `<div class="set-row"><span>${r.name}</span><span class="vv">${r.priority} · ${r.cores}</span></div>`);
  const applied = JSON.parse(th.applied || "{}");
  const names = Object.keys(applied);
  if (names.length) rows.push(`<div class="set-row"><span>当前生效</span><span class="vv">${names.join("、")}</span></div>`);
  $("throttle").innerHTML = rows.join("");
}
const ACTION_CN = a => a.startsWith("degraded") ? "降级处理" : { kill_process: "自动终结", toast: "通知", muted: "已静音", apply: "施加", restore: "还原" }[a] || a;
function renderEvents(events) {
  const pend = events.filter(([, , a]) => a.startsWith("degraded"));
  $("evPlain").textContent = pend.length ? `危 · ${pend.length} 起待人工` : "安 · 无待人工";
  $("events").innerHTML = events.slice(0, 20).map(([ts, rule, action, detail]) => {
    const cls = action.startsWith("degraded") ? "pi-wei" : action === "muted" ? "pi-mute" : "pi-an";
    const pi = cls === "pi-wei" ? "待人工 ◉" : cls === "pi-mute" ? "已静音" : "已处置";
    return `<div class="ev"><span class="t">${ts.slice(5)}</span><span class="b">${RULE_CN[rule] || rule} · ${ACTION_CN(action)}：${(detail || "").slice(0, 80)}</span><span class="pi ${cls}">${pi}</span></div>`;
  }).join("") || `<div class="ev"><span class="b">本区间无事件 —— 系统安泰</span></div>`;
  renderPendline(pend);
}

/* ── 待办与动作（二次确认 + 令牌 POST） ── */
let pendTarget = "";
function adviceOf(detail) {
  try { return ADVICE[(JSON.parse(detail || "{}").status || "").split(":")[1]] || ADVICE.frequency_cap; }
  catch (e) { return ADVICE.frequency_cap; }
}
function renderPendline(pend) {
  if (!pend.length) { $("pendline").hidden = true; return; }
  const [ts, rule, , detail] = pend[0];
  let target = "";
  try { target = JSON.parse(detail || "{}").process || ""; } catch (e) { }
  pendTarget = target || "HYPHelper.exe";
  $("pendBody").textContent = `${ts.slice(5)} · ${RULE_CN[rule] || rule}降级：${(detail || "").slice(0, 60)}`;
  $("pendAlt").textContent = adviceOf(detail).split("，")[0];
  $("actZone").innerHTML = `<button class="act-btn" onclick="askConfirm()">手动终结</button>`;
  $("pendline").hidden = false;
}
window.askConfirm = function () {
  $("actZone").innerHTML = `<span class="act-ask">确认终结 ${pendTarget} 乎？</span>` +
    `<button class="act-btn" onclick="doKill()">确认</button> ` +
    `<button class="act-no" onclick="resetAct()">再思</button>`;
};
window.resetAct = function () {
  $("actZone").innerHTML = `<button class="act-btn" onclick="askConfirm()">手动终结</button>`;
};
window.doKill = async function () {
  $("actZone").innerHTML = `<span class="act-ask">处置中…</span>`;
  const resp = await fetch("/api/action", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ action: "kill_process", target: pendTarget, token: TOKEN })
  });
  const result = await resp.json();
  const ok = result.status === "killed";
  $("actZone").innerHTML = `<button class="act-btn" disabled>${ok ? "已终结 ✓" : "未终结：" + (result.status || "失败")}</button>`;
};

/* ── 主刷新 ── */
async function refresh() {
  const cur = await jget("/api/current");
  META = cur._meta || META;
  $("programPath").textContent = META.program_path || "";
  const r = RANGES[curRange];
  document.querySelectorAll(".rng-name").forEach(e => e.textContent = r.name);
  renderHero(cur);
  const evts = await jget(`/api/events?hours=${r.hours}&limit=50`);
  mk._labels = (await jget(`/api/history?collector=memory&key=commit_percent&hours=${r.hours}&bucket=${r.bucket}`)).points.map(p => r.label(p[0]));
  await renderZ1(cur);
  await renderZ4(cur);
  await renderZ2(cur);
  await renderZ5(cur);
  await renderZ3(cur);
  await renderZ6(cur);
  renderRanks(cur);
  renderEvents(evts.events);
  await renderThrottle();
  state.needRebuild = false;
}

/* ── 主题与区间 ── */
function applyTheme(mode) {
  document.documentElement.dataset.theme = mode;
  $("eyeOpen").style.display = mode === "light" ? "" : "none";
  $("eyeClosed").style.display = mode === "light" ? "none" : "";
  $("themeLabel").textContent = mode === "light" ? "昼" : "夜";
  try { localStorage.setItem("zhulong-theme", mode); } catch (e) { }
}
$("themeBtn").addEventListener("click", async () => {
  applyTheme(document.documentElement.dataset.theme === "light" ? "dark" : "light");
  state.needRebuild = true;
  await refresh();
});
$("rangePills").addEventListener("click", async e => {
  const pill = e.target.closest(".pill");
  if (!pill) return;
  document.querySelectorAll("#rangePills .pill").forEach(p => p.classList.remove("on"));
  pill.classList.add("on");
  curRange = pill.dataset.r;
  state.needRebuild = true;
  await refresh();
});

(async function init() {
  let saved = "light";
  try { saved = localStorage.getItem("zhulong-theme") || "light"; } catch (e) { }
  applyTheme(saved);
  const tok = await jget("/api/action_token");
  TOKEN = tok.token || "";
  await refresh();
  setInterval(refresh, 30000);
})();

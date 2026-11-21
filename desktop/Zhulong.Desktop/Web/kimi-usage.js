"use strict";
// 独立卡片和账本共用本机日用量；挂件只加载此页所需的脚本。
const KimiCard = (() => {
  const compact = location.hash === "#usage-card";
  const root = document.documentElement;
  function readTheme(theme) {
    if (!["light", "dark"].includes(theme)) {
      try { theme = localStorage.getItem("zhulong-panel-theme"); } catch { /* 使用系统配色。 */ }
    }
    root.dataset.theme = ["light", "dark"].includes(theme) ? theme : matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }
  if (compact) {
    root.dataset.usageCard = "true";
    readTheme();
  }
  const dashboardReady = compact ? Promise.resolve() : Promise.all(
    Array.from(document.querySelector("#dashboard-scripts").content.querySelectorAll("script"), item => new Promise((resolve, reject) => {
      const script = document.createElement("script");
      script.async = false;
      script.src = item.src;
      script.onload = resolve;
      script.onerror = () => reject(new Error("面板脚本加载失败：" + item.getAttribute("src")));
      document.head.append(script);
    }))
  );
  function element(tag, text, cls) {
    const item = document.createElement(tag);
    if (text !== undefined) item.textContent = text;
    if (cls) item.className = cls;
    return item;
  }
  function count(row, field) {
    const value = row?.[field + "_exact"] ?? row?.[field];
    if (typeof value === "string" && /^\d+$/.test(value)) return BigInt(value);
    return Number.isSafeInteger(value) && value >= 0 ? BigInt(value) : null;
  }
  const fields = ["total_tokens", "input_tokens", "output_tokens"];
  const selection = new Map();
  function daily(report, title, source, format) {
    const block = element("section", undefined, "kimi-daily");
    block.dataset.kimiSource = source;
    block.append(element("h3", "本机 Token 用量"), element("p", title + " · 按本地自然日汇总", "fine-print"));
    const rows = new Map((Array.isArray(report?.daily_usage) ? report.daily_usage : []).map(row => [row.local_date, row]));
    const today = new Date();
    const dates = Array.from({length:60}, (_,index) => {
      const day = new Date(today.getFullYear(), today.getMonth(), today.getDate()-59+index);
      return day.getFullYear() + "-" + String(day.getMonth()+1).padStart(2,"0") + "-" + String(day.getDate()).padStart(2,"0");
    });
    if (!dates.some(date => rows.has(date))) {
      block.append(element("p", "近 60 日尚无可用的本机日记录，用量和环比未知。", "fine-print"));
      return block;
    }
    function period(end, days) {
      const sums = [0n,0n,0n], known = [0,0,0];
      for (const date of dates.slice(end-days+1,end+1)) {
        const row = rows.get(date);
        fields.forEach((field,i) => {
          const value = count(row,field);
          if (value !== null) { sums[i] += value; known[i]++; }
        });
      }
      return {sums,known};
    }
    function delta(end, days) {
      const now = period(end,days), prior = period(end-days,days);
      if (now.known[0] !== days || prior.known[0] !== days) return "未知（缺日）";
      if (prior.sums[0] === 0n) return "未知（前期为零）";
      const diff = now.sums[0]-prior.sums[0], magnitude = diff < 0n ? -diff : diff;
      const tenths = (magnitude*1000n+prior.sums[0]/2n)/prior.sums[0];
      return (diff===0n?"":diff>0n?"+":"−") + (tenths/10n) + "." + (tenths%10n) + "%";
    }
    const table=element("table",undefined,"kimi-periods"), head=table.createTHead().insertRow();
    for(const label of ["统计周期","总量","输入","输出","环比"]) head.append(element("th",label));
    const tbody=table.createTBody();
    for(const [days,label] of [[1,"今日"],[7,"近 7 天"],[30,"近 30 天"]]) {
      const row=tbody.insertRow(), snapshot=period(59,days);
      const caption=element("th",label);caption.scope="row";
      if(snapshot.known[0]<days) caption.append(element("small","已采 "+snapshot.known[0]+"/"+days+" 天"));
      row.append(caption);
      snapshot.sums.forEach((value,i)=>{
        const cell=row.insertCell();
        if (!snapshot.known[i]) { cell.textContent="—"; return; }
        const amount=element("span",format(String(value)));
        amount.dataset.tokenCount=String(value);
        amount.title=value.toLocaleString("zh-CN")+" Token，已采 "+snapshot.known[i]+"/"+days+" 天";
        cell.append(amount);
      });
      row.insertCell().textContent=delta(59,days);
    }
    block.append(table,element("p","表中为已采集 Token；缺少日记录时只合计已有记录，环比显示未知。", "fine-print"));
    const keys=dates.slice(-30), data=keys.map(day=>count(rows.get(day),"total_tokens"));
    const peak=data.reduce((a,b)=>b!==null&&b>a?b:a,0n);
    const chart=element("div",undefined,"kimi-bars");
    chart.setAttribute("role","group");chart.setAttribute("aria-label","近 30 日 Token 总量，左右方向键切换日期，缺日以横线表示");
    const caption = element("p", "最高 ", "fine-print");
    const maximum = element("span", data.every(x => x === null) ? "未知" : format(String(peak)));
    if (data.some(x => x !== null)) maximum.dataset.tokenCount = String(peak);
    caption.append(maximum, " Token · 点选日期查看明细");
    block.append(element("h4", "近 30 日 Token 总量"), caption, chart);
    const result=element("details",undefined,"kimi-day-detail"), label=element("summary"), detail=element("p");
    detail.setAttribute("aria-live","polite");result.append(label,detail);
    const buttons=data.map((amount,i)=>{
      const button=element("button");button.type="button";button.dataset.date=keys[i];
      const desc=keys[i]+" · "+(amount===null?"无记录":amount.toLocaleString("zh-CN")+" Token");
      button.title=desc;button.setAttribute("aria-label",desc);
      const bar=element("i");button.dataset.state=amount===null?"missing":amount===0n?"zero":"known";
      if(amount!==null&&amount>0n)bar.style.height=(Number(amount*10000n/peak)/100)+"%";
      button.append(bar);chart.append(button);
      button.addEventListener("click",()=>pick(i,true));
      button.addEventListener("keydown",e=>{
        const next={ArrowLeft:i-1,ArrowRight:i+1,Home:0,End:29}[e.key];
        if (next===undefined)return;
        e.preventDefault();const at=Math.max(0,Math.min(29,next));buttons[at].focus();pick(at,true);
      });
      return button;
    });
    const span=element("div",undefined,"kimi-date-range");span.append(element("span",keys[0].slice(5)),element("span",keys[29].slice(5)));
    block.append(span,result);
    function pick(at,open) {
      const day=keys[at],row=rows.get(day);
      selection.set(source,{day,open});buttons.forEach((b,i)=>b.setAttribute("aria-pressed",String(i===at)));
      const amount=field=>{const n=count(row,field);return n===null?"未知":n.toLocaleString("zh-CN");};
      label.textContent=day+" 日明细 · "+(row?amount("total_tokens")+" Token":"无采集记录");
      detail.textContent="输入 "+amount("input_tokens")+" · 输出 "+amount("output_tokens")+" · 事件 "+amount("event_count")+" · 日环比 "+delta(30+at,1)+" · 周环比 "+delta(30+at,7);
      result.open=open;
    }
    const saved=selection.get(source), index=keys.indexOf(saved?.day);
    pick(index<0?29:index,Boolean(saved?.open));
    result.addEventListener("toggle",()=>{const s=selection.get(source);if(block.isConnected&&s)s.open=result.open;});
    return block;
  }
  function openWindow(bridge, quota, status, render, countdown) {
    root.dataset.live="true";
    const main=element("main",undefined,"kimi-card-window"), head=element("header"), close=element("button","关闭","text-button");
    close.type="button";close.setAttribute("aria-label","关闭 AI 用量卡");
    head.append(element("h1","Kimi 用量"),close);main.append(head,status,quota);
    document.body.replaceChildren(main);
    let token=null, seq=0, active=0, tick=null, timeout=null;
    function finish(){clearTimeout(timeout);active=0;}
    function query() {
      if(!token||document.hidden||active)return;
      active=++seq;
      timeout=setTimeout(()=>{finish();status.textContent="用量读取超时，可重新打开卡片。";},20000);
      bridge.postMessage({kind:"query",id:active,token,api:"usage"});
    }
    function syncVisibility(){
      clearInterval(tick);tick=null;
      if(document.hidden){finish();return;}
      query();if(token)tick=setInterval(countdown,1000);
    }
    function dismiss(){if(token)bridge.postMessage({kind:"window-command",token,command:"close"});}
    close.addEventListener("click",dismiss);
    document.addEventListener("keydown",e=>{if(e.key==="Escape"){e.preventDefault();dismiss();}});
    document.addEventListener("visibilitychange",syncVisibility);
    window.addEventListener("pagehide",()=>{clearInterval(tick);finish();});
    bridge?.addEventListener("message",e=>{
      const msg=e.data;
      if(msg?.kind==="init"){token=msg.token;readTheme(msg.theme);syncVisibility();return;}
      if(msg?.kind==="card-theme"){readTheme(msg.theme);return;}
      if(msg?.kind==="usage-updated"){finish();if(!document.hidden)render(msg.value);return;}
      if(!active||msg?.id!==active)return;
      finish();if(document.hidden)return;
      if(msg.error)status.textContent="用量读取失败："+msg.error;else render(msg.result);
    });
    if(bridge)bridge.postMessage({kind:"ready"});else status.textContent="请从烛龙桌面挂件打开用量卡。";
  }
  return {dashboardReady,daily,openWindow};
})();

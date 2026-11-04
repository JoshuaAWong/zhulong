import React, { useEffect, useLayoutEffect, useState } from "react";
import { createRoot } from "react-dom/client";

type Snapshot = { sequence: number; points: number[]; rows: { name: string; value: number }[] };
declare const chrome: any;

function Panel() {
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  useEffect(() => {
    const receive = (event: MessageEvent) => setSnapshot(event.data);
    chrome.webview.addEventListener("message", receive);
    chrome.webview.postMessage("ready");
    return () => chrome.webview.removeEventListener("message", receive);
  }, []);
  useLayoutEffect(() => {
    if (!snapshot) return;
    let second = 0;
    const first = requestAnimationFrame(() => {
      second = requestAnimationFrame(() => chrome.webview.postMessage("paint:" + snapshot.sequence));
    });
    return () => { cancelAnimationFrame(first); cancelAnimationFrame(second); };
  }, [snapshot]);
  if (!snapshot) return <h1>等待共用数据源</h1>;
  return <>
    <h1>WebView2 + React · 300 点 / 50 行 · 第 {snapshot.sequence} 次刷新</h1>
    <svg viewBox="0 0 932 230" preserveAspectRatio="none">
      {[0, 1, 2, 3, 4].map(i => <line key={i} x1="0" x2="932" y1={i * 57.5} y2={i * 57.5} stroke="#d3d3d3" />)}
      <polyline fill="none" stroke="#a0372d" strokeWidth="2" points={snapshot.points.map((v, i) => `${i * 932 / 299},${230 * (1 - v / 100)}`).join(" ")} />
    </svg>
    <div className="list">{snapshot.rows.map(row => <div className="row" key={row.name}><span>{row.name}</span><span>{row.value}%</span></div>)}</div>
  </>;
}
createRoot(document.getElementById("root")!).render(<Panel />);

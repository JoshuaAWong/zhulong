"use strict";
// 本地浏览器复用受限查询桥；不创建常驻轮询，原生确认只在动作通道中等待。
if (location.origin === "http://127.0.0.1:17631") {
  const listeners = new Set(), active = new Set();
  let token = null;
  function deliver(value) { listeners.forEach(listener => listener({ data: value })); }
  window.zhulongPortalBridge = {
    addEventListener(kind, listener) { if (kind === "message") listeners.add(listener); },
    removeEventListener(kind, listener) { if (kind === "message") listeners.delete(listener); },
    postMessage(message) {
      if (!message || !["ready", "query"].includes(message.kind)) return;
      if (active.size >= 8) { deliver({ id: message.id, error: "本地请求繁忙，请稍后再试" }); return; }
      const control = new AbortController(); active.add(control);
      const action = message.kind === "query" && message.api === "action";
      const timer = action ? null : setTimeout(() => control.abort(), 15000);
      const request = message.kind === "ready"
        ? fetch("/api/session", { signal: control.signal, cache: "no-store" })
        : fetch(action ? "/api/action" : "/api/query", {
          method: "POST", headers: { "Content-Type": "application/json", "X-Zhulong-CSRF": token || "" },
          body: JSON.stringify(message), signal: control.signal, cache: "no-store"
        });
      request.then(async response => {
        let value;
        try { value = await response.json(); } catch { throw new Error(response.ok ? "本地响应未完整返回" : response.status === 429 ? "本地请求繁忙，请稍后再试" : "本地请求失败 " + response.status); }
        if (!response.ok) throw new Error(value?.error || "本地请求失败 " + response.status);
        if (message.kind === "ready") {
          if (value?.kind !== "init" || typeof value.token !== "string") throw new Error("本地会话未就绪");
          token = value.token;
        } else if (value.id === null || value.id === undefined) value.id = message.id;
        deliver(value);
      }).catch(error => {
        const detail = error.name === "AbortError" ? "本地请求已取消或超时" : error.message;
        if (message.kind === "ready") {
          const status = document.querySelector(".sample-time");
          if (status) status.textContent = "浏览器连接未就绪：" + detail;
        }
        deliver({ id: message.id, error: detail });
      }).finally(() => { clearTimeout(timer); active.delete(control); });
    }
  };
  window.addEventListener("pagehide", () => {
    active.forEach(control => control.abort()); active.clear();
  });
  // 保留页面内监听以支持浏览器往返缓存；整页卸载时由页面上下文统一回收。
  window.addEventListener("pageshow", event => {
    if (event.persisted) window.zhulongPortalBridge.postMessage({ kind: "ready" });
  });
}

"""烛龙入口：pythonw -X utf8 -m zhulong
启动序：日志 → 清除退出意图 → 单实例互斥 → schema → 扩展点发现 → daemon 线程 → 主线程托盘"""
import ctypes
import logging
import subprocess
import sys
import threading
import time
from logging.handlers import RotatingFileHandler

from zhulong import config
from zhulong.core import registry, scheduler, storage
from zhulong.core.engine import Engine

LOG = logging.getLogger("zhulong")
WIN32_ERROR_ALREADY_EXISTS = 183


def _spawn_restarter():
    """分离进程延迟重启：约 5 秒后拉起新实例（ping 做延迟——分离进程没有 stdin，
    timeout 会直接报错；ping 是 Windows 惯用的无窗口延迟法）。等本进程退出、
    互斥锁与面板端口释放后新实例才启动；若起早撞锁，新实例自行退出，
    未写 stopped.intent，看门狗会兜底拉起。"""
    cmd = f'ping -n 6 127.0.0.1 >nul & "{config.PYTHONW}" -X utf8 -m zhulong'
    LOG.info("托盘重启：已派出延迟启动器（约 5 秒后拉起新实例）")
    subprocess.Popen(["cmd", "/c", cmd], cwd=config.PROJECT_DIR,
                     creationflags=subprocess.DETACHED_PROCESS,
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def _init_logging():
    config.LOG_DIR.mkdir(parents=True, exist_ok=True)
    handler = RotatingFileHandler(config.LOG_DIR / "zhulong.log",
                                  maxBytes=10 * 1024 * 1024, backupCount=5, encoding="utf-8")
    logging.basicConfig(level=logging.INFO, handlers=[handler],
                        format="%(asctime)s [%(threadName)s] %(levelname)s %(message)s")


def _acquire_single_instance():
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateMutexW(None, False, "Global\\ZhulongSingleInstance")
    return ctypes.get_last_error() != WIN32_ERROR_ALREADY_EXISTS


def main():
    _init_logging()

    class _LastChance:
        def write(self, s):
            if s.strip():
                LOG.error("stdout/stderr: %s", s.strip())
        def flush(self):
            pass
    sys.stdout = _LastChance()
    sys.stderr = _LastChance()

    threading.excepthook = lambda a: LOG.exception("线程未捕获异常", exc_info=a)

    intent = config.STATE_DIR / "stopped.intent"
    if intent.exists():
        LOG.info("清除退出意图标记，正常启动")
        intent.unlink()

    if not _acquire_single_instance():
        LOG.info("已有实例在运行，退出")
        return

    conn = storage.connect(config.DB_PATH)
    storage.init_schema(conn)

    collectors = registry.discover(__import__("zhulong.collectors", fromlist=["*"]))
    actions = {a["name"]: a for a in registry.discover(__import__("zhulong.actions", fromlist=["*"]))}
    rules = registry.load_rules()
    LOG.info("扩展点：collectors=%s actions=%s rules=%s",
             [c["name"] for c in collectors], list(actions), [r["name"] for r in rules])

    kv_conn = conn   # muted_until 持久化复用主库
    engine = Engine(kv_get=lambda k: storage.kv_get(kv_conn, k),
                    kv_set=lambda k, v: storage.kv_set(kv_conn, k, v))

    # 面板服务（daemon 线程 B）：端口被占用不致命，降级为无面板模式（采集/规则/托盘照常）
    from zhulong.web import server as web_server
    try:
        srv = web_server.serve(conn)
    except OSError:
        srv = None
        LOG.error("面板端口 %d 被占用，降级为无面板模式", config.PORT)
    panel_note = "" if srv is not None else " ｜ 面板端口被占用"
    if srv is not None:
        threading.Thread(target=srv.serve_forever, name="web", daemon=True).start()

    # 托盘（主线程）与采集（daemon）互相通过回调通信
    stop_event = threading.Event()
    from zhulong.tray import Tray
    tray_holder = {}

    def do_pause():
        engine.mute(time.monotonic(), 300)
        LOG.info("动作静音 5 分钟")

    def _shutdown():
        """退出/重启共用清理链：还原限流 → 停采集 → 停面板 → 停托盘消息循环。
        注意：看门狗崩溃路径无法 restore——进程意外死亡没机会清理（已施加清单
        仍在 kv 里，下次正常退出或手动「限流 恢复默认规则」时可还原）"""
        try:
            from zhulong import throttle
            n = throttle.restore(conn)
            if n:
                LOG.info("退出前还原限流 %d 个进程", n)
        except Exception:
            LOG.exception("退出前限流还原失败")
        stop_event.set()
        if srv is not None:
            srv.shutdown()   # 面板线程退出（无面板模式跳过）
        t = tray_holder.get("tray")   # 停掉主线程托盘消息循环，进程才能真正退出
        if t is not None:
            t.stop()

    def do_exit():
        config.STATE_DIR.mkdir(parents=True, exist_ok=True)
        intent.write_text("user exit", encoding="utf-8")
        _shutdown()
        LOG.info("用户退出：意图标记已写入")

    def do_restart():
        # 先派延迟启动器再退出（不写 stopped.intent：新实例起不来时看门狗可兜底拉起）
        try:
            _spawn_restarter()
        except Exception:
            LOG.exception("延迟启动器派发失败，放弃重启")
            return
        _shutdown()
        LOG.info("托盘重启：旧实例退出，等待新实例接续")

    def do_throttle_toggle():
        from zhulong import throttle as _th   # noqa: F401（保持导入一致性）
        cur = storage.kv_get(conn, "throttle_enabled") or "1"
        storage.kv_set(conn, "throttle_enabled", "0" if cur == "1" else "1")
        LOG.info("限流开关切换为 %s", "关" if cur == "1" else "开")

    def do_throttle_reset():
        from zhulong import throttle
        ov = config.DATA_DIR / "throttle_override.json"
        if ov.exists():
            ov.unlink()
        n = throttle.restore(conn)
        LOG.info("限流恢复默认规则，还原 %d 个进程", n)

    def do_open_config():
        import os
        os.startfile(str(config.__file__))

    tray = Tray(on_pause=do_pause, on_exit=do_exit, on_restart=do_restart,
                on_throttle_toggle=do_throttle_toggle,
                on_throttle_reset=do_throttle_reset,
                on_open_config=do_open_config)
    tray_holder["tray"] = tray

    def on_cycle(triggers, state):
        row = storage.latest(conn, "memory", "commit_percent")
        used = storage.latest(conn, "memory", "commit_used_gb")
        limit = storage.latest(conn, "memory", "commit_limit_gb")
        healthy = state.get("collect_failures", 0) == 0
        tray.update(percent=row[1] if row else None,
                    used_gb=used[1] if used else None,
                    limit_gb=limit[1] if limit else None,
                    healthy=healthy, note=panel_note)
        for t in triggers:
            LOG.info("触发 %s actions=%s muted=%s", t["rule"], t["actions"], t["muted"])

    threading.Thread(target=scheduler.loop, name="collector",
                     args=(conn, config, collectors, rules, engine, actions, stop_event, on_cycle),
                     daemon=True).start()
    LOG.info("烛龙启动完成%s", f"，面板 {config.PANEL_URL}" if srv is not None else "（无面板模式）")
    tray.run()          # 主线程阻塞于托盘消息循环
    LOG.info("烛龙退出")


if __name__ == "__main__":
    main()

"""烛龙入口：pythonw -X utf8 -m zhulong
启动序：日志 → 清除退出意图 → 单实例互斥 → schema → 扩展点发现 → daemon 线程 → 主线程托盘"""
import ctypes
import logging
import sys
import threading
import time
from logging.handlers import RotatingFileHandler

from zhulong import config
from zhulong.core import registry, scheduler, storage
from zhulong.core.engine import Engine

LOG = logging.getLogger("zhulong")
WIN32_ERROR_ALREADY_EXISTS = 183


def _init_logging():
    config.LOG_DIR.mkdir(parents=True, exist_ok=True)
    handler = RotatingFileHandler(config.LOG_DIR / "zhulong.log",
                                  maxBytes=10 * 1024 * 1024, backupCount=5, encoding="utf-8")
    logging.basicConfig(level=logging.INFO, handlers=[handler],
                        format="%(asctime)s [%(threadName)s] %(levelname)s %(message)s")
    # pythonw 下 stdout/stderr 是黑洞，重定向防意外 print 崩溃
    log_stream = handler.stream
    sys.stdout = sys.stdout or log_stream
    sys.stderr = sys.stderr or log_stream


def _acquire_single_instance():
    ctypes.windll.kernel32.CreateMutexW(None, False, "Global\\ZhulongSingleInstance")
    return ctypes.windll.kernel32.GetLastError() != WIN32_ERROR_ALREADY_EXISTS


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

    # 面板服务（daemon 线程 B）
    from zhulong.web import server as web_server
    srv = web_server.serve(conn)
    threading.Thread(target=srv.serve_forever, name="web", daemon=True).start()

    # 托盘（主线程）与采集（daemon）互相通过回调通信
    stop_event = threading.Event()
    from zhulong.tray import Tray
    tray_holder = {}

    def do_pause():
        engine.mute(time.monotonic(), 300)
        LOG.info("动作静音 5 分钟")

    def do_exit():
        config.STATE_DIR.mkdir(parents=True, exist_ok=True)
        intent.write_text("user exit", encoding="utf-8")
        stop_event.set()
        srv.shutdown()   # 面板线程退出
        LOG.info("用户退出：意图标记已写入")
        t = tray_holder.get("tray")   # 停掉主线程托盘消息循环，进程才能真正退出
        if t is not None:
            t.stop()

    tray = Tray(on_pause=do_pause, on_exit=do_exit)
    tray_holder["tray"] = tray

    def collect_and_update():
        cycle_state = {}   # 跨周期保留：每日清理标记（每轮新建会让清理每 30s 空跑一次）
        while not stop_event.is_set():
            triggers = scheduler.run_cycle(conn, cfg=config, collectors=collectors,
                                           rules=rules, engine=engine, actions=actions,
                                           state=cycle_state, evaluate=True)
            row = storage.latest(conn, "memory", "commit_percent")
            used = storage.latest(conn, "memory", "commit_used_gb")
            limit = storage.latest(conn, "memory", "commit_limit_gb")
            tray.update(percent=row[1] if row else None,
                        used_gb=used[1] if used else None,
                        limit_gb=limit[1] if limit else None,
                        healthy=True)
            for t in triggers:
                LOG.info("触发 %s actions=%s muted=%s", t["rule"], t["actions"], t["muted"])
            stop_event.wait(config.SAMPLE_INTERVAL)

    threading.Thread(target=collect_and_update, name="collector", daemon=True).start()
    LOG.info("烛龙启动完成，面板 %s", config.PANEL_URL)
    tray.run()          # 主线程阻塞于托盘消息循环
    LOG.info("烛龙退出")


if __name__ == "__main__":
    main()

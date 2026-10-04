"""烛龙全局配置：所有可调参数集中于此。用户日常只需要改这个文件。"""
from pathlib import Path

PROJECT_DIR = Path(__file__).resolve().parent.parent
DATA_DIR = PROJECT_DIR / "data"
LOG_DIR = DATA_DIR / "logs"
STATE_DIR = PROJECT_DIR / "zhulong" / "state"
DB_PATH = DATA_DIR / "zhulong.db"

HOST = "127.0.0.1"
PORT = 8890

SAMPLE_INTERVAL = 30      # 采集周期（秒）
RETENTION_DAYS = 30       # metrics 保留天数
COOLDOWN_S = 600          # 规则触发冷却（秒）
HEARTBEAT_STALE_S = 120   # 心跳过期阈值（秒），看门狗用

# 白名单自动处置：仅以下进程允许被自动结束（红线：永不加入安全软件进程）
WATCH_PROCESSES = [
    {
        "name": "HYPHelper.exe",
        "display_name": "米哈游启动器组件",   # 面板中文展示名
        "max_commit_gb": 15,        # commit 超此值判定泄漏
        "min_age_s": 60,            # 进程存活不足此时长不杀（避开启动峰值）
        "max_kills_per_10min": 2,   # 频次熔断，超限降级为仅通知
        "path_contains": "miHoYo Launcher",   # 路径校验片段（防同名假冒）
    },
]

# 高水位告警（commit_percent 取值 0-100）
HIGH_WATER = {"enter": 85, "exit": 80, "for_seconds": 90}

# 面板展示：指标中文名与含义说明（影响解读）
DISPLAY_NAMES = {
    "commit_percent": ("提交内存水位", "所有程序向系统预订的内存额度占用率。超 85% 游戏/桌面可能因内存分配失败闪退黑屏"),
    "commit_used_gb": ("已用提交内存", "当前已承诺的内存总量（GB）"),
    "commit_limit_gb": ("提交内存上限", "物理内存+页面文件的总额度（GB），逼近上限时系统会拒绝新的内存申请"),
    "mem_percent":    ("物理内存占用", "真实内存条的使用率（GB 数据含页面文件的部分在 commit 体现）"),
}

TOAST_APP_ID = "Zhulong.烛龙"
PANEL_URL = f"http://{HOST}:{PORT}/"

# 部署用：锁死 3.13 解释器（本机另有 3.14，禁止误用）
PYTHONW = Path(r"C:\Users\Administrator\AppData\Local\Programs\Python\Python313\pythonw.exe")

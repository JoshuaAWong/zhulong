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
    "commit_percent": ("提交内存水位", "虚拟内存总额度（物理内存+页面文件）的占用率。超 85% 游戏/桌面可能因内存分配失败闪退黑屏"),
    "commit_used_gb": ("已用提交内存", "所有程序当前承诺的内存总量（GB），横跨物理内存与页面文件"),
    "commit_limit_gb": ("提交内存上限", "物理内存+页面文件的总额度（GB）。页面文件为系统自动管理，用量升高时上限会随之扩大——这是正常缓冲，警惕的是已用逼近且上限不再涨"),
    "mem_percent":    ("物理内存占用", "真实内存条的使用率"),
    "mem_used_gb":    ("物理内存已用", "真实内存条已用量（GB）"),
    "mem_total_gb":   ("物理内存总量", "真实内存条容量（GB）"),
    "pagefile_used_gb": ("页面文件已用", "虚拟内存的磁盘后备部分（pagefile.sys）当前用量，物理内存不够才会用到"),
    "pagefile_total_gb": ("页面文件总量", "页面文件当前大小（GB），系统自动管理会随 commit 压力动态扩缩"),
    "cpu_percent": ("CPU 总占用", "所有核心平均使用率。卡顿主因之一，需配合单核峰值看（单核打满也会卡）"),
    "cpu_max_core": ("CPU 单核峰值", "最忙核心的使用率。很多软件只吃单核——总值不高但这里顶到 100% 一样卡"),
    "cpu_top_pct": ("CPU 大户", "当前吃 CPU 最多的进程及其占用（每 2 分钟刷新一次）"),
    "io_read_mb_s": ("磁盘读取速率", "全系统磁盘读取 MB/s，标签为最忙的盘"),
    "io_write_mb_s": ("磁盘写入速率", "全系统磁盘写入 MB/s，标签为最忙的盘"),
    "io_top_mb": ("IO 大户", "累计读写量最大的进程（每 2 分钟刷新一次）——外接存储被扫描时通常就是它在干活"),
}

# 进程级"大户"扫描分频：每 N 个采集周期一次（30s × 4 = 2 分钟，总量指标仍 30 秒）
CPU_TOP_INTERVAL = 4

TOAST_APP_ID = "Zhulong.烛龙"
PANEL_URL = f"http://{HOST}:{PORT}/"

# 部署用：锁死 3.13 解释器（本机另有 3.14，禁止误用）
PYTHONW = Path(r"C:\Users\Administrator\AppData\Local\Programs\Python\Python313\pythonw.exe")

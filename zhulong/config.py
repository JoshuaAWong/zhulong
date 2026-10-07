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
    "net_down_mb_s": ("下载速率", "全网络下载速率（MB/s）"),
    "net_up_mb_s": ("上传速率", "全网络上传速率（MB/s）"),
    "ping_gw_ms": ("网关延迟", "到路由器的延迟（ms），本地链路质量"),
    "ping_net_ms": ("公网延迟", "到公网 DNS 的延迟（ms），宽带质量"),
    "ping_jitter_ms": ("延迟抖动", "近 10 次公网延迟的标准差（ms），越小越稳，游戏/视频通话敏感"),
    "gpu_temp": ("显卡温度", "GPU 当前温度（°C），83°C 以上会降频"),
    "gpu_util": ("显卡利用率", "GPU 计算占用率（%）"),
    "gpu_mem_used": ("显存已用", "显卡显存已用量（GB）"),
    "gpu_mem_total": ("显存总量", "显卡显存容量（GB）"),
    "proctop1": ("大户①", "commit 占用第 1 的进程"),
    "proctop2": ("大户②", "commit 占用第 2 的进程"),
    "proctop3": ("大户③", "commit 占用第 3 的进程"),
    "proctop4": ("大户④", "commit 占用第 4 的进程"),
    "proctop5": ("大户⑤", "commit 占用第 5 的进程"),
}

# 限流器出厂规则（初始状态副本，永不改动；用户覆盖见 data/throttle_override.json）
# 红线：priority 仅 normal/below_normal；cores 支持 "tail:N"（尾部 N 核）或核号列表
THROTTLE_DEFAULTS = {
    "enabled": True,
    "rules": [
        {"name": "ToDesk.exe", "priority": "below_normal", "cores": "tail:2"},
        {"name": "QQPCTray.exe", "priority": "below_normal", "cores": "tail:4"},
        {"name": "AweSun.exe", "priority": "below_normal", "cores": "tail:2"},
        {"name": "sunloginclient.exe", "priority": "below_normal", "cores": "tail:2"},
    ],
}
THROTTLE_INTERVAL_CYCLES = 4   # 每 4 个采集周期（2 分钟）巡检应用一次

# 网络探测：烛龙唯一主动发包功能（每周期网关+公网各 1 个 ICMP 包）
PING_ENABLED = True
PING_HOST = "223.5.5.5"   # 阿里公共 DNS
# GPU 采集分频：每 N 个周期查一次 nvidia-smi（30s × 2 = 1 分钟）
GPU_INTERVAL = 2

# 进程级"大户"扫描分频：每 N 个采集周期一次（30s × 4 = 2 分钟，总量指标仍 30 秒）
CPU_TOP_INTERVAL = 4

TOAST_APP_ID = "Zhulong.烛龙"
PANEL_URL = f"http://{HOST}:{PORT}/"

# 部署用：锁死 3.13 解释器（本机另有 3.14，禁止误用）
PYTHONW = Path(r"C:\Users\Administrator\AppData\Local\Programs\Python\Python313\pythonw.exe")

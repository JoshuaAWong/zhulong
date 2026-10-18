"""烛龙全局配置：所有可调参数集中于此。用户日常只需要改这个文件。"""
import sys
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
    "io_top_mb_s": ("IO 大户", "当前读写速率（MB/s，差分）最高的进程（每 2 分钟刷新一次）——外接存储被扫描时通常就是它在干活。B4 起由累计量改为差分速率（原名 io_top_mb）"),
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
# 红线：priority 仅 normal/below_normal；cores 三种写法：
#   "spread:N" 错峰——按规则顺序从尾部向下各切 N 核块，规则间不重叠（v1.6 起出厂默认，专治多规则叠核）
#   "tail:N"   尾部 N 核——多条 tail 规则会共享同组核（叠核）
#   [0,1,...]  显式核号列表
THROTTLE_DEFAULTS = {
    "enabled": True,
    "rules": [
        {"name": "ToDesk.exe", "priority": "below_normal", "cores": "spread:2"},
        {"name": "QQPCTray.exe", "priority": "below_normal", "cores": "spread:2"},
        {"name": "AweSun.exe", "priority": "below_normal", "cores": "spread:2"},
        {"name": "sunloginclient.exe", "priority": "below_normal", "cores": "spread:2"},
    ],
}
THROTTLE_INTERVAL_CYCLES = 4   # 每 4 个采集周期（2 分钟）巡检应用一次

# 通用异常进程：自动杀仅限显式名单（默认空 = 全部只告警）；保护名单见 core/anomaly.NEVER_KILL
# （NEVER_KILL 在 kill_process 入口硬阻断，误配名单也杀不动）
# 名单元素允许字符串（仅按名匹配）或 dict：{"name": "x.exe", "path_contains": "路径片段"}
# ——不配 path_contains 则无路径校验；阈值固定取 core/anomaly.ANOMALY_REDLINE_GB（20GB）
AUTOKILL = []

# ── 温和资源回收（v1.6）────────────────────────────────────────────────────
# 内存回收：对白名单后台进程调 EmptyWorkingSet 挤出物理内存页（不杀进程不丢数据，
# 下次访问从待备列表快速调回）。如实分工：只降物理内存（working set）不减 commit——
# 泄漏处置仍走上面 WATCH/AUTOKILL；页面文件由系统自动回收，烛龙不做任何页面文件操作。
# 面板总开关（自动整理 开/关）写 kv autoclean_enabled，缺省取这里的 enabled。
# 关键进程（安全软件/系统账户/System32 路径/前台窗口进程）多重信号识别后一律不动（fail-closed）。
MEM_TRIM = {
    "enabled": True,           # 出厂默认开（配合压力门槛：内存不紧张时不折腾）
    "interval_cycles": 2,      # 每 2 周期（1 分钟）巡检一次
    "min_mem_percent": 60,     # 物理内存占用低于此值整轮跳过（无压力不折腾）
    "processes": [
        {"name": "HYPHelper.exe",           # 启动器挂后台时白占物理内存
         "min_ws_gb": 2.0,                  # working set 超此值才回收
         "min_age_s": 300,                  # 存活不足 5 分钟不动（刚启动还没稳定）
         "path_contains": "miHoYo Launcher",
         "max_trims_per_10min": 1},         # 频次熔断
    ],
}

# 泄漏服务温和重启（stop→start）：出厂空 = 只告警不动手（服务重启影响面大，纯白名单）。
# 触发：trigger_processes 里任一进程被异常检测判为泄漏（anomalies）即重启该服务。
# 例：WmiPrvSE.exe 泄漏 → 重启宿主服务 Winmgmt（杀 WmiPrvSE 进程会连坐同宿主服务，重启服务才对）。
# rpcss/dcomlaunch 等关键服务有硬阻断（actions/restart_service.SERVICES_NEVER_RESTART）。
RESTART_SERVICES = [
    # {"name": "Winmgmt", "display_name": "WMI 管理",
    #  "trigger_processes": ["WmiPrvSE.exe"], "max_restarts_per_hour": 1},
]

# 单核饱和告警：最忙核 ≥ enter% 持续 for_seconds 且总占用 < avg_below（多进程挤核特征）
SINGLE_CORE = {"enter": 95, "avg_below": 70, "for_seconds": 90}

# 网络探测：烛龙唯一主动发包功能（每周期网关+公网各 1 个 ICMP 包）
PING_ENABLED = True
PING_HOST = "223.5.5.5"   # 阿里公共 DNS
# 采集分频已收编：由调度器统一按各 COLLECTOR["interval"] 实现（gpu=2、proctop=4，其余=1），
# 不再从这里配置（B3 删除 GPU_INTERVAL / CPU_TOP_INTERVAL 两个死配置）

TOAST_APP_ID = "Zhulong.烛龙"
PANEL_URL = f"http://{HOST}:{PORT}/"

def runtime_pythonw(executable=None):
    """返回当前运行实例对应的 pythonw。
    生产环境由计划任务用 pythonw 启动时，sys.executable 就是应复用的解释器；
    用 python.exe 调试时优先选同目录 pythonw.exe，避免再次猜测其他安装。"""
    exe = Path(executable or sys.executable)
    if exe.name.lower() == "python.exe":
        candidate = exe.with_name("pythonw.exe")
        if candidate.is_file():
            return candidate
    return exe


# 看门狗与托盘重启都复用当前实例的解释器，安装后不会因 PATH 变化换一套 Python
PYTHONW = runtime_pythonw()

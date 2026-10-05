# 烛龙 Zhulong · Windows 系统监控托盘

> 察于幽冥 · 睁眼为昼 闭眼为夜
> A personal Windows system tray monitor — watches the invisible metrics (commit charge, CPU, disk IO), auto-disposes known memory leaks, and tells you before things break.

监控平时看不到的系统数值（**commit 提交内存水位**、物理/页面文件三层内存、CPU 总占用与单核峰值、磁盘 IO 速率），白名单**自动处置内存泄漏进程**，异常时 Windows toast 主动通知，配套烛龙风**昼夜双态**本机面板回溯历史。

诞生于一次真实事故：两个后台软件泄漏 40GB 提交内存，导致游戏闪退、桌面黑屏——烛龙把那次事故的取证与守护能力常态化。

## 特性

- **看不见的数值持续记录**：commit charge（虚拟内存总额度水位，游戏闪退/桌面黑屏的头号预警指标）、物理内存/页面文件分层、CPU（总占用+单核峰值+大户进程）、磁盘 IO（读写速率+最忙盘+IO 大户，覆盖"安全软件扫描外接存储致卡"场景）
- **白名单自动处置**：泄漏进程超阈值 → 七步安全链确认后自动结束 + toast + 事件落库；频次熔断防拉锯；其余异常仅通知
- **烛龙风面板**（127.0.0.1 只读）：睁眼/闭眼昼夜双态主题、24h/7天/30天区间切换、全中文标注、事件语义标记（处理/提醒/待处理+推荐处理思路）、悬停十字线全图联动、龙形 Logo 与朱砂印章
- **轻量与自愈**：常驻内存 <80MB、空闲 CPU <1%、零外联；计划任务自启 + 每 5 分钟心跳看门狗；单实例互斥；托盘四态图标
- **可扩展**：指标采集器/规则/动作三类扩展点，扔文件进目录即生效，schema 永不动

## 快速开始

```bat
:: 前提：Windows 11 + Python 3.13（脚本已写死解释器路径，与其他 Python 版本互不干扰）
:: 1. 克隆后双击安装（装依赖 + 注册开机自启与看门狗 + 立即启动）
scripts\安装.bat

:: 2. 打开面板
http://127.0.0.1:8890

:: 3. CLI 诊断（也可交给 AI 分析）
python -X utf8 -m zhulong.diag --hours 24
```

托盘图标：绿 <70% / 黄 70~85% / 红 ≥85% / 灰=采集异常；双击开面板，右键暂停 5 分钟或退出。

## 架构速览

```
单进程三线程：主线程托盘(pystray) / daemon 采集循环(30s) / daemon 只读面板(http.server)
collectors ──► SQLite(WAL 窄表) ──► rules ──► actions
  指标插件         通用 schema          数据式规则      kill/toast 自降级
```

- 数据：SQLite WAL 通用窄表（30 天保留自动清理），扩展指标不改表结构
- 安全红线：面板零写端点、只绑 127.0.0.1；kill fail-closed 路径校验 + PID 复用竞态防护；无提权、无注入
- 依赖仅 4 个纯 pip 包：psutil、pystray、Pillow、winotify（面板用标准库 http.server，无 Flask）

## 文档

| 文档 | 内容 |
|---|---|
| [docs/需求文档.md](docs/需求文档.md) | 背景事故、目标/非目标、功能需求（FR/D 系列）、验收标准 |
| [docs/设计文档.md](docs/设计文档.md) | 架构、线程模型、数据模型、规则引擎、kill 安全链、部署自愈、扩展指南 |
| [docs/实施计划.md](docs/实施计划.md) | 13 任务 TDD 实施计划 + 实施后记（终审修复与部署教训实录） |
| [docs/原型/](docs/原型/) | 面板设计迭代原型（A/B/C/D/D2）与 Logo 候选对比页 |

## 测试

```bat
python -X utf8 -m zhulong.tests.test_engine   :: 其余 test_* 同理，断言式自测无 pytest
```

## 排障速查

| 症状 | 处理 |
|---|---|
| 安装时安全软件弹窗 | 点"允许"；建议把项目目录加入信任区 |
| 托盘图标不出现 | 查 pythonw.exe 与 `data/logs/zhulong.log`；勿用管理员身份跑计划任务 |
| 通知收不到 | Windows 设置 → 系统 → 通知：总开关开启、勿扰关闭 |
| 面板打不开/数据陈旧 | 查 `data/logs/zhulong.log`；托盘是否变灰；杀 pythonw 由看门狗拉起 |

---

*Windows 11 · Python 3.13 · 个人项目，欢迎参考与借鉴。*

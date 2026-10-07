---
name: 烛龙
description: Windows 系统监控托盘软件：30 秒采集 commit 虚拟内存等不可见数值，白名单自动处置泄漏进程（如 HYPHelper 内存泄漏），异常 toast 通知，E5 国风昼夜面板（宣纸/漆器手卷、三栏界格、数据条判词、焦点待办二次确认直接处置、限流器与大户榜、网络延迟/GPU全维度、24h/7天/30天区间）。当用户说"电脑怎么卡了/游戏又闪退/看看内存/系统体检/烛龙"时使用。
---

# 烛龙 · 系统监控守护

## 使用
- 状态查看：托盘图标颜色（绿/黄/红/灰），悬停看 commit 百分比；双击打开面板 http://127.0.0.1:8890
- CLI 诊断：`python -X utf8 -m zhulong.diag --hours 24`（项目根执行）
- 启停：托盘右键菜单；安装/卸载：`scripts/安装.bat`、`scripts/卸载.bat`

## AI 排障流程（游戏闪退/电脑卡顿时）
1. 读 `python -X utf8 -m zhulong.diag --hours 6` 输出
2. 结合事件表（面板/CLI）判断：白名单进程泄漏？（高水位持续？）
3. 处置建议参考 docs/需求文档.md 场景 S1/S2

## 维护
- 全部阈值/白名单：`zhulong/config.py`（唯一可调文件）
- 加监控项/规则/动作：见 docs/设计文档.md §14 扩展指南
- 测试：`python -X utf8 -m zhulong.tests.test_engine`（各 test_* 同理）
- 首次部署被安全软件拦截：见 README.md 排障节

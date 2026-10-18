# 直接依赖与 vendored 组件许可证

本项目感谢以下开源组件。版本号为整理本文时本机安装/分发版本，实际安装版本以包管理器解析结果为准。

| 组件 | 版本 | 许可证 | 用途与链接 |
|---|---|---|---|
| Chart.js | 4.4.7 | MIT | 本地面板图表；https://github.com/chartjs/Chart.js |
| @kurkle/color | 0.3.2 | MIT | Chart.js 随包附带的色彩工具；https://github.com/kurkle/color |
| psutil | 7.2.2 | BSD-3-Clause | Windows 进程与系统指标采集；https://github.com/giampaolo/psutil |
| pystray | 0.19.5 | LGPLv3 | Windows 系统托盘；https://github.com/moses-palmer/pystray |
| Pillow | 12.3.0 | MIT-CMU | 托盘图标绘制；https://github.com/python-pillow/Pillow |
| winotify | 1.1.0 | MIT | Windows toast 通知；https://github.com/versa-syahptr/winotify |

随包分发的 Chart.js 与 @kurkle/color 的完整 MIT 许可证文本分别见：

- [licenses/Chart.js-LICENSE.md](licenses/Chart.js-LICENSE.md)
- [licenses/kurkle-color-LICENSE.md](licenses/kurkle-color-LICENSE.md)

其他依赖的许可证文本可通过各组件仓库及其发行包中的 `LICENSE` / `LICENSE.txt` 获取。pystray 以 Python 包形式独立安装、动态调用，本项目未修改其源码。本清单覆盖直接依赖和 vendored 组件，不展开传递依赖。

本项目自身的许可证见根目录 [LICENSE](LICENSE)。

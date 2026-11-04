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


## P0-A 独立实验依赖

以下只用于 prototypes/p0-ui，未加入旧版 Python 运行路径；版本由 npm/NuGet 锁文件固定，构建产物和 SDK 不提交。

| 组件 | 版本 | 许可证 | 用途 |
|---|---|---|---|
| Microsoft.Web.WebView2 | 1.0.4314.35 | Microsoft WebView2 SDK 许可，以 NuGet 包内许可为准 | 原型 WebView 宿主 |
| React / ReactDOM | 19.3.1 | MIT | 原型静态面板 |
| esbuild | 0.28.2 | MIT | 仅前端构建 |

依赖取得自 [NuGet](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4314.35) 和 npm 官方注册表；完整许可随安装的依赖保留。自包含正式发布前仍须完成整体分发许可核对。

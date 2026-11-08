# 烛龙独立桌面版

新版观察预览，旧Python程序、网页服务及数据库不参与运行。源码在desktop/Zhulong.Desktop，运行产物在dist/Zhulong；分发时只发便携ZIP或整个运行目录。

## 运行

双击Zhulong.exe打开水墨窗口。关闭窗口后新版留在托盘，WebView销毁；双击托盘打开，右键“退出新版烛龙”彻底停止。再次启动程序会唤醒已有新版实例。首次CPU差分值未知，30秒后取得第二次采样。

默认数据位于程序目录data：monitor.db为新系统历史，ai.db为Codex统计，cache/WebView为本程序独立浏览器缓存。退出后可复制整个便携目录；更新程序保留data。程序不自动迁移旧记录、不设置自启。

这是纯.NET运行版，已自包含.NET Runtime，不需要Python、Node或源码；系统需有Microsoft Edge WebView2 Runtime，缺失会显示说明，不静默安装。

## 当前功能与边界

- Windows原生提交内存、物理内存、整机CPU差分；单进程工作集前五名。
- 独立历史和事件：真实采样历史最多288聚合点；提交跨85%或采集错误变化时记录事件。
- Codex只读累计快照、请求用量及日志额度窗口倒计时。累计快照与请求总账分列，不相加；额度保留快照时间，到期等待新记录确认。
- GPU、磁盘、网络和进程CPU尚未迁移，显示未知/未接入。新版没有结束进程、限流或服务重启功能，旧版保持原状。
- 原水墨背景、昼夜波浪、太极占用变速/悬停展开及聚卦沿用；正式长时间动效预算仍待验证。
- AI每分钟独立任务，手动刷新不重叠，每轮日志正文最多16MiB；目录排序和state库快照仍是全量，不把正文预算称为所有IO上限。系统每30秒采样，面板隐藏暂停前端刷新。

## 构建与验证

从项目根执行，构建工具需要Python与.NET10 SDK，仅开发机需要：

```bat
python -X utf8 desktop/build.py
dist\Zhulong\Zhulong.exe --self-test out\desktop-tests
```

build.py生成自包含win-x64运行目录、SHA256资源清单与便携ZIP，排除data、源码和PDB。只复制已选UI依赖、两组昼夜图与品牌资源，不复制全部设计候选。运行源码里复用P0 Codex模块的编译输入，发行时只有程序集，不需要P0源码目录。

当前已部署的独立目录：C:/Users/Administrator/AppData/Local/Programs/Zhulong。默认构建输出与实际运行目录分开；如需要重建覆盖，请先退出新版，保留data，不在运行时覆盖DLL。

[方案、验证与未完成项](../docs/桌面运行版方案.md)。[TokenTracker算法许可](../prototypes/p0-ai/THIRD_PARTY_NOTICES.md)。

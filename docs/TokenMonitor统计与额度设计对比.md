# Token Monitor：统计、套餐重置与资源开销对比

核查日期：2026-11-08。Token Monitor 固定版本 `f0e394e226c3168b92862f466e11866845014c43`；TokenTracker 对照版本 `68c22580e0a62152cc7cfa2c476e36bf216904dc`。本轮只读源码，没有运行上游测试、读取用户凭据或测得两项目的实际资源占用。TokenTracker 详见[既有源码核查](AI用量采集源码核查.md)。

## 设计差异

| 维度 | TokenTracker | Token Monitor |
|---|---|---|
| 普通 Codex token | JS 直接解析 rollout、累计增量与请求去重 | vendored Tokscale CLI 负责 token 统计；JS 负责调度、归一化及会话补充 |
| 会话展示 | 独立会话分析与用量账本，保留父子关系 | Tokscale 用量结果加本地 rollout 元数据、上下文及活动状态 |
| 刷新策略 | 文件偏移、恢复状态和语义去重 | watch 事件合并后扫描 today，再更新完整周期锚点；至少每小时完整校准 |
| 运行结构 | Node 采集与解析服务 | Electron、Node、Tokscale 二进制、用量 Worker、watcher 子进程 |
| 套餐额度 | 与 token 用量、费用分别处理 | 独立 LimitsRuntime；在线 OAuth usage 优先、CLI app-server RPC 为条件性后备 |

Token Monitor 的进程隔离可以减少同步解析对界面的影响，也增加了进程、通信、打包与生命周期管理成本。本轮不能据源码结构断言其常驻内存或 CPU 更低。烛龙沿用已定 .NET 后台，参考统计与调度规则，不引入整套 Electron/Tokscale 运行结构。

## 套餐额度如何取得重置时间

用户所说“刷新时间”指套餐额度重置时间和剩余等待时间，与本地扫描完成时间分开。

Token Monitor 的主要路径为：读取 Codex 登录材料后访问在线 usage 接口，通常为 `https://chatgpt.com/backend-api/wham/usage`，兼容 `/api/codex/usage` 形状。OAuth 优先；特定未配置、未授权或瞬态故障才尝试 CLI app-server 的 `account/rateLimits/read`，并有账户/工作区约束。不是每次失败都无条件切换。

每个额度窗口分别保存 `limitId`、primary/secondary、`usedPercent`、`resetsAt` 和 `windowMinutes`，另外保留额外 metered-feature 窗口。不能将多个额度合为一个倒计时，也不能仅凭 primary/secondary 标签硬编码“5 小时/每周”。

共享契约把有效时间转为 ISO；剩余百分比由已用百分比推导。窗口缺失保留未知，不能当作零用量或满额。成功的新快照可以降低已用比例、改变重置时间，因此额度不遵循客户端单调增长假设。

## 控制刷新开销的方式

- 用量与额度独立调度；本地 token 变化不直接触发在线额度请求。
- 额度默认 5 分钟，固定间隔选项为 1/2/5/15/30 分钟；另有独立自适应模式。
- 到 `resetsAt + 30 秒`，仅对对应账户发起一次补刷；记录已尝试的边界，避免重复轮询同一个过期窗口。
- 用量 watch 的持续事件最长等待约 5 秒；完整校准至少每小时一次。watch 增量只扫描 today，并依赖追加日志与周期锚点的条件。
- Worker 与 watcher 的替换等待旧实例退出，避免重叠采集；目录轮询设 20,000 条目上限，超过后退回定时采集，避免无限大目录反复 stat。
- 上下文读取与完整 token 记账分开：初次主要读最新 1 MiB，查找 turn 边界最多延伸到 8 MiB；后续复用追加状态。

上述数值是上游实现，不自动成为烛龙预算。烛龙须以读取字节、耗时、内存和空闲扫描量验证自己的限制。

## 本轮烛龙采用边界

Codex 首个切片读取本地 rollout，统计 token 与会话；额度读取日志 `token_count.rate_limits` 快照，保留原始窗口身份、源事件时间及来源。倒计时依据真实 `resetsAt` 计算，到零显示“待新快照确认”，不能自动把剩余额度恢复为 100%。

没有新日志时，本地快照不会主动变新。界面应同时说明“本地日志快照”和快照时间；扫描成功时间不能替代额度观测时间。缺失窗口、时间或身份均保留未知。日志不提供足够账户身份时，不把多个会话的额度快照擅自归并为某个已确认账户，也不宣称多账户额度支持完成。

在线 OAuth/CLI 额度读取留给后续独立切片：明确账户范围、凭据使用、请求间隔和失败状态后再实施。本轮不读取 OAuth 凭据，不调用在线套餐接口，不启动 app-server。未来联网补刷与当前离线倒计时必须具有不同来源标记。

部分 Dots 创建的本地任务不写 rollout。Token Monitor 的补充采集器会连接云端 WebSocket，并对符合本机执行器身份的活跃线程执行 `thread/resume`；该调用会加载 runtime，可能初始化资源。它默认关闭，且只统计观察期间通知，首条、断线与恢复期间存在缺口。烛龙本轮不实现该路径，覆盖范围明确为“本地 rollout”，不能声称包含所有 Codex 桌面聊天用量。

Token Monitor 另有来自 `codex-resets.com` 的可选全局重置预测，具有独立缓存。它不是账户套餐额度权威，不纳入本轮重置倒计时依据。

## 固定版本源码

- [TokenTracker Codex 增量](https://github.com/xiufengsun/TokenTracker/blob/68c22580e0a62152cc7cfa2c476e36bf216904dc/src/lib/codex-token-usage.js)。
- [Token Monitor Tokscale 调用](https://github.com/Javis603/token-monitor/blob/f0e394e226c3168b92862f466e11866845014c43/src/shared/collector.js#L563)、[完整校准及 watch 限制](https://github.com/Javis603/token-monitor/blob/f0e394e226c3168b92862f466e11866845014c43/src/shared/collector.js#L2357)。
- [用量 Worker 生命周期](https://github.com/Javis603/token-monitor/blob/f0e394e226c3168b92862f466e11866845014c43/src/shared/usage/usageHost.js#L20)、[上下文增量读取](https://github.com/Javis603/token-monitor/blob/f0e394e226c3168b92862f466e11866845014c43/src/shared/providers/codex/sessionContext.js)。
- [Codex 在线 usage](https://github.com/Javis603/token-monitor/blob/f0e394e226c3168b92862f466e11866845014c43/src/shared/providers/codex/limits.js#L316)、[OAuth/RPC 选择](https://github.com/Javis603/token-monitor/blob/f0e394e226c3168b92862f466e11866845014c43/src/shared/providers/codex/limits.js#L1210)。
- [窗口映射](https://github.com/Javis603/token-monitor/blob/f0e394e226c3168b92862f466e11866845014c43/src/shared/providers/codex/limits.js#L527)、[共享额度契约](https://github.com/Javis603/token-monitor/blob/f0e394e226c3168b92862f466e11866845014c43/src/shared/limits/core.js#L169)。
- [额度刷新间隔](https://github.com/Javis603/token-monitor/blob/f0e394e226c3168b92862f466e11866845014c43/src/shared/limits/collector.js#L96)、[默认 5 分钟](https://github.com/Javis603/token-monitor/blob/f0e394e226c3168b92862f466e11866845014c43/src/shared/limits/core.js#L7)、[到期补刷](https://github.com/Javis603/token-monitor/blob/f0e394e226c3168b92862f466e11866845014c43/src/shared/limits/resetBoundary.js#L37)。
- [Codex 覆盖边界说明](https://github.com/Javis603/token-monitor/blob/f0e394e226c3168b92862f466e11866845014c43/docs/providers/codex.md#L20)、[补充采集器](https://github.com/Javis603/token-monitor/blob/f0e394e226c3168b92862f466e11866845014c43/src/shared/providers/codex/localUsageSource.js#L134)。
- [第三方重置预测](https://github.com/Javis603/token-monitor/blob/f0e394e226c3168b92862f466e11866845014c43/src/electron/providers/codex/resetForecast.js#L5)、[用量与额度分离](https://github.com/Javis603/token-monitor/blob/f0e394e226c3168b92862f466e11866845014c43/docs/architecture.md#L101)。

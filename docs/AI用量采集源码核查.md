# AI 用量采集：TokenTracker 源码核查与烛龙接入方向

核查日期：2026-11-08。TokenTracker 固定版本：`68c22580e0a62152cc7cfa2c476e36bf216904dc`。本轮读取源码，没有运行上游全量测试，也没有读取用户会话正文或凭据。

## 已确认方向

用户指定 Codex、Kimi、GLM，GLM 主要通过 ZCode 使用，倾向按会话统计。按首版已有“一个 AI 工具”范围，先交付 Codex，随后 Kimi、ZCode；不是把三者同时加入首版。GLM 是模型维度，ZCode 是采集来源，二者分别存储。

以会话组织展示，以请求用量事件记账：同一会话可有多个模型和多个请求，父会话可以展示包含子会话的汇总，但总账每条事件只计一次。保留来源、会话、父子关系、请求标识、模型、时间和原始计数语义；不根据正文长度推算账单，不复制完整提示词或回答。

## 上游怎么统计

| 来源 | 读取位置与记录 | 统计与去重 |
|---|---|---|
| Codex | rollout JSONL；会话元数据、模型归属、token_count 的 last_token_usage / total_token_usage | 累计基线求增量，重复快照不再相加；处理交错计数流、分叉回放。token_usage_record 与 token_count 不能各算一遍；压缩调用按 response_id 和上下文规则补计 |
| Kimi | 旧 .kimi/sessions 下的 wire.jsonl；新版 .kimi-code/sessions/.../agents/.../wire.jsonl | 旧 StatusUpdate.token_usage 按 message_id 去重；新版 step.end.usage 按 uuid 去重，并跟踪 config.update/profile.bind/llm.request 的模型；新版 step.end 与 usage.record 是同份用量，不双算 |
| ZCode | .zcode/cli/db/db.sqlite 的 session、model_usage，兼容旧 message 表 | completed 请求读取输入、输出、缓存与推理；用会话/请求标识、计数差分及窄指纹处理副本/分叉；从首条原生用量开始采用新表，其前保留旧表历史，不能两表全量相加 |

Codex 的输入含缓存，解析时拆开普通输入与缓存；推理是输出子集，不再加一次。ZCode 的原始 input 包含缓存读写，output 包含 reasoning；上游先拆成互斥分类再汇总。例如 input=1000（缓存读800、写100），output=200（推理50），普通输入100、普通输出150，总量仍是1200，不能加成2150。

会话分析与用量账本不是完全相同的数据结构。上游 ZCode 会话分析复用同一 normalized reader，另读 turn_usage/tool_usage 等得到交互计数；这些计数不再贡献 token。账本按来源、模型和 UTC 半小时桶聚合，会话分析另外保留会话/分钟用量及父子关系。

## 费用、额度和可靠性边界

- 费用一般用分类 token × 对应模型费率 / 1,000,000，另处理缓存、长上下文和服务档位。未知模型价格标为 unpriced，不把零估算误报免费；本地 token 不等于订阅剩余额度或真实扣款。
- 上游另有 zcode-limits / usage-limits 模块。额度来源与会话 token 分开核对，本轮未细审其认证与额度接口，不能宣称无需联网即可得出准确套餐余量。
- ZCode 按 provider 排除由独立 Claude/Codex/Gemini 采集器记账的内嵌代理，不能只根据 GLM 模型名区分来源。这个策略依赖来源完整性，烛龙须用跨来源夹具验证，不能无条件照抄。
- Codex 支持偏移、恢复状态和前缀哈希；Kimi 增量路径用文件偏移及 ID 集合。ZCode 本轮看到的 model_usage SQL 没有时间水位 WHERE，读取 completed 历史后再做语义去重；会话扫描会检查数据库及 WAL/SHM 的变化并复用缓存。语义增量不代表物理读取量已最优。
- Kimi 当前增量路径采用 readline，读后写 postStat.size；半行、边读边追加、同尺寸重写必须作为烛龙自己的验收样例，不以“参考了开源”替代验证。
- 当前上游 rollout.js 已约 1 MB，职责集中。参考规则与有许可的夹具，不直接引入整个 Node 服务或照搬单文件结构。引用代码/测试时复核 MIT 许可与版权声明。

## 烛龙的实现边界与验收

接入层分别实现 Codex、Kimi、ZCode 读取器，产出统一用量事件；一个账本负责幂等写入、游标与聚合。会话页和总览读取同一账本。记录来源时间与入库时间，明确 token 子集关系、价格来源和版本。旧库只读，先新库、用户主动一次性导入；不改写旧记录以迎合新统计口径。

Codex 首个切片的验收样例：普通追加、重复累计快照、多模型、父子代理、分叉回放、压缩计费、半行尾部、截断/重写、跨天、未知价格、重启、写库失败。Kimi 补旧新协议重叠与缓存字段；ZCode 补新旧表时间边界、SQLite 锁/WAL、Native/WSL 副本、内嵌代理重复与失败重试。全部用脱敏或合成夹具，人工核定期望总量。

P0-C 仍须冻结输入规模、冷热扫描、读取字节、延迟和全部自有进程资源；本轮源码核查不等于提供器已接入或性能通过。

## 固定版本源码定位

- [Codex 累计增量和交错流](https://github.com/xiufengsun/TokenTracker/blob/68c22580e0a62152cc7cfa2c476e36bf216904dc/src/lib/codex-token-usage.js)：consumeUsageDelta。
- [Codex 会话解析](https://github.com/xiufengsun/TokenTracker/blob/68c22580e0a62152cc7cfa2c476e36bf216904dc/src/lib/codex-rollout-parser.js#L470)：normalizeUsage、parseCodexRolloutFile。
- [Codex 压缩调用](https://github.com/xiufengsun/TokenTracker/blob/68c22580e0a62152cc7cfa2c476e36bf216904dc/src/lib/codex-usage-record.js)：普通 token_count 与压缩记录的去重边界。
- [Kimi 增量读取](https://github.com/xiufengsun/TokenTracker/blob/68c22580e0a62152cc7cfa2c476e36bf216904dc/src/lib/rollout.js#L10113)、[Kimi 会话分析](https://github.com/xiufengsun/TokenTracker/blob/68c22580e0a62152cc7cfa2c476e36bf216904dc/src/lib/kimi-session.js)。
- [ZCode 查询、归一化与新旧历史衔接](https://github.com/xiufengsun/TokenTracker/blob/68c22580e0a62152cc7cfa2c476e36bf216904dc/src/lib/rollout.js#L5931)。
- [ZCode 会话分析与共享读取器](https://github.com/xiufengsun/TokenTracker/blob/68c22580e0a62152cc7cfa2c476e36bf216904dc/src/lib/session-analytics.js#L1404)。
- [分类费用计算](https://github.com/xiufengsun/TokenTracker/blob/68c22580e0a62152cc7cfa2c476e36bf216904dc/src/lib/pricing/index.js#L344)。
- [ZCode 重复计数历史修正](https://github.com/xiufengsun/TokenTracker/blob/68c22580e0a62152cc7cfa2c476e36bf216904dc/docs/zcode-history-correction.md)。

此前 [重构设计](重构设计文档.md) 已记录 TokenTracker 的 905a54e 和 Token Monitor 的 9e0d0e9。保留旧核查点，本轮只更新 TokenTracker 的这条采集链路；未重新完成 Token Monitor 全项目审查。

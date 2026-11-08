# Codex 会话统计与套餐重置 · P0-C

独立 C#/.NET 10 命令行切片，读取本地 Codex rollout JSONL，写入显式指定的新 SQLite 库。没有常驻进程、托盘、网络采集或生产动作。不要把数据库参数指向旧烛龙、TokenTracker 或 Codex 自身数据库。

## 构建与运行

需要 .NET 10 SDK。以下从项目根执行；本机开发SDK可使用 out/p0-tools/dotnet-local/dotnet.exe 替代 dotnet，不需要改系统PATH。

```bat
dotnet build prototypes/p0-ai/P0.AI.csproj -c Release -p:UseSharedCompilation=false
dotnet prototypes/p0-ai/bin/Release/net10.0/P0.AI.dll self-test
dotnet prototypes/p0-ai/bin/Release/net10.0/P0.AI.dll scan out/p0-ai/codex.db "%USERPROFILE%/.codex/sessions"
dotnet prototypes/p0-ai/bin/Release/net10.0/P0.AI.dll report out/p0-ai/codex.db
```

scan也接受单个JSONL文件。数据库的父目录需预先存在。每批最多4 MiB，命令正文总预算最多16 MiB；budget_exhausted=true时再调用继续，保留已提交进度。扫描只输出读取量/新增数/耗时摘要，查询单独执行；输出最多各100行会话、来源和额度窗口，session_rows_limit等字段明确限制。统计输入/输出包含缓存/推理子集，不重复加到总量。

## 用量与额度的含义

- 总览按唯一事件记账；会话按事件关联展示，包含回放/继承历史，不能跨会话直接相加。
- ordinary token_count没有稳定请求ID时采用时间/模型/计数指纹；完全相同的独立事件仍有合并风险，等待真实脱敏样本对账。
- 套餐重置来自日志rate_limits。primary/secondary各自保留真实resets_at/window_minutes，不硬编码窗口长度。remaining_seconds是剩余等待，status=awaiting_refresh表示已到期但尚无新快照。
- 额度保留observed_at与snapshot_age_seconds，来源标local_rollout_account_unknown；不同来源会话分别保留。缺失为未知，不代表免费或满额；离线快照不能保证账户当前余额。
- 本地rollout不覆盖所有Codex桌面任务；不读auth.json、不启动app-server或恢复云端线程。在线额度与正式UI将在独立切片接入。
- 来源诊断保留RecordOnly、坏JSON行数等；record-only尚不能完整计账。历史中部改写同时追加、改写同时保持mtime等不列为已验收。

## 验证

self-test使用工作区out/p0-ai下的合成夹具，真实调用解析、扫描和SQLite事务；触发写入失败后重试、半行/追加、分批/重启、复制/分叉、交错累计流、压缩和额度边界。输出一次合成扫描的读取量/耗时/CPU/末尾内存，不等同长期常驻预算。结果见[方案与证据](../../docs/P0Codex统计切片.md)。上游算法与许可见[第三方说明](THIRD_PARTY_NOTICES.md)，[两项目对比](../../docs/TokenMonitor统计与额度设计对比.md)保留来源依据。

## Codex 桌面累计快照

部分桌面会话不写token_count。可运行 `dotnet prototypes/p0-ai/bin/Release/net10.0/P0.AI.dll metadata out/p0-ai/codex.db "%USERPROFILE%/.codex/state_5.sqlite"`，然后report查询metadata_sessions。该命令以只读连接查询threads.id/tokens_used/updated_at，支持源WAL；目标与源路径不可相同。累计快照重复读取采用覆盖，不相加；没有输入/输出拆分，不能用于今日账单。metadata_sessions不与请求总账合并，避免重复计算。0计数也只是源库快照，不证明不存在其它未覆盖用量。

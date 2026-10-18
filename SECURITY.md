# 安全说明

## 报告问题

- 普通缺陷与功能建议：通过本仓库的 GitHub Issues 提交。
- 尚未修复的安全漏洞：请通过 GitHub Security Advisories 私密报告（`https://github.com/JoshuaAWong/zhulong/security/advisories/new`），不要先在公开 Issue 披露细节。
- 提交任何内容前，请移除密钥、Cookie、个人路径、进程命令行中的敏感参数及日志中的隐私信息。

## 安全边界

- 面板只监听 `127.0.0.1`，不提供配置写入接口。
- 唯一动作通道为 `POST /api/action`，要求 CSRF 令牌，并校验 Host、Origin 与动作白名单。
- 进程结束采用 fail-closed：白名单、NEVER_KILL 硬阻断、存活时长、PID 复用检测与频次熔断；配置了 `path_contains` 的条目还会执行路径校验，未配置的 AUTOKILL 字符串条目不具备同名路径防伪保证。
- 不处置安全软件、系统关键进程及烛龙自身；不要求管理员权限。
- 第三方依赖仅限 psutil、pystray、Pillow、winotify；前端使用本地 Chart.js，不依赖 CDN。

## 使用注意

- 不要把 `data/`、`zhulong/state/`、日志、数据库、个人配置或密钥提交到仓库。
- 修改白名单前请确认进程身份与路径；同名并不足以证明进程可信。
- 如果必须远程访问面板，请自行增加可信反向代理与认证，不要直接把 `127.0.0.1:8890` 暴露到局域网或公网。

# Kimi 账号直连授权核查

2026-11-16 核对。烛龙可将 Kimi 的在线账号绑定和额度查询做成独立功能，采用浏览器设备授权。现有会话账本继续读取本机已有的 Kimi 会话记录。在线额度和本机 Token 用量分别标明来源。

## Kimi Code Monitor 如何绑定多个账号

- 浏览器扩展使用 Kimi 的 OAuth 设备流程。它向 `auth.kimi.com/api/oauth/device_authorization` 请求设备码，打开 Kimi 官方授权页，再向 `/api/oauth/token` 轮询。等待状态继续轮询，收到 `slow_down` 会延长间隔。取到凭据后向 `api.kimi.com/coding/v1/usages` 查询额度。[设备授权](https://github.com/bfjnbvf/kimi-code-monitor/blob/da493973944171122e063fb5d37413a0c21c82ef/src/background/oauth.js#L79-L121) · [启动授权](https://github.com/bfjnbvf/kimi-code-monitor/blob/da493973944171122e063fb5d37413a0c21c82ef/src/background/oauth.js#L340-L437) · [额度查询](https://github.com/bfjnbvf/kimi-code-monitor/blob/da493973944171122e063fb5d37413a0c21c82ef/src/background/quota.js)
- 「添加账户」会发起一轮新设备授权。成功后为该记录生成本地 ID，并保存备注名和加密凭据。重新授权指定记录时覆盖该记录的凭据。刷新请求按记录 ID 合并，某一条需要重登时其他记录仍可使用。[账户存储和重绑](https://github.com/bfjnbvf/kimi-code-monitor/blob/da493973944171122e063fb5d37413a0c21c82ef/src/background/oauth.js#L131-L285) · [凭据加密](https://github.com/bfjnbvf/kimi-code-monitor/blob/da493973944171122e063fb5d37413a0c21c82ef/src/background/vault.js) · [凭据刷新](https://github.com/bfjnbvf/kimi-code-monitor/blob/da493973944171122e063fb5d37413a0c21c82ef/src/background/oauth.js#L590-L635)
- 授权链接用普通浏览器标签页打开。扩展在保存新记录前没有查 Kimi 真实账号 ID，所以浏览器继续使用已登录的账号时，两条本地记录可能指向同一个 Kimi 账号。此处多账号指扩展内多份授权记录。该项目的桌面补丁则从已安装的 Kimi 桌面客户端本地接口读取当前账号额度。[创建浏览器标签与添加账户](https://github.com/bfjnbvf/kimi-code-monitor/blob/da493973944171122e063fb5d37413a0c21c82ef/src/background/oauth.js#L421-L485) · [桌面补丁额度读取](https://github.com/bfjnbvf/kimi-code-monitor/blob/da493973944171122e063fb5d37413a0c21c82ef/src/panel-app/direct.js#L66-L86)

## TokenTracker 和 Token Monitor 的参照范围

烛龙此前参考的是 `xiufengsun/TokenTracker`。核对的版本根据目录、命名配置或地区定位已有 Kimi 凭据，直接读取及续期其中的 Token。其额度汇总只有一个 Kimi 读取入口。另一个项目 `Javis603/token-monitor` 提供 Codex 多账号管理。核对的 Kimi 设置提供 Coding Plan API Key 和可选的网页 access token，服务于同一项 Kimi 额度读取。[TokenTracker 配置选择](https://github.com/xiufengsun/TokenTracker/blob/93632c64fedd972bc381502812146c9d061cd75a/src/lib/kimi-profile.js#L25-L58) · [TokenTracker Kimi 额度](https://github.com/xiufengsun/TokenTracker/blob/93632c64fedd972bc381502812146c9d061cd75a/src/lib/usage-limits.js#L1034-L1148) · [Token Monitor Codex 多账号](https://github.com/Javis603/token-monitor/blob/bff6b3bc3ee0b10acbd1ac12ec1a845b45a3ccf9/src/electron/renderer/index.html#L867-L899) · [Token Monitor Kimi 设置](https://github.com/Javis603/token-monitor/blob/bff6b3bc3ee0b10acbd1ac12ec1a845b45a3ccf9/src/electron/renderer/index.html#L1050-L1084)

## 烛龙接入建议与验证边界

1. 在原生「账户来源设置」新增「授权 Kimi 账号」，每次新增或重新绑定均请求新设备码。显示官方授权地址和验证码，用户可以用不同浏览器配置或无痕窗口登录指定账号。
2. 授权完成后先访问对应地区的 `/coding/v1/me`，按服务端用户 ID 和地区校验重复，再把该账号自己的凭据保存到 Windows 凭据管理器。取消、超时、取不到身份或绑定重复时保留当前绑定，界面提示实际状态。[Kimi 官方客户端的账号接口](https://github.com/MoonshotAI/kimi-code/blob/f082f04a093a3e71a2263c5461d52507dc31f83a/packages/oauth/src/managed-userinfo.ts#L46-L114)
3. 按账号按需读取 `/coding/v1/usages`，并独立续期。云端额度字段与现有 CLI 本地响应的结构不同，需按官方客户端源码适配。国内和国际按地区成对选择授权、账号与额度域名。现有历史会话仍按本机来源展示，新增在线授权不作为旧会话归属证据。[地区端点](https://github.com/MoonshotAI/kimi-code/blob/f082f04a093a3e71a2263c5461d52507dc31f83a/packages/oauth/src/region.ts#L56-L73) · [额度字段映射](https://github.com/MoonshotAI/kimi-code/blob/f082f04a093a3e71a2263c5461d52507dc31f83a/packages/oauth/src/managed-usage.ts#L108-L140)

实测用 HTTPS 直连国内设备授权端点，未启动 CLI。官方返回 HTTP 200 和设备码，首次按服务端间隔轮询返回 `authorization_pending`。试验到此结束，两个真实账号授权、额度读取和凭据续期仍待验证。

浏览器扩展复用 Kimi CLI 的公开客户端 ID，并上报 CLI 的平台标识。Kimi 官方公开的第三方开发工具接入文档介绍的是 API Key。设备授权能发起，第三方桌面应用的专用 OAuth 客户端及接口稳定性仍未确认。启用前应在隔离版本验证两个真实账号分别绑定、重复拒绝、额度读取、重启后续期和授权失效。[Kimi 官方客户端常量](https://github.com/MoonshotAI/kimi-code/blob/f082f04a093a3e71a2263c5461d52507dc31f83a/packages/oauth/src/constants.ts#L3-L18) · [Kimi 第三方开发工具接入说明](https://www.kimi.com/en/help/kimi-code/membership-guide#api-access)

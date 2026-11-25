# RDC Spike 调查报告（T02，G0-RDC）

**结论：部分可复用但缺GUI。**

> 即使 consumer Muse 走完公开 OAuth 授权并连上 Remote Desktop Commander，它也只能拿到文件 / 终端 / 进程能力——官方工具表里没有任何截图、鼠标、键盘工具，因此不能作为本项目的 GUI 主线，只能保留为开发辅助。OAuth 本身能否在 consumer Muse 账号上走完，本轮尚未验证（见“没测什么”），那是唯一待 Sam 完成的动作。

- 调查日期：2026-11-13（Europe/London；探测执行于 01:49–01:53 UTC）
- 调查员：Windows Bridge 项目 RDC spike 调查员（只读调查；未取任何他人 token，未登录 Sam 的任何账号，未执行 Windows 命令）
- 门依据：`docs/03_Muse_Integration_Gates.md` §四 G0-RDC；`research/muse_platform.md` §9 追加部分（S33–S34）；`research/SOURCE_INDEX.md` SRC033 / SRC034

## 1. 测了什么

| # | 检查项 | 方法 | 结果 |
|---|---|---|---|
| 1 | 官方 README 全文 | `browser.open` GitHub blob 页（抓取于 2026-11-13 01:49 UTC） | 全文 244 行已读，见 §2 |
| 2 | 官方 SECURITY 全文 | `browser.open` GitHub blob 页（同上） | 全文 60 行已读，见 §4 |
| 3 | 官方定价页 | `browser.open` https://desktopcommander.app/pricing/ | Free $0/月 10,000 tool calls；Pro $20/月不限量；本地 MCP 免费不限量，见 §5 |
| 4 | `GET https://mcp.desktopcommander.app/mcp`（无凭据） | `curl` | **HTTP 401**，`{"error":"Unauthorized","message":"Authorization header required"}`；`WWW-Authenticate` 指向 `/.well-known/oauth-protected-resource/mcp`；响应头 `x-mcp-version: 2024-11-05`，`x-server-type: Supabase-MCP-HTTP` |
| 5 | `OPTIONS https://mcp.desktopcommander.app/mcp` | `curl` | **HTTP 204**，`Allow: GET,POST,PUT,DELETE,OPTIONS` |
| 6 | `GET /.well-known/oauth-protected-resource/mcp` | `curl` | **HTTP 200**，`{"resource":"https://mcp.desktopcommander.app","resource_server":"https://mcp.desktopcommander.app","authorization_servers":["https://mcp.desktopcommander.app"],"scopes_supported":["mcp:tools","desktop:auth"],"bearer_methods_supported":["header"],"resource_documentation":"https://mcp.desktopcommander.app/docs"}` |
| 7 | `GET /.well-known/oauth-authorization-server`（mcp host） | `curl` | **HTTP 200**，`{"issuer":"https://mcp.desktopcommander.app","authorization_endpoint":"https://mcp.desktopcommander.app/authorize","token_endpoint":"https://mcp.desktopcommander.app/token","registration_endpoint":"https://mcp.desktopcommander.app/register","response_types_supported":["code"],"grant_types_supported":["authorization_code","refresh_token"],"code_challenge_methods_supported":["S256"],"scopes_supported":["mcp:tools","desktop:auth"],"token_endpoint_auth_methods_supported":["none","client_secret_post"],"resource_indicators_supported":true}` |
| 8 | `POST /mcp` initialize（无凭据，一次探测） | `curl`，JSON-RPC `initialize`，`protocolVersion 2024-11-05` | **HTTP 401**（66 字节，与 #4 同一错误体；首次尝试输出被代理 CONNECT 行截断，已重跑确认）——服务端在鉴权前不处理 MCP 握手，符合预期 |
| 9 | `GET https://mcp.desktopcommander.app/docs` 与 `/` | `curl` | 均为 **HTTP 200** `text/html`（文档站可达，未深入爬取） |
| 10 | `GET https://auth.desktopcommander.app/.well-known/openid-configuration` | `curl` | **HTTP 200** 但返回 HTML 页面而非 JSON discovery 文档——真实的 OAuth discovery 在 `mcp.desktopcommander.app` 上（见 #7），不要以 `auth.` 主机为准 |

以上探测均未发送任何凭据、token、cookie 或 Authorization 头；未做任何绕过尝试；未对生产服务做批量请求（SECURITY.md 明确请勿做 volumetric testing）。

## 2. 接口来源（官方 README，2026-11-13 实读）

- 仓库：https://github.com/desktop-commander/remote-desktop-commander
- README：https://github.com/desktop-commander/remote-desktop-commander/blob/main/README.md（全文 244 行，抓取 2026-11-13 01:49 UTC）
- SECURITY：https://github.com/desktop-commander/remote-desktop-commander/blob/main/SECURITY.md（全文 60 行，同上）
- 远程 MCP endpoint：`https://mcp.desktopcommander.app/mcp`
- Transport：Streamable HTTP；认证：OAuth 2.0 + PKCE（README 原文：“Authentication is standard OAuth 2.0 with PKCE; devices pair via the OAuth device authorization flow”）
- Dashboard：https://mcp.desktopcommander.app
- 状态：**currently in beta**
- 设备 agent：`npx @wonderwhy-er/desktop-commander@latest remote`（需 Node.js 18+，每台被控电脑各跑一个终端；浏览器打开核对页面显示 pairing code，与终端打印的 code 一致后确认，终端报 "Device ready"；关闭终端即断开访问）
- 支持系统：macOS、Windows、Linux
- 隐私政策 / 服务条款：`legal.desktopcommander.app`（本次未全文细读，仅记录链接存在）
- License：仓库仅含 docs/manifests，托管服务为 proprietary（README 原文：“This repository hosts the public manifests, documentation and issue tracker for the hosted service. The service implementation is not open source.”）

### 2.1 公开工具清单（README “Available tools” 表原文）

| 类别 | 工具 |
|---|---|
| Read & explore | Read File, Read Multiple Files, List Directory, Get File Info |
| Search | Start Search, Get More Search Results, Stop Search, List Active Searches |
| Write & edit | Write File, Edit File Block, Move or Rename File, Create Directory, Write or Modify PDF |
| Terminal & processes | Start Process, Send Input to Process, Read Process Output, List Active Sessions, List System Processes, Terminate Session, Kill System Process |
| Devices & account | List Paired Devices, Current User Info, Ping Device, Shutdown Device Agent |
| Configuration & help | Get Configuration, Set Configuration Value, Get Prompt Library, Get Recent Tool Activity, Get Usage Statistics, Submit Feedback |

**没有截图、鼠标、键盘、窗口、UI Automation 类工具。** README “What you can do” 只列建功能、管理开发环境、探索代码库、原地编辑文档、管理上下文、多机控制。README 未承诺任何桌面视觉或 GUI 操作能力。

### 2.2 官方列出的支持客户端（README 原文）

- claude.ai / Claude Desktop（Settings → Connectors → Add custom connector）
- Claude Code（`claude mcp add --transport http …`）
- ChatGPT（Settings → Apps & Connectors → Advanced settings → 启用 Developer mode，需含该功能的套餐）
- Cursor（一键 Add to Cursor 或 `~/.cursor/mcp.json`）
- VS Code（`copilot plugin install desktop-commander/remote-desktop-commander` 或 `.vscode/mcp.json`）
- Gemini CLI（`gemini extensions install …`）

**Meta Muse 不在官方支持客户端列表中。** README 没有任何关于 Muse / Custom Connector 的说明。“支持能接远程 MCP 的 AI 客户端”不等于“consumer Muse 已验证可接”。

## 3. 没测什么（全部保留 NOT_RUN，不得误记为通过或失败）

1. **未在 consumer Muse 账号上创建 Custom Connector，也未走完 RDC 的 OAuth 授权流程。** 这是 G0-RDC §4.2 要求的核心验证，只能由 Sam 本人在 Muse 的正常安全凭据入口完成；本调查按任务边界不登录、不建连接器、不索要授权。
2. **未列工具、未列设备、未读任何设备在线状态。** 无凭据时服务端正确返回 401，无法也不应绕过；Sam 的 Windows 设备当前是否在线、是否已装 device agent、是否已配对——全部未知，**离线设备按门文档保持 NOT_RUN / 环境 BLOCKED，绝不误判为不兼容**。
3. **未在 Sam 的 Windows 上运行 `npx … remote`，未执行任何 Windows 命令，未读取任何 Windows 文件。**
4. **未复用、未导出、未索取任何 ChatGPT 绑定凭据。** ChatGPT 已绑定 ≠ Muse 可复用：RDC 的授权是按 AI 客户端账号分别建立的 OAuth 授权，Muse 需要它自己获准的凭据绑定（门文档 §4.1 第 4 条）。
5. **未在 Muse VM 本地跑 stdio 版 Desktop Commander。** 即使跑了，它控制的也只是 Muse 的 Linux VM，不能控制 Sam 的 Windows；本地 stdio 成功不能记为远程复用成功。
6. **未验证 consumer Muse 的图片查看能力与 RDC 的配合。** 无关——RDC 根本没有截图工具可供验证。
7. 定价页 FAQ 外链的 Help center（plans and tool call limits / renewals, cancellations and refunds）未逐页细读；套餐细节以定价页正文为准。

## 4. 授权范围与信任边界（官方 SECURITY 全文要点）

- 工具以被配对机器上**用户账号的全部权限**执行；服务是“所连 AI 客户端所要求之事的放大器”，能做该账号能做的任何事。
- **allowedDirectories / blockedCommands 只是 guardrails，不是针对恶意客户端的 sandbox**（SECURITY 原文：“Built-in restrictions are guardrails, not a sandbox… they are not a security boundary against a malicious or compromised client.”）
- 配对经 OAuth device authorization flow 核对短码：只在浏览器 code 与自己终端 code 一致时批准。
- 机器仅在 device agent 运行时可达；可随时在 dashboard 单独或全部撤销设备。
- 传输 HTTPS/TLS；中继调用与结果是 transient、送达后不保留；服务只收集操作 telemetry（工具名与成功/失败等元数据，不含文件内容），见其隐私政策。
- 报告范围：`mcp.desktopcommander.app` / `auth.desktopcommander.app`（OAuth、MCP endpoint、设备配对、会话处理）及 device agent。
- 本调查未发现、未测试任何 RDC 安全漏洞；以上仅为官方文档陈述的信任模型摘要。

## 5. 是否需要新订阅

- **RDC 本身：不需要新订阅即可开始验证。** 官方定价页（https://desktopcommander.app/pricing/，实读 2026-11-13）：Free $0/月，含 10,000 tool calls/月，可接 ChatGPT、Claude 与任何 MCP 客户端、可连设备、社区支持；Pro $20/月，tool calls 不限量、优先支持。本地 MCP server 免费开源、无账号、无月度上限。
- Free 额度是否够 spike 与后续开发辅助使用，取决于实际调用量；超过 10,000 calls/月才需 Pro（$20/月）。本次调查未产生任何 RDC 调用（全部探测均在鉴权前被拒），未消耗额度。
- **Muse 侧：不需要另购模型 API key。** 主线本来就不要求 API key；RDC 路线也不改变这一点。
- 注意：RDC 账号与 Muse 订阅是两套独立的东西；“ChatGPT 已绑定”不产生可转移到 Muse 的额度或授权。

## 6. 截图视觉是否过

**否——不是失败，而是 RDC 根本不提供该能力。**

- 官方工具表无截图工具，因此无法通过 RDC 取得 Windows 桌面 PNG，也就无法走 G0-C 的 PNG 视觉门。截图视觉必须由自有主线（Windows 托盘 agent + relay + artifact/鉴权图片端点）实现。
- 这是选择主线而非 RDC 的决定性原因之一，与 OAuth 能否走通无关：即使 OAuth 明天就通，GUI 目标仍需自有执行层。

## 7. 判定：consumer Muse 能否通过正常公开授权复用 RDC

**结论：部分可复用但缺GUI。**

分步阻断点分析：

| 步骤 | 状态 | 依据 |
|---|---|---|
| ① 服务端公开入口存在且行为标准 | ✅ 已证实 | endpoint、Streamable HTTP、RFC 8414 资源元数据、RFC 9728 授权服务器元数据（PKCE S256、public client 可用 `token_endpoint_auth_methods_supported: ["none", …]`、grants `authorization_code` + `refresh_token`）均实测返回；无凭据统一 401，错误体规范 |
| ② consumer Muse 完成 Custom Connector + OAuth 授权 | ❓ 未验证（NOT_RUN） | 官方客户端列表无 Muse；Meta Help 称 Muse 支持 Custom Connector 与安全凭据入口，但**本账号对该服务的 OAuth 流程尚未走过**。这是唯一待 Sam 的动作（见 §9），不是技术不兼容的证据 |
| ③ 列设备 / 确认 Sam 的 Windows 在线 | ❓ 未验证（NOT_RUN） | 需先完成 ②；且需 Sam 的 Windows 上 device agent 正在运行。设备离线 ≠ 不兼容 |
| ④ 文件 / 终端 / 进程能力 | ✅ 文档已证实（待 ②③ 后实测） | README 工具表明细，见 §2.1 |
| ⑤ 截图 / 鼠标 / 键盘 / 窗口 GUI 能力 | ❌ 官方文档明确缺失 | 工具表无此类工具；README 能力描述无 GUI 操作。**此缺失与 OAuth 成败无关，不可通过“换个客户端”绕过** |
| ⑥ 本地 `npx` stdio 版能否替代 | ❌ 概念错误 | 在 Muse VM 启动的 stdio MCP 只控制 Muse VM 自身，不能控制 Sam 的 Windows；远程复用必须走托管 relay + 设备 agent |

因此：RDC **不能**成为 GUI 主线；**可以**作为文件/终端开发辅助（若 Sam 完成一次授权且设备在线）。四选一中不选“可复用”（GUI 缺口已证实），不选“当前账号阻断”（无任何账号被拒的证据——401 只是无凭据的正常响应），不选“证据不足”（服务端入口与工具缺口的证据已经充分，唯一未验证的是 Muse 侧 OAuth 一步）。

## 8. 主线选择

**继续自有主线：Windows 托盘程序 + HTTPS 桥（relay）+ Muse VM Python CLI + Custom Connector + Skill。**

- RDC 保留为**开发辅助分支**：待 Sam 完成授权后，可用于在开发期对 Windows 做文件/终端操作（例如查看日志、跑测试），但不进入面向普通用户的默认安装体验，不作为 GUI 验收证据。
- 不把 RDC 的 device agent（`npx … remote`，需 Node 18+、常驻终端）写进普通用户安装步骤：它要求用户装 Node、手动跑终端、常驻进程，与“双击安装、登录绑定、一次允许后使用”的目标不符。
- 不 fork、不自托管 RDC：托管 relay 不是开源（README 与 LICENSE 均明确），只有 docs/manifests/issues 在仓库里；本地引擎另有开源仓库（DesktopCommanderMCP），那是 stdio 本地 MCP，与远程 relay 是两回事。

## 9. 需要 Sam 做的唯一待办（不要现在索要，先完成后置准备）

> 在 Windows 托盘程序 + HTTPS 桥主线就绪、且 RDC 开发辅助分支被排期时，Sam 在 Windows 上做两件事（约 5 分钟，均在他自己的设备与账号上完成，Muse 不碰他的密码与 token）：
>
> 1. 在 Windows 上装 Node.js 18+（如尚未安装），跑 `npx @wonderwhy-er/desktop-commander@latest remote`，在浏览器核对 pairing code 与终端一致后确认，保持终端运行；
> 2. 在 consumer Muse 里按门文档 §4.2 的请求创建 Remote Desktop Commander 的 Custom Connector（`https://mcp.desktopcommander.app/mcp`），走正常安全凭据入口完成 OAuth 授权，授权后只列工具与设备在线状态，不读文件、不执行命令。
>
> 完成后 Muse 自动继续：列工具/列设备、确认只读范围、再验证撤销后调用被拒（G0-RDC §4.2 第 6 步）。

**现在不要索要这一步。** 在主线 G0-A 自有 HTTPS 探针完成之前，RDC 授权不是关键路径；提前索要只会消耗 Sam 一次在场操作而不推进主线。

## 10. 引用

- Remote Desktop Commander README：https://github.com/desktop-commander/remote-desktop-commander/blob/main/README.md（全文，抓取 2026-11-13 01:49 UTC）
- Remote Desktop Commander SECURITY：https://github.com/desktop-commander/remote-desktop-commander/blob/main/SECURITY.md（全文，抓取 2026-11-13 01:49 UTC）
- Remote Desktop Commander 定价：https://desktopcommander.app/pricing/（实读 2026-11-13）
- MCP endpoint：https://mcp.desktopcommander.app/mcp
- OAuth protected resource metadata：https://mcp.desktopcommander.app/.well-known/oauth-protected-resource/mcp（HTTP 200，2026-11-13 01:50 UTC）
- OAuth authorization server metadata：https://mcp.desktopcommander.app/.well-known/oauth-authorization-server（HTTP 200，2026-11-13 01:52 UTC）
- 门文档：`docs/03_Muse_Integration_Gates.md` §四 G0-RDC；研究：`research/muse_platform.md` §9 追加部分（S33–S34）；`research/SOURCE_INDEX.md` SRC033 / SRC034
- 探测原始记录：本文件 §1 表格（HTTP 状态、响应体摘要、时间戳）。探测未发送任何凭据；无 secret 需要脱敏。本文件不含任何 token、授权码或设备标识。

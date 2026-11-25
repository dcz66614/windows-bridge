# consumer_flow.md — G0-00（第一轮，2026-11-13）

## 当前执行环境就是目标 consumer Muse
- 产品：Meta Muse（个人智能体），入口 ai.meta.com/muse / muse.ai，web 会话；模型 Muse Spark。
- 运行位置：Muse 专属 Linux 云电脑（Secure VM），非用户 Windows 本机。
- 区分：不是 Muse Code（另一产品，有原生 MCP/Skills/插件文档），不是 Muse Spark API 直接调用。本包主线目标客户端就是当前这个 consumer Muse；Muse Code/API 只作独立适配项。
- 官方帮助入口（包内研究 S02/S05/S06）：Meta Help “How Muse works with Connectors”、platform.muse.ai/docs。Custom Connector 可在聊天中请求创建，凭据走安全凭据存储（Secure Credentials Store），网络与连接器动作受 Sentinel 独立审批。

## 本会话真实可见的能力（已核实）
- 工具：shell、文件读写、浏览器（search/open/browse）、subagent、GitHub 连接器（status connected）、Gmail、Google Drive 等；图片文件可直接读取并识别（见 G0-C 本地三轮）。
- 出网：经 VM 出口代理访问公网 HTTPS；本机 loopback 服务可由本 VM 的 Python 客户端访问，但这不是 Muse connector 的出网路径。
- 安全凭据：Custom Connector 凭据应走 credentials.request_api_access（API key / OAuth 2.0 authorization code）；不把 token 写进聊天、Skill、README 或 URL。MWB_ACCESS_TOKEN 仅为本地 muse_client 测试注入接口。

## 尚未证实（NOT_RUN，须走完才算）
- 在本账号真实创建一个指向已部署 HTTPS 服务的 Custom Connector，并经安全凭据卡授权、调用、撤销。
- 新会话自动发现并复用该 connector/Skill（G0-D）。
- 经 connector 返回的 PNG 进入视觉上下文（G0-C 的 connector 路径；本地文件读取已证实的是模型视觉能力本身，不是 connector 图片传输）。
- Sentinel 审批次数/延迟（G0-A 要求 20 次只读探针统计）。

## 结论
G0-00 身份确认：PASS（当前即 consumer Muse）。其余 G0 门第一轮状态见 gate_report.json。

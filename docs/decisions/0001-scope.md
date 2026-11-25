# ADR 0001 — 范围与默认技术栈（T00，2026-11-13）

- 状态：已采纳（沿用包内 docs/02 §2 固定决策，未变更）
- 目标：普通 Windows 用户双击安装、登录绑定、一次允许后使用；无需命令行、WSL、临时隧道。
- 目标客户端：用户已有的 consumer Meta Muse；Muse Code/API 为独立适配项。
- 主接入：Custom Connector + HTTPS API；Python 标准库客户端作落地参考。
- Windows 宿主：.NET 10 LTS、WinForms、普通用户交互会话、asInvoker。
- UIA：FlaUI.UIA3 隔离工作进程；输入 Win32 SendInput，物理像素映射。
- 中继：ASP.NET Core 10 单实例；设备主动出站 WSS；EF Core SQLite + WAL。
- 身份：ASP.NET Core Identity + OpenIddict；原生 Windows 系统浏览器 Auth Code + PKCE 优先。
- 图片：按需 PNG，最长边默认 1920，单图上限 4MiB，同源授权下载。
- 控制：每设备一个写租约，动作串行；未知结果标 uncertain，永不自动重放输入。
- 首版不做：无头/锁屏控制、UAC 绕过、默认管理员、持续视频流、多实例集群、任意 shell（T24 可选）。
- 变更规则：任何路线变更须另写 ADR，记录原假设、实际失败、候选比较与受影响接口；不得悄悄换语言/框架/协议。

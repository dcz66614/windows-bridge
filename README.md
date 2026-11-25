# Muse Windows Bridge — README（预发布）

让 consumer Muse 操作 Windows 真机的桥：Windows 托盘 agent + HTTPS 中继。
普通 Windows 用户双击安装、登录绑定、一次允许后使用。

## 状态：预发布，未在真机验证

- 代码完成：全方案 **158 测试全绿**，0 警告 0 错误（2026-11-25 复核）。
- **以下均未验证**：截图、UIA、SendInput、托盘暂停联动、安装流程——
  代码已写、只交叉编译通过，真机门（T13–T19）与连接器实测均为 NOT_RUN。
- 生产中继未部署；Custom Connector 未注册。

## 结构

- `src/Bridge.WindowsAgent/` — WinForms 托盘 agent（含自安装程序）
- `src/Bridge.DesktopWorker/` — FlaUI.UIA3 工作进程（命名管道，仅当前用户）
- `src/Bridge.DesktopCore/` — 纯逻辑：坐标映射、校验、命令 journal、受限文件访问
- `src/Bridge.RelayService/` — ASP.NET Core 中继：Identity + OpenIddict、
  设备配对、WSS 通道、同意会话与控制租约
- `src/Bridge.Relay/`、`src/Bridge.Contracts/`、`src/Bridge.DeviceIdentity/`、
  `src/Bridge.DeviceChannel/` — 协议与状态机
- `deploy/` — Docker + Caddy 部署包与运维手册
- `release/` — SBOM、发布说明、GitHub 上传清单
- `connector/`、`skills/`、`workflows/` — 连接器定义、操作 Skill、端到端工作流

## 安装（Windows）

1. 下载 `Bridge-Windows-0.1.0-win-x64.zip`，解压后双击
   `Bridge.WindowsAgent.exe`，按安装对话框走。
2. 安装包未签名：SmartScreen 很可能弹警告。不要关闭系统保护；
   如继续，在警告上点“更多信息 → 仍要运行”（只针对这一个文件）。

## 安全模型（摘要）

设备本地生成 P-256 密钥对，私钥不出设备；配对用 8 位短码（5 分钟有效）
+ 一次性密钥；控制需本地同意会话与单调租约；本地暂停/锁屏优先于
一切远程命令，且只能在本地恢复；文件访问默认关闭，需用户授权目录。

## 许可证

MIT（自有代码）。依赖许可证见 `release/SBOM.md`（162 个包：
MIT 为主，OpenIddict 系 Apache-2.0，5 个 BSD-3-Clause，无 GPL/AGPL）。

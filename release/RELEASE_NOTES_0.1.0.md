# Windows Bridge 0.1.0 — 发布说明

状态：**预发布（pre-release）**。本版本尚未在真实 Windows 真机与
真实 consumer Muse 上通过验收；以下内容如实记录已验证与未验证的部分。

## 版本与产物

- 版本：0.1.0（中继 `/healthz` 报告 `0.1.0-t11`）。
- Windows 安装包：`artifacts/Bridge-Windows-0.1.0-win-x64.zip`
  （293 个文件，56M，SHA256 `12682c4f9e08a916da4ea6e48a3efcb7e9d32bc668887ba2c58cd1b895d9519f`）。
- 中继（Linux x64 自包含）：`artifacts/relay-linux-x64/`（157M）。
- 源码：`~/workspace/windows-bridge/work/`（Bridge.sln，14 个工程）。
- 许可证：本项目自有代码的许可证**待 Sam 决定**（P021 前例为 MIT；
  在决定前不要以 MIT 名义分发）。依赖许可证见 `release/SBOM.md`
  （162 个包：MIT 为主，OpenIddict 系 Apache-2.0，5 个 BSD-3-Clause，
  无 GPL/AGPL；xunit.abstractions 只有许可证 URL，未解析——公开前确认）。

## 支持系统

- Windows agent：Windows 10 19041+ / Windows 11 x64（.NET 10
  WinForms，自包含，无需另装运行时）。ARM64 未测试，不列为支持。
- 中继：Linux x64（Docker，见 `deploy/`）或任何能跑 .NET 10 的主机。
- 浏览器：配对确认页需要现代浏览器；Muse 连接器需要 Sam 的
  Muse 账号支持 Custom Connector。

## 已验证（Linux / 协议层）

- 全方案 158 测试全绿：contracts 23、relay 16、deviceidentity 5、
  relayservice 59（含 Production 启动门 3 个）、desktopcore 55。
  全方案 0 警告 0 错误。
- 协议：11 字段信封、幂等、租约、暂停/锁屏 423、WSS 握手与驱逐、
  配对（8 位短码 + 一次性密钥）、受限文件 API、安装器布局、
  更新清单签名 + 包散列验证。

## 已知限制（未验证，不可宣称为已实现）

- **所有 Windows 真机行为均未验证**：截图、UIA、SendInput、
  托盘暂停联动、安装流程、快捷方式与开机启动项——代码已写、
  只交叉编译通过。真机门（T13–T19）与五人安装试用（T29）待做。
- **生产中继未部署**：域名待定；Docker 镜像未在本机构建
  （本 VM 无 Docker）；Custom Connector 未注册；Skill 未对真实
  连接器实测。
- 未签名：可执行程序与安装包均未代码签名（T28 后续）；不要让
  用户关闭系统保护来通过 SmartScreen。
- 中继单实例、无 EF 迁移（`EnsureCreated`）、无命令记录保留策略、
  应用内无限流（限流在边缘做）。
- 文件 API 首版无删除；`file.move`/`file.mkdir` 已实现。

## 失败恢复

- 中继启动失败：检查 `/healthz` 与日志；确认签名 PFX 路径与
  密码（Production 无 PFX 时拒绝启动，属预期行为）。
- 数据库损坏：用 `deploy/scripts/restore.sh` 从备份恢复；恢复后
  所有在线租约与已批准同意会话全部失效，需重新取得本地同意。
- Agent 更新失败：安装器保留旧版本目录并回滚快捷方式与启动项；
  旧版可直接重新启动。
- 命令结果 `uncertain`：查询命令状态并观察实际界面；绝不自动
  重发原动作。

## 卸载

- Windows：托盘菜单“卸载 Bridge…”——移除开机启动项、开始菜单
  快捷方式与安装目录；服务端撤销设备后绑定才完全失效。
- 中继：`docker compose down -v`（删除数据卷前先备份）。

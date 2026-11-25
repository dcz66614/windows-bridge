# Windows Agent

状态：**CODE_COMPLETE / LINUX_CROSS_COMPILE_OK / WINDOWS_NOT_RUN**。

桌面后端已实现（T13–T19，见 `docs/decisions/0007-t13-t19-desktop-backend.md`），在 Linux 上交叉编译通过，win-x64 自包含发布产物在 `artifacts/win-x64/`（含 `Bridge.DesktopWorker.exe`）。**不是 Windows 真机证据**：截图、UIA、SendInput、托盘暂停联动均未在真实 Windows 上运行过，所有真机门仍为 NOT_RUN。没有假截图、模拟“点击成功”或假在线设备。

T22 受限文件 API 已实现（`Backend/FileBackend.cs`，授权根默认关闭，托盘“文件访问…”里授权）；T25 安装器已实现（`Install/`：首次运行显示安装对话框，按用户安装、无需管理员、开始菜单快捷方式 + 可选登录启动项、升级回滚、卸载；分发包 `artifacts/Bridge-Windows-0.1.0-win-x64.zip`，56M，sha256 `11659ac9…`）。安装流程本身也从未在 Windows 上跑过；未签名（T28）。

## 当前实际包含

- .NET 10 Windows Forms 项目、当前用户 `asInvoker` manifest、Per-Monitor V2 声明。
- 托盘状态、暂停/恢复界面、退出；不联网、不注册开机启动、不读屏、不注入输入。
- 与交付包协议一致的 mutation envelope：`schema_version, command_id, device_id, lease_id, epoch, sequence, operation, observation_id, issued_at, expires_at, payload`。
- `observe` 是独立读取入口；外部 `ObservationRequest` 只有 device_id、lease_id，内部 `InternalObservationRequest` 的 epoch 从认证后的租约取得。写动作只定义 `input.click`、`input.type_text`、`input.key`；v1.0 click 只有 x/y，固定一次左键点击。没有任意shell。
- Observation使用 schema_version=1.0，绑定lease、epoch、layout_revision、foreground_generation、input_generation（均long）；foreground_identity是独立窗口身份hash，不从generation数字推断。还带含负坐标的native crop/virtual desktop矩形、实际image_width/height，以及嵌套 artifact（artifact_id、sha256、mime_type、byte_length）；没有旧版顶层image_id/image_mime。
- 命令状态只含accepted/executing/succeeded/failed/uncertain/cancelled；前置拒绝属于HTTP错误，不是已接收命令的rejected终态。`ActionResult`只是内部后端回执，不能直接当作HTTP 202或status响应。
- JSON为snake_case，时间序列化为RFC3339 UTC `Z`。这只是编码辅助，尚无完整协议验证/鉴权。
- `IDesktopBackend` 与一律拒绝的 `NotImplementedDesktopBackend`。

## 首次构建（由 Muse 在 Windows 开发机执行）

先确认系统为 Windows，安装官方 .NET 10 SDK；记录 `dotnet --info`。普通最终用户不应执行这些命令。

```powershell
dotnet restore .\Bridge.WindowsAgent.csproj
dotnet build .\Bridge.WindowsAgent.csproj -c Release --no-restore
dotnet publish .\Bridge.WindowsAgent.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\win-x64
```

这是开发构建命令，不是“已经通过”的结果。不要将 `EnableWindowsTargeting` 误解为 Linux 能运行GUI测试。第一版采用自包含目录发布；签名安装器、更新器、SBOM、NuGet lock和单文件打包均待实现。不要为省包体直接开启AOT/trimming后跳过UIA与WinForms验证。

## 按顺序补齐

1. 先完成总计划 G0：Muse实际接入、图片回传和凭据存储；Gadget方案另验Windows BLE发现名称和条款适用。不要为未证实入口写大规模实现。
2. 添加schema验证与能力探测。拒绝未知字段/操作、无效UUID、非Z时间、过期时间、过长文本和越界坐标。key allowlist与contract_lab共享契约，不从聊天文本生成任意VK。
3. 本地批准与单写租约。认证principal/controller从可信传输上下文来，不接受body自报身份。暂停锁存、递增epoch，远端不能恢复。
4. 添加SQLite命令journal：副作用前事务落盘accepted，随后执行；绑定完整canonical envelope hash与principal/controller/lease/epoch。相同ID查已有状态，不重执行；超时/崩溃后不确定用uncertain；重启使旧租约失效。
5. 实现真实观察。原生物理像素截图、图像尺寸与crop映射；截屏和UIA来源时间一致性明确。先实现单屏并声明能力，多屏矩阵通过再开放。
6. 实现UIA独立worker和Win32输入。不要在托盘STA/UI线程遍历桌面；UIA使用独立MTA worker，硬超时可重启。输入前重复确认用户授权、epoch、焦点、layout、observation；SendInput检查事件数量。
7. 实现图像有界回传与opaque artifact_id，校验嵌套metadata中的SHA256、PNG类型和不超过4MiB的正byte_length。不要在模型提供的URL/文件路径上下载或读任意数据；不要把屏幕内容放入日志。
8. 接入选定transport，然后做交付包真实Windows验收；通过前不得移除本文件的未验证标记。

## FlaUI 与依赖选择

本目录尚无第三方NuGet依赖。建议正式UIA后端采用固定版本`FlaUI.UIA3`，不要手写完整UIA COM层；采用前读取该实际版本LICENSE与依赖树，保留MIT声明。上游官方源码：https://github.com/FlaUI/FlaUI ，许可证：https://github.com/FlaUI/FlaUI/blob/main/LICENSE.txt 。FlaUI并不保证所有控件都有可访问性树，仍需真机验证。

Windows-MCP适合作为P0对照，不能因为顶层MIT而忽略其Python依赖许可证。相关研究详见交付包Windows生态研究文档。不要把未经审查的上游源代码复制进本目录。

## 必须记录的验收证据

commit SHA、构建SDK、OS build、x64/ARM64、DPI/显示器布局、是否当前用户已解锁、实际操作输入、预期与实际结果、脱敏日志。真实GUI测试与模拟测试分别记录。没有跑写NOT_RUN；仅编译通过不代表桌面控制通过。当前目录没有安装器、自动更新、OAuth、BLE、Gadget Noise、MCP、relay或生产级停止保证。

# Gadget SDK 实验分支调查（T05 桌面研究，只读）

**结论：BLOCKED — 切回 HTTPS 主线，仅保留研究；不进入默认安装体验。**

- 调查日期：2026-11-13（Europe/London）；调查员：子代理（Gadget spike）；方式：只读桌面研究，未申请 token、未配对、未安装、未改系统设置。
- 门依据：`docs/03_Muse_Integration_Gates.md` §十二；研究依据：`research/muse_platform.md` §6（S16–S20）。
- 退出规则（门 §十二）：BLE 配对、服务接纳、图像回传、服务条款任一项未解即 BLOCKED。本调查四项中三项未解、一项仅限个人实验，故 BLOCKED。

## 1. 官方资料阅读记录（2026-11-13 实读全文）

| 资料 | URL | 阅读范围与关键事实 |
|---|---|---|
| Linux README | https://github.com/facebookincubator/muse-gadget-sdk/blob/main/linux/README.md | 全文 751 行。Linux 设备经手机 App 配对，需 BLE + 个人 SDK token（`mgst_…`，Account > SDK tokens）。配对窗口 10 分钟，`sudo musegadget pair` 可重开；每次安装产生全新加密会话；社区设备无厂商验证，不能防主动中间人攻击。 |
| DEVICE.md（设备身份契约） | https://github.com/facebookincubator/muse-gadget-sdk/blob/main/protocols/DEVICE.md | 全文 152 行。`link.register` 的 `device_family` 恒为 `"gadget"`；`model_id` 为 1–64 字符 `[a-z0-9-]`；`muse-` 与 `esp-link` 前缀保留，SDK 默认 `muse-gadget` / `muse-gadget-linux`；`model_name` 1–128 可打印 ASCII，不含 `"` 或 `\`；`device.guide` ≤ 4096 字节 UTF-8 Markdown，不含秘密、不授予权限。 |
| SDK Token 条款 | https://gadgets.muse.ai/sdk-terms | 全文 7 条（见 §5）。代码 Apache-2.0，云服务使用权另受本条款约束。 |
| install.sh（补充核对依赖） | https://raw.githubusercontent.com/facebookincubator/muse-gadget-sdk/main/linux/install.sh | 前 558 行已读。见 §6 依赖表。 |

## 2. 配对方式与命令（Linux README 实证）

- 配对：手机 Muse App → Settings > Devices > Developer mode → Add Device（+）→ 发现名为 `MuseGadgetXXXXXX` 的设备 → 社区设备警告后继续 → 选 Wi-Fi（机器已在线，无需密码）。配对后保持连接、跨重启不断线；BLE 只用于配对，`doctor` 另查到 Muse API 的 443 端口 TCP 连通性。
- 不绑定（bonding）：README 与 install.sh 均明示 Gadget 从不 bond；iPhone 场景下 BlueZ 电池插件会诱发 bond 请求，安装器以 `--noplugin=battery` 禁用该插件。
- 内置命令：`system.run`（shell 命令，返回输出与 exit code）、`file.read`（每次 64 KB）、`file.write`（每次 64 KB，完整后才替换）、`device.health`（uptime/load/memory/disk/temperature）。命令以安装账号权限运行；该账号能 sudo，Muse 就能 sudo；最多 4 条命令并发（含已断线会话中仍在跑的）。
- 自定义命令：`/etc/musegadget/commands.d/<name>.json`，`exec` 数组 + `required`/`optional` 参数（stdin 传 JSON，stdout 印 JSON 回传），`timeout_ms` 默认 30 秒；名字为小写点分，`system.`/`file.`/`device.`/`link.` 前缀保留；注册序列化总量上限 256 KiB。
- 断线/重连语义：断线不停止已启动的命令（占位直至返回）；旧会话排队命令取消；旧会话结果重连后不重发；服务不自动重试。`musegadget doctor` 只做本地检查，不验证在线凭据、不确认与 Muse VM 的 live 会话。
- 语音/视频（附带）：`voice-call` 用 voice-only Noise 传输，不走 `/link-control`、不发 `link.register`，不能替代 run 服务的 live 传输；与本项目桌面控制无关，列为 NOT_RUN。

## 3. 图片发送方式与上限

- `musegadget send-user-msg --image porch.jpg "Who is at the door?"`：JPEG/PNG/GIF/WebP，**上限 2 MiB**；另有 `--wait 1–600` 秒等回覆，超时或仍在回答时 exit 3。
- 关键限制（README 原文）：主聊天里的 Muse 回覆不标明它回答的是哪条消息，同一时刻多方发消息会造成**归因错配**；`--session-id` 可把消息发进 side chat 缓解，但仍是“主动向聊天发消息”，不是 tool response 返回图像。
- 对本项目的影响：截图→点击坐标的闭环需要“观测与动作一一对应”；`send-user-msg --image` 未证明能进入视觉 tool 上下文、未证明坐标可被可靠读取。门 §十二第 7 条同此判断。状态：**未解，需真机实测**。

## 4. 服务条款要点（sdk-terms 全文 7 条）

1. 仅限**个人、非商业**使用，限本人 Muse 账号与自己制作的设备。
2. Token 属个人，不得分享或公开；最多嵌入 **50 台**自己提供给他人的设备，且一个 token 最多关联 50 台设备；不得嵌入任何出售、公开宣传/上架（含应用商店）、促销/奖励关联的设备；不得声称 Meta 制造或背书；其他分发须 Meta 事先书面许可；token 下全部活动由持有人负责。
3. 不得用 token 访问他人 Muse 账号或处理他人 Muse 数据；向他人提供设备前须先告知其信息处理方式。
4. 不得绕过用量限制或安全措施，不得启用 Meta 已禁用的功能。
5. **非受支持产品、非开发者平台**，按现状提供，可随时变更、停止或撤回，无需通知。
6. Meta 可随时暂停或撤销 token 访问；撤销后必须停用并从已分发设备中移除 token。
7. SDK 源码 Apache-2.0；**源码许可不授予任何 token 权利**，token 访问另受本条款约束。
- 对本项目的影响：条款本身不禁止 Sam 个人实验，但**禁止把开发者 token 嵌进公开安装包、禁止公开分发/商用**——“普通用户双击安装即用”的产品形态与本分支不兼容。状态：**个人实验可行，公开分发 BLOCKED**。

## 5. Windows 原生 BLE peripheral / GATT 可行性

桌面研究结论：**WinRT 存在 GATT server API，但 peripheral 角色是硬件抽奖，且与 Muse 配对协议的兼容性一个字都未证明。**

| 事实 | 来源 | 影响 |
|---|---|---|
| `GattServiceProvider.CreateAsync(uuid)` + `StartAdvertising(IsDiscoverable/IsConnectable)` 可做 GATT server，始于 Win10 1703（build 15063） | Microsoft Learn《Bluetooth GATT Server》；《What's New in Windows 10, build 15063》 | API 存在，不等于 Muse 协议兼容 |
| Peripheral 角色硬件依赖，须先查 `BluetoothAdapter.IsPeripheralRoleSupported`；微软 FAQ 原文：“No… Currently supported devices include Windows Phone on 8992+ and RPi3 (Windows IoT)”；不支持时 `StartAdvertising` 报 `ERROR_DEVICE_FEATURE_NOT_SUPPORTED`，换驱动无用 | Microsoft Learn《Bluetooth developer FAQ》；WinError.h 316 | 桌面 PC 不保证支持；Surface 等多款内置适配器仅 central 角色 |
| 同一 Intel 芯片不同 VID/PID 变体支持情况不同（NUC 报 TRUE、Surface Pro 4 报 FALSE），社区称之为“roulette”；USB 外接适配器（如 Realtek 8761B）可补位，但属于额外硬件 | Intel 社区帖；poster-runner #48 | 不能写进“双击安装”默认体验；要求用户另购适配器即违反 §十二第 8 条 |
| 传统广播包仅 31 字节，128-bit UUID 占 16 字节；Windows 传统广播拒绝携带 local name 或 service UUID（flutter_ble_peripheral 实测）；扩展广播（BLE 5.0）需查 `IsExtendedAdvertisingSupported` | Microsoft Q&A；flutter_ble_peripheral 文档 | Muse App 靠广播名 `MuseGadgetXXXXXX` 发现设备；广播名上不了天 = 配对第一步即失败，待真机验证 |
| Windows 不支持 broadcast 特性；DIS/GATT/GAP 系统服务不可发布 | Microsoft Learn《Bluetooth GATT Server》 | 协议映射时须避开保留服务 |
| Nearby Sharing 会占用广播所需蓝牙资源，报 `ResourceInUse` | flutter_ble_peripheral 文档 | 安装器须预检并引导用户关闭，属额外摩擦 |
| 非打包桌面应用可用 WinRT API（目标 `netX-windows10.0.19041+` + Microsoft.Windows.SDK.Contracts）；UWP 须在 appxmanifest 声明 bluetooth 能力 | Microsoft Learn；社区教程 | 技术上可写 C# 层，但须重写而非移植 |

## 6. Linux 依赖 → Windows 替代/缺口逐项表

install.sh 包清单：`bluez python3 python3-dbus python3-gi python3-cryptography curl ca-certificates`；另有 pin 的 uv 0.9.9、venv、systemd unit、BlueZ `ExchangeMTU = 256`。

| Linux 依赖 | Windows 替代 | 状态 |
|---|---|---|
| BlueZ + D-Bus GATT server（`python3-dbus`/`python3-gi`） | WinRT `GattServiceProvider` + `BluetoothLEAdvertisementPublisher`（C#/.NET 层）；Python `winrt` 包或 bleak 均非等价物（bleak 在 Windows 仅 central） | **缺口**：须重写 BLE 层；配对所用的确切 GATT service/characteristic UUID 与 Noise 握手未从 SDK 源码映射 |
| BlueZ `ExchangeMTU = 256`（Muse Android App 写 `(MTU-3)` 字节包，>512 即失败） | WinRT 无公开 MTU 强制旋钮，MTU 由系统协商 | **缺口**：无等价配置，需真机测协商值 |
| systemd service（`musegadget.service`，`journalctl`） | Windows Service / 任务计划 / 托盘自启 | 可替代，但 Session 0 服务摸不到交互桌面；须跑在用户会话内，与主线托盘同此约束 |
| `/opt/musegadget` + `/var/lib/musegadget`（root-only state，`sdk_token` 仅 root 可读） | `%ProgramData%`/`%LOCALAPPDATA%` + ACL + DPAPI / Credential Manager 存 token 与 pairing secret | 可替代（门 §十二第 5 条即此要求） |
| Unix socket IPC（`send-user-msg`/`chat` 经 socket，仅 root 与命令账号组可访） | 命名管道（Named Pipe + ACL）或 loopback TCP | 可替代，中等工作量 |
| `pwd`/Unix 用户、`--run-as`、sudo 提示 | Windows 用户账号 + UAC；管理员账号风险提示对等 | 可替代 |
| uv 0.9.9 + venv + `requirements.lock` | Windows 版 Python/嵌入式 Python，或改写为 .NET | 可替代；但 BLE 层重写后 Python 层的复用价值有限 |
| `curl`、`ca-certificates` | .NET HttpClient / WinHTTP + Windows 证书库 | 可替代 |
| `uname` 为 Linux、`/etc/os-release`、`/sys/class/bluetooth` 预检 | PowerShell 预检：`IsPeripheralRoleSupported`、`IsExtendedAdvertisingSupported`、radio 状态 | 须新增预检，未实现 |
| “无蓝牙板子先在别机配对再复制 pairing state”（README Raspberry Pi 2 笔记） | —— | **缺口/禁区**：门 §十二第 8 条明确定为开发实验，不纳入默认安装体验 |

## 7. 逐项判定表（门 §十二退出规则）

| 门项 | 判定 | 依据 |
|---|---|---|
| BLE 配对 | **BLOCKED（未解）** | WinRT peripheral 角色硬件依赖、Surface 等机型不支持；传统广播带不上 `MuseGadgetXXXXXX` 本地名；MTU 256 无等价旋钮；配对 GATT profile 未映射 |
| 服务接纳（`link.register`，`device_family=gadget`，自有 `model_id`） | **NOT_RUN** | 规则已读（DEVICE.md），但无任何证据表明 Windows 移植版会被 Muse 服务接纳；须 Sam 本人 token + 真机配对实测 |
| 图像回传（`send-user-msg --image`，2 MiB） | **BLOCKED（未解）** | 机制存在，但主聊天归因错配 + 非 tool response 图像；未证明可用于点击坐标闭环 |
| 服务条款 | **个人实验可行 / 公开分发 BLOCKED** | sdk-terms 第 2、5、6 条：个人非商业、≤50 台、禁公开上架/销售/促销、随时可撤；源码 Apache-2.0 不授予 token 权利 |
| Windows 依赖替代 | **部分可替代，核心缺口 3 项** | 见 §6：BLE 层重写、MTU、配对 profile 映射；其余（systemd→托盘、DPAPI、命名管道）可替代 |

## 8. 未验证项（NOT_RUN）清单

1. 未在任何 Windows 真机上查询 `IsPeripheralRoleSupported` / `IsExtendedAdvertisingSupported`（无真机、无 Sam 操作授权）。
2. 未验证 Muse 手机 App 能否发现并配对 Windows 广播的 `MuseGadgetXXXXXX`（未申请 token、未配对）。
3. 未映射 SDK 源码中 BLE 配对的 GATT service/characteristic UUID、Noise 握手与 `link.register` 载荷（仅读 README/DEVICE.md/安装脚本，未读 `linux/src` 配对实现）。
4. 未验证 Windows 移植版 `link.register` 会被 Muse 服务接纳（无 token、无实测）。
5. 未验证 `send-user-msg --image` 的图能进入视觉上下文并用于坐标定位（无真机、无配对）。
6. 未验证断线重连、跨重启恢复在 Windows 上的行为。
7. 未验证自定义命令（如 `screen.capture` / `input.click`）能否注册并被 Muse 调用——且即使能注册，条款第 4 条禁止启用 Meta 已禁用的功能，桌面控制类命令的接纳边界未知。
8. 未验证 WinRT GATT server 在锁屏/UAC/多用户会话下的行为。

## 9. 补充：注册载荷与 token 生命周期（已读，未实测）

- `link.register` 在每次重连时重发；ESP32 上限 8 KB，Linux 自定义命令序列化总量上限 256 KiB，超限的命令不被广告、不可执行（README“Add your own commands”与 DEVICE.md）。
- Token 生命周期：token 以 root-only 文件存于 `/var/lib/musegadget/sdk_token`；Muse 只在配对时记录设备的 token；更换 token 后须 `sudo musegadget pair --force` 重新配对；撤销 token 后设备无法触达 Muse，直至用新 token 重新配对。
- 配对状态与设备身份存于 `/var/lib/musegadget`；`install.sh --uninstall` 默认保留身份与配对，`--purge` 才清除。
- 设备过期 token 由 `musegadget run` 服务先刷新（voice-call 路径的说明）；`doctor` 不在线验证凭据、不确认 live VM 会话——“服务在跑 + API 可达”不等于“已接通 Muse”。
- Windows 映射：上述 state 目录对应 DPAPI 保护的本机 state；`pair --force` 对应托盘里的“重新配对”动作；但在 Windows 上这一切都以前置 BLE 配对成功为前提（见 §7）。

## 10. 建议

- **主线不变**：继续 HTTPS 托盘桥（Custom Connector + Skill）；Gadget 分支不阻塞主线、不进入默认安装体验。
- **仅保留研究**：本文件 + §6 映射表作为后续个人实验的起点；任何继续实验须满足三前提：Sam 本人提供 SDK token（不写入仓库/安装包）、一台已知 `IsPeripheralRoleSupported == true` 的 Windows 真机（或经核验的外接适配器）、Sam 本人在场完成手机 App 配对。
- **若日后重启实验**，第一步不是写代码，而是：读 `linux/src` 配对实现定 GATT profile → 在真机上跑最小 WinRT 广播 + `IsPeripheralRoleSupported` 预检 → 再谈 `link.register` 接纳。
- **不建议的方向**：WSL/Linux 虚拟机承载 Gadget 再转控 Windows 桌面（门 §十二第 8 条已排除）；复制别机 pairing state；把 token 嵌进安装包分发。

## 11. 来源

- https://github.com/facebookincubator/muse-gadget-sdk/blob/main/linux/README.md（2026-11-13 实读）
- https://github.com/facebookincubator/muse-gadget-sdk/blob/main/protocols/DEVICE.md（2026-11-13 实读）
- https://gadgets.muse.ai/sdk-terms（2026-11-13 实读）
- https://raw.githubusercontent.com/facebookincubator/muse-gadget-sdk/main/linux/install.sh（2026-11-13 实读前 558 行）
- https://learn.microsoft.com/pl-pl/windows/uwp/devices-sensors/gatt-server（搜索摘录）
- https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/bluetooth-dev-faq（搜索摘录：peripheral 角色硬件依赖）
- https://learn.microsoft.com/en-us/windows/uwp/whats-new/windows-10-build-15063（搜索摘录：15063 起支持 peripheral）
- https://community.intel.com/t/how-to-tell-if-an-intel-based-system-supports-bluetooth-4-peripheral-role/35581/7（搜索摘录）
- https://github.com/joemc3/poster-runner/issues/48（搜索摘录：Surface 内置适配器无 peripheral 角色）
- https://flutter.ducafecat.com/en/github/repo/juliansteenbakker/flutter_ble_peripheral（搜索摘录：Windows 传统广播限制、Nearby Sharing 冲突）
- https://learn.microsoft.com/en-us/answers/questions/953751/how-to-set-service-data-in-windows-ble-peripheral（搜索摘录：31 字节广播包、扩展广播）
- 包内门文档：`docs/03_Muse_Integration_Gates.md` §十二；研究：`research/muse_platform.md` §6（S16–S20）

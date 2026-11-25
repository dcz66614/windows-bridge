# Windows Bridge — GitHub 上传清单（PASS 后执行）

## 目标

- 仓库：**公开**（Sam 2026-11-25 00:57 GMT 主聊：“公开 都公开”，
  覆盖 2026-11-22 “非 P 系列传私有”规则与本任务最初的“传私有库”说法）。
- 许可证：MIT（Sam 在 P019–P023 的通用决定 + P021 前例）。
- 上传条件：**PASS 后才传**。当前仍未通过：真机门 T13–T19、
  连接器实测 T04/T20/T21 均为 NOT_RUN。FAIL 时绝不上传。
- 上传路线：**云端浏览器**（Sam 2026-11-25 ~01:02 GMT 主聊：
  “你的虚拟机登录过 GitHub，走你虚拟机就行”；RelayLint 侧 01:01 已验证
  云端浏览器登着 GitHub 且在传文件）。不再等 Sam 回电脑用 Chrome。

## 包含

- `src/`、`tests/`、`docs/`（含 decisions/ ADR 0001–0012）
- `deploy/`、`release/`（SBOM、发布说明、本清单）
- `connector/`、`skills/`、`workflows/`、`implementation/`、`evidence/`
- `Bridge.sln`、顶层 schema JSON、顶层 `README.md`

## 排除（绝不进仓库）

- `artifacts/`（win-x64 发布 146M、中继 linux-x64 发布 157M、安装 ZIP 56M）
- `.dotnet/`（SDK）、`.tmp/`、各工程 `bin/`、`obj/`
- `*.db`、密钥文件（`.pfx`、`.pem`、`.key`）
- `dotnet-install.sh`（按 AGENTS.md 纪律不使用，不随仓库分发）

## 上传前复核（执行时逐项打勾）

- [ ] 密钥扫描干净（2026-11-25 已扫：185 个文件，无真实密钥；
  仅公开 OAuth 元数据 JSON 与 PFX 密码输入提示；`.tmp/relay-debug.db` 不进仓库）
- [ ] 全方案测试 158 绿、0 警告 0 错误（2026-11-25 复核通过）
- [ ] `README.md` 状态与 `release/RELEASE_NOTES_0.1.0.md` 一致（预发布、真机未验）
- [ ] 仓库名确定（待定；参考 dcz66614 命名）
- [ ] 上传后核对远端文件树与本地一致，再报链接

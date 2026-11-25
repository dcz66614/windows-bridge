# T03 生产 HTTPS 部署清单（待 Sam 批准后执行，不擅自创建付费资源）

本地 G0 测试服务（work/src/g0_service.py）已在 loopback 上验证全部错误路径，
但它使用 ThreadingHTTPServer + 明文 HTTP + 单一测试 token，**不得原样作为公网服务**。

## 部署前必须一次列清、由 Sam 决定的事项
1. 固定域名（例如 bridge.<自有域>）：注册/已有域名、DNS 指向部署主机。
2. TLS：有效证书（Let's Encrypt 或托管平台自动证书），HSTS；证书无效时客户端 hard fail，不忽略校验。
3. 托管位置：单实例 ASP.NET Core 10 服务（VPS/云主机二选一），含月费估算；不默认付费。
4. 持久卷：SQLite（WAL）+ DataProtection key ring + OpenIddict signing/encryption keys 分别持久化与备份；数据库回滚会使撤销倒退，恢复演练须使旧租约全部失效。
5. 账号隔离：两个独立测试账号先完成隔离与撤销演练，再邀请 Sam 绑定日常电脑。
6. 日志与限流：请求上限 64KiB、图片 4MiB、超时、限流；日志默认只含元数据，不含 token/屏幕正文/输入文字。
7. 域名与凭据：服务配置示例只用占位符（.invalid）；真实域名、token 永不进仓库、聊天或 Skill。

## 本轮状态
- 本地服务：PASS（loopback，见 gate_report.json https_round_trip 的本地部分）。
- 生产 HTTPS 部署：NOT_RUN / BLOCKED_EXTERNAL——无域名、无 TLS、无托管授权。
- 下一步唯一动作（需 Sam）：确认域名与托管方式（或授权使用已有 VPS/云账号），随后我提交资源清单与预算，再部署。

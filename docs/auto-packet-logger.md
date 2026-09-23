# 内置自动调试录包

2026-09-24 起使用 **Deucalion 1.5.0 命名管道后端**。旧 OnReceivePacket 前置段头假设已被实机否定，不再安装这个接收 Hook。插件设置 → **Debug packet logger** → **Automatically record packets at mahjong tables**，原有开关和默认关闭行为保留，不需要 NetworkPacket 或 ACT。

## 使用与实机验收

更新完整插件目录（包括 `capture-runtime/`），热重载后打开录包开关。后台先连接本进程已有的 Deucalion 服务；没有服务时，校验随包 DLL 的 SHA-256 后从插件目录加载。只在 ffxiv_dx11 进程中执行 native 加载；测试不会加载 DLL。版本必须是 1.5.x，握手必须报告 RECV ON 和 CREATE_TARGET ON，否则明确降级，不把未解码的数据交给策略。

- 连接成功后等待入桌；入桌后 `Saved` 应持续增加。初次先采 10–20 秒，关闭录包开关封口，可正常继续对局，不必为采样中途离桌。
- 若显示 Deucalion unavailable，保存状态信息和 `%APPDATA%/deucalion/` 最新日志。排查后关闭、重新开启录包可重试；不循环重复加载 DLL。
- 新后端已在国际服 2026.09.15.0000.0000 实机取得 71 包短样本及 1435 包长样本，均无本地丢包/拒绝。后四局有 118 个自身手牌检查点；杠/宝牌等公开事件仍未完整验证，因此 Mortal 协议继续禁用。

## 生命周期与限制

连接、DLL 校验/加载、管道收发和心跳均在后台。关闭或卸载立即取消订阅，不在游戏线程等待。重新启用会先等旧订阅结束，旧会话不能把回调发布给新订阅。不会发送全局 Exit 或自行 FreeLibrary；Deucalion 自行管理最后一个订阅者断开后的卸载，已有 ACT 等订阅者不受主动停止影响。

本订阅只选择接收的 Zone IPC；本地命名、筛选和心跳命令不发送游戏报文。握手有 10 秒超时，连续 15 秒没有任何管道消息（服务端正常每秒发送心跳）会报告失联。长度不在 9–65545、消息中途断开、IPC 不足 16 字节或标记不符时，停止本次连接并记录缺失。错误保留到关闭/重新启用或重载。Mortal 与公开局面使用同一源，按已验证版本/变体/长度过滤；长度不匹配和连接丢失会增加缺失计数，触发恢复门禁。

录包仍保留两秒、最多 256 包 / 1 MiB 的内存预缓冲；桌内写入队列最多 512 条，每文件最多 64 MiB，满队列不等待。文件按入桌、离桌、开关自动开始和结束；后台先排空再封口。磁盘故障不阻断游戏。文件在 `pluginConfigs/Mahjong.Plugin.Dalamud/debug-packets/`，仅保存在本机，不自动上传；内容可能有玩家信息。

## schema 3 与审计

新文件 `schema_version=3`、`hook_mode=deucalion-pipe`。有效包携带：

- `transport=deucalion`、`transport_length`：实际命名管道消息总长（9 字节封装 + 16 字节 Deucalion 元数据 + 16 字节 IPC 头 + 载荷）。
- `length_source=deucalion-envelope`、`ipc_length`、`ipc_header_hex`、`payload_length`、`payload_hex`。
- `source_actor`、`target_actor`、`server_timestamp_ms`；本地 `t`/sequence 保留接收顺序。
- `segment_length=null`，因为此后端未提供原始网络段头，不伪装为观测到的段长。

读取单条消息前校验长度，再按这个明确边界读取；支持管道分片，不从 opcode 猜长度。旧 schema 1/2 文件仍可审计。失败诊断最多每场 8 条、每类 2 条；schema 3 的 transport 失败不包含候选内存头。`Saved` 只计有效包，诊断不进入 opcode 清单。

```powershell
node tools/audit-debug-packets.mjs "C:\path\to\capture.ndjson" 2026.09.15.0000.0000 artifacts/debug-audit.json
```

`stream_complete` 仅说明已观察窗口没有报告本地丢失或读取错误，不证明第三方后端没有未上报损失，也不证明开局完整或麻将协议正确；`opening_boundary_verified=false` 不变。对局中途连接不能据此恢复 Mortal 所需的开局历史。

## 构建依赖

先运行 `./tools/install-deucalion.ps1`，再执行正常构建。下载固定的官方 1.5.0 DLL 和对应 tag 源码 ZIP，校验值在脚本及 NOTICE 中；构建时只核验本地文件，不联网下载或加载库。CI 与发布使用同一步骤。最终包同时附带原 DLL、完整源码归档、GPL-3.0 许可证和来源说明，见 [NOTICE](deucalion-NOTICE.md)。

协议依据：[官方 1.5.0 README](https://github.com/ff14wed/deucalion/blob/1.5.0/README.md)。旧 Hook、FireCallback 和短样本排查记录保留在 [9 月 23 日审查](reviews/20260923-international-match.md)及 [9 月 24 日审查](reviews/20260924-international-match.md)。FireCallback 仍是独立的操作观察 Hook，其初始化失败与管道录包不是同一个故障。

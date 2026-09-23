# 内置自动调试录包

插件设置 → **Debug packet logger** → **Automatically record packets at mahjong tables**。默认关闭，旧配置保持关闭；与 Mortal、Off / Hints / Auto-play 独立。重载新构建后，在匹配前打开一次即可，不需要另外安装 Packet Logger。

## 自动生命周期

- 打开开关后安装独立的只读接收 Hook，在内存保留最近 2 秒、最多 256 包 / 1 MiB 的预缓冲，不在桌外持续写文件。
- UI 检测到麻将桌时，在插件配置目录 `debug-packets/` 新建 `capture-*.ndjson`，先写预缓冲，再录制桌内 Zone 接收包。离桌、关闭开关、卸载时封口，后台按顺序排空已接收队列再写结束记录。强制结束游戏可能缺少结尾，审计会标记中断。
- 后台队列最多 512 包；满队列不等待磁盘，记录丢包。每个文件最多 64 MiB，到达限制即停止本次录制，不在同一桌自动循环创建文件。磁盘错误同样停止本次录制并显示错误，不阻塞游戏。
- 文件保留到手动删除，不受对局归档的自动清理影响。原始包可能包含玩家信息，保存在本机，不上传。收集完两场后可关闭开关。

状态 **Armed; waiting for mahjong table** 表示等待入桌；入桌后应为 **Recording**，**Saved** 持续增加。**Queue drops** 是最近文件的队列/容量丢包数；**Read rejects** 是本次插件加载以来的读取拒绝总数。不能仅凭 Recording 判断录包成功。**Copy packet log folder** 复制真实目录；下面显示最近文件路径。

## 数据与审计

首行记录游戏目录实际读取的客户端版本、Emj/EmjL 变体、协议状态、插件构建 ID。包记录包含时间、顺序号、opcode、实际段长度和十六进制载荷；尾行记录结束原因、保存数、丢包数及读取拒绝数。`stream_complete` 只表示已观察录制窗口的写入/读取未报告丢失，不证明捕获开局前全部网络历史，更不代表协议已验证；`opening_boundary_verified` 始终为 false。

```powershell
node tools/audit-debug-packets.mjs "C:\path\to\capture.ndjson" 2026.09.15.0000.0000 artifacts/debug-audit-match1.json
```

报告校验顺序、载荷长度、版本和结束标记，输出 opcode / 长度统计及缺失项，不输出载荷。保留未知 opcode，不按旧名称表猜麻将消息，不修改协议配置。输出路径必须不存在。外部 Packet Logger 的 `.log` 仍使用 `audit-mahjong-capture.mjs`。

## 传输边界与实机限制

独立诊断 Hook 使用 [Dalamud NetworkMonitor 的 OnReceivePacket 入口](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Interface/Internal/Windows/Data/Widgets/NetworkMonitorWidget.cs)。段头依据 [Deucalion 的 packet 定义](https://github.com/ff14wed/deucalion/blob/main/deucalion/src/hook/packet.rs)：16 字节段头后跟 16 字节 IPC 头。实现通过本进程 `ReadProcessMemory` 读取 IPC 指针前 16 字节开始的头，检查总长 32–65536、段类型 3、目标 ID 匹配、IPC 标志 0x14，复制后再次对比包头。读取失败或校验不符直接拒绝，不按旧 opcode 长度猜测，更不会改写或发送报文。

上述内存布局在国际服 2026.09.15 的本机实战尚待验证。若 Saved 一直为 0 或 Read rejects 持续增加，保留文件和显示状态供定位；不得通过关闭长度检查或填写固定长度来规避。即使能捕获原始包，麻将字段仍需要 UI 对照与回放验证。未知游戏协议依然停用正式网络推理，调试文件不进入公开局面或 Mortal 队列。

## FireCallback Hook 内存分配失败

`InputEventLogger: failed to hook FireCallback` 与自动 packet logger 是两个独立的 Hook。`Unable to find memory location to fit MemoryBuffer` 表示 Reloaded 未能在要求的地址范围内分配跳转缓冲，并不表示麻将 opcode 已失效，也不能仅凭此判断物理内存不足。

插件现在在诊断页显示 FireCallback 的可用状态，失败时明确停用手动 Arm-and-click 捕获，并清理初始化未完成的 Hook。UI 生命周期日志、读牌与录包保持各自状态；缺失的点击回调、input-pre/input-post 快照不会伪装成已经采集。完整退出并重启游戏可能恢复地址空间条件，单纯重载插件未必有效；本次改动不保证修复 Dalamud 的底层分配失败。

2026-09-23 实机证据表明虚表槽位 Hook 全场零回调，该方案已撤回。自动 packet logger 恢复拦截 OnReceivePacket 函数入口，覆盖绕过虚表槽位的调用；若 Dalamud 无法分配入口 Hook，会明确显示初始化失败，不再回退到已证实无效的槽位方案。仍不切换已移除的 MinHook 后端。

入桌后先显示 Waiting for first packet，5 秒仍无数据即显示 Capture stalled；有读取拒绝则显示包头失败。零包文件的 stream_complete 为 false，并带 no_packets=true。下一次只需先短时验证 Saved 是否增长，当前客户端实际包头仍需验证。

`[DiscardCapture] using addon-poll strategy` 是正常的信息日志。旧版本的 `sigscan recorded for telemetry` 文案已改为本地诊断，不存在远程上传。

从虚表 Hook 版本升级到接收入口版本时，请完整退出并重启游戏。Dalamud 的函数指针 Hook 卸载后可能保留转发槽位，而其 FollowJmp 只追踪跳转指令，不会追踪以 movabs 开头的该转发桩。新版拒绝把游戏模块之外的指针当作接收入口，并给出完整重启提示；不自动修改其他插件的 Hook。入桌后五秒无包会在设置和本地日志中报告一次。

## 2026-09-24 诊断采样（schema 2）

最新实战已确认入口收到回调，但 1262 次读取都被旧版合并原因 `invalid-segment-header` 拒绝。新版保持原校验条件，将失败细分为 `invalid-ipc-pointer`、`unreadable-header`、`invalid-segment-length`、`segment-type-mismatch`、`target-mismatch`、`ipc-marker-mismatch`、`unreadable-segment` 和 `segment-changed-during-copy`。一次头部校验有多个不符项时，样本的 `failed_checks` 全部保留；汇总按首个原因计数，避免重复计数。

桌内失败会写入 `capture-diagnostic`：每场最多尝试保存 8 个样本，每种首要原因最多 2 个。样本只使用现有读取的 IPC 指针前 16 字节开始的 32 字节候选头，不扫描相邻内存、不扩展读取范围、不从未验证长度复制载荷。头部不能完整读取时不保存任何缓冲区内容，只记录 Win32 错误码；读取成功时保存候选长度、目标、段类型、IPC 标记、opcode 和头部十六进制。所有字段均为未验证候选值，`layout_verified=false`，不用于公开局面或 Mortal。

诊断与有效包共享有界后台写入队列，队列满不等待；达到采样上限后只增加计数。桌外失败仅影响既有预缓冲不完整标记，不保存头部样本。每次新入桌重置采样预算，关闭开关或离桌会排空已接收记录再封口。状态显示具体失败原因与已写诊断数；`Saved` 仍只计算有效包，诊断不能让零包录制变为成功。

尾部新增 `rejection_counts`、`diagnostic_samples`、`diagnostic_dropped`、`diagnostic_unsampled`。其中 `rejected = diagnostic_samples + diagnostic_dropped + diagnostic_unsampled`；这些是录制会话的计数，既有预缓冲损失仍按不完整标记计入。审计工具兼容 schema 1/2，诊断不进入 opcode 目录，报告不回显候选内存内容。头部可能包含本机地址或实体标识，仅保存在本地原文件。

验证步骤：更新并重载插件（若提示 Hook 初始化失败再完整重启游戏），打开自动录包；入桌后等待 10–20 秒，查看失败原因和 `diagnostics` 数量，然后关闭录包开关使文件封口，正常继续对局即可。只需检查最新 `capture-*.ndjson`，不必为了采样完成整场或中途退赛。本次更新提供定位证据，尚不宣称解决实际包头布局问题。

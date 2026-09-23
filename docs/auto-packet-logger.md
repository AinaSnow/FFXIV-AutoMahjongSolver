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

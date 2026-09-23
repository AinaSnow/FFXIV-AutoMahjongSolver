# 国际服 2026-09-24 实机检查

本次仅审查本地采集证据；不改写原始文件，不标记协议已验证。

## 样本

- 北京时间：2026-09-24 01:33:03–01:49:05。
- 客户端：2026.09.15.0000.0000 / Emj。
- 插件构建 ID：d8bce8cc-b73c-434d-ba92-6e84f17799a6。
- 录包：capture-20260923-173303-177-85accd84f7c045e995dc70545be5a9ff.ndjson（712 字节）。
- 归档：match-20260923-174905Z；对应 inputs/findings 使用 UTC 日期 20260923。
- 原始录包 SHA-256：6b2371bf523eeb038d643353c763dda16082d35d129ff0f863a7ba42fdea5ee1。

## 录包：入口已收到回调，包头读取仍阻断

文件声明 hook_mode=function-entry，尾部 packets=0、dropped=0、rejected=1262、last_rejection=invalid-segment-header。与上一场 rejected=0 不同，接收回调已触发，但没有一条包通过读取校验。no_packets=true、stream_complete=false 正确反映失败，没有再把空录标为成功。

审计工具报告 no_packets、transport_read_rejected、incomplete_capture。无有效载荷，不能分析麻将 opcode、校准字段或启用 Mortal。

当前 TryCopy 从 IPC 指针前 16 字节读取 32 字节头，并合并报告读取失败、长度、段类型、目标 ID、IPC 标记不符。现有文件不包含各字段的失败值，不能确定是不可读内存还是哪项布局假设不成立。Dalamud 本地 NetworkMonitorWidget 在同一入口只读取 packet+2 的 opcode，不证明前置段头或长度可用。下一步应拆分失败原因并增加有限的本地头部诊断，核实此入口是否保留长度信息；若没有，应使用携带已知缓冲区长度的上游接收边界。不能取消长度校验或猜固定载荷长度。无需再靠完整对局复现零包。

## 自动操作：自摸路由已获得本场实机证据

- 01:38:59.311，自动输入 [11,0]；01:38:59.315，自摸状态由 6 变 19，耗时约 3 ms。
- 01:39:00.874，自摸二次确认再次记录自动 [11,0]，该动作最终记录 state-changed。
- 01:41:44.660，自动荣和 [11,0]；约 8 ms 后状态由 15 变 19。01:41:46.230 自动二次确认，01:41:48.838 状态变 32。
- 89 条动作均有 outcome：87 条 state-changed、2 条 superseded，未记录 timeout 或缺失 outcome。state-changed 是同步自动回调和 UI 变化的关联，不等于网络确认。
- 两条 superseded 均为立直按钮（action_id 25、44），分别在约 3.21 s、2.81 s 后由 riichi-tsumogiri 舍牌替代；随后舍牌状态转 30。此状态表示独立按钮确认被后续步骤覆盖，不能直接判定立直失败，也不能证明服务端接受立直。后续可改为分阶段追踪立直流程。
- 输入源共 147 条 automation、27 条 external-or-game；后者包含游戏或其他来源，不能一概称为用户手动操作。

## 结算与本地统计

5 份手局文件均各有一次起手、一次结算；动作 hand_id 依次为 1–5，没有虚假第六局或全零起始分。自身每局分差为 [-2000,2700,5200,0,0]，最终分数 [30900,10500,31400,27200]，自身第 2 名、净增 5900 分。结算类型仍为 UI 分差推断（result_inferred=true）。

245 次决策，243 次 stable、2 次 terminal-guard；平均 4.23 ms、P95 15.61 ms；无 policy-error、归档未报告 IO 失败。Mortal 没有处理包或运行推理；本场名次不能作为策略提升的对照证据。

本次未修改运行代码，因此不重复运行测试。录包审计、逐手结算、动作及输入时间线已交叉检查。

## 02:20 短时诊断：前置段头假设不成立

用户热重载后采集了 02:20:03–02:20:18 的短样本，文件 capture-20260923-182003-227-14eaf0b5435849189d234cce5e83fb15.ndjson，2360 字节，schema 2，构建 ID 0d58fdab-6114-4434-aa8b-c80737470281。SHA-256：92b451b7df8932c107d27b7120784c7ce0ba9780f12c6706a682b0fdafe458ad。

记录为 1 个开头、3 个诊断、1 个结尾，无有效网络包。16 次拒绝包含 1 个 pre-roll-incomplete 标记和 15 次 invalid-segment-length；3 个诊断包含该预缓冲标记及 2 个候选头样本。diagnostic_dropped=0、diagnostic_unsampled=13，计数一致，正常以 disabled 封口。审计未报告格式或计数问题，但仍报告 no_packets、transport_read_rejected、incomplete_capture。

两份候选头均显示：IPC 指针本身起始标记为 0x14，+2 位置的候选 opcode 分别为 0x03D9、0x0242；这符合本地 Dalamud NetworkMonitor 读取 opcode 的位置。原先假定的 IPC-16 段长却为 0，同时段类型及目标 ID 校验不符。因此当前样本不具有实现所假定的“16 字节网络段头紧邻 IPC 头”布局。前置字节可能是对象或缓冲区管理信息，其具体类型尚未确认，不将其解释为其他有效字段，也不推断这些 opcode 的消息名称。

这是当前采集实现对入口布局的错误假设，不是用户没有正确开始抓包，也没有证据表明继续打一整场能解决。不能把 length=0 改为固定长度、只保留 IPC 标记校验或猜测另一个偏移后复制载荷。两份诊断中没有可靠载荷长度，尚不能直接构造完整网络包。

后续方向是从具有真实帧/段长度的上游边界取得长度，再与解码后的 IPC 对应，或接入 Deucalion 输出。其 [recv 实现](https://github.com/ff14wed/deucalion/blob/main/deucalion/src/hook/recv.rs) 在上游解压后处理帧；[packet 实现](https://github.com/ff14wed/deucalion/blob/main/deucalion/src/hook/packet.rs) 从帧解析长度，并在需要时保留长度用于后续解码重建。这支持更换采集边界的方向，不等于已经验证该版本 Deucalion 与本机客户端兼容。

本次仅审计、核对本地接收函数签名及上游源码，并记录结论；没有改动或部署运行代码。原始文件不修改，无需继续采集同类样本。

# 2026-10-03：Mortal 入桌前预热与会话取消

基于 `ee0fef7` 继续处理 [计划 P0](../plan-20261003.md) 的首摸加载超时。本轮不更换权重，不开放半庄，也不改变 1.5 秒和牌、5 秒鸣牌、4 秒舍牌等待默认值。

## 实现

- 登录后，Mortal 开启、处于 Hints 或 Auto-play 模式、当前版本存在已验证或用户已启用的有限试用 profile 时，在桌外后台准备一个空模型进程。无需先有 addon 变体；此时不发送任何 MJAI，也不启用 Mortal 网络消费者。
- `MortalProcessWarmup` 由 framework 线程管理，一次最多持有一个准备进程，不订阅建议回调、不携带上一场历史。已准备进程可以长期等待；关闭 Mortal、Off 模式、登出、失去版本准入、配置变化或卸载会取消。
- runner 原有 stderr `mortal_startup / ready=true` 标记模型准备完成；stdout 的合法关联确认也可提供兼容就绪信号。进程启动不再被当作模型已就绪，就绪前不能发布/取用推荐。
- 入桌后仍检查准确版本/实际变体、传输连接和完整开局。桥接接管同一个进程，不重复调用 Start。若尚在加载，可接管并按原队列/超时约束等待，不能强行暂停游戏时限。
- 桌外准备超过 45 秒未就绪或进程退出，释放后至少退避 15 秒。启动/退出仍由后台传输实现，framework 不等待 WSL、模型或磁盘。
- PreFinalize 归档后立即取消旧会话、清空推荐及旧网络预缓冲；等 reader 确认桌面消失后才允许准备下一场。新进程不继承 journal，不受上一场重启退避影响。
- 切换配置时先取消旧会话再 Poll，防止旧队列中的 reach 反应继续发包。旧回调仍核对实例身份。

没有新增配置字段，保留用户原设置。预热会保持一个模型进程，占用相应内存/显存；关闭 Mortal 或进入 Off 模式可释放。准备进程不等于获得实战准入。

## 修正的边界

测试复现了快速离桌再入桌时，旧 lastStartAttempt 仍阻止新准备进程接管的问题。现在有可接管进程时直接接管，后台失败重试仍保留退避。

离桌除清理桥接 journal 外，也清理 capture 的两秒 pre-roll 和两条消费者队列，避免上一桌开局在下一场启用消费者时被回放。原始日志及归档不删除，累计丢包计数不重置。

上一轮关于回调线程的判断有误：ReadAsync 把反应放入 notifications，Poll 才调用订阅者。生产桥接由 framework 调用 Poll。已恢复准确注释并更正架构/计划；本轮没有据此删除现有锁。

## 验证

插件测试项目 **637 项通过，0 失败，0 跳过**，比上一轮增加 19 项。覆盖：

- 未入桌不发送事件、准备中/已就绪进程一次性接管。
- 45 秒超时、15 秒退避、退出、Start 异常与资源释放。
- Off、配置变更、未知版本、传输未连接及 South 门禁。
- 旧回调隔离、PreFinalize 残留桌面、快速重入与历史不跨场。
- stderr 就绪无需游戏输入，Dispose 撤销就绪。
- 原有进程不读输入、队列满、重复确认、过期反应等回归同时通过。

命令：

```powershell
dotnet test tests/Mahjong.Plugin.Dalamud.Tests/Mahjong.Plugin.Dalamud.Tests.csproj -c Release --no-restore --nologo --verbosity minimal
```

日志：`artifacts/prewarm-tests-20261003.log`。测试构建同时更新原开发目录 `Mahjong.Plugin.Dalamud/bin/Release/` 的插件及 latest.zip。纯逻辑模块和 Python 本轮未修改，没有把上一轮检查冒充本轮重跑。远端 CI 尚未运行此次提交。

## 真实模型离线探针

使用同一份 v4 权重（SHA-256 `0a88ddad649804d085491b5397d895f596b0e55f30632c549ea145bb44786563`）和已有完整半庄的第一手。先启动真实 runner、等 stderr 就绪；此前 stdin **零条输入**，之后按历史顺序重放整手。

| 测量 | 本次结果 |
|---|---:|
| 进程创建至 ready | 6905 ms |
| runner 内部初始化 | 6826 ms |
| 其中依赖导入 | 4839 ms |
| ready 后第一条输入确认 | 2.76 ms |
| 第一条摸牌响应（包含首次推理准备） | 1663 ms |
| 完整手局 | 122 条输入、22 条响应，通过 |
| 子进程退出 | 正常，退出码 0 |

这说明准备等待可以放到入桌前，进程能在无 MJAI 输入的情况下就绪。**2.76 ms 是生命周期确认，不是摸牌决策耗时**；首次推理仍有约 1.66 秒准备。没有同步游戏 GPU 负载，不能保证实机首摸永不超时。

私有产物：`artifacts/prewarm-probe-20261003/report.json`、`runner.stderr.log`、`reactions.ndjson`；本机探针 `artifacts/probe_prewarm_20261003.py` 保留调用细节。模型、runner 与 libriichi 哈希在报告中。复跑探针需换输出目录，旧产物不覆盖。

## 使用与后续

在原 dev 目录热重载即可，权重和设置不用切换；Mortal 子进程由插件管理，不需要手工重启。登录后保持 Mortal 开启且处于提示/自动模式，桌外状态先显示 Prewarming model，完成后显示 Prewarm ready，入桌显示 Running。

本轮没有新实机验收。下次正常游玩再核对 first_ack、首摸响应和 timeout 日志即可，无需为开发额外刷对局。后续继续处理过期鸣牌提示，以及半庄比赛模式/结束边界；权重保持原样。

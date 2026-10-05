# 2026-10-05 连续采集离场修复

## 原因与修改

两场实机记录都在 DutyCompleted 后显示约 1.5 秒结算，随后 FinalResult 消失。控制器原本每次消失都清零 5 秒等待，适配器也要求结算画面仍可见，造成自动离场停滞。

控制器改为按已确认的整场完成锁存等待。适配器根据同一角色、已验证的东风副本、麻将桌存在、DutyCompleted、非切图/登出和游戏原生允许离场判断；派发前重新读取，结算面板不再是必要条件。局间 state 27 不等于完成。停止连续采集仍允许已完成的当前场退出但不重排；关闭自动操作禁止退出。

## 验证

- Release 插件测试 646/646 通过（原 637，本次新增净 9 项）。
- 覆盖短暂结算后在第 5 秒退出、停止后不重排、下一场清除完成状态、非目标副本/切图/原生拒绝退出及最多 3 次尝试。
- 构建输出仍为 Mahjong.Plugin.Dalamud/bin/Release，原 dev 路径可热重载。
- 尚未实机验证修改后的离场；无需为了策略实验额外排队，下次正常游玩观察即可。

命令：`dotnet test tests/Mahjong.Plugin.Dalamud.Tests/Mahjong.Plugin.Dalamud.Tests.csproj -c Release --no-restore --nologo --verbosity minimal`。
本地日志：`artifacts/exit-tests-20261005.log`。实机依据见 [两场复盘](20261005-live-prewarm-check.md)。

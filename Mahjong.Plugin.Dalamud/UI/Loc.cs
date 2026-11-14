namespace Mahjong.Plugin.Dalamud.UI;

/// <summary>
/// Inline UI localisation: every user-facing string is written as an English/Chinese pair and follows
/// Dalamud's UI language. Status strings stay English at the source because code compares them and logs
/// record them; <see cref="Status"/> translates them only at display time.
/// </summary>
internal static class Loc
{
    public static bool Chinese { get; private set; }

    public static void SetLanguage(string? language) =>
        Chinese = language is not null && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public static string T(string english, string chinese) => Chinese ? chinese : english;

    /// <summary>Display-time translation of status text; unknown fragments (versions, exception text) pass through.</summary>
    public static string Status(string? text)
    {
        if (string.IsNullOrEmpty(text) || !Chinese) return text ?? "";
        foreach (var (english, chinese) in StatusPhrases)
            text = text.Replace(english, chinese, StringComparison.Ordinal);
        return text;
    }

    // Longest phrases first so a shorter fragment never splits a longer one.
    private static readonly (string English, string Chinese)[] StatusPhrases = new (string, string)[]
    {
        // Continuous collection
        ("Continuous player East-only collection enabled. It runs until you stop it.", "已开启玩家东风战连续采集，会一直运行到你手动停止。"),
        ("No protocol profile for this client build yet. Play one match by hand with Mortal enabled so it can be learned.", "当前客户端版本还没有协议配置。请开启 Mortal 后手动打一场，让插件自动学习。"),
        ("Unverified duty or no protocol profile for this build; collection stopped", "任务未验证或当前版本没有协议配置，采集已停止"),
        ("This client build's matchmaking has not been verified.", "当前客户端版本的匹配流程尚未验证。"),
        ("Enable Auto-play in the main window first.", "请先在主窗口启用自动打牌。"),
        ("Log in before starting collection.", "请先登录游戏再开始采集。"),
        ("Leave your party before collecting solo ranked matches", "单人排位采集前请先退出小队"),
        ("Turn off unrestricted party / explorer mode", "请关闭超越之力限制 / 探索模式"),
        ("Waiting for Duty Finder availability / penalty", "等待任务搜索器可用 / 惩罚时间结束"),
        ("Waiting until character is free to queue", "等待角色空闲后排队"),
        ("Less than 1 GiB free for capture; collection stopped", "录包可用空间不足 1 GiB，采集已停止"),
        ("Recording disabled; collection stopped", "录制已关闭，采集已停止"),
        ("Packet capture reached its 64 MiB limit; collection stopped", "录包达到 64 MiB 上限，采集已停止"),
        ("Cannot check capture storage: ", "无法检查录包存储空间："),
        ("Checking capture storage", "正在检查录包存储空间"),
        ("Another or unknown duty queue is active; collection stopped", "正在排其他或未知的任务，采集已停止"),
        ("Another duty is active; collection stopped", "正在进行其他任务，采集已停止"),
        ("Auto-play disabled; collection stopped", "自动打牌已关闭，采集已停止"),
        ("Character changed; start collection again on this character", "角色已切换，请在当前角色上重新开始采集"),
        ("Commence was not available within 30 seconds; collection stopped", "30 秒内没有出现“开始”按钮，采集已停止"),
        ("Commence did not complete; collection stopped", "“开始”未能完成，采集已停止"),
        ("Could not leave final standings; leave the finished match manually", "无法离开最终排名画面，请手动退出已结束的对局"),
        ("Final standings; waiting to leave", "最终排名，等待离场"),
        ("Finishing current match; no new queue", "打完当前这一场，不再排队"),
        ("Leaving completed match", "正在离开已结束的对局"),
        ("Match found; waiting for Commence", "已匹配到对局，等待“开始”"),
        ("Playing and recording", "对局中，正在记录"),
        ("Queue registration timed out; collection stopped", "排队登记超时，采集已停止"),
        ("Queue registration was not accepted; check the game's error message", "排队登记未被接受，请查看游戏内的错误提示"),
        ("Queue withdrawal did not complete; cancel the mahjong queue manually", "撤销排队未完成，请手动取消麻将排队"),
        ("Queued for player East-only mahjong", "已排队：玩家东风战"),
        ("Registering for player East-only mahjong", "正在登记：玩家东风战"),
        ("Registering for mahjong", "正在登记麻将"),
        ("Confirming player East-only mahjong", "正在确认：玩家东风战"),
        ("Stopping: withdrawing mahjong queue", "正在停止：撤销麻将排队"),
        ("Unknown matchmaking state; collection stopped", "匹配状态未知，采集已停止"),
        ("Waiting to queue player East-only mahjong", "等待排队：玩家东风战"),
        ("Waiting to enter mahjong", "等待进入麻将"),
        ("Waiting for table transition", "等待牌桌切换"),
        ("Waiting for login", "等待登录"),
        ("Matchmaking unavailable", "匹配不可用"),
        // Mortal bridge
        ("Running; current kyoku quarantined, waiting for the next kyoku", "运行中；当前这一局已隔离，等待下一局"),
        ("Running; quarantined kyoku ended", "运行中；被隔离的一局已结束"),
        ("Running (model may still be loading)", "运行中（模型可能仍在加载）"),
        ("Running (model ready)", "运行中（模型已就绪）"),
        ("Running (replayed ", "运行中（已重放 "),
        (" MJAI events)", " 个 MJAI 事件）"),
        ("Waiting for the UI hand to repair an unknown start tile", "等待界面手牌修复未知的起手牌"),
        ("Protocol context changed; waiting for an observed opening", "协议上下文已变化，等待观测到新的开局"),
        ("Limited trial: incomplete public history", "限定试用：公开历史不完整"),
        ("Blocked an unsafe start_kyoku event", "已拦截一个不安全的开局事件"),
        ("Restarting after configuration change", "配置已变更，正在重启"),
        ("Restarting after process exit", "进程已退出，正在重启"),
        ("Recovering from decision timeout", "正在从决策超时中恢复"),
        ("Left table; old session cancelled", "已离桌，旧会话已取消"),
        ("Correcting provisional discard", "正在修正临时打牌"),
        ("Reach handshake failed: ", "立直握手失败："),
        ("Kan confirmation failed: ", "杠确认失败："),
        ("Send failed: ", "发送失败："),
        ("Start failed: ", "启动失败："),
        ("Prewarm failed or timed out; backing off", "预热失败或超时，稍后重试"),
        ("Prewarm failed: ", "预热失败："),
        ("; backing off", "，稍后重试"),
        ("Prewarm ready; waiting for table", "预热完成，等待上桌"),
        ("Prewarming model; no game inputs", "正在预热模型，尚未接收对局输入"),
        ("Prewarm adopted", "已接管预热好的进程"),
        ("Prewarm off", "预热关闭"),
        ("Mortal (red identity corrected)", "Mortal（已修正赤牌身份）"),
        ("Exited (", "已退出（"),
        // Protocol and learning
        ("Limited trial: East rounds, score-derived riichi pool, combined riichi, opponent kans, self closed/ordinary added kan/dora; self open/consecutive added kan unverified; ",
            "限定试用：东场、由分数推算的立直棒、合并立直、对手杠、自家暗杠/普通加杠/宝牌；自家明杠/连续加杠未验证；"),
        ("Verified protocol; ", "已验证协议；"),
        ("No admitted protocol for ", "没有可用的协议配置："),
        ("Waiting for a mahjong table; play one match by hand with Mortal enabled", "等待上桌：开启 Mortal 后手动打一场"),
        ("Using auto-learned protocol for this build", "当前版本使用自动学习的协议"),
        ("Learning protocol for ", "正在学习协议，版本 "),
        (" candidate packets this match", " 个本场候选数据包"),
        ("Analysing ", "正在分析上一场的 "),
        (" packets from the last match", " 个数据包"),
        ("Learned protocol for ", "已学会协议，版本 "),
        ("Not learned yet (", "暂未学会（"),
        ("); play another match", "），请再打一场"),
        ("Learning skipped: client variant unknown", "跳过学习：客户端变体未知"),
        ("Learning failed: ", "学习失败："),
        ("Ambiguous: several opcode combinations replay equally well", "有歧义：多种 opcode 组合回放效果相同"),
        (" decoded openings disagree with the game's table scores", " 个解码出的开局与游戏牌桌点数不一致"),
        ("Draw and discard counts do not balance", "摸牌与打牌数量对不上"),
        (" hands failed to decode under the best mapping", " 手在最佳映射下解码失败"),
        (" draw/discard events; need ", " 个摸牌/打牌事件，至少需要 "),
        (" clean hands; need ", " 手完整解出，至少需要 "),
        ("Only ", "只有 "),
        ("Not needed", "不需要"),
        // Packet capture
        ("Deucalion unavailable", "Deucalion 不可用"),
        ("Deucalion connected (decoded Zone receive)", "Deucalion 已连接（解码 Zone 接收）"),
        ("Connecting to Deucalion", "正在连接 Deucalion"),
        ("Deucalion DLL checksum mismatch", "Deucalion DLL 校验和不匹配"),
        ("Deucalion pipe did not become available; inspect %APPDATA%/deucalion logs", "Deucalion 管道未就绪，请查看 %APPDATA%/deucalion 日志"),
        (". Toggle capture to retry.", "。切换一次录包开关即可重试。"),
        ("Capture hook unavailable: ", "录包挂钩不可用："),
        ("Capture failed: packet headers rejected", "录包失败：数据包头被拒绝"),
        ("Capture stalled: no packets observed", "录包停滞：没有收到数据包"),
        ("Capture rejected: ", "录包被拒绝："),
        ("Armed; waiting for mahjong table", "已就绪，等待上麻将桌"),
        ("Recording with rejected packets", "录制中（有被拒绝的数据包）"),
        ("Waiting for first packet", "等待第一个数据包"),
        ("Waiting for packet capture", "等待录包"),
        ("Waiting for completed match", "等待对局结束"),
        ("Saved ", "已保存 "),
        // Single words last
        ("Disabled", "已禁用"),
        ("Disposed", "已释放"),
        ("Stopped", "已停止"),
        ("Recording", "录制中"),
        ("Running", "运行中"),
        ("Off", "关闭"),
    };
}

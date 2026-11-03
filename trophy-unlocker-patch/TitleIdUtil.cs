// 故意不写 namespace：挂在全局命名空间，Form1.cs 里任何 namespace 都能直接引用，
// 也就不用去猜原项目的命名空间叫什么。
using System;

/// <summary>
/// 原工具把 newpkgid 硬编码成 XDPX30000_00，导致所有 Unlocker 抢同一个 Title ID，
/// 装第二个就把第一个顶掉。这里按游戏 Title ID 派生出稳定的唯一 Title ID。
///
/// 输出必须满足两个约束：
///   1. 9 个字符（XXXX#####），param.sfo 的 TITLE_ID 定长 9；
///   2. 带 _00 后 12 字节，和 nptitle.dat 里的 "TITLEID_00" 等长，ReplaceBytes 才能原地替换。
/// </summary>
internal static class TitleIdUtil
{
    private const string Prefix = "XDPX";

    /// <summary>给某个游戏分配唯一 Title ID（9 字符，无 _00）。</summary>
    public static string ForGame(string gameTitleId)
    {
        if (string.IsNullOrEmpty(gameTitleId))
            return Prefix + "30000";

        // FNV-1a 32bit：同一个游戏每次算出同一个值，方便复现和排查
        uint h = 2166136261u;
        foreach (char c in gameTitleId.ToUpperInvariant())
        {
            h ^= c;
            h *= 16777619u;
        }

        uint n = h % 100000u;                  // 00000-99999，5 位十进制
        return Prefix + n.ToString("D5");
    }

    /// <summary>带 _00 后缀的版本，写 nptitle.dat 用。</summary>
    public static string ForGameNpTitle(string gameTitleId)
    {
        return ForGame(gameTitleId) + "_00";
    }
}

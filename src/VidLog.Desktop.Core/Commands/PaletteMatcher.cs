namespace VidLog.Desktop.Core.Commands;

/// <summary>命令面板里的一个候选（T12）。</summary>
/// <param name="Id">给调用方认的号（`settings` 这种）。界面拿它去查要做的事。</param>
/// <param name="Label">显示给人看的那一句。</param>
/// <param name="Keywords">还能用什么词命中它（「回放」之于「检索录像」）。**不显示**。</param>
public sealed record PaletteCandidate(string Id, string Label, string Keywords);

/// <summary>
/// 命令面板的匹配（T12）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>它是纯函数，住在 Core 里，理由只有一个：能测。</b>
/// App 那个工程是 WinExe，测试工程够不着它 —— 匹配写在那儿就只能靠手点。
/// 而「敲进去的词命中了什么、排在第几」恰恰是这个功能**唯一**会悄悄坏掉的地方
/// （坏的表现是「有时候搜得到、有时候搜不到」，没人会为此报缺陷）。
/// </para>
/// <para>
/// <b>不做拼音、不做首字母</b>：那是另一套东西（要字库），而命令表只有十来条，
/// 真需要时再加。子序列匹配已经兜住了「少打几个字」。
/// </para>
/// </remarks>
public static class PaletteMatcher
{
    /// <summary>
    /// 按 <paramref name="query"/> 排一遍，命中的在前。
    /// </summary>
    /// <remarks>
    /// <b>空查询原样返回全部</b>：面板刚打开、用户还没敲字时，列出全部是对的。
    /// </remarks>
    public static IReadOnlyList<PaletteCandidate> Rank(
        string? query, IReadOnlyList<PaletteCandidate> all)
    {
        ArgumentNullException.ThrowIfNull(all);

        var text = query?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            return all;
        }

        var scored = new List<(PaletteCandidate Candidate, int Score)>(all.Count);

        foreach (var candidate in all)
        {
            var score = Score(text, candidate);

            // 0 = 不命中。**不返回「勉强像的」** —— 一个排在第 6 位的无关条目
            // 比什么都没有更浪费时间，而回车是按第一条走的。
            if (score > 0)
            {
                scored.Add((candidate, score));
            }
        }

        // ⚠️ **同分时保持表里的顺序**（`OrderBy` 是稳定排序，所以不写 ThenBy 就是这个意思）：
        // 那张表是手写的，靠前的是更要紧的入口 —— 那个顺序是作者给的意思，
        // 比按显示名的码点排（对中文是**毫无意义**的顺序）强得多。
        // 而且它必须是**确定的**：同一次输入两次结果不一样会被当成随机现象。
        return scored
            .OrderByDescending(pair => pair.Score)
            .Select(pair => pair.Candidate)
            .ToList();
    }

    /// <summary>
    /// 打分。越大越靠前，<b>0 = 不命中</b>。
    /// </summary>
    /// <remarks>
    /// 四档，从强到弱：显示名<b>以它开头</b> → 显示名<b>包含</b>它 →
    /// 关键词包含它 → 显示名里**按顺序散落着**它的每个字（VS Code 那种模糊）。
    /// 每一档里「越靠前 / 越紧凑」的分越高。
    /// </remarks>
    private static int Score(string query, PaletteCandidate candidate)
    {
        const StringComparison how = StringComparison.OrdinalIgnoreCase;

        var label = candidate.Label;
        var at = label.IndexOf(query, how);

        if (at == 0)
        {
            // 1000 档：完全等于的排最前，然后越短的越靠前（短的那个更可能就是要找的）。
            return 1000 - label.Length;
        }

        if (at > 0)
        {
            return 800 - at;
        }

        if (candidate.Keywords.Contains(query, how))
        {
            return 600;
        }

        // 子序列：query 的每个字都得在 label 里按顺序找得到。
        // 记**最后一个字的位置**当跨度，越紧凑分越高。
        //
        // ⚠️ 这里用手写的循环，不用 `label.IndexOf(ch, cursor, …)` ——
        // 那个重载（char + 起点 + 比较方式）**不存在**，而 string 那个重载
        // 要为每个字造一个临时字符串。命令表只有十来条，但没必要。
        var last = 0;
        var cursor = 0;

        foreach (var ch in query)
        {
            var wanted = char.ToUpperInvariant(ch);
            var found = -1;

            for (var i = cursor; i < label.Length; i++)
            {
                if (char.ToUpperInvariant(label[i]) == wanted)
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                return 0;
            }

            last = found;
            cursor = found + 1;
        }

        return 400 - last;
    }
}

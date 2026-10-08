using VidLog.Desktop.Core.Configuration;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「设备与外观」那两行下拉：档位表本身、选中哪一档、点了没做的那一档会怎样
/// （T27② 第 4 批；外观主题那三档 2026-10-08 做出来了）。
/// </summary>
/// <remarks>
/// ⚠️ 抽出来的理由只有一个：**点了没做的那一档必须当场退回来**。
/// 那一档不落盘（见 <see cref="PreferenceOption"/> 的类型注释），
/// 留在下拉里就是一个**骗人的假开关**（踩坑 #13）——
/// 而它原先写在 <c>SettingsWindow.Preferences</c> 里，那个工程没有测试工程。
/// <para>
/// ⚠️ 那一套现在**只剩「界面语言」那一行**在用：外观主题 2026-10-08 起三档都真了，
/// 选中的依据从「表里真做了的那一档」变成「设置里存着的那一档」——
/// 见下面「外观主题那三档」一节。
/// </para>
/// </remarks>
public class AppPreferencesTests
{
    [Fact]
    public void 选中真做了的那一档()
    {
        // 照实显示现状：一进去就该停在真做了的那一档上，而不是从头一个开始。
        //
        // ⚠️ 原来是跑**两张**表的 `[Theory]`。2026-10-08 外观主题三档都做出来之后
        // 它只能留着**语言**那一半 —— 主题那一行改成「选**设置里存着**的那一档」，
        // 不再问 `RealIndex`（三档都能选，没有「真做了的」可挑）。
        // 只剩一张表的 `[Theory]` 等于 `[Fact]`，索性摊平，别拿它当空跑的幌子。
        var options = AppPreferences.Languages;
        var real = AppPreferences.RealIndex(options);

        Assert.True(options[real].Implemented);
    }

    [Fact]
    public void 语言那张表里眼下只该有一档是真做了的()
    {
        // ⚠️ 这一条钉的是 `RealIndex` 那条注释里的**前提**：眼下的表里真做了的
        // 只有一档，所以「取最后一个真的」与「取唯一那个真的」今天等价。
        //
        // ⚠️ 它原先跑的是**两张**表。2026-10-08 外观主题三档都做出来之后
        // **这一条在主题那张表上必须红**（三档全是真的）—— 那正是这两条
        // 各自分开的理由：`RealIndex` 从此只服务于**语言**那一行
        // （主题那一行改成「选设置里存着的那一档」，见下面的 `主题表…` 几节）。
        Assert.Single(AppPreferences.Languages, o => o.Implemented);
    }

    [Fact]
    public void 没做的那几档都要有一句解释()
    {
        // ⚠️ 少一句的话，界面上会出现「『English』还在开发中 —— 」后面空着，
        // 而那等于让用户去猜为什么不能选（正好是踩坑 #13 要防的）。
        //
        // ⚠️ 与上面那条同一个理由摊平成 `[Fact]`，只跑**语言**那一张表：
        // 主题那张表现在一档「没做的」都没有，foreach 一次都不进 ——
        // 留着它就是一条**永远绿的空测试**（主题那半由下面
        // `外观主题三档现在都是真的` 断言 `Hint` 全为 null）。
        foreach (var option in AppPreferences.Languages.Where(o => !o.Implemented))
        {
            Assert.False(string.IsNullOrWhiteSpace(option.Hint), option.Label);
        }
    }

    // ─────────────────────────────────────────────
    // 外观主题那三档（2026-10-08 起三档都做出来了）
    // ─────────────────────────────────────────────

    [Fact]
    public void 外观主题三档现在都是真的()
    {
        // ⚠️ 这一条盯的是「三档都做了」这个事实 —— 它一变红，说明有人把某一档
        // 又标回了「还没做」，而那会让界面上那档点下去**说一句过时的话再退回来**。
        // 与它成对的是上面那条语言表只该有一档真的。
        Assert.Equal(Enum.GetValues<AppThemeMode>().Length, AppPreferences.Themes.Count);
        Assert.All(AppPreferences.Themes, o => Assert.True(o.Implemented, o.Label));

        // 真做了的档不该再挂着「还没做」那句话（挂了就会被 `RejectNote` 捡去念）。
        Assert.All(AppPreferences.Themes, o => Assert.Null(o.Hint));
    }

    [Fact]
    public void 表里的第i项就是枚举的第i档()
    {
        // ⚠️ **承重**，而且是两条独立的规矩：
        //
        //  ① **表里的次序 = 枚举的次序**：界面按下标填下拉、设置里按下标选档
        //     （`IndexOf` 就是 `(int)mode`）。两边错开一位，下拉里写着「深色」
        //     而存进去的是「浅色」—— 屏幕上只是颜色不对，不会有任何东西喊。
        //  ② **枚举的数值别动**（顺序即格式）：值落进 `settings.json`，
        //     重排 = 老设置文件里那个数字换了意思。尤其 `FollowSystem` 必须是 0
        //     （老文件里没这一项 ⇒ 取默认值 0，见那个枚举的 remarks）。
        //
        // ⚠️ 这里**故意把预期写死一份**：不写死就没法证明上面那两条 ——
        // 拿 `IndexOf`/`ThemeOf` 互相验是**同义反复**（两个都是 `(int)` 转换），
        // 表里怎么排它都绿。重复一份预期，才是那条只会红的检查。
        Assert.Equal(["跟随系统", "浅色", "深色"], AppPreferences.Themes.Select(o => o.Label));

        Assert.Equal(0, (int)AppThemeMode.FollowSystem);
        Assert.Equal(1, (int)AppThemeMode.Light);
        Assert.Equal(2, (int)AppThemeMode.Dark);

        // 一对一的往返回路（`ThemeOf` 与 `IndexOf` 是一对，都吃枚举数值）。
        foreach (var mode in Enum.GetValues<AppThemeMode>())
        {
            Assert.Equal(mode, AppPreferences.ThemeOf(AppPreferences.IndexOf(mode)));
        }

        for (var i = 0; i < AppPreferences.Themes.Count; i++)
        {
            Assert.Equal(i, AppPreferences.IndexOf(AppPreferences.ThemeOf(i)));
        }
    }

    [Fact]
    public void 认不出的下标退跟随系统()
    {
        // ⚠️ 退这一档的理由：它**就是做出这三档之前的行为**，也是「用户没表态」时
        // 唯一说得通的那一档。退浅色/深色等于凭空替用户选了一种颜色。
        foreach (var bad in new[] { -1, 3, 99, int.MinValue, int.MaxValue })
        {
            Assert.Equal(AppThemeMode.FollowSystem, AppPreferences.ThemeOf(bad));
        }
    }

    [Fact]
    public void 主题那句说明必须把系统现状说出来()
    {
        // ⚠️ 「跟随系统」那两句**必须不一样**：系统是浅色的人选了这一档会看到
        // 什么都没变，不说出「现在是浅色」就像点了没反应（踩坑 #13）。
        var followDark = AppPreferences.ThemeNote(AppThemeMode.FollowSystem, systemIsDark: true);
        var followLight = AppPreferences.ThemeNote(AppThemeMode.FollowSystem, systemIsDark: false);

        Assert.NotEqual(followDark, followLight);
        Assert.Contains("深色", followDark);
        Assert.Contains("浅色", followLight);

        // 固定那两档反过来：**不许**跟着系统现状变（它们与系统无关），
        // 而且各自要把自己那一档说出来 —— 说反了就是「选深色、写着浅色」。
        Assert.Equal(
            AppPreferences.ThemeNote(AppThemeMode.Dark, systemIsDark: true),
            AppPreferences.ThemeNote(AppThemeMode.Dark, systemIsDark: false));
        Assert.Equal(
            AppPreferences.ThemeNote(AppThemeMode.Light, systemIsDark: true),
            AppPreferences.ThemeNote(AppThemeMode.Light, systemIsDark: false));
        Assert.Contains("固定深色", AppPreferences.ThemeNote(AppThemeMode.Dark, systemIsDark: false));
        Assert.Contains("固定浅色", AppPreferences.ThemeNote(AppThemeMode.Light, systemIsDark: true));

        // 三档各说各的，没有两句撞在一起（撞了说明某一档漏了分支、落到兜底上）。
        var all = new[]
        {
            followDark, followLight,
            AppPreferences.ThemeNote(AppThemeMode.Light, false),
            AppPreferences.ThemeNote(AppThemeMode.Dark, false),
        };
        Assert.Equal(all.Length, all.Distinct().Count());
    }

    [Fact]
    public void 显示的字从表里取不另写一份()
    {
        // 写两份的下场是同一边写「跟随系统」、另一边写「跟随 Windows」。
        foreach (var mode in Enum.GetValues<AppThemeMode>())
        {
            Assert.Equal(AppPreferences.Themes[AppPreferences.IndexOf(mode)].Label,
                AppPreferences.Describe(mode));
        }
    }

    [Fact]
    public void 真做出两档时取靠后的那一档()
    {
        // ⚠️ 真实的两张表眼下各只有一档是真的（上面那条绊线盯着），所以
        // 「取最后一个」与「取唯一那个」在那两张表上分不开 —— 这一条用一份
        // **造的**表把规矩钉死：将来真做出第二档时，它多半排在后面，
        // 而「选中第一个真的」会静默选错那一档。
        var fake = new List<PreferenceOption>
        {
            new("甲", true, null),
            new("乙", true, null),
        };

        Assert.Equal(1, AppPreferences.RealIndex(fake));
    }

    [Fact]
    public void 一档都没做时也不至于没有选中项()
    {
        // 不是正常情形；回 0 是为了至少停在一个看得见的地方。
        Assert.Equal(0, AppPreferences.RealIndex([new("甲", false, "还没做")]));
    }

    [Fact]
    public void 选中的就是真做了的那一档时放行()
    {
        var real = AppPreferences.RealIndex(AppPreferences.Languages);

        Assert.Null(AppPreferences.RejectNote(AppPreferences.Languages, real, real));
    }

    [Fact]
    public void 退回时又进来那一趟也要放行()
    {
        // ⚠️ 退回（`SelectedIndex = real`）会让事件再进来一次。那一趟必须放行，
        // 否则就是递归 —— 「没选中任何一档」这一支就是给那一刻用的。
        Assert.Null(AppPreferences.RejectNote(AppPreferences.Languages, selected: -1, real: 1));
    }

    [Fact]
    public void 点了没做的那一档要说清是哪一档加为什么()
    {
        var note = AppPreferences.RejectNote(AppPreferences.Languages, selected: 2, real: 1);

        Assert.NotNull(note);
        Assert.Contains("English", note);
        Assert.Contains("还在开发中", note);
        Assert.Contains("英文界面还没做", note);
    }

    [Fact]
    public void 越界的下标不许抛()
    {
        // 事件是外部触发的，别为此崩一个窗口 —— 放行即可（那时什么都不会发生）。
        Assert.Null(AppPreferences.RejectNote(AppPreferences.Languages, selected: 9, real: 1));
    }
}

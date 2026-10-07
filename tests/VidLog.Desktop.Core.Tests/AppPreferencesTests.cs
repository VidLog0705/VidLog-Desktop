using VidLog.Desktop.Core.Configuration;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「设备与外观」那两行下拉：选中哪一档、点了没做的那一档会怎样
/// （T27② 第 4 批）。
/// </summary>
/// <remarks>
/// ⚠️ 抽出来的理由只有一个：**点了没做的那一档必须当场退回来**。
/// 那一档不落盘（见 <see cref="PreferenceOption"/> 的类型注释），
/// 留在下拉里就是一个**骗人的假开关**（踩坑 #13）——
/// 而它原先写在 <c>SettingsWindow.Preferences</c> 里，那个工程没有测试工程。
/// </remarks>
public class AppPreferencesTests
{
    public static TheoryData<IReadOnlyList<PreferenceOption>> 两张表 => new()
    {
        AppPreferences.Languages,
        AppPreferences.Themes,
    };

    [Theory]
    [MemberData(nameof(两张表))]
    public void 选中真做了的那一档(IReadOnlyList<PreferenceOption> options)
    {
        // 照实显示现状：一进去就该停在真做了的那一档上，而不是从头一个开始。
        var real = AppPreferences.RealIndex(options);

        Assert.True(options[real].Implemented);
    }

    [Theory]
    [MemberData(nameof(两张表))]
    public void 表里眼下只该有一档是真做了的(IReadOnlyList<PreferenceOption> options)
    {
        // ⚠️ 这一条钉的是 `RealIndex` 那条注释里的**前提**：眼下的表里真做了的
        // 只有一档，所以「取最后一个真的」与「取唯一那个真的」今天等价。
        // 哪天真做出第二档，这条会红 —— 那时回来看 `RealIndex` 的注释。
        Assert.Single(options, o => o.Implemented);
    }

    [Theory]
    [MemberData(nameof(两张表))]
    public void 没做的那几档都要有一句解释(IReadOnlyList<PreferenceOption> options)
    {
        // ⚠️ 少一句的话，界面上会出现「『深色』还在开发中 —— 」后面空着，
        // 而那等于让用户去猜为什么不能选（正好是踩坑 #13 要防的）。
        foreach (var option in options.Where(o => !o.Implemented))
        {
            Assert.False(string.IsNullOrWhiteSpace(option.Hint), option.Label);
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

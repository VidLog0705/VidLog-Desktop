using System.Text.Json;
using System.Text.Json.Serialization;
using VidLog.Desktop.Core.Labels;

namespace VidLog.Desktop.Core.Cleanup;

/// <summary>
/// 一个保留期档位（规格 §3.5.2.1）。
/// </summary>
/// <remarks>
/// <para>
/// 规格原话（2026-09-24 需求变更）：「本机可以选择存储的退货和发货视频保留多久，
/// 已备份保留多久，未备份保留多久，退货和发货视频分开选择。」选项列表是
/// 「**不保留 / 3 / 5 / 7 / 10 / 15 / 30 / 自定义 / 全部保留**」（9 项）。
/// </para>
/// <para>
/// ⚠️ <b><see cref="Days"/> 是 <c>int?</c>：<c>null</c> = 全部保留，<c>0</c> = 不保留。</b>
/// 这两者的区别是真实存在的（永远不删 vs 归档后最快 24 小时），
/// 所以不能拿 0 或 -1 去兼职表示「不删」——手机端 <c>RetentionSetting.days</c>
/// 是同一个形状、同一个理由。
/// </para>
/// <para>
/// ⚠️ <b>「自定义」不是一个值，是「列表之外的任意正整数天」.</b>
/// 它在类型上不需要单独一个成员：界面上给一个输入框，存下来的就是那个数，
/// 而 <see cref="Label"/> 对它显示成「N 天」。
/// </para>
/// </remarks>
[JsonConverter(typeof(RetentionSettingJsonConverter))]
public sealed record RetentionSetting(int? Days)
{
    /// <summary>全部保留（**默认值**，规格 §3.5.2.1）。</summary>
    /// <remarks>
    /// ⚠️ 规格特意提醒过：选项列表里「不保留」排在最前面、而「全部保留」排在最后，
    /// **默认值仍然是它** —— 绝不能因为「不保留」排在第一就当默认
    /// （那会在 24 小时后开始删东西）。
    /// </remarks>
    public static RetentionSetting KeepAll { get; } = new((int?)null);

    /// <summary>「不保留」。⚠️ 实际生效是「归档成功后**最快 24 小时**」——见下。</summary>
    /// <remarks>
    /// 规格 §3.5.3③「最近 24 小时内产生的」是**硬性豁免、用户关不掉**，
    /// 所以「不保留」= 备份成功后最快的那个清理时机。
    /// **界面上必须把这句话写出来**：用户选了它却看见东西还在，
    /// 不说清楚他会以为坏了（踩坑 #13）。
    /// </remarks>
    public static RetentionSetting Immediate { get; } = new(0);

    /// <summary>下拉里那 8 个**具体档位**（第 9 项「自定义」是个输入框，不是一个值）。</summary>
    public static IReadOnlyList<RetentionSetting> Standard { get; } =
    [
        Immediate,
        new(3), new(5), new(7), new(10), new(15), new(30),
        KeepAll,
    ];

    /// <summary>档位是不是「列表里那 8 个之一」；不是就是用户自己填的天数。</summary>
    public bool IsCustom => !Standard.Contains(this);

    public string Label => Days switch
    {
        null => "全部保留",
        0 => "不保留",
        { } days => $"{days} 天",
    };

    /// <summary>永不清理。</summary>
    public bool KeepsEverything => Days is null;

    /// <summary>
    /// 解析一个天数。**非法值一律回落到「全部保留」。**
    /// </summary>
    /// <remarks>
    /// 朝**少删**的那头落 —— 与手机端 <c>RetentionSetting.fromConfig</c>、
    /// 锁值认不出当「锁着」是同一条规矩。负数尤其要拦：它会让
    /// <c>now.AddDays(-(-5))</c> 把 cutoff 推到未来，于是**一律判超期 ⇒ 全删**。
    /// </remarks>
    public static RetentionSetting FromConfig(int? days) =>
        days is null or < 0 ? KeepAll : new RetentionSetting(days);
}

/// <summary>
/// 把「保留期」读写成一个单独的整数（或 <c>null</c>）。
/// </summary>
/// <remarks>
/// ⚠️ <b>必须同时认老格式。</b> 2026-09-27 之前存的是
/// <c>{"Mode":1,"KeepDays":7}</c>（那时它是另一个类型 <c>RetentionPolicy</c>）。
/// 不认老格式的话，老设置文件会因为一个字段反序列化失败而**整份回落默认值** ——
/// 用户丢的不只是保留期，还有工作模式、端口、摄像头那些。那种损失是**静默**的。
/// </remarks>
public sealed class RetentionSettingJsonConverter : JsonConverter<RetentionSetting>
{
    /// <summary>
    /// ⚠️ **必须为真，否则 <c>null</c> 会绕过这个转换器。**
    /// </summary>
    /// <remarks>
    /// System.Text.Json 对引用类型默认**不把 <c>null</c> 交给转换器** ——
    /// 它直接把属性赋成 null。而这里写出去的就是 <c>null</c>
    /// （「全部保留」没有天数可写），于是读回来时四个槽位全是 null，
    /// 下一个碰它们的地方当场 NRE（**测试抓到的**）。
    /// </remarks>
    public override bool HandleNull => true;

    /// <summary>
    /// ⚠️ **这里刻意不「宽容」：越界的值原样留着，交给 `IsPlausible` 去拦。**
    /// </summary>
    /// <remarks>
    /// 宽容（`FromConfig`）会让一个手改出来的 <c>-5</c> 被**静默**改成「全部保留」——
    /// 方向是对的（朝少删），但用户不会知道他的设置被改过（I3）。
    /// 留着原值的话，`IsPlausible` 会判否 ⇒ **整份回落默认值 + 一条警告**，
    /// 那正是这个仓一贯的口径（越界不静默接受）。
    /// <para>
    /// 宽容留给**另一头**：界面上手输的文本、以及程序内部的调用，
    /// 那里「随便给什么都行」是对的（见 <see cref="RetentionSetting.FromConfig"/>）。
    /// </para>
    /// <para>
    /// 认不出**类型**的写法（字符串、数组…）给一个越界哨兵值 <c>-1</c>，
    /// 理由同上：宁可让整份回落并说一句，也不要静默换掉用户的选择。
    /// </para>
    /// </remarks>
    public override RetentionSetting Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return RetentionSetting.KeepAll;

            case JsonTokenType.Number:
                return reader.TryGetInt32(out var days)
                    ? new RetentionSetting(days)
                    : Invalid;

            case JsonTokenType.StartObject:
                var legacy = ReadLegacy(ref reader);
                return legacy.Days is < 0 ? Invalid : legacy;

            default:
                reader.Skip();
                return Invalid;
        }
    }

    /// <summary>越界哨兵：让 <c>IsPlausible</c> 判否，从而整份回落并说出来。</summary>
    private static RetentionSetting Invalid { get; } = new(-1);

    /// <summary>老格式：<c>{"Mode":0|1|2,"KeepDays":n}</c>。</summary>
    internal static RetentionSetting ReadLegacy(ref Utf8JsonReader reader)
    {
        int? mode = null;
        int? keepDays = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var name = reader.GetString();
            reader.Read();

            if (string.Equals(name, "Mode", StringComparison.OrdinalIgnoreCase))
            {
                mode = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var m) ? m : null;
            }
            else if (string.Equals(name, "KeepDays", StringComparison.OrdinalIgnoreCase))
            {
                keepDays = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var d) ? d : null;
            }
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }
        }

        // Mode：0 = 全部保留，1 = 按天数，2 = 按空间。
        // ⚠️ 按空间那一档在新模型里**没有对应值** —— 回落到「全部保留」
        // （朝少删的那头落）。真要按空间清理，那是另一条触发路径，不是保留期档位。
        return mode switch
        {
            1 => RetentionSetting.FromConfig(keepDays),
            _ => RetentionSetting.KeepAll,
        };
    }

    public override void Write(
        Utf8JsonWriter writer, RetentionSetting value, JsonSerializerOptions options)
    {
        // ⚠️ `value` 可能是 **null 本身**（<see cref="HandleNull"/> 为真时
        // 那个 null 也会走到这里来），所以这里不能直接点它的成员。
        if (value?.Days is { } days)
        {
            writer.WriteNumberValue(days);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>
/// **老格式的那两个数**（发货 / 退货各一个）—— 只读、不写。
/// </summary>
/// <remarks>
/// ⚠️ 它与 <see cref="RetentionSettingJsonConverter"/> 的差别只有一个字，但很要紧：
/// **<c>null</c> 保持 <c>null</c>。**
/// <para>
/// 那个转换器把 <c>null</c> 读成「全部保留」（因为四个槽位写出去的就是 <c>null</c>，
/// 读回来必须是默认值）；而这里 <c>null</c> 的含义恰恰相反 ——
/// 它是「**老格式里没有这个键**」。把两者混用的话，
/// 每一次存读都会被误判成「从旧格式迁移」，还平白多一条警告
/// （**测试抓到的**）。
/// </para>
/// <para>
/// <see cref="JsonConverter{T}.HandleNull"/> 默认是 <see langword="false"/>，
/// 所以 <c>null</c> 由 STJ 直接赋值、根本不进 <see cref="Read"/> —— 正是要的行为。
/// </para>
/// </remarks>
public sealed class LegacyRetentionSettingJsonConverter : JsonConverter<RetentionSetting?>
{
    public override RetentionSetting? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt32(out var days)
                ? RetentionSetting.FromConfig(days)
                : null,
            JsonTokenType.StartObject => RetentionSettingJsonConverter.ReadLegacy(ref reader),
            _ => null,
        };

    public override void Write(
        Utf8JsonWriter writer, RetentionSetting? value, JsonSerializerOptions options)
    {
        // 永远不该走到这里 —— 那两个属性写盘时被 `WhenWritingNull` 挡掉了
        // （它们恒为 null，而这一份数据是**只读**的兼容入口）。
        writer.WriteNullValue();
    }
}

/// <summary>
/// 保留期**四个数**：发货 / 退货 × 已备份 / 未备份（规格 §3.5.2.1）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>两列的语义完全不同 —— 这是这一节最要紧的一句话。</b>
/// </para>
/// <list type="table">
/// <item><description><b>已备份</b>：到期<b>真删本地副本</b>（先过 §3.5.4 回查），起算点 = <b>归档成功时刻</b>。</description></item>
/// <item><description><b>未备份</b>：到期<b>只标红、只催上传，永不自动删</b>，起算点 = <b>录完时刻</b>。</description></item>
/// </list>
/// <para>
/// 「未备份」那一列永远不删任何东西 —— 那是**唯一副本**，删了就永久没了
/// （§3.5.3① 的硬豁免，I2）。它到期的动作只有提醒。
/// </para>
/// <para>
/// ⚠️ 界面上**必须写明**这一列「到期只提醒、不会删除」。否则用户选了「不保留」
/// 却看见东西还在，会以为坏了；反过来更糟 —— 他会以为自己选了「24 小时删」，
/// 于是**不敢选**，或者**误以为删过了**。
/// </para>
/// </remarks>
public sealed record RetentionSettings
{
    /// <summary>
    /// ⚠️ **这个无参构造是必需的，而且带 <see cref="JsonConstructorAttribute"/>。**
    /// </summary>
    /// <remarks>
    /// 四个槽位都是引用类型，而设置文件里**没有这四个键**是常态
    /// （老格式只有 <c>Outbound</c>/<c>Return</c>，全新装的过程则一个都没有）。
    /// 位置记录 + 参数化构造下，System.Text.Json 会给缺失的属性传
    /// <c>default</c> —— 也就是把四个槽位全填成 <see langword="null"/>，
    /// 而 <see cref="PlausibleDays"/> 紧接着就会 NRE（**测试抓到的**）。
    /// 属性初始化器把「缺」变成「全部保留」，这也正是它该是的默认值。
    /// </remarks>
    [JsonConstructor]
    public RetentionSettings()
    {
    }

    public RetentionSettings(
        RetentionSetting archivedOutbound,
        RetentionSetting archivedReturn,
        RetentionSetting unarchivedOutbound,
        RetentionSetting unarchivedReturn)
    {
        ArchivedOutbound = archivedOutbound;
        ArchivedReturn = archivedReturn;
        UnarchivedOutbound = unarchivedOutbound;
        UnarchivedReturn = unarchivedReturn;
    }

    /// <summary>已备份那一列 —— 发货。到期**真删**（先过 §3.5.4 回查），起算点 = 归档成功时刻。</summary>
    public RetentionSetting ArchivedOutbound { get; init; } = RetentionSetting.KeepAll;

    /// <summary>已备份那一列 —— 退货。</summary>
    public RetentionSetting ArchivedReturn { get; init; } = RetentionSetting.KeepAll;

    /// <summary>未备份那一列 —— 发货。到期**只催上传、永不删**，起算点 = 录完时刻。</summary>
    public RetentionSetting UnarchivedOutbound { get; init; } = RetentionSetting.KeepAll;

    /// <summary>未备份那一列 —— 退货。</summary>
    public RetentionSetting UnarchivedReturn { get; init; } = RetentionSetting.KeepAll;

    /// <summary>四个数全是「全部保留」—— 出厂默认（规格 §6.2：删除必须极度克制）。</summary>
    public static RetentionSettings KeepAll { get; } = new(
        RetentionSetting.KeepAll, RetentionSetting.KeepAll,
        RetentionSetting.KeepAll, RetentionSetting.KeepAll);

    /// <summary>
    /// **老格式**（两个数）留下来的那一对，只为读。
    /// </summary>
    /// <remarks>
    /// 2026-09-27 之前存的是 <c>{"Outbound":7,"Return":30}</c>，那时它是一个
    /// 「已备份后的本地保留期」。所以它对应的是**新模型的已备份那一列** ——
    /// 语义没变，只是多了一列。
    /// <para>
    /// ⚠️ 不认它的话，老设置文件里那个数会被**静默丢掉**（反序列化跳过未知属性），
    /// 用户看到的是「我明明设过 7 天，怎么变回全部保留了」。丢掉的方向是安全的
    /// （朝少删的那头），但**静默**是这里不能接受的。
    /// </para>
    /// </remarks>
    /// <remarks>
    /// ⚠️ 写盘时**不写这两个键**（`WhenWritingNull` + 它们恒为 null）——
    /// 写出去的话，下一版读回来还得再判一次「这是不是老格式」，
    /// 而那个判断每一次往返都多一条假警告。
    /// </remarks>
    [JsonPropertyName("Outbound")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(LegacyRetentionSettingJsonConverter))]
    public RetentionSetting? LegacyOutbound { get; init; }

    /// <summary>老格式的退货那一份。见 <see cref="LegacyOutbound"/>。</summary>
    [JsonPropertyName("Return")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(LegacyRetentionSettingJsonConverter))]
    public RetentionSetting? LegacyReturn { get; init; }

    /// <summary>把老格式的两个数搬进已备份那一列。</summary>
    public RetentionSettings WithLegacyApplied() =>
        this with
        {
            ArchivedOutbound = LegacyOutbound ?? ArchivedOutbound,
            ArchivedReturn = LegacyReturn ?? ArchivedReturn,
        };

    /// <summary>已备份那一列，按业务类型取。</summary>
    public RetentionSetting ArchivedFor(BusinessType type) =>
        type == BusinessType.Return ? ArchivedReturn : ArchivedOutbound;

    /// <summary>未备份那一列，按业务类型取。</summary>
    public RetentionSetting UnarchivedFor(BusinessType type) =>
        type == BusinessType.Return ? UnarchivedReturn : UnarchivedOutbound;

    /// <summary>四个数里有没有任何一个会删东西。</summary>
    /// <remarks>
    /// 界面上用来说清楚「现在到底会不会删」。⚠️ 只看**已备份**那一列 ——
    /// 未备份那一列永远不删（这正是它容易被误解的地方）。
    /// </remarks>
    public bool DeletesAnything => !ArchivedOutbound.KeepsEverything || !ArchivedReturn.KeepsEverything;
}

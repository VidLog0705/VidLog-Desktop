namespace VidLog.Desktop.Core.Diagnostics;

/// <summary>
/// 「把诊断包发回给我们」那封信的**收件人、标题、正文**（需求方 2026-10-03 指定）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 这里**只装文本**，发信那一步在界面层（<c>Platform/MailComposer</c>，
/// 走系统邮件客户端）。分开的理由很实际：这几行是**本机测得了**的部分
/// （地址写错了这份包就寄到别人那儿去了，而那是个没人会发现的错），
/// 而调 MAPI 那一半在这台机器上根本测不了。
/// </para>
/// <para>
/// ⚠️ 正文里**不许出现包裹信息**：它是随包一起外发的，而单号是客户的东西。
/// 现有的正文只有本机名与版本号。
/// </para>
/// </remarks>
public static class SupportMail
{
    /// <summary>收件人。需求方 2026-10-03 写死，**不许**从别处拼出来。</summary>
    public const string Address = "Allen816@foxmail.com";

    /// <summary>
    /// 标题。带**机器名 + 时间**：售后同时收到几台机器的包时，光看标题就能分开，
    /// 而附件里那份 zip 的文件名是不带机器名的。
    /// </summary>
    public static string Subject(string machineName, DateTimeOffset at) =>
        $"VidLog 诊断包 - {machineName} - {at:yyyy-MM-dd HH:mm}";

    /// <summary>
    /// 正文。**最后一行是留给用户的** —— 出问题的是他那台机器，他比日志多知道
    /// 一件事：「什么时候开始的、屏幕上当时是什么样」。那句话往往才是解开问题的钥匙。
    /// </summary>
    public static string Body(string machineName, string version, DateTimeOffset at) =>
        $"""
         这台机器的诊断包在附件里。
         机器名：{machineName}
         版本：{version}
         导出时间：{at:yyyy-MM-dd HH:mm:ss zzz}

         附件里是日志、设置与环境信息（不含任何视频，也不含包裹单号）。

         请在这里补一句这台机器出了什么问题：
         """;
}

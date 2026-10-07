namespace VidLog.Desktop.Core.Configuration;

/// <summary>
/// 写进 Run 键的那条命令行（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 抽出来只为让「引号加对了没有」这件事**看得见** —— 这是整套里唯一一处
/// 写错了会让 Windows 启动**别的程序**的地方：路径里有空格
/// （<c>C:\Program Files\…</c>）而不加引号时，系统会去启动 <c>C:\Program</c>，
/// 而那条命令**登记成功**、开机时也**真的尝试了**，报错在别的地方。
/// </para>
/// <para>
/// ⚠️ 拿不到 exe 路径时回 <see langword="null"/>，**不猜一个**：猜的那个
/// 后果与上面一样（开机启动一个不存在甚至不对的东西）。
/// </para>
/// </remarks>
public static class StartupCommand
{
    /// <summary>要写进 Run 键的那条命令行；拿不到 exe 路径时为 <see langword="null"/>。</summary>
    public static string? Build(string? exePath) =>
        string.IsNullOrWhiteSpace(exePath) ? null : $"\"{exePath}\"";
}

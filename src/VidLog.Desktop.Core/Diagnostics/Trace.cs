using System.Diagnostics;

namespace VidLog.Desktop.Core.Diagnostics;

/// <summary>
/// 一次请求（或一次任务）的关联 id，<b>随异步调用链自动流动</b>。
/// </summary>
/// <remarks>
/// <para>
/// 用 <see cref="AsyncLocal{T}"/> 而不是「把 id 一路当参数传下去」：一次上传要经过
/// 路由 → 鉴权 → 收分片 → 校验 → 拼装 → 落盘 → 签回执，中间还会 `await` 到别的线程上。
/// 每一层都多一个参数的话，**漏传一层就是那一段日志没有 id**，
/// 而 id 的全部意义正是「把这些行串起来」。
/// </para>
/// <para>
/// ⚠️ AsyncLocal 的流动是**向下**的：谁设的、它 `await` 出去的那些调用才看得见。
/// 所以只能由**最外层**（<c>PlaybackServer.HandleAsync</c> 入口）设，
/// 不能在里层设了指望外面看见。
/// </para>
/// <para>
/// ⚠️ 它是<b>异步上下文</b>的属性，不是线程的属性：同一个线程先后处理两个请求，
/// 两个请求各有各的 id（各自在自己的 async 上下文里）。这一点正是不能用
/// <c>[ThreadStatic]</c> 的原因 —— HTTP 处理是线程池上的异步续体，线程会串。
/// </para>
/// </remarks>
public static class Trace
{
    private static readonly AsyncLocal<string?> Holder = new();

    /// <summary>当前上下文里的 id；不在任何请求里时为 <see langword="null"/>。</summary>
    public static string? Current => Holder.Value;

    /// <summary>设一个新的 id（返回它，方便顺手记进第一行日志）。</summary>
    public static string Start()
    {
        var id = NewId();
        Holder.Value = id;
        return id;
    }

    /// <summary>清掉（请求结束时）。</summary>
    /// <remarks>
    /// 不清理也不是灾难（下一个请求会盖掉），但线程池上的续体可能**晚于**
    /// 请求结束才跑到某条日志 —— 那种行带着上一个请求的 id 会更误导。
    /// </remarks>
    public static void Clear() => Holder.Value = null;

    /// <summary>
    /// 8 个十六进制字符。
    /// </summary>
    /// <remarks>
    /// 短是刻意的：它是给人肉眼对日志用的（在诊断包里按它筛），不是安全标识 ——
    /// 撞了就撞了，两条日志的其它字段（时间、路径）本来也能区分开。
    /// 用 <see cref="Activity"/> 那套的话要多一层订阅与传播，对这里不值得。
    /// </remarks>
    private static string NewId() =>
        Random.Shared.Next(0x10000000, int.MaxValue).ToString("x8");
}

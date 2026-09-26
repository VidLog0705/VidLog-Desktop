using System.Speech.Synthesis;

// 本工程同时开了 UseWPF 与 UseWindowsForms，ImplicitUsings 会把两边的同名类型
// 都带进来 —— 与 App.xaml.cs / CrashGuard.cs 同一条规矩：钉死来源。
namespace VidLog.Desktop.App.Platform;

/// <summary>
/// 语音播报（规格 §3.3.3 的闲置提醒要「**语音播报**提醒」）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>用系统的语音合成（SAPI），不随包带任何音频</b> —— 与手机端走系统 TTS、
/// 与「不许引入音频素材文件」（规格 §3.3.6 / §10 的许可证账）是同一条理由。
/// 这里用到的是 Windows 自带的语音引擎：<c>System.Speech</c> 这个包只是**调它的门**，
/// 不携带任何声音数据。
/// </para>
/// <para>
/// ⚠️ <b>一切失败都吞掉</b>：没装语音包、引擎被禁用、正在说话 ——
/// 这些都不该影响录制（I4 的同一条精神）。提醒是尽力而为，
/// 而**录制不能因为一句提醒播不出来就出问题**。
/// </para>
/// <para>
/// 做成静态而不是实例：调用点在 <c>App</c> 的事件处理器里（一句 <c>Speak</c>），
/// 而语音引擎是**进程级**的资源 —— 每一句都新建一个 <see cref="SpeechSynthesizer"/>
/// 会明显卡顿（它要初始化引擎）。这里用一个懒建的共享实例。
/// </para>
/// </remarks>
internal static class Speech
{
    private static readonly Lock Gate = new();
    private static SpeechSynthesizer? _synth;

    /// <summary>念一句。失败什么都不做（也不抛）。</summary>
    public static void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                _synth ??= Create();
                if (_synth is null)
                {
                    return;
                }

                // SpeakAsync：**不阻塞**调用方。这句话是从协调器的看门狗线程
                // 发出来的，同步念完要好几秒 —— 那会把看门狗拖住，
                // 而下一个到点的提醒就晚了。
                _synth.SpeakAsync(text);
            }
        }
        catch (Exception)
        {
            // 见类注释：播不出来不是错误。
        }
    }

    private static SpeechSynthesizer? Create()
    {
        try
        {
            var synth = new SpeechSynthesizer();

            // 语速稍快：提醒要在一两秒内说完，用户正在干活。
            synth.Rate = 1;

            return synth;
        }
        catch (Exception)
        {
            // 没有可用的语音引擎（精简版 Windows、语音包被卸载）——
            // 退化成「只有托盘气泡」，如实这样，不假装说了。
            return null;
        }
    }
}

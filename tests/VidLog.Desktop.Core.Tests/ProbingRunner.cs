using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 规格探测的假 runner：只记 argv，并按 <c>-y</c> 那个位置造一个产物，让探测能走完。
/// </summary>
/// <remarks>
/// ⚠️ <b>提成共用的，不在各自的测试类里各写一份。</b>
/// 它守的不变量是「**探测跑的就是录制那一份 argv**」，而那条不变量对
/// **本机设备与网络地址两只**都要成立（见 <c>docs/实现决策.md</c> §65）。
/// 各写一份替身的话，两边的判据迟早会走岔 —— 而走岔的恰好就是这条不变量本身。
/// </remarks>
internal sealed class ProbingRunner : IProcessRunner
{
    public List<List<string>> Invocations { get; } = [];

    public Task<ProcessResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var args = arguments.ToList();
        Invocations.Add(args);

        // 按 `-y` 的位置定位产物 —— 与 ProductLocator 的约定同一处来源：
        // 录制 argv 里 `-y` 后面紧跟输出路径。
        var y = args.IndexOf("-y");
        if (y >= 0 && y + 1 < args.Count)
        {
            var path = args[y + 1];
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "probed-bytes");
        }

        // 其余调用（解码校验）一律报成功 —— 本类只关心 argv。
        return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
    }
}

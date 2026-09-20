using System.Text;
using System.Text.Json;

namespace VidLog.Desktop.Core.Punches;

/// <summary>一次打点的来源。</summary>
public enum PunchSource
{
    /// <summary>键盘扫码枪。</summary>
    KeyboardScanner,

    /// <summary>摄像头识码。</summary>
    CameraDecoder,

    /// <summary>手动输入兜底（规格 §3.2.2：框内识别不到时用户可手动输入）。</summary>
    ManualEntry,
}

/// <summary>
/// 一次打点 —— **时刻**与单号的关联。
/// </summary>
/// <remarks>
/// 规格 §1 术语表：打点是「识别到单号的那**一时刻**」。
/// 与录像（区间）的关系见母仓 `docs/02-数据模型.md` §2：
/// 一次打包事件是区间，可能横跨多个分段；打点只是区间里的一个点。
/// </remarks>
/// <param name="PunchId">本地生成，全局唯一（回放要按它定位）。</param>
/// <param name="SessionId">所属录制会话。</param>
/// <param name="WaybillNumber">该时刻识别到的单号。</param>
/// <param name="PunchedAt">墙钟时刻。仅用于呈现 —— 算位置用 <paramref name="MonotonicOffset"/>。</param>
/// <param name="MonotonicOffset">相对会话起点的**单调**偏移，毫秒。</param>
/// <param name="Source">识别入口。</param>
public sealed record Punch(
    string PunchId,
    string SessionId,
    WaybillNumber WaybillNumber,
    DateTimeOffset PunchedAt,
    long MonotonicOffsetMilliseconds,
    PunchSource Source);

/// <summary>打点日志。</summary>
public interface IPunchLog
{
    /// <summary>
    /// 追加一次打点。
    /// </summary>
    /// <remarks>
    /// 规格 §3.2.4：**打点必须立即持久化**，不能只存内存（掉电会丢）。
    /// 所以这个方法是「产生即调用」，不是会话结束时批量写。
    /// </remarks>
    Task AppendAsync(Punch punch, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Punch>> LoadAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>按行追加的打点日志。与录像索引同样的形态与理由（见 <c>RecordingIndex</c>）。</summary>
public sealed class JsonLinesPunchLog : IPunchLog
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLinesPunchLog(string path)
    {
        _path = path;
    }

    public async Task AppendAsync(Punch punch, CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(PunchDto.From(punch), SerializerOptions);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(
                _path,
                line + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Punch>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(_path, cancellationToken);
        var punches = new List<Punch>(lines.Length);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var dto = JsonSerializer.Deserialize<PunchDto>(line, SerializerOptions);
            if (dto is not null)
            {
                punches.Add(dto.ToPunch());
            }
        }

        return punches;
    }
}

/// <summary>打点的落盘形态（值对象有私有构造函数，不能直接反序列化）。</summary>
public sealed record PunchDto(
    string PunchId,
    string SessionId,
    string WaybillNumber,
    string PunchedAt,
    long MonotonicOffsetMilliseconds,
    string Source)
{
    public static PunchDto From(Punch punch) => new(
        punch.PunchId,
        punch.SessionId,
        punch.WaybillNumber.Value,
        punch.PunchedAt.ToString("O"),
        punch.MonotonicOffsetMilliseconds,
        punch.Source.ToString());

    public Punch ToPunch() => new(
        PunchId,
        SessionId,
        Core.WaybillNumber.Parse(WaybillNumber),
        DateTimeOffset.Parse(PunchedAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
        MonotonicOffsetMilliseconds,
        Enum.TryParse<PunchSource>(Source, out var source) ? source : PunchSource.ManualEntry);
}

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Punches;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 规格 §3.4 / §5.1：手机端把录像传上来，接收方校验、**原子发布**、签名回执。
/// </summary>
/// <remarks>
/// 这些测试直接打 <see cref="UploadReceiver"/>，不起 HTTP —— 路由那一层由
/// <c>PlaybackServerTests</c> 用真 HTTP 覆盖。分在这里是为了让
/// 「发布是不是原子的」「重发会不会换时间锚」这类断言读起来就是它们说的那件事。
/// </remarks>
public class UploadReceiverTests
{
    private const string DeviceName = "测试主机";
    private const string DeviceId = "phone-1";
    private const string Waybill = "SF1000000001";
    private const string SessionId = "sess-1";
    private const int Sequence = 0;
    private const string EvidenceId = "sess-1-000";

    private static readonly DateTimeOffset StartedAt = new(2026, 9, 23, 2, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 一把形态正确的凭据（base64url 的 32 字节），与入网时签发的那种一致。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不能用随手写的字符串：签名的 HMAC 密钥是**凭据的原始字节**，所以要真是 base64url。
    /// 生产里这一点由 <see cref="DeviceRegistry"/> 保证 —— 它只产出这种形态，
    /// 而验签时用的那把凭据又必然来自它的登记簿。
    /// </remarks>
    private static string NewCredential() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-upload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>解码校验结果可控的假 FFmpeg。</summary>
    private sealed class FakeRunner : IProcessRunner
    {
        public string? StandardError { get; set; }
        public int ExitCode { get; set; }

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(ExitCode, string.Empty, StandardError ?? string.Empty));
    }

    private sealed class Harness : IDisposable
    {
        public required TempDir Dir { get; init; }
        public required DataLayout Layout { get; init; }
        public required JsonLinesRecordingIndex Index { get; init; }
        public required JsonLinesPunchLog Punches { get; init; }
        public required JsonLinesLabelStore Labels { get; init; }
        public required FakeRunner Runner { get; init; }
        public required UploadReceiver Receiver { get; init; }
        public required DeviceRegistry Registry { get; init; }

        public void Dispose() => Dir.Dispose();
    }

    private static Harness Build()
    {
        var dir = new TempDir();
        var layout = new DataLayout(dir.Path);
        layout.EnsureCreated();

        var index = new JsonLinesRecordingIndex(layout.IndexPath);
        var punches = new JsonLinesPunchLog(layout.PunchLogPath);
        var labels = new JsonLinesLabelStore(layout.LabelStorePath);
        var runner = new FakeRunner();

        return new Harness
        {
            Dir = dir,
            Layout = layout,
            Index = index,
            Punches = punches,
            Labels = labels,
            Runner = runner,
            Receiver = new UploadReceiver(
                layout,
                index,
                punches,
                labels,
                new DecodeVerifier("ffmpeg", runner),
                DeviceName),
            Registry = new DeviceRegistry(layout.DevicesPath),
        };
    }

    // ───────────── 造一份可提交的东西 ─────────────

    private sealed record Payload(int ChunkCount, ContentHash Hash, IReadOnlyList<byte[]> Chunks, CommitRequest Request);

    private static Payload BuildPayload(params int[] chunkSizes)
    {
        var chunks = new List<byte[]>();
        var all = new List<byte>();

        foreach (var size in chunkSizes)
        {
            var bytes = RandomNumberGenerator.GetBytes(size);
            chunks.Add(bytes);
            all.AddRange(bytes);
        }

        var whole = all.ToArray();
        var hash = ContentHash.Parse(Convert.ToHexStringLower(SHA256.HashData(whole)));

        var request = new CommitRequest(
            EvidenceId: EvidenceId,
            SessionId: SessionId,
            Sequence: Sequence,
            Waybill: Waybill,
            StartedAt: StartedAt,
            EndedAt: StartedAt.AddSeconds(300),
            ContentHash: hash.Value,
            SourceDeviceId: DeviceId,
            ChunkCount: chunks.Count,
            ChunkHashes: chunks.Select(c => Convert.ToHexStringLower(SHA256.HashData(c))).ToList(),
            Punches:
            [
                new PunchPayload("p1", SessionId, Waybill, StartedAt.AddSeconds(30), 30_000, "CameraDecoder"),
            ],
            Labels:
            [
                new LabelPayload(EvidenceId, LabelKeys.BusinessType, BusinessTypes.OutboundValue, StartedAt),
            ]);

        return new Payload(chunks.Count, hash, chunks, request);
    }

    /// <summary>把分片按序喂给接收方，模拟手机端的一次正常上传。</summary>
    private static async Task UploadChunksAsync(Harness h, Payload payload)
    {
        for (var i = 0; i < payload.Chunks.Count; i++)
        {
            using var body = new MemoryStream(payload.Chunks[i]);
            await h.Receiver.StoreChunkAsync(EvidenceId, i, body);
        }
    }

    // ─────────────────────────────────────────────
    // 原子发布
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 提交成功才进索引_提交之前归档层与检索里都看不见它()
    {
        using var h = Build();
        var payload = BuildPayload(1024);
        await UploadChunksAsync(h, payload);

        // 分片收齐了，但还没提交 —— 此刻它**不该**出现在任何一条读取路径上。
        // 这就是「要么完整可见、要么完全不可见」：判据是索引，而索引还没写过。
        Assert.Empty(await h.Index.LoadAllAsync());

        var response = await h.Receiver.CommitAsync(payload.Request, DeviceId, NewCredential());

        var entries = await h.Index.LoadAllAsync();
        var entry = Assert.Single(entries);
        Assert.Equal(EvidenceId, entry.EvidenceId);
        Assert.Equal(payload.Hash.Value, entry.ContentHash.Value);
        Assert.Equal(EvidenceId, response.Receipt.EvidenceId);
    }

    [Fact]
    public async Task 归档路径与本机录制是同一套布局()
    {
        using var h = Build();
        var payload = BuildPayload(1024);
        await UploadChunksAsync(h, payload);

        var response = await h.Receiver.CommitAsync(payload.Request, DeviceId, NewCredential());

        // ArchiveLayout 是本地录制与远端上传**共用**的那一份规则。
        // 两处各写一遍的话，它们会在某次改动里悄悄走岔，而走岔之后不会报错。
        Assert.Equal("2026/09/23/SF1000000001/sess-1_000.mp4", response.Receipt.Location);
        Assert.True(System.IO.File.Exists(
            System.IO.Path.Combine(h.Layout.ArchiveRoot, response.Receipt.Location)));
    }

    [Fact]
    public async Task 分片不齐时被拒_并且什么都不落()
    {
        using var h = Build();

        // 两片的东西，只传了第 0 片。
        var payload = BuildPayload(16, 16);
        using var only = new MemoryStream(payload.Chunks[0]);
        await h.Receiver.StoreChunkAsync(EvidenceId, 0, only);

        var error = await Assert.ThrowsAsync<UploadRejectedException>(
            () => h.Receiver.CommitAsync(payload.Request, DeviceId, NewCredential()));

        Assert.Equal(UploadErrors.ChunkMissing, error.Code);
        Assert.Empty(await h.Index.LoadAllAsync());
        Assert.Empty(Directory.GetFiles(h.Layout.ArchiveRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task 整体哈希对不上被拒_并且不发布()
    {
        using var h = Build();
        var payload = BuildPayload(1024);

        // 分片本身是好的，但报文里报的整体哈希是另一份 —— 拼起来对不上。
        var wrong = payload.Request with { ContentHash = new string('a', 64) };

        await UploadChunksAsync(h, payload);

        var error = await Assert.ThrowsAsync<UploadRejectedException>(
            () => h.Receiver.CommitAsync(wrong, DeviceId, NewCredential()));

        Assert.Equal(UploadErrors.HashMismatch, error.Code);
        Assert.Empty(await h.Index.LoadAllAsync());
        Assert.Empty(Directory.GetFiles(h.Layout.ArchiveRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task 解码校验不过的东西不发布_手机上的原文件因此不会被删()
    {
        using var h = Build();
        h.Runner.StandardError = "Invalid data found when processing input";

        var payload = BuildPayload(1024);
        await UploadChunksAsync(h, payload);

        var error = await Assert.ThrowsAsync<UploadRejectedException>(
            () => h.Receiver.CommitAsync(payload.Request, DeviceId, NewCredential()));

        Assert.Equal(UploadErrors.Unplayable, error.Code);
        Assert.Empty(await h.Index.LoadAllAsync());
        Assert.Empty(Directory.GetFiles(h.Layout.ArchiveRoot, "*", SearchOption.AllDirectories));
    }

    // ─────────────────────────────────────────────
    // 重发一次 commit（回执丢了）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 同一次提交重发_返回的是原样那份回执_时间锚不变()
    {
        using var h = Build();
        var payload = BuildPayload(1024);
        await UploadChunksAsync(h, payload);

        var credential = NewCredential();
        var first = await h.Receiver.CommitAsync(payload.Request, DeviceId, credential);

        // 回执在网络上丢了，发送方重发。⚠️ 若这时重新签一份，同一条证据就有了两个
        // 时间锚 —— 而那正是取证产品里最像篡改的东西。
        var second = await h.Receiver.CommitAsync(payload.Request, DeviceId, credential);

        Assert.Equal(first.Receipt.TimeAnchor, second.Receipt.TimeAnchor);
        Assert.Equal(first.Receipt.PublishedAt, second.Receipt.PublishedAt);
        Assert.Equal(first.Signature, second.Signature);
        Assert.Single(await h.Index.LoadAllAsync());
    }

    [Fact]
    public async Task 重发不会把打点写两遍()
    {
        using var h = Build();
        var payload = BuildPayload(1024);
        await UploadChunksAsync(h, payload);

        var credential = NewCredential();
        await h.Receiver.CommitAsync(payload.Request, DeviceId, credential);
        await h.Receiver.CommitAsync(payload.Request, DeviceId, credential);

        // 打点日志是追加写且读取时不去重 —— 多写一遍就会在回放里同一时刻出现两个打点。
        Assert.Single(await h.Punches.LoadAllAsync());
    }

    [Fact]
    public async Task 同一个id但内容不同_判already_published()
    {
        using var h = Build();
        var first = BuildPayload(1024);
        await UploadChunksAsync(h, first);
        await h.Receiver.CommitAsync(first.Request, DeviceId, NewCredential());

        var second = BuildPayload(2048);
        await UploadChunksAsync(h, second);

        var error = await Assert.ThrowsAsync<UploadRejectedException>(
            () => h.Receiver.CommitAsync(second.Request, DeviceId, NewCredential()));

        Assert.Equal(UploadErrors.AlreadyPublished, error.Code);
        Assert.Single(await h.Index.LoadAllAsync());
    }

    // ─────────────────────────────────────────────
    // 续传
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 传了一半的分片不算已有_否则续传会永远卡住()
    {
        using var h = Build();

        var totalBytes = UploadReceiver.ChunkSize + 1024;
        const int chunkCount = 2;

        var probe = new ProbeRequest(EvidenceId, chunkCount, new string('a', 64), totalBytes);

        // 第 0 片传完整；第 1 片只写了一半 —— 就像连接在写到一半时断了。
        using var first = new MemoryStream(RandomNumberGenerator.GetBytes((int)UploadReceiver.ChunkSize));
        await h.Receiver.StoreChunkAsync(EvidenceId, 0, first);

        var directory = System.IO.Path.Combine(h.Layout.UploadIncomingRoot, EvidenceId);
        await System.IO.File.WriteAllBytesAsync(System.IO.Path.Combine(directory, "1.part"), new byte[512]);

        var response = await h.Receiver.ProbeAsync(probe);

        // ⚠️ 半截的第 1 片**不能**报成已有。报了的话发送方就不重传，
        // 一路卡到重试耗尽 —— 界面上是一条永远好不了的「上传失败」，而只差最后半片。
        Assert.Equal(new[] { 0 }, response.Have);
    }

    [Fact]
    public async Task 分片大小由接收方回报()
    {
        using var h = Build();
        var response = await h.Receiver.ProbeAsync(new ProbeRequest(EvidenceId, 1, new string('a', 64), 10));

        Assert.Equal(UploadReceiver.ChunkSize, response.ChunkSize);
        Assert.Empty(response.Have);
    }

    // ─────────────────────────────────────────────
    // 报文校验（信任边界）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 自称的设备id与凭据不符被拒()
    {
        using var h = Build();
        var payload = BuildPayload(1024);
        await UploadChunksAsync(h, payload);

        var forged = payload.Request with { SourceDeviceId = "别人的设备" };

        var error = await Assert.ThrowsAsync<UploadRejectedException>(
            () => h.Receiver.CommitAsync(forged, DeviceId, NewCredential()));

        Assert.Equal(UploadErrors.BadRequest, error.Code);
    }

    [Fact]
    public async Task 索引里的sourceDeviceId来自凭据而不是报文()
    {
        using var h = Build();
        var payload = BuildPayload(1024);
        await UploadChunksAsync(h, payload);

        await h.Receiver.CommitAsync(payload.Request, DeviceId, NewCredential());

        var entry = Assert.Single(await h.Index.LoadAllAsync());
        Assert.Equal(DeviceId, entry.SourceDeviceId);
    }

    [Fact]
    public async Task evidenceId与sessionId加序号对不上被拒()
    {
        using var h = Build();
        var payload = BuildPayload(1024);
        await UploadChunksAsync(h, payload);

        // sessionId 自己带 `-`，从 evidenceId 反推序号是有歧义的。
        // 对不上就拒 —— 拼错了不会报错，只会让文件名与证据 id 静默对不上。
        var mismatched = payload.Request with { EvidenceId = "sess-1-007" };

        var error = await Assert.ThrowsAsync<UploadRejectedException>(
            () => h.Receiver.CommitAsync(mismatched, DeviceId, NewCredential()));

        Assert.Equal(UploadErrors.EvidenceMismatch, error.Code);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public async Task 证据id里有路径字符一律拒(string evil)
    {
        using var h = Build();

        // 比「转义一下」更严：两个不同的 id 转义后可能撞成同一个目录，
        // 而那是两条证据混在一起 —— 不会报错。
        var error = await Assert.ThrowsAsync<UploadRejectedException>(
            () => h.Receiver.ProbeAsync(new ProbeRequest(evil, 1, new string('a', 64), 10)));

        Assert.Equal(UploadErrors.BadRequest, error.Code);
    }

    [Fact]
    public async Task 分片超过接收方定的分片大小被拒()
    {
        using var h = Build();

        var oversized = new MemoryStream(new byte[UploadReceiver.ChunkSize + 1]);

        var error = await Assert.ThrowsAsync<UploadRejectedException>(
            () => h.Receiver.StoreChunkAsync(EvidenceId, 0, oversized));

        Assert.Equal(UploadErrors.BadRequest, error.Code);
    }

    // ─────────────────────────────────────────────
    // 入网（规格 §3.4.5）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 配对码正确才发凭据_而且只发一次()
    {
        using var h = Build();

        var pending = await h.Registry.RequestAsync(DeviceId, "打包手机-1");

        var approved = await h.Registry.ClaimAsync(DeviceId, pending.Code);
        Assert.Equal(EnrollStatus.Approved, approved.Status);
        Assert.NotNull(approved.Credential);

        // 凭据只发放一次：领走之后这条请求就销毁了，同一个设备再来要重新走一遍入网
        // （§1.1 第 4 步：凭据丢失 → 重新入网，**不得降级为免凭据**）。
        var again = await h.Registry.ClaimAsync(DeviceId, pending.Code);
        Assert.Equal(EnrollStatus.NoPendingRequest, again.Status);
        Assert.Null(again.Credential);
    }

    [Fact]
    public async Task 配对码连错到上限就作废()
    {
        using var h = Build();
        var pending = await h.Registry.RequestAsync(DeviceId, "打包手机-1");

        // 前 4 次只是「不对」；第 5 次同时把这条请求作废掉 —— 那一步报「不对」没用，
        // 那时要说得出下一步该干什么（重发入网请求）。
        for (var i = 0; i < DeviceRegistry.MaxFailedAttempts - 1; i++)
        {
            var attempt = await h.Registry.ClaimAsync(DeviceId, "000000");
            Assert.Equal(EnrollStatus.BadCode, attempt.Status);
        }

        var last = await h.Registry.ClaimAsync(DeviceId, "000000");
        Assert.Equal(EnrollStatus.NoPendingRequest, last.Status);

        // 6 位码是 10⁶ 量级，没有次数限制的话，局域网里跑一晚上足够撞开。
        // 作废之后，连**正确的**那个码也换不到凭据。
        var after = await h.Registry.ClaimAsync(DeviceId, pending.Code);
        Assert.Equal(EnrollStatus.NoPendingRequest, after.Status);
    }

    [Fact]
    public async Task 配对码过期后不能再换()
    {
        var directory = new TempDir();
        try
        {
            var now = new DateTimeOffset(2026, 9, 23, 2, 0, 0, TimeSpan.Zero);
            var registry = new DeviceRegistry(directory.File("devices.jsonl"), () => now);

            var pending = await registry.RequestAsync(DeviceId, "打包手机-1");

            now += DeviceRegistry.CodeLifetime + TimeSpan.FromSeconds(1);

            var late = await registry.ClaimAsync(DeviceId, pending.Code);
            Assert.Equal(EnrollStatus.NoPendingRequest, late.Status);
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public async Task 重新入网后旧凭据立即失效()
    {
        using var h = Build();

        var first = await h.Registry.RequestAsync(DeviceId, "打包手机-1");
        var oldCredential = (await h.Registry.ClaimAsync(DeviceId, first.Code)).Credential!;

        var second = await h.Registry.RequestAsync(DeviceId, "打包手机-1");
        var newCredential = (await h.Registry.ClaimAsync(DeviceId, second.Code)).Credential!;

        // 后写的赢：旧凭据作废。两份都能用的话，重新入网就没起到作用。
        Assert.Null(await h.Registry.FindByCredentialAsync(oldCredential));
        Assert.NotNull(await h.Registry.FindByCredentialAsync(newCredential));
    }

    [Fact]
    public async Task 凭据能反查出是哪台设备()
    {
        using var h = Build();

        var pending = await h.Registry.RequestAsync(DeviceId, "打包手机-1");
        var credential = (await h.Registry.ClaimAsync(DeviceId, pending.Code)).Credential!;

        var device = await h.Registry.FindByCredentialAsync(credential);

        Assert.NotNull(device);
        Assert.Equal(DeviceId, device.DeviceId);
        Assert.Equal("打包手机-1", device.DeviceName);
    }

    [Fact]
    public async Task 认不出的凭据反查不出设备()
    {
        using var h = Build();

        var pending = await h.Registry.RequestAsync(DeviceId, "打包手机-1");
        await h.Registry.ClaimAsync(DeviceId, pending.Code);

        Assert.Null(await h.Registry.FindByCredentialAsync(NewCredential()));
    }

    // ─────────────────────────────────────────────
    // 签名（docs/05-上传接口形状.md §2.7）
    // ─────────────────────────────────────────────

    [Fact]
    public void 规范串是七行且不带末尾换行()
    {
        var receipt = new ReceiptPayload(
            EvidenceId: EvidenceId,
            ContentHash: new string('a', 64),
            PublishedAt: new DateTimeOffset(2026, 9, 23, 2, 31, 52, 117, TimeSpan.Zero),
            TimeAnchor: new DateTimeOffset(2026, 9, 23, 2, 31, 52, 117, TimeSpan.Zero),
            ReceiverDeviceId: "host-1",
            ReceiverDeviceName: "打包间-左",
            Location: "2026/09/23/SF1000000001/sess-1_000.mp4");

        var canonical = ReceiptSignature.Canonicalize(receipt);
        var lines = canonical.Split('\n');

        Assert.Equal(8, lines.Length);
        Assert.Equal("vidlog-receipt/v1", lines[0]);

        // ⚠️ 时间必须是 **7 位小数 + `+00:00`**。Dart 的 toIso8601String() 给的是
        // 3 位小数 + `Z`，两端在这一处对不上就会表现成「回执验签失败」——
        // 而那看起来像被篡改了。
        Assert.Equal("2026-09-23T02:31:52.1170000+00:00", lines[3]);
        Assert.Equal("2026-09-23T02:31:52.1170000+00:00", lines[4]);

        // contentHash 是裸的 64 位十六进制，不带 `sha256:` 前缀。
        Assert.Equal(new string('a', 64), lines[2]);

        Assert.False(canonical.EndsWith("\n", StringComparison.Ordinal));
    }

    [Fact]
    public void 换了内容签名就变()
    {
        var receipt = new ReceiptPayload(
            EvidenceId, new string('a', 64),
            new DateTimeOffset(2026, 9, 23, 2, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 23, 2, 0, 0, TimeSpan.Zero),
            "host-1", "打包间-左", "2026/09/23/SF1/sess-1_000.mp4");

        var credential = NewCredential();
        var original = ReceiptSignature.Compute(credential, receipt);

        Assert.NotEqual(original, ReceiptSignature.Compute(credential, receipt with { Location = "别处" }));
        Assert.NotEqual(original, ReceiptSignature.Compute(NewCredential(), receipt));
    }

    // ─────────────────────────────────────────────
    // 回执存储
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 回执按evidenceId追加_后者胜出()
    {
        var directory = new TempDir();
        try
        {
            var store = new ReceiptStore(directory.File("receipts.jsonl"));

            var first = new ReceiptPayload(
                EvidenceId, new string('a', 64),
                new DateTimeOffset(2026, 9, 23, 2, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 23, 2, 0, 0, TimeSpan.Zero),
                "host-1", "打包间-左", "2026/09/23/SF1/sess-1_000.mp4");

            await store.AppendAsync(first);

            Assert.Null(await store.FindAsync("别的证据"));
            Assert.Equal(first.Location, (await store.FindAsync(EvidenceId))!.Location);

            await store.AppendAsync(first with { Location = "2026/09/23/SF1/改过了.mp4" });
            Assert.Equal("2026/09/23/SF1/改过了.mp4", (await store.FindAsync(EvidenceId))!.Location);
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public async Task 回执文件里的坏行不影响其他行()
    {
        var directory = new TempDir();
        try
        {
            var path = directory.File("receipts.jsonl");
            await System.IO.File.WriteAllTextAsync(path, "这不是 JSON\n", Encoding.UTF8);

            var store = new ReceiptStore(path);
            var receipt = new ReceiptPayload(
                EvidenceId, new string('a', 64),
                new DateTimeOffset(2026, 9, 23, 2, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 23, 2, 0, 0, TimeSpan.Zero),
                "host-1", "打包间-左", "2026/09/23/SF1/sess-1_000.mp4");

            await store.AppendAsync(receipt);

            Assert.NotNull(await store.FindAsync(EvidenceId));
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public async Task 回执落盘是可读的JSON行()
    {
        using var h = Build();
        var payload = BuildPayload(1024);
        await UploadChunksAsync(h, payload);

        await h.Receiver.CommitAsync(payload.Request, DeviceId, NewCredential());

        var line = Assert.Single(await System.IO.File.ReadAllLinesAsync(h.Layout.ReceiptsPath));
        using var document = JsonDocument.Parse(line);

        Assert.Equal(EvidenceId, document.RootElement.GetProperty("EvidenceId").GetString());
    }
}

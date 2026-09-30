using VidLog.Desktop.Core.Cloud;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 百度网盘那一档的上传：出入队、对比去重、分片跳过、失败与重试（批次 5）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>这里没有一条真请求。</b>HTTP 被收在 <see cref="IBaiduPanApi"/> 这一个窄缝隙后面，
/// 上面那一整套流程（预创建 → 传分片 → 合并 → 列目录对账 → 重试）用假件跑到 ——
/// 而它正是这一批里唯一有真逻辑的部分。
/// </para>
/// <para>
/// <b>验不了的</b>（要真账号、真网络，本机一条都验不了，如实写在这里）：
/// 真登录过百度网盘没有、真文件传上去没有、网盘回的 <c>errno</c> 是不是这几个数、
/// 限流与超时在真实网络下的表现。见 <c>docs/实现决策.md</c> §87。
/// </para>
/// </remarks>
public class CloudUploadTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.FromHours(8));

    // ─────────────────────────────────────────────
    // 收尾那一条路（设计图 `_45` 的「启用自动上传」）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 自动上传没开时_收尾那条路回的是失败而不是成功()
    {
        // ⚠️ 这条是这一批里**后果最大**的一条判据。写成「没开就当传过了」的话，
        // 回查归档层（I8）会以为云端有一份，于是本机**唯一**的那一份被清理删掉。
        using var harness = new Harness(CloudUploadSettings.Default); // AutoUpload = false
        var location = await harness.AddRecordingAsync("e1");

        var result = await harness.Service.PublishAsync(
            location, harness.LocalPathOf(location));

        Assert.False(result.Published);
        Assert.Contains("启用自动上传", result.FailureReason);

        Assert.Empty(harness.Api.Created);
        Assert.Empty(await harness.Queue.LoadAsync());

        // 而且本机那一份**一个字都没动**。
        Assert.True(File.Exists(harness.LocalPathOf(location)));
    }

    [Fact]
    public async Task 自动上传开着时_收尾那条路真的传上去()
    {
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");

        var result = await harness.Service.PublishAsync(
            location, harness.LocalPathOf(location));

        Assert.True(result.Published, result.FailureReason);

        // 远端路径照设计图 `_46` 的形状（默认标签是「发货」）。
        var created = Assert.Single(harness.Api.Created);
        Assert.Matches(@"^/apps/VidLog/2026/09/30/发货/SF1000000001_", created);
        Assert.EndsWith("_000.mp4", created);

        var item = await harness.Queue.FindAsync("e1");
        Assert.NotNull(item);
        Assert.Equal(CloudUploadState.Done, item.State);
        Assert.Equal(1, item.Attempts);
    }

    [Fact]
    public async Task 收尾传上去的那一条_补传不会再传一遍()
    {
        // 两条入口（收尾 / 补传）共用同一个队列与同一段上传逻辑。
        // 各写一份的话，这里就会看到同一个文件被传两次。
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");

        await harness.Service.PublishAsync(location, harness.LocalPathOf(location));
        await harness.Service.SyncNowAsync();

        Assert.Single(harness.Api.Created);
    }

    // ─────────────────────────────────────────────
    // 补传那一条路（设计图 `_46` 的「自动对比补传」）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 网盘上已经有了的_标成完成且不重传()
    {
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");

        // 网盘那个目录在，里面已经有同名的那个文件。
        var remote = harness.Layout.RemotePath(location, BusinessType.Outbound);
        harness.Api.Put(remote);

        await harness.Service.SyncNowAsync();

        Assert.Empty(harness.Api.UploadedSlices);
        Assert.Empty(harness.Api.Created);

        var item = await harness.Queue.FindAsync("e1");
        Assert.NotNull(item);
        Assert.Equal(CloudUploadState.Done, item.State);
    }

    [Fact]
    public async Task 对比时目录还不存在_当空目录而不是查不了()
    {
        // ⚠️ 网盘对不存在的目录回 errno -9（`DirectoryMissing`）。
        // 把它当成「查不了」的话，**第一次同步会一条都传不出去** ——
        // 而那时用户看到的只是「立即对比同步：这次没有新传上去的」。
        using var harness = new Harness(Open());
        await harness.AddRecordingAsync("e1");

        // 完全不去建任何目录 ⇒ ListFilesAsync 一路回 -9。
        Assert.Empty(harness.Api.Directories);

        var uploaded = await harness.Service.SyncNowAsync();

        Assert.Equal(1, uploaded);
        Assert.Single(harness.Api.Created);
    }

    [Fact]
    public async Task 改过分类的那一条_回查时去另一个目录也找得到()
    {
        // 分类取自可改的标签（I5）。只按当前分类找的话，用户把它从发货改成退货之后，
        // 云端那一份在回查眼里就「不存在」—— 于是那条录像**永远清不掉**（I8）。
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");

        // 传的时候是「发货」。
        await harness.Service.PublishAsync(location, harness.LocalPathOf(location));
        Assert.Contains("/发货/", Assert.Single(harness.Api.Created));

        // 用户之后把标签改成了「退货」。
        await harness.Labels.SetAsync(
            "e1", LabelKeys.BusinessType, BusinessTypes.ToValue(BusinessType.Return));

        var backend = new CloudArchiveBackend(
            harness.Layout, harness.Api, harness.Session, harness.Service);

        var verify = await backend.VerifyAsync(location);

        Assert.True(verify.Exists, "两个分类目录都要找");
        Assert.False(verify.CouldNotVerify);
    }

    [Fact]
    public async Task 云端两个目录都没有_才算不存在()
    {
        // 「不存在」与「查不了」**必须分开**（I8）：两者都导致不删，
        // 但一个该说「云端那份没了」，另一个该说「现在问不到」。
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");

        // 目录摸得到，只是里面没有这一条。
        harness.Api.MakeDirectory(harness.Layout.DirectoriesFor(location)[0]);

        var backend = new CloudArchiveBackend(
            harness.Layout, harness.Api, harness.Session, harness.Service);
        var verify = await backend.VerifyAsync(location);

        Assert.False(verify.Exists);
        Assert.False(verify.CouldNotVerify);
        Assert.Null(verify.FailureReason);
    }

    [Fact]
    public async Task 问不到网盘时算查不了_不是不存在()
    {
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");

        // 令牌没了 ⇒ 会话拿不到 token ⇒ 这一条问不到。
        await harness.Session.LogoutAsync();

        var backend = new CloudArchiveBackend(
            harness.Layout, harness.Api, harness.Session, harness.Service);
        var verify = await backend.VerifyAsync(location);

        Assert.False(verify.Exists);
        Assert.True(verify.CouldNotVerify, "问不到的时候**不能**说「云端那份不存在」");
        Assert.NotNull(verify.FailureReason);
    }

    // ─────────────────────────────────────────────
    // 补传范围（设计图 `_46` 的「补传范围」）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 补传范围按开录自然日筛()
    {
        using var harness = new Harness(Open() with
        {
            BackfillScope = BackfillScope.FromDate,
            BackfillFrom = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.FromHours(8)),
        });

        await harness.AddRecordingAsync("老的", Now.AddDays(-40));
        await harness.AddRecordingAsync("新的", Now.AddDays(-1));

        await harness.Service.SyncNowAsync();

        var queued = (await harness.Queue.LoadAsync()).Select(i => i.EvidenceId).ToList();

        Assert.Contains("新的", queued);
        Assert.DoesNotContain("老的", queued);
        Assert.Single(harness.Api.Created);
    }

    [Fact]
    public async Task 补传范围是全部时_库里全都收进来()
    {
        using var harness = new Harness(Open()); // 默认 BackfillScope.All
        await harness.AddRecordingAsync("老的", Now.AddDays(-400));
        await harness.AddRecordingAsync("新的", Now);

        await harness.Service.SyncNowAsync();

        Assert.Equal(2, harness.Api.Created.Count);
    }

    // ─────────────────────────────────────────────
    // 失败与重试
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 本机这一份不在了的不入队()
    {
        // 记进去只会让队列里躺着一堆永远传不上去的东西，
        // 把真正失败、真正需要用户看的那些淹掉。
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1", createFile: false);

        await harness.Service.SyncNowAsync();

        Assert.Empty(await harness.Queue.LoadAsync());
        Assert.Empty(harness.Api.Created);

        // 但**照样能回答**「这一条在不在云端」——回查不看本机那份在不在。
        Assert.NotNull(location);
    }

    [Fact]
    public async Task 传失败时记账_而且绝不动本机那一份()
    {
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");

        harness.Api.FailUploads = new HttpRequestException("网断了");

        await harness.Service.SyncNowAsync();

        var item = await harness.Queue.FindAsync("e1");
        Assert.NotNull(item);
        Assert.Equal(CloudUploadState.Failed, item.State);
        Assert.Equal(1, item.Attempts);
        Assert.Contains("网断了", item.LastError);

        var status = await harness.Service.StatusAsync();
        Assert.Equal(1, status.Failed);
        Assert.Contains("网断了", status.LastError);

        // I2：上传失败唯一的后果是「这条还不能被清理」，本机那一份**一个字都没动**。
        Assert.True(File.Exists(harness.LocalPathOf(location)));
    }

    [Fact]
    public async Task 立即重试失败上传_把失败的那些翻回等待并传上去()
    {
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");

        harness.Api.FailUploads = new HttpRequestException("网断了");
        await harness.Service.SyncNowAsync();
        Assert.Equal(CloudUploadState.Failed, (await harness.Queue.FindAsync("e1"))!.State);

        harness.Api.FailUploads = null;
        var uploaded = await harness.Service.RetryFailedAsync();

        Assert.Equal(1, uploaded);

        var item = await harness.Queue.FindAsync("e1");
        Assert.Equal(CloudUploadState.Done, item!.State);
        Assert.Equal(2, item.Attempts);
        Assert.Null(item.LastError);
    }

    [Fact]
    public async Task 没登录时上传失败说的是还没登上_而不是崩掉()
    {
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");
        await harness.Session.LogoutAsync();

        // 手点的「立即对比同步」在没登录时也照样干活（图上那四个按钮是可点的），
        // 但它必须**失败得说得清**，而不是抛出去。
        var uploaded = await harness.Service.SyncNowAsync();

        Assert.Equal(0, uploaded);

        var item = await harness.Queue.FindAsync("e1");
        Assert.Equal(CloudUploadState.Failed, item!.State);
        Assert.Contains("还没登上百度网盘", item.LastError);

        Assert.True(File.Exists(harness.LocalPathOf(location)));
    }

    // ─────────────────────────────────────────────
    // 分片：照预创建说的那几片传（断点续传 / 秒传）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 预创建说要传哪几片就传哪几片()
    {
        // ⚠️ 判据是文档 018 那句原话：「需要上传的分片序号列表，索引从 0 开始」。
        // 反着读（当成「已经有了的」）的后果不是慢一点：全新的文件网盘回的正是
        // [0,1,2]，当成「它都有了」就会**一片都不传**，接着 create 去合并
        // 一份空的分片集，报 31190/31363 —— 错指不回这里。
        using var dir = new TempDir();
        var api = new FakeApi();

        // 3 片，网盘说只缺第 2 片（前两片上一轮已经收下了）。
        api.PendingSlices = new HashSet<int> { 2 };

        var path = dir.Write("大文件.mp4", BaiduPanBlocks.SliceSize * 3 - 1);

        await new BaiduPanUploader(api).UploadAsync("token", "/apps/VidLog/a.mp4", path);

        var slice = Assert.Single(api.UploadedSlices);
        Assert.Equal(2, slice);

        // 合并那一步仍然要做 —— 不做的话网盘上**不会出现文件**（前面那些片白传了）。
        Assert.Single(api.Created);

        // 摘要算的是**每一片各自的 MD5**，最后一片按实际长度算。
        Assert.Equal(3, api.BlockListSizes.Single());
    }

    [Fact]
    public async Task 全新的文件_网盘说要传全部就一片不少地传()
    {
        // 这一条钉的是「反着读」那个 bug 的具体形态：全新的 3 片文件。
        using var dir = new TempDir();
        var api = new FakeApi(); // PendingSlices = null ⇒ 网盘要全部

        var path = dir.Write("大文件.mp4", BaiduPanBlocks.SliceSize * 3 - 1);

        await new BaiduPanUploader(api).UploadAsync("token", "/apps/VidLog/a.mp4", path);

        Assert.Equal(new[] { 0, 1, 2 }, api.UploadedSlices);
        Assert.Single(api.Created);
    }

    [Fact]
    public async Task 上传前要先问上传域名_而且整条文件只问一次()
    {
        // 016 原话：「上传文件数据前必须调用本接口」「不应在客户端固定写死上传服务器地址」。
        // 一个文件问一次（不是一片一次）：未过审的应用每小时只有 10 次调用。
        using var dir = new TempDir();
        var api = new FakeApi();

        var path = dir.Write("大文件.mp4", BaiduPanBlocks.SliceSize * 3 - 1);

        await new BaiduPanUploader(api).UploadAsync("token", "/apps/VidLog/a.mp4", path);

        Assert.Equal(1, api.LocatedUploads);

        // 每一片用的都是问回来的那个域名。
        Assert.All(api.UploadedHosts, host => Assert.Equal("https://c3.pcs.baidu.com", host));
    }

    [Fact]
    public async Task 网盘回显的分片摘要对不上就不合并()
    {
        // ⚠️ 这一片传歪了。create 只看分片齐不齐、不看内容，它会成功 ——
        // 于是网盘上出现一个大小对、播出来是坏的文件，而本机那一份
        // 已经因为「云端有了」被允许清理（I8）。
        using var dir = new TempDir();
        var api = new FakeApi { EchoSliceMd5 = _ => new string('f', 32) };

        var path = dir.Write("大文件.mp4", BaiduPanBlocks.SliceSize + 1);

        var error = await Assert.ThrowsAsync<BaiduPanException>(
            () => new BaiduPanUploader(api).UploadAsync("token", "/apps/VidLog/a.mp4", path));

        Assert.Contains("摘要对不上", error.Message);

        // 而且**没有**去合并。
        Assert.Empty(api.Created);
    }

    // ─────────────────────────────────────────────
    // 父目录不存在时的兜底（文档没写的那件事）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 一路顺利时_一个目录都不会去建()
    {
        // ⚠️ 这一条是那个兜底的**另一半**，也是它敢加进来的理由：
        // 正常情况下（上传自动建目录，或者目录早就在）**一次额外请求都不发**。
        // 未过审的应用每小时只有 10 次调用 —— 一上来就先建 4 层目录，
        // 等于把用户第一次真机验收的额度直接吃掉一半。
        using var dir = new TempDir();
        var api = new FakeApi();

        var path = dir.Write("大文件.mp4", BaiduPanBlocks.SliceSize + 1);

        await new BaiduPanUploader(api)
            .UploadAsync("token", "/apps/VidLog/2026/09/30/发货/a.mp4", path);

        Assert.Empty(api.CreatedDirectories);
        Assert.Single(api.Created);
    }

    [Fact]
    public async Task 网盘说路径不对时_按文档把目录建出来再试一次()
    {
        // ⚠️ 文档**从头到尾没说** precreate 会不会自动建父目录（018 对 autoinit
        // 只有一句「本接口固定为 1」），所以这条路必须走得通：真回了路径类错误，
        // 就按 020 把目录补出来，然后再试一次。
        // 远端落点是 `/apps/<应用名>/2026/09/30/发货/`，而代码里没有任何一处建目录 ——
        // 这条不成立的话，**整条归档路径第一次往一个新日期目录里传就会全失败**。
        using var dir = new TempDir();
        var api = new FakeApi();

        api.PrecreateErrnos.Enqueue(-9); // 「文件或目录不存在」（063）

        var path = dir.Write("大文件.mp4", BaiduPanBlocks.SliceSize + 1);

        await new BaiduPanUploader(api)
            .UploadAsync("token", "/apps/VidLog/2026/09/30/发货/a.mp4", path);

        // 逐层建，从 `/apps/VidLog` **下面**那一层起 —— 不去 create 应用目录本身，
        // 那会被按「无权访问」拒掉（-7），兜底还没开始就先换了个更看不懂的错。
        Assert.Equal(
            new[]
            {
                "/apps/VidLog/2026",
                "/apps/VidLog/2026/09",
                "/apps/VidLog/2026/09/30",
                "/apps/VidLog/2026/09/30/发货",
            },
            api.CreatedDirectories);

        // 而且**真的重试了**：文件传上去了。
        Assert.Single(api.Created);
    }

    [Fact]
    public async Task 同一个目录里的第二条_不会再把目录建一遍()
    {
        // 一个日期目录下面有几十条录像。不去重的话同一条链会被反复建，
        // 而未过审的应用每小时只有 10 次调用。
        using var dir = new TempDir();
        var api = new FakeApi();
        var uploader = new BaiduPanUploader(api);

        api.PrecreateErrnos.Enqueue(-9);
        await uploader.UploadAsync(
            "token", "/apps/VidLog/2026/09/30/发货/a.mp4",
            dir.Write("a.mp4", BaiduPanBlocks.SliceSize + 1));

        var first = api.CreatedDirectories.Count;
        Assert.Equal(4, first);

        api.PrecreateErrnos.Enqueue(-9);
        await uploader.UploadAsync(
            "token", "/apps/VidLog/2026/09/30/发货/b.mp4",
            dir.Write("b.mp4", BaiduPanBlocks.SliceSize + 1));

        Assert.Equal(first, api.CreatedDirectories.Count);
    }

    [Fact]
    public async Task 建了目录还是不行_抛的是原来那个错()
    {
        // ⚠️ 兜底**不许把真正的错盖掉**。建目录成功、重试仍然失败时，
        // 抛出去的必须是网盘原本那个 errno —— 否则「授权被撤了」「路径不在
        // 允许范围内」这类问题会被报成「目录建不出来」，越查越远。
        using var dir = new TempDir();
        var api = new FakeApi();

        api.PrecreateErrnos.Enqueue(-9);
        api.PrecreateErrnos.Enqueue(-9);

        var path = dir.Write("大文件.mp4", BaiduPanBlocks.SliceSize + 1);

        var error = await Assert.ThrowsAsync<BaiduPanException>(
            () => new BaiduPanUploader(api)
                .UploadAsync("token", "/apps/VidLog/2026/09/30/发货/a.mp4", path));

        Assert.Equal(-9, error.Errno);

        // 没有第三次：兜底只重试**一次**，再失败就交给队列去决定。
        Assert.Equal(4, api.CreatedDirectories.Count);
        Assert.Empty(api.Created);
    }

    [Fact]
    public async Task 空文件被拒()
    {
        // 0 字节的「录像」在网页回放里看起来与一条正常的录像一模一样 ——
        // 点开才发现什么都没有。
        using var dir = new TempDir();
        var path = dir.Write("空的.mp4", 0);

        var error = await Assert.ThrowsAsync<BaiduPanException>(
            () => new BaiduPanUploader(new FakeApi()).UploadAsync("token", "/apps/VidLog/a.mp4", path));

        Assert.Contains("空的", error.Message);
    }

    [Fact]
    public async Task 本机那一份不在了就说清楚()
    {
        using var dir = new TempDir();

        var error = await Assert.ThrowsAsync<BaiduPanException>(
            () => new BaiduPanUploader(new FakeApi())
                .UploadAsync("token", "/apps/VidLog/a.mp4", System.IO.Path.Combine(dir.Path, "没有.mp4")));

        Assert.Contains("不在了", error.Message);
    }

    [Fact]
    public void 单次合并的分片上限就是那条边界()
    {
        // ⚠️ 真去造一个 2GB 的文件来撞这个上限不是测试该做的事（在别人机器上写 3GB）。
        // 这里钉的是**判据本身**：`UploadAsync` 用的就是这两个数的比较，
        // 而可能写错的是 `Count`（多一片少一片）。
        Assert.Equal(BaiduPanBlocks.MaxSlicesPerCreate,
            BaiduPanBlocks.Count((long)BaiduPanBlocks.SliceSize * BaiduPanBlocks.MaxSlicesPerCreate));

        Assert.True(
            BaiduPanBlocks.Count((long)BaiduPanBlocks.SliceSize * BaiduPanBlocks.MaxSlicesPerCreate + 1)
            > BaiduPanBlocks.MaxSlicesPerCreate);
    }

    private static CloudUploadSettings Open() => CloudUploadSettings.Default with { AutoUpload = true };

    // ─────────────────────────────────────────────
    // 夹具
    // ─────────────────────────────────────────────

    /// <summary>一整套真的 Core 对象 + 一个假网盘。</summary>
    private sealed class Harness : IDisposable
    {
        public string Root { get; }
        public string ArchiveRoot { get; }
        public FakeApi Api { get; } = new();
        public BaiduPanLayout Layout { get; } = new("VidLog");
        public BaiduPanSession Session { get; }
        public UploadQueue Queue { get; }
        public JsonLinesRecordingIndex Index { get; }
        public JsonLinesLabelStore Labels { get; }
        public StorageLocations Storage { get; }
        public CloudUploadService Service { get; }

        private int _sequence;

        public Harness(CloudUploadSettings settings)
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-cloud-" + Guid.NewGuid().ToString("N"));
            ArchiveRoot = System.IO.Path.Combine(Root, "archive");
            Directory.CreateDirectory(ArchiveRoot);

            Storage = StorageLocations.Single(ArchiveRoot);
            Index = new JsonLinesRecordingIndex(System.IO.Path.Combine(Root, "index.jsonl"));
            Labels = new JsonLinesLabelStore(System.IO.Path.Combine(Root, "labels.jsonl"));

            Queue = new UploadQueue(System.IO.Path.Combine(Root, "upload-queue.jsonl"));

            Session = new BaiduPanSession(
                Api, new BaiduPanTokenStore(System.IO.Path.Combine(Root, "token.json")), now: () => Now);

            // 装成「刚登录过」——这一批测的是上传，不是登录。
            new BaiduPanTokenStore(System.IO.Path.Combine(Root, "token.json"))
                .SaveAsync(new BaiduLogin("token-1", "refresh-1", Now.AddHours(1), "测试账号"))
                .GetAwaiter().GetResult();
            Session.RestoreAsync().GetAwaiter().GetResult();

            Service = new CloudUploadService(
                Session,
                new BaiduPanUploader(Api),
                Api,
                Layout,
                Queue,
                Index,
                Labels,
                Storage,
                settings,
                now: () => Now);
        }

        /// <summary>往索引里放一条，并按需在本机落一个真的文件。</summary>
        /// <returns>它的归档相对路径 —— 回查与远端路径都按它算。</returns>
        public async Task<RelativePath> AddRecordingAsync(
            string id, DateTimeOffset? startedAt = null, bool createFile = true)
        {
            var at = startedAt ?? Now;
            var location = ArchiveLayout.BuildLocation(
                WaybillNumber.Parse("SF1000000001"), "ab12", _sequence++, at);

            if (createFile)
            {
                var full = System.IO.Path.Combine(ArchiveRoot, location.Value);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                await File.WriteAllBytesAsync(full, [1, 2, 3, 4]);
            }

            await Index.AddAsync(new RecordingEntry(
                id,
                "sess-1",
                WaybillNumber.Parse("SF1000000001"),
                at,
                at.AddMinutes(5),
                TimeSpan.FromMinutes(5),
                location,
                ContentHash.Parse(new string('a', 64)),
                "device-1"));

            return location;
        }

        public string LocalPathOf(RelativePath location) =>
            System.IO.Path.Combine(ArchiveRoot, location.Value);

        public void Dispose()
        {
            Service.DisposeAsync().AsTask().GetAwaiter().GetResult();

            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// 假网盘。只做「翻译」，与真实现同一个约定（<see cref="IBaiduPanApi"/> 上写着的那句）。
    /// </summary>
    private sealed class FakeApi : IBaiduPanApi
    {
        /// <summary>哪些目录**存在**。不在里面的会被回 <c>errno -9</c>（目录不存在）。</summary>
        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, HashSet<string>> Files { get; } = new(StringComparer.Ordinal);

        public List<int> UploadedSlices { get; } = [];

        public List<string> Created { get; } = [];

        /// <summary>每一片用的上传域名（验「问回来那个」而不是写死的）。</summary>
        public List<string> UploadedHosts { get; } = [];

        /// <summary>问了上传域名几次。</summary>
        public int LocatedUploads { get; private set; }

        /// <summary>
        /// 预创建时网盘说「**还要传**这几片」。
        /// </summary>
        /// <remarks>
        /// ⚠️ 为 <see langword="null"/> 时按「全都要传」—— 那就是真网盘对
        /// 一个全新文件的回法（018 的示例：3 片时回 <c>[0,1,2]</c>）。
        /// </remarks>
        public IReadOnlySet<int>? PendingSlices { get; set; }

        /// <summary>传分片时网盘回显的 MD5。<see langword="null"/> = 不回显。</summary>
        public Func<int, string?> EchoSliceMd5 { get; set; } = _ => null;

        /// <summary>合并时收到的摘要个数（验「最后一片按实际长度算」）。</summary>
        public List<int> BlockListSizes { get; } = [];

        /// <summary>非空时每一次传分片都抛它。</summary>
        public Exception? FailUploads { get; set; }

        /// <summary>
        /// 预创建按顺序回这几个 <c>errno</c>（非 0 就抛），队列空了就正常回。
        /// </summary>
        /// <remarks>
        /// 用来演「父目录还不存在」：文档**没写**网盘回的是哪个码，所以这里
        /// 拿 063 表里那几个路径类的码来演（<c>-9</c>「文件或目录不存在」最像）。
        /// ⚠️ 真机上到底是哪一个，仍然是没验过的（<c>docs/实现决策.md</c> §87.15）。
        /// </remarks>
        public Queue<int> PrecreateErrnos { get; } = new();

        /// <summary>真去建了哪些目录（按调用顺序，含重复）。</summary>
        public List<string> CreatedDirectories { get; } = [];

        /// <summary>把某个远端路径上的文件摆上去（父目录会一起出现）。</summary>
        public void Put(string remotePath)
        {
            var directory = DirectoryOf(remotePath);
            MakeDirectory(directory);
            Files[directory].Add(BaiduPanLayout.FileNameOf(remotePath));
        }

        public void MakeDirectory(string directory)
        {
            if (Directories.Add(directory))
            {
                Files[directory] = new HashSet<string>(StringComparer.Ordinal);
            }
        }

        public Task<BaiduDeviceCode> StartDeviceLoginAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BaiduDeviceCode(
                "dev-1", "USER01", "https://example.invalid/d", "", 5, 300));

        public Task<BaiduToken?> PollDeviceTokenAsync(
            string deviceCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<BaiduToken?>(null);

        public Task<BaiduToken> RefreshAsync(
            string refreshToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BaiduToken("token-1", refreshToken, 3600));

        public Task<string> GetDisplayNameAsync(
            string accessToken, CancellationToken cancellationToken = default) =>
            Task.FromResult("测试账号");

        public Task<BaiduPrecreate> PrecreateAsync(
            string accessToken,
            string remotePath,
            long size,
            IReadOnlyList<string> blockList,
            CancellationToken cancellationToken = default)
        {
            BlockListSizes.Add(blockList.Count);

            if (PrecreateErrnos.Count > 0)
            {
                throw new BaiduPanException(PrecreateErrnos.Dequeue(), "网盘说这个路径不对。");
            }

            var pending = PendingSlices
                ?? new HashSet<int>(Enumerable.Range(0, blockList.Count));

            return Task.FromResult(new BaiduPrecreate("upload-1", pending));
        }

        public Task CreateDirectoryAsync(
            string accessToken, string directory, CancellationToken cancellationToken = default)
        {
            CreatedDirectories.Add(directory);
            MakeDirectory(directory);

            return Task.CompletedTask;
        }

        public Task<string> LocateUploadAsync(
            string accessToken,
            string remotePath,
            string uploadId,
            CancellationToken cancellationToken = default)
        {
            LocatedUploads++;

            return Task.FromResult("https://c3.pcs.baidu.com");
        }

        public Task CreateAsync(
            string accessToken,
            string remotePath,
            long size,
            IReadOnlyList<string> blockList,
            string uploadId,
            CancellationToken cancellationToken = default)
        {
            Created.Add(remotePath);
            Put(remotePath);

            return Task.CompletedTask;
        }

        public Task<string?> UploadSliceAsync(
            string accessToken,
            string uploadHost,
            string remotePath,
            string uploadId,
            int partSeq,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            if (FailUploads is { } problem)
            {
                throw problem;
            }

            UploadedSlices.Add(partSeq);
            UploadedHosts.Add(uploadHost);

            return Task.FromResult(EchoSliceMd5(partSeq));
        }

        public Task<IReadOnlySet<string>> ListFilesAsync(
            string accessToken, string directory, CancellationToken cancellationToken = default)
        {
            if (!Directories.Contains(directory))
            {
                // 真网盘对不存在的目录就是这么回的。
                throw new BaiduPanException(-9, $"目录不存在：{directory}");
            }

            return Task.FromResult<IReadOnlySet<string>>(Files[directory]);
        }

        private static string DirectoryOf(string remotePath)
        {
            var slash = remotePath.LastIndexOf('/');

            return slash < 0 ? "/" : remotePath[..(slash + 1)];
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vidlog-blocks-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        /// <summary>落一个指定大小的文件（内容全是同一个字节 —— 这一批不看内容）。</summary>
        public string Write(string name, long bytes)
        {
            var full = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(full, new byte[bytes]);

            return full;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

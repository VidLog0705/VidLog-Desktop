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
    // 分片：跳过网盘已有的那些（秒传 / 断点续传）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 网盘说它已经有那几片了_就跳过不传()
    {
        // ⚠️ 这是断点续传的省力点：上一次传到一半断了，这一次从头再传一遍
        // 会让一条 4GB 的录像重传三次就是 12GB 上行，而用户看到的只是「怎么这么慢」。
        using var dir = new TempDir();
        var api = new FakeApi();

        // 3 片：第 0、1 片网盘已经有了。
        api.AlreadyThere = new HashSet<int> { 0, 1 };

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

        /// <summary>预创建时网盘说「这几片我已经有了」。</summary>
        public IReadOnlySet<int> AlreadyThere { get; set; } = new HashSet<int>();

        /// <summary>合并时收到的摘要个数（验「最后一片按实际长度算」）。</summary>
        public List<int> BlockListSizes { get; } = [];

        /// <summary>非空时每一次传分片都抛它。</summary>
        public Exception? FailUploads { get; set; }

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

            return Task.FromResult(new BaiduPrecreate("upload-1", AlreadyThere));
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

        public Task UploadSliceAsync(
            string accessToken,
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

            return Task.CompletedTask;
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

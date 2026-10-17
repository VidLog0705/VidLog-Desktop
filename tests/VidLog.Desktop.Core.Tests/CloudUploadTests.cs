using VidLog.Desktop.Core.Cloud;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
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
        using var harness = new Harness(Closed());
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

        // 远端路径照需求方 2026-10-12 定的形状：/apps/<应用名>/<年-月-日>/<发货|退货>/<单号>.mp4
        var created = Assert.Single(harness.Api.Created);
        Assert.Equal("/apps/VidLog/2026-09-30/发货/SF1000000001.mp4", created);

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

    [Fact]
    public async Task 收尾正在传的时候_定时检查不会把同一条再传一遍()
    {
        // ⚠️ A2（2026-10-16）：同一份录像被**并发**传两遍，不是重试。
        // 根因：收尾这条路（`PublishAsync`）从前直接调 `UploadOneAsync`，
        // **绕开了 `_drainGate`** —— 而那道闸的全部意义就是「同一时刻只有一个
        // 上传者在读这份队列」。绕开之后，下一趟定时检查读队列时那个崩溃自愈
        // 又把「正在传」看成了「等着传」，于是它也来传这一条。
        // 实测 `upload-queue.jsonl`：13:48:48 起传、13:48:53 第 11 个 tick 又挑一次，
        // 两条的 `attempts` 都还是 0。真正的规则是「上传在飞的那段窗口里
        // 落进了一个 tick」—— 慢传只是让它变成必然。
        //
        // 代价：`rtype=3` 覆盖，云端不多文件，是**双倍的流量**。
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");

        // 把收尾那条路**卡在第一片上传上**，好让定时检查正好落进那个窗口。
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var first = 1;

        harness.Api.OnSlice = async () =>
        {
            if (Interlocked.Exchange(ref first, 0) == 1)
            {
                started.TrySetResult();
                await release.Task;
            }
        };

        var publish = harness.Service.PublishAsync(location, harness.LocalPathOf(location));

        await started.Task;

        // ★ 这一趟定时检查必须一条都不挑（闸被收尾那条路拿着）。
        await harness.Service.TickAsync(default);

        release.SetResult();

        var result = await publish;

        Assert.True(result.Published, result.FailureReason);
        Assert.Single(harness.Api.Created);
    }

    [Fact]
    public async Task 总开关关了_补传也跟着停()
    {
        // ⚠️ 需求方 2026-10-16 裁定「走甲」：补传那一支**挂在总开关下面**。
        // 它从前在外面 —— 用户关掉「启用自动上传」之后，只要「自动对比补传」
        // 还开着，每一趟定时检查照样按 `settings.Floor` 把**整个库**收进队列；
        // 而「补传范围=全部」时那个下界是 `DateTimeOffset.MinValue`，
        // 于是连「只上传开启之后新录的」那个承诺也一起作废了。
        // 实测 `vidlog-20261016-140840.log`：14:19:34 收尾那条路正确地回绝了
        // （「启用自动上传」没开），14:20:16 定时检查却把同一条收进了队列并传了上去。
        using var harness = new Harness(CloudUploadSettings.Default with
        {
            AutoUpload = false,
            CompareAndBackfill = true,
            BackfillScope = BackfillScope.All,
        });

        await harness.AddRecordingAsync("e1");

        await harness.Service.TickAsync(default);

        Assert.Empty(await harness.Queue.LoadAsync());
        Assert.Empty(harness.Api.Created);
    }

    [Fact]
    public async Task 总开关开着时_补传照旧把整库收进来()
    {
        // ★ 上一条的**反面**。少了它，上一条在「补传那条路整个坏了」时
        // 也是绿的 —— 而那是「空集永远绿」那一类假绿。
        using var harness = new Harness(CloudUploadSettings.Default with
        {
            AutoUpload = true,
            CompareAndBackfill = true,
            BackfillScope = BackfillScope.All,
        });

        await harness.AddRecordingAsync("e1");

        await harness.Service.TickAsync(default);

        var item = await harness.Queue.FindAsync("e1");
        Assert.NotNull(item);
        Assert.Equal(CloudUploadState.Done, item.State);
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
    // 取回（甲，2026-10-16）：点播放时本机没有 → 从网盘搬回来
    // ─────────────────────────────────────────────

    /// <summary>按真实现的样子建一个网盘归档后端（这一组都只要它）。</summary>
    private static CloudArchiveBackend CloudOf(Harness harness) =>
        new(harness.Layout, harness.Api, harness.Session, harness.Service);

    [Fact]
    public async Task 网盘上有这一条_取回落到本机而且内容一致()
    {
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");

        // 正常传上去，然后假装本机那一份没了（用户清过盘）。
        await harness.Service.PublishAsync(location, harness.LocalPathOf(location));
        var local = harness.LocalPathOf(location);
        File.Delete(local);

        var progress = new List<(long Received, long? Total)>();
        var result = await CloudOf(harness).FetchAsync(
            location, local, (received, total) => progress.Add((received, total)));

        Assert.True(result.Fetched, result.FailureReason);
        Assert.Equal(harness.Api.DownloadContent, await File.ReadAllBytesAsync(local));

        // ⚠️ **两处都不留痕**才算搬干净：`.part` 留着的话它是一份看着像成品的坏文件。
        Assert.False(File.Exists(local + ".part"));

        // 进度要一路报到尾 —— 界面那根条靠它（总长度来自 Content-Length）。
        Assert.Equal((long)harness.Api.DownloadContent.Length, progress[^1].Received);
        Assert.Equal((long)harness.Api.DownloadContent.Length, progress[^1].Total);
    }

    [Fact]
    public async Task 网盘上没有这一条_说的是没找到_不是取不回来()
    {
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");

        var local = System.IO.Path.Combine(harness.Root, "本机", "SF1000000001.mp4");
        var result = await CloudOf(harness).FetchAsync(location, local);

        Assert.False(result.Fetched);
        Assert.True(result.Missing, "网盘上没有这一条");
        Assert.Null(result.FailureReason);
        Assert.False(File.Exists(local));
    }

    [Fact]
    public async Task 改过分类的那一条_取回时去另一个目录也找得到()
    {
        // 与回查同一条道理（I5）：分类取自可改的标签。只按当前分类找的话，
        // 用户把它从发货改成退货之后，云端那一份在取回眼里就「不存在」。
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");

        // 传的时候是「发货」。
        await harness.Service.PublishAsync(location, harness.LocalPathOf(location));

        await harness.Labels.SetAsync(
            "e1", LabelKeys.BusinessType, BusinessTypes.ToValue(BusinessType.Return));

        var local = harness.LocalPathOf(location);
        File.Delete(local);

        var result = await CloudOf(harness).FetchAsync(location, local);

        Assert.True(result.Fetched, result.FailureReason);
    }

    [Fact]
    public async Task 问不到网盘时_取回算失败_不是没找到()
    {
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");

        await harness.Session.LogoutAsync();

        var local = System.IO.Path.Combine(harness.Root, "本机", "SF1000000001.mp4");
        var result = await CloudOf(harness).FetchAsync(location, local);

        Assert.False(result.Fetched);
        Assert.False(result.Missing, "问不到的时候**不能**说「网盘上没有这一条」");
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task 下到一半断了_说失败_而且半截文件不许留成本品()
    {
        // ⚠️ 半截的 `<单号>.mp4` 落在盘上，检索页会判「本机有」—— 于是那句警告消失，
        // 而播放器打开的是一个坏文件。清理不干净就是这条测试要挡的事。
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");
        await harness.Service.PublishAsync(location, harness.LocalPathOf(location));

        var local = harness.LocalPathOf(location);
        File.Delete(local);

        harness.Api.FailDownload = new IOException("网络断了");

        var result = await CloudOf(harness).FetchAsync(location, local);

        Assert.False(result.Fetched);
        Assert.False(result.Missing);
        Assert.NotNull(result.FailureReason);
        Assert.False(File.Exists(local), "半截的东西**不许**冒充成品");
        Assert.False(File.Exists(local + ".part"));
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
    // 自动重试（2026-10-12，建议 2：失败上传要有出口，现场没人盯着）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 传失败后_记下下次自己再试的时刻()
    {
        using var harness = new Harness(Open());
        await harness.AddRecordingAsync("e1");

        harness.Api.FailUploads = new HttpRequestException("网断了");
        await harness.Service.SyncNowAsync();

        var item = await harness.Queue.FindAsync("e1");

        Assert.Equal(1, item!.Attempts);
        // 第 1 次失败 ⇒ 60 秒后再试（`Backoff`：60/120/240/480/960/1800…封顶 30 分钟）。
        Assert.Equal(Now.AddSeconds(60), item.NextAttemptAt);
        Assert.True(CloudUploadStates.WillRetry(item.State, item.NextAttemptAt));
    }

    [Fact]
    public async Task 还没到点的不重传_到点了自己传上去()
    {
        // ⚠️ 这条是「自动重试」的**承重判据**：不看到点时刻的话，
        // 退避就退化成「每一拍都重试」—— 而那正好会把这 10 次/小时的额度烧光。
        using var harness = new Harness(Open());
        await harness.AddRecordingAsync("e1");

        harness.Api.FailUploads = new HttpRequestException("网断了");
        await harness.Service.SyncNowAsync();

        harness.Api.FailUploads = null;

        harness.Clock = Now.AddSeconds(59);
        Assert.Equal(0, await harness.Service.SyncNowAsync());
        Assert.Equal(CloudUploadState.Failed, (await harness.Queue.FindAsync("e1"))!.State);

        // 正好到点（`due <= now`，两端都算数）。
        harness.Clock = Now.AddSeconds(60);
        Assert.Equal(1, await harness.Service.SyncNowAsync());

        var item = await harness.Queue.FindAsync("e1");
        Assert.Equal(CloudUploadState.Done, item!.State);
        Assert.Equal(2, item.Attempts);
        Assert.Null(item.NextAttemptAt);
    }

    [Fact]
    public async Task 次数用完就不再自动重试了()
    {
        // ⚠️ 上限**必须有**：过审之前每小时只有 10 次调用，而无上限的重试会
        // 把这 10 次全喂给同一个失败，别的录像一次机会都没有。
        using var harness = new Harness(Open());
        await harness.AddRecordingAsync("e1");

        harness.Api.FailUploads = new HttpRequestException("网断了");

        for (var i = 0; i < CloudUploadService.MaxAttempts; i++)
        {
            await harness.Service.SyncNowAsync();

            // 推过这一轮的退避时刻（最大 30 分钟，推 1 小时一定过）。
            harness.Clock = harness.Clock.AddHours(1);
        }

        var item = await harness.Queue.FindAsync("e1");
        Assert.Equal(CloudUploadService.MaxAttempts, item!.Attempts);
        Assert.Null(item.NextAttemptAt);

        // 再推着时钟跑一轮也不会动它 —— 落终态了。
        var attemptsBefore = item.Attempts;
        await harness.Service.SyncNowAsync();

        Assert.Equal(attemptsBefore, (await harness.Queue.FindAsync("e1"))!.Attempts);
    }

    [Fact]
    public async Task 重试没有用的那种错_一次就落终态()
    {
        // 「应用还在审核中」（20011）重发一万次都是同一个结果，
        // 而每发一次都要花掉那 10 次额度里的一次。
        using var harness = new Harness(Open());
        await harness.AddRecordingAsync("e1");

        harness.Api.PrecreateErrnos.Enqueue(20011);
        await harness.Service.SyncNowAsync();

        var item = await harness.Queue.FindAsync("e1");
        Assert.Equal(CloudUploadState.Failed, item!.State);
        Assert.Null(item.NextAttemptAt);

        // 但**次数照记**：界面上要能说出「试过 1 次」。
        Assert.Equal(1, item.Attempts);
    }

    [Fact]
    public void 终态判据_限流不算终态()
    {
        // ⚠️ `20012` 文档原话是「应用过审之前只能用于测试开发，**也可能就是调得太密**」——
        // 当终态会把一条本来传得上去的录像**永久判死**。
        Assert.False(new BaiduPanException(20012, "调用量达上限").IsHopeless);
        Assert.False(new BaiduPanException(31034, "请求太频繁").IsHopeless);

        // 这三个是要去开放平台后台才有解的。
        Assert.True(new BaiduPanException(20011, "还在审核中").IsHopeless);
        Assert.True(new BaiduPanException(20013, "没有接口权限").IsHopeless);
        Assert.True(new BaiduPanException(20020, "路径不在允许范围").IsHopeless);

        // 凭据那四个（`IsCredentialProblem`）也是终态 —— 那条路另有处置。
        Assert.True(new BaiduPanException(-6, "身份验证失败").IsHopeless);
        Assert.True(new BaiduPanException(20017, "已被用户解绑").IsHopeless);
    }

    // ─────────────────────────────────────────────
    // 放弃 / 恢复（2026-10-12，建议 6：队列行要有出口）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 放弃之后_补传和重试都不再碰它()
    {
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");

        harness.Api.FailUploads = new HttpRequestException("网断了");
        await harness.Service.SyncNowAsync();
        harness.Api.FailUploads = null;

        Assert.True(await harness.Service.AbandonAsync("e1"));

        // ①【立即重试失败上传】只认「失败」—— 用户明确说不传的那条不该被它翻出来。
        Assert.Equal(0, await harness.Service.RetryFailedAsync());

        // ② 收尾那条路也要认：同一个单号又收尾一次时会走到这儿。
        var again = await harness.Service.PublishAsync(location, harness.LocalPathOf(location));
        Assert.False(again.Published);
        Assert.Contains("放弃", again.FailureReason);

        Assert.Empty(harness.Api.Created);

        var item = await harness.Queue.FindAsync("e1");
        Assert.Equal(CloudUploadState.Abandoned, item!.State);
        Assert.Equal(1, item.Attempts); // 放弃不等于再试一次
    }

    [Fact]
    public async Task 恢复之后能传上去()
    {
        using var harness = new Harness(Open());
        await harness.AddRecordingAsync("e1");

        harness.Api.FailUploads = new HttpRequestException("网断了");
        await harness.Service.SyncNowAsync();
        harness.Api.FailUploads = null;

        await harness.Service.AbandonAsync("e1");
        Assert.True(await harness.Service.ResumeAsync("e1"));

        // 恢复 = 重新排队。⚠️ 不该还等着那个退避时刻（它是上一次失败留下的）。
        var queued = await harness.Queue.FindAsync("e1");
        Assert.Equal(CloudUploadState.Pending, queued!.State);
        Assert.Null(queued.NextAttemptAt);

        Assert.Equal(1, await harness.Service.SyncNowAsync());
        Assert.Equal(CloudUploadState.Done, (await harness.Queue.FindAsync("e1"))!.State);
    }

    [Fact]
    public async Task 已经传完的不许再放弃()
    {
        // 界面上那颗按钮是按同一把尺子显隐的（`CloudUploadStates`），
        // 所以这一条同时也是「按钮亮着但按下去说了不算」那个病的闸。
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");

        await harness.Service.PublishAsync(location, harness.LocalPathOf(location));

        Assert.False(await harness.Service.AbandonAsync("e1"));
        Assert.Equal(CloudUploadState.Done, (await harness.Queue.FindAsync("e1"))!.State);
    }

    [Fact]
    public async Task 队列里没有的那一条_放弃与恢复都说不算()
    {
        using var harness = new Harness(Open());

        Assert.False(await harness.Service.AbandonAsync("没有这一条"));
        Assert.False(await harness.Service.ResumeAsync("没有这一条"));
    }

    [Fact]
    public void 三种状态能做什么()
    {
        Assert.True(CloudUploadStates.CanAbandon(CloudUploadState.Pending));
        Assert.True(CloudUploadStates.CanAbandon(CloudUploadState.Failed));
        Assert.False(CloudUploadStates.CanAbandon(CloudUploadState.Done));
        Assert.False(CloudUploadStates.CanAbandon(CloudUploadState.Abandoned));

        Assert.True(CloudUploadStates.CanResume(CloudUploadState.Abandoned));
        Assert.False(CloudUploadStates.CanResume(CloudUploadState.Failed));

        // ⚠️ 【重试】只认「失败」：已放弃的是用户自己说「这条不传了」，
        // 让它再传一次的入口是【恢复】，不是【重试】。
        Assert.True(CloudUploadStates.CanRetry(CloudUploadState.Failed));
        Assert.False(CloudUploadStates.CanRetry(CloudUploadState.Abandoned));
        Assert.False(CloudUploadStates.CanRetry(CloudUploadState.Done));
        Assert.False(CloudUploadStates.CanRetry(CloudUploadState.Pending));
        Assert.False(CloudUploadStates.CanRetry(CloudUploadState.Uploading));

        Assert.True(CloudUploadStates.WillRetry(CloudUploadState.Failed, Now));
        Assert.False(CloudUploadStates.WillRetry(CloudUploadState.Failed, null));
        Assert.False(CloudUploadStates.WillRetry(CloudUploadState.Pending, Now));
    }

    [Fact]
    public async Task 队列计数把已放弃的算进总数()
    {
        // `Total` 少加一档的话，界面上「队列 N 条」与底下那几档加起来对不上。
        using var harness = new Harness(Open());
        await harness.AddRecordingAsync("e1");

        harness.Api.FailUploads = new HttpRequestException("网断了");
        await harness.Service.SyncNowAsync();
        harness.Api.FailUploads = null;

        await harness.Service.AbandonAsync("e1");

        var status = await harness.Service.StatusAsync();
        Assert.Equal(1, status.Abandoned);
        Assert.Equal(0, status.Failed);
        Assert.Equal(1, status.Total);
    }

    // ─────────────────────────────────────────────
    // 队列每一行那颗【重试】（2026-10-14，可抄清单第 20 条）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 单条重试_只翻这一条_别的失败的原样不动()
    {
        // ⚠️ 这一条就是那颗按钮**存在的理由**：上面那颗「立即重试失败上传」是全局的，
        // 一次把失败的全翻出来 —— 队列里失败十几条、只想救其中一条时它会把
        // 那 10 次/小时的额度一次性喂掉。判据是「另一条还在失败」。
        using var harness = new Harness(Open());
        await harness.AddRecordingAsync("e1");
        await harness.AddRecordingAsync("e2");

        harness.Api.FailUploads = new HttpRequestException("网断了");
        await harness.Service.SyncNowAsync();
        harness.Api.FailUploads = null;

        Assert.Equal(CloudUploadState.Failed, (await harness.Queue.FindAsync("e2"))!.State);

        Assert.True(await harness.Service.RetryAsync("e1"));

        Assert.Equal(CloudUploadState.Done, (await harness.Queue.FindAsync("e1"))!.State);
        Assert.Equal(CloudUploadState.Failed, (await harness.Queue.FindAsync("e2"))!.State);
    }

    [Fact]
    public async Task 单条重试_已放弃的那一条不认()
    {
        // ⚠️ 判据与界面那颗按钮的显隐共用 `CloudUploadStates.CanRetry`。
        // 两边各写一份的话，就会出现「按钮亮着、按下去说了不算」——
        // 而这里「说了不算」还额外冒犯：用户明确说过「这条不传了」。
        using var harness = new Harness(Open());
        await harness.AddRecordingAsync("e1");

        harness.Api.FailUploads = new HttpRequestException("网断了");
        await harness.Service.SyncNowAsync();
        harness.Api.FailUploads = null;

        Assert.True(await harness.Service.AbandonAsync("e1"));

        Assert.False(await harness.Service.RetryAsync("e1"));
        Assert.Equal(CloudUploadState.Abandoned, (await harness.Queue.FindAsync("e1"))!.State);
    }

    [Fact]
    public async Task 单条重试_已经传完的不认()
    {
        using var harness = new Harness(Open());
        var location = await harness.AddRecordingAsync("e1");

        await harness.Service.PublishAsync(location, harness.LocalPathOf(location));
        Assert.Equal(CloudUploadState.Done, (await harness.Queue.FindAsync("e1"))!.State);

        Assert.False(await harness.Service.RetryAsync("e1"));
        Assert.Equal(CloudUploadState.Done, (await harness.Queue.FindAsync("e1"))!.State);
    }

    [Fact]
    public async Task 单条重试_队列里没有的说不算()
    {
        using var harness = new Harness(Open());

        Assert.False(await harness.Service.RetryAsync("没有这一条"));
    }

    [Fact]
    public async Task 单条重试_把退避时刻与上次的错一起清掉()
    {
        // ⚠️ 不清的话，界面那一行仍然写着「X 小时后自己再试」，
        // 而它其实马上就被这一轮传掉了 —— 那句话就成了假话。
        using var harness = new Harness(Open());
        await harness.AddRecordingAsync("e1");

        harness.Api.FailUploads = new HttpRequestException("网断了");
        await harness.Service.SyncNowAsync();
        harness.Api.FailUploads = null;

        await harness.Service.RetryAsync("e1");

        var item = await harness.Queue.FindAsync("e1");
        Assert.Null(item!.LastError);
        Assert.Null(item.NextAttemptAt);
    }

    // ─────────────────────────────────────────────
    // 手点那一轮的进度 / 每一行自己的进度（第 14、15 条）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 手点的那一轮_跑的时候报得出进度_跑完就收掉()
    {
        // ⚠️ 「跑完就收掉」是承重的：不清 `_syncTotal` 的话，下一轮还没开始，
        // 界面就会拿着一根停在 100% 的条当「正在跑」。
        using var harness = new Harness(Open());
        await harness.AddRecordingAsync("e1");
        await harness.AddRecordingAsync("e2");

        CloudSyncProgress? mid = null;
        harness.Api.OnSlice = () =>
        {
            mid ??= harness.Service.SyncProgress;

            return Task.CompletedTask;
        };

        await harness.Service.SyncNowAsync();

        Assert.NotNull(mid);
        Assert.Equal(2, mid!.Total);
        Assert.InRange(mid.Fraction, 0, 1);

        Assert.Null(harness.Service.SyncProgress);
    }

    [Fact]
    public async Task 每一行自己的进度_在传的时候读得到_传完就摘掉()
    {
        // ⚠️ 传完摘掉是承重的：不摘的话，下一条**用同一个 EvidenceId** 的
        // （重传同一场）会先看到上一轮停下的那个数，而那根条应该从 0 起。
        using var harness = new Harness(Open());

        await harness.AddRecordingAsync("e1");

        // 两片：传完第 1 片之后进度是 1/2 —— 用一个 4 字节的文件是读不到中间的数的。
        var second = await harness.AddRecordingAsync("e2");
        await File.WriteAllBytesAsync(
            harness.LocalPathOf(second), new byte[BaiduPanBlocks.SliceSize + 10]);

        Assert.Null(harness.Service.ProgressOf("e2"));

        var samples = new List<double?>();
        harness.Api.OnSlice = () =>
        {
            samples.Add(harness.Service.ProgressOf("e2"));

            return Task.CompletedTask;
        };

        await harness.Service.SyncNowAsync();

        Assert.Contains(0.5, samples);

        Assert.Null(harness.Service.ProgressOf("e2"));
    }

    // ─────────────────────────────────────────────
    // 网盘容量（2026-10-12，建议 5：显示还剩多少）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 容量问到了就回读数()
    {
        using var harness = new Harness(Open());

        harness.Api.Quota = new BaiduPanQuota(UsedBytes: 36_776_166_068, TotalBytes: 107_374_182_400);

        var quota = await harness.Service.QuotaAsync();

        Assert.NotNull(quota);
        Assert.Equal(107_374_182_400 - 36_776_166_068, quota.FreeBytes);
        Assert.Equal(1, harness.Api.QuotaCalls);
    }

    [Fact]
    public async Task 容量问不到时回空_而且不抛()
    {
        // 容量是**锦上添花**：拿不到不该让设置页那一块变成一句错误。
        using var harness = new Harness(Open());
        harness.Api.Quota = null;

        Assert.Null(await harness.Service.QuotaAsync());
    }

    [Fact]
    public async Task 没登录时不问容量()
    {
        // 没登录时问必定失败，还白留一条日志 —— 而且会占掉那 10 次额度里的一次。
        using var harness = new Harness(Open());
        await harness.Session.LogoutAsync();

        Assert.Null(await harness.Service.QuotaAsync());
        Assert.Equal(0, harness.Api.QuotaCalls);
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

    [Fact]
    public void 定时检查起来与停下_各记一条日志()
    {
        // ⚠️ §6.1「起定时器 ⇒ 起、停、失败各一条」。
        // 这个循环平时一声不响（没登录、队列空的时候一个字都不记），少了这一对，
        // 日志里「正常关机」与「定时器压根没起来 / 挂了」长得**一模一样**。
        var logger = new CapturingLogger();
        var harness = new Harness(Open(), logger);

        try
        {
            harness.Service.Start();
            harness.Service.Start(); // 重复调用是安全的 ⇒ 也不该多记一条

            Assert.Single(logger.Messages, message => message.Contains("定时检查上传队列已起"));
        }
        finally
        {
            // ⚠️ 关这一次就是「停」那条的判据：`DisposeAsync` 会 `await _loop`，
            // 所以它返回时那条一定已经落下了（不像档案那边只发信号不等）。
            harness.Dispose();
        }

        Assert.Single(logger.Messages, message => message.Contains("定时检查上传队列已停"));
    }

    private static CloudUploadSettings Open() => CloudUploadSettings.Default with { AutoUpload = true };

    /// <summary>开关关掉那一组（出厂默认现在是开的，所以这里要显式关）。</summary>
    private static CloudUploadSettings Closed() => CloudUploadSettings.Default with { AutoUpload = false };

    // ─────────────────────────────────────────────
    // 夹具
    // ─────────────────────────────────────────────

    /// <summary>把日志收下来。只记文本 —— 这些用例判的是「有没有这一条」。</summary>
    private sealed class CapturingLogger : IAppLogger
    {
        public List<string> Messages { get; } = [];

        public void Log(LogLevel level, string category, string message) => Messages.Add(message);

        public void Log(
            LogLevel level, string category, string message, IReadOnlyDictionary<string, object?> data) =>
            Messages.Add(message);
    }

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

        /// <summary>
        /// 服务眼里的「现在」。
        /// </summary>
        /// <remarks>
        /// ⚠️ <b>要能推着走</b>：自动重试是「到点了才试」，而真等的退避是
        /// 60 秒起、后面每次翻倍 —— 用例里真等一等就是十几分钟。
        /// 只有**服务**用这个钟；会话那边仍用固定的 <see cref="Now"/>（它只关心令牌过期）。
        /// </remarks>
        public DateTimeOffset Clock { get; set; } = Now;

        public Harness(CloudUploadSettings settings, IAppLogger? logger = null)
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
                logger: logger,
                now: () => Clock);
        }

        /// <summary>往索引里放一条，并按需在本机落一个真的文件。</summary>
        /// <returns>它的归档相对路径 —— 回查与远端路径都按它算。</returns>
        public async Task<RelativePath> AddRecordingAsync(
            string id, DateTimeOffset? startedAt = null, bool createFile = true)
        {
            var at = startedAt ?? Now;

            // ⚠️ 一条录像一个落点：新形状（2026-10-12）里文件名**没有会话号与序号**，
            // 同一个单号 + 同一个时刻会拼出同一个名字。这里靠 `_sequence` 加 `_2`/`_3`
            // 把它们分开 —— 与生产代码 `SessionFinalizer` 定名那一段是同一个手法。
            var location = ArchiveLayout.Deduplicated(
                ArchiveLayout.BuildLocation(WaybillNumber.Parse("SF1000000001"), BusinessType.Outbound, at),
                _sequence++);

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
        /// 每一片**传之前**跑一下。
        /// </summary>
        /// <remarks>
        /// ⚠️ 它存在的理由只有一个：进度那两根条（<c>SyncProgress</c> /
        /// <c>ProgressOf</c>）**只在传的过程中有值**，跑完就收掉了 ——
        /// 不在这儿凿一个口子，用例只能看到「跑之前 null、跑之后 null」。
        /// </remarks>
        public Func<Task>? OnSlice { get; set; }

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

        /// <summary>
        /// 保护上面那几个可变集合的一把锁。
        /// </summary>
        /// <remarks>
        /// ⚠️ <b>必需，不是保险</b>：生产里 <c>ParallelUploads</c> 默认就是 **2**
        /// （见 <c>CloudUploadSettings</c>），所以两条录像会**同时**走到
        /// <see cref="CreateAsync"/> / <see cref="UploadSliceAsync"/>，而底下那几个是
        /// 普通的 <c>HashSet</c> / <c>List</c>。不加锁的表现是**偶发**的一条
        /// <c>InvalidOperationException</c>「...a concurrent update was performed...
        /// The collection's state is no longer correct」——
        /// 只在机器忙的时候冒出来（2026-10-14 实测：同一段连跑 5 次红了 2 次，
        /// 单跑必绿，所以光看「本机全绿」是发现不了的）。
        /// </remarks>
        private readonly object _gate = new();

        /// <summary>把某个远端路径上的文件摆上去（父目录会一起出现）。</summary>
        public void Put(string remotePath) => Locked(() =>
        {
            var directory = DirectoryOf(remotePath);
            MakeDirectory(directory);
            Files[directory].Add(BaiduPanLayout.FileNameOf(remotePath));
        });

        public void MakeDirectory(string directory) => Locked(() =>
        {
            if (Directories.Add(directory))
            {
                Files[directory] = new HashSet<string>(StringComparer.Ordinal);
            }
        });

        /// <summary>跑一小段碰共享状态的活。</summary>
        private void Locked(Action work)
        {
            lock (_gate)
            {
                work();
            }
        }

        public Task<BaiduToken> ExchangeCodeAsync(
            string code, string redirectUri, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BaiduToken("token-1", "r1", 3600));

        public Task<BaiduToken> RefreshAsync(
            string refreshToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BaiduToken("token-1", refreshToken, 3600));

        public Task<string> GetDisplayNameAsync(
            string accessToken, CancellationToken cancellationToken = default) =>
            Task.FromResult("测试账号");

        /// <summary>网盘容量问回来什么；<see langword="null"/> = 问不到。</summary>
        public BaiduPanQuota? Quota { get; set; }

        /// <summary>问了几次容量（验「限频」是靠调用方做的）。</summary>
        public int QuotaCalls { get; private set; }

        public Task<BaiduPanQuota?> GetQuotaAsync(
            string accessToken, CancellationToken cancellationToken = default)
        {
            Locked(() => QuotaCalls++);

            return Task.FromResult(Quota);
        }

        public Task<BaiduPrecreate> PrecreateAsync(
            string accessToken,
            string remotePath,
            long size,
            IReadOnlyList<string> blockList,
            CancellationToken cancellationToken = default)
        {
            BaiduPrecreate precreate = null!;

            Locked(() =>
            {
                BlockListSizes.Add(blockList.Count);

                if (PrecreateErrnos.Count > 0)
                {
                    throw new BaiduPanException(PrecreateErrnos.Dequeue(), "网盘说这个路径不对。");
                }

                var pending = PendingSlices
                    ?? new HashSet<int>(Enumerable.Range(0, blockList.Count));

                precreate = new BaiduPrecreate("upload-1", pending);
            });

            return Task.FromResult(precreate);
        }

        public Task CreateDirectoryAsync(
            string accessToken, string directory, CancellationToken cancellationToken = default)
        {
            Locked(() =>
            {
                CreatedDirectories.Add(directory);
                MakeDirectory(directory);
            });

            return Task.CompletedTask;
        }

        public Task<string> LocateUploadAsync(
            string accessToken,
            string remotePath,
            string uploadId,
            CancellationToken cancellationToken = default)
        {
            Locked(() => LocatedUploads++);

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
            Locked(() =>
            {
                Created.Add(remotePath);
                Put(remotePath);
            });

            return Task.CompletedTask;
        }

        public async Task<string?> UploadSliceAsync(
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

            if (OnSlice is { } hook)
            {
                await hook();
            }

            Locked(() =>
            {
                UploadedSlices.Add(partSeq);
                UploadedHosts.Add(uploadHost);
            });

            return EchoSliceMd5(partSeq);
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

        /// <summary>取直链时下回来的内容（验「落盘的是这一份」）。</summary>
        public byte[] DownloadContent { get; set; } = new byte[4096];

        /// <summary>取过直链的远端路径（按调用顺序）。</summary>
        public List<string> Dlinks { get; } = [];

        /// <summary>非空时下载下到一半就抛它（演「断了」）。</summary>
        public Exception? FailDownload { get; set; }

        public Task<string> GetDlinkAsync(
            string accessToken, string remotePath, CancellationToken cancellationToken = default)
        {
            string leaf;

            lock (_gate)
            {
                var directory = DirectoryOf(remotePath);

                leaf = remotePath[directory.Length..];

                if (!Directories.Contains(directory) || !Files[directory].Contains(leaf))
                {
                    // 真网盘对不存在的文件回的就是这个码（063 的 31066）。
                    throw new BaiduPanException(31066, $"文件不存在：{remotePath}");
                }

                Dlinks.Add(remotePath);
            }

            return Task.FromResult($"https://d.pcs.baidu.com/fake/{leaf}");
        }

        public async Task DownloadAsync(
            string accessToken,
            string dlink,
            Stream destination,
            Action<long, long?>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var content = DownloadContent;

            // ⚠️ 断了的时候**先写半截再抛**：真断线就是这样 —— 目标那头已经落了
            // 一部分字节。一点不写的话，「半截文件不许留」那条测试就测不到东西。
            if (FailDownload is { } problem)
            {
                await destination.WriteAsync(content[..(content.Length / 2)], cancellationToken);
                progress?.Invoke(content.Length / 2, content.Length);

                throw problem;
            }

            await destination.WriteAsync(content, cancellationToken);
            progress?.Invoke(content.Length, content.Length);
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

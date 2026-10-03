using System.Security.Cryptography;
using VidLog.Desktop.Core.License;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 试用码与试用记录（`docs/04-许可设计.md` §6）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>这一组的存放位置全是假的（内存里 / 临时文件）</b> —— §6.3 那四处里有两处
/// （<c>%ProgramData%</c> 与 HKLM）在真实机器上**是会真写进去的**。
/// 拿真的来测，等于每跑一次测试就在开发机上留一份试用记录。
/// </para>
/// <para>
/// ⚠️ 这里钉的核心是一条**客服会撞上**的行为：
/// <b>同一台机器再签一个试用码，既不重置也不延长</b>（只接着用剩下的窗口）。
/// </para>
/// </remarks>
public class TrialTests
{
    /// <summary>2026-10-03 09:00 +08:00 —— 所有用例的共同起点。</summary>
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 9, 0, 0, TimeSpan.FromHours(8));

    private static MachineIdentity Machine() => new("MB-SERIAL-1234", "BFEBFBFF000806EC", "1.2.3");

    // ─────────────────────────────────────────────
    // 假的位置与假的机器
    // ─────────────────────────────────────────────

    /// <summary>一个活在内存里的存放位置。</summary>
    private sealed class FakeLocation(string name) : ITrialLocation
    {
        public string Name { get; } = name;

        public TrialRecord? Record { get; set; }

        /// <summary>写进去就抛（模拟非管理员机器上的 <c>%ProgramData%</c> 与 HKLM）。</summary>
        public bool WriteFails { get; set; }

        /// <summary>读也抛（那个位置压根访问不了）。</summary>
        public bool ReadFails { get; set; }

        public TrialRecord? Read() => ReadFails
            ? throw new UnauthorizedAccessException("读不动")
            : Record;

        public void Write(TrialRecord record)
        {
            if (WriteFails)
            {
                throw new UnauthorizedAccessException("写不进");
            }

            Record = record;
        }
    }

    private static List<FakeLocation> Four() =>
        [new("①"), new("②"), new("③"), new("④")];

    private static TrialRecordStore Store(IReadOnlyList<FakeLocation> locations) => new(locations);

    // ─────────────────────────────────────────────
    // 记录本身（§6.3 / §6.4）
    // ─────────────────────────────────────────────

    [Fact]
    public void 四处全空_算首次_而且四处都写上了起算点()
    {
        var locations = Four();
        var state = Store(locations).Evaluate(T0);

        Assert.Equal(TrialPhase.Running, state.Phase);
        Assert.Equal(TrialRecordStore.Window, state.Remaining);
        Assert.Equal(T0, state.FirstRunAt);

        // ⚠️ 四处都要写 —— 只写一两处的话，「删掉一个文件就重置」这条防线直接没了。
        Assert.All(locations, l => Assert.Equal(T0, l.Record!.FirstRunAt));
    }

    [Fact]
    public void 删掉三处只留一处_起算点不许重置()
    {
        var locations = Four();
        var store = Store(locations);

        store.Evaluate(T0);

        // 用户删掉三处（会删文件的人做得到），只留一处。
        for (var i = 1; i < locations.Count; i++)
        {
            locations[i].Record = null;
        }

        var state = store.Evaluate(T0.AddHours(10));

        // 剩下的那一处仍是最早的 ⇒ 已经用掉 10 小时，不是重新开始。
        Assert.Equal(T0, state.FirstRunAt);
        Assert.Equal(TrialRecordStore.Window - TimeSpan.FromHours(10), state.Remaining);
    }

    [Fact]
    public void 把系统时间往回拨_试用不会延长()
    {
        var locations = Four();
        locations[0].Record = new TrialRecord(T0, T0.AddHours(100));

        // 用户把时间调回 500 小时前（约 20 天）。
        var state = Store(locations).Evaluate(T0.AddHours(-500));

        // maxSeenAt 只增不减 ⇒ 用掉的仍是 100 小时，剩 68。
        Assert.Equal(TimeSpan.FromHours(68), state.Remaining);
        Assert.Equal(T0, state.FirstRunAt);
    }

    [Fact]
    public void 用满_168_小时就算结束_一分钟都不多给()
    {
        var locations = Four();

        // 差一点点 —— 还在试用里。
        locations[0].Record = new TrialRecord(T0, T0.AddHours(168) - TimeSpan.FromMinutes(1));
        Assert.Equal(
            TrialPhase.Running,
            Store(locations).Evaluate(T0.AddHours(167)).Phase);

        // 正好到点 —— 结束（判据是 >=，不是 >）。
        locations[0].Record = new TrialRecord(T0, T0.AddHours(168));
        var ended = Store(locations).Evaluate(T0.AddHours(168));

        Assert.Equal(TrialPhase.Ended, ended.Phase);
        Assert.Equal(TimeSpan.Zero, ended.Remaining);
    }

    [Fact]
    public void 写不进去的位置不阻止试用_而且一条都不抛()
    {
        var locations = Four();

        // ②④ 就是非管理员机器上的样子：读写都进不去。
        locations[1].WriteFails = locations[1].ReadFails = true;
        locations[3].WriteFails = locations[3].ReadFails = true;

        var state = Store(locations).Evaluate(T0);   // ← 不许抛

        Assert.Equal(TrialPhase.Running, state.Phase);

        // 进得去的那两处照写照存。
        Assert.Equal(T0, locations[0].Record!.FirstRunAt);
        Assert.Equal(T0, locations[2].Record!.FirstRunAt);
    }

    [Fact]
    public void 四处一个都读不到时_用激活那一刻兜底_而不是当成首次()
    {
        var locations = Four();

        foreach (var location in locations)
        {
            location.ReadFails = true;
        }

        var store = Store(locations);

        // 没有兜底 ⇒ 只能当成首次（那正是首次激活的样子）。
        Assert.Equal(TrialPhase.Running, store.Evaluate(T0).Phase);

        // ⚠️ 有兜底（= 当初激活那一刻）⇒ 已经过去 200 小时 ⇒ **结束**。
        //    少了这一条，一台四个位置都写不进去的机器会「每次启动重新开始 7 天」。
        var state = store.Evaluate(T0.AddHours(200), fallbackFirstRunAt: T0);

        Assert.Equal(TrialPhase.Ended, state.Phase);
        Assert.Equal(T0, state.FirstRunAt);
    }

    // ─────────────────────────────────────────────
    // 真文件位置（尾码那一条只能在这里测）
    // ─────────────────────────────────────────────

    [Fact]
    public void 记事本改过的记录不算数_尾码对不上()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "vidlog-trial-" + Guid.NewGuid().ToString("N"), "trial.dat");

        try
        {
            var location = new FileTrialLocation("临时文件", path);

            location.Write(new TrialRecord(T0, T0));

            Assert.NotNull(location.Read());

            // 「用记事本把 firstRunAt 改到未来」—— 尾码没跟着改，对不上。
            var tampered = File.ReadAllText(path).Replace("2026-10-03", "2030-10-03");
            Assert.NotEqual(File.ReadAllText(path), tampered);   // 确实改到了，不是空动作
            File.WriteAllText(path, tampered);

            // ⚠️ 对不上就**当作这个位置没有记录** —— 于是它没法参与「取最早」。
            Assert.Null(location.Read());
        }
        finally
        {
            var directory = Path.GetDirectoryName(path);

            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void 文件位置写得进也读得回_而且往返不丢时刻()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "vidlog-trial-" + Guid.NewGuid().ToString("N"), "trial.dat");

        try
        {
            var location = new FileTrialLocation("临时文件", path);
            var written = new TrialRecord(T0, T0.AddHours(3.5));

            location.Write(written);

            Assert.Equal(written, location.Read());
        }
        finally
        {
            var directory = Path.GetDirectoryName(path);

            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // ─────────────────────────────────────────────
    // 接进 LicenseService：这才是「机位从 0 变成 4」那一步
    // ─────────────────────────────────────────────

    private static (ECDsa Signing, LicenseVerifier Verifier) NewKeyPair()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, new LicenseVerifier(key));
    }

    /// <summary>签一个码（测试里的卖家）。</summary>
    private static string Issue(
        ECDsa signing, MachineIdentity machine, bool trial, byte slots = 4, uint serial = 1)
    {
        var payload = new LicensePayload(
            trial ? LicensePayload.TrialVersion : LicensePayload.CurrentVersion,
            trial ? LicensePayload.TrialSlots : slots,
            3,
            100,
            serial,
            machine.AllHashes);

        var bytes = payload.ToBytes();
        var signature = signing.SignData(
            bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return LicenseVerifier.DisplayPrefix + Base32.Encode([.. bytes, .. signature]);
    }

    /// <summary>新建一个服务。<paramref name="licensePath"/> 给同一个值就等于「重启后」。</summary>
    private static (LicenseService Service, List<FakeLocation> Locations) NewService(
        ECDsa signing,
        MachineIdentity machine,
        Func<DateTimeOffset> now,
        string? licensePath = null,
        List<FakeLocation>? locations = null)
    {
        locations ??= Four();

        var service = new LicenseService(
            new LicenseVerifier(signing),
            new EntitlementStore(licensePath ?? Path.Combine(
                Path.GetTempPath(),
                "vidlog-trial-license-" + Guid.NewGuid().ToString("N"),
                "license.json")),
            machine,
            trial: Store(locations),
            now: now);

        return (service, locations);
    }

    [Fact]
    public async Task 激活试用码_机位是_4_而且是试用不是买断()
    {
        var (signing, _) = NewKeyPair();
        var machine = Machine();
        var (service, locations) = NewService(signing, machine, () => T0);

        var status = await service.ActivateAsync(Issue(signing, machine, trial: true));

        Assert.True(status.Activated);
        Assert.True(status.IsTrial);
        Assert.Equal(LicensePayload.TrialSlots, status.Slots);
        Assert.Equal(TrialRecordStore.Window, status.TrialRemaining);

        // 激活那一刻就是起算那一刻。
        Assert.Equal(T0, locations[0].Record!.FirstRunAt);
    }

    [Fact]
    public async Task 同一台机器再输一次试用码_不重置也不延长()
    {
        var (signing, _) = NewKeyPair();
        var machine = Machine();

        var now = T0;
        var (service, _) = NewService(signing, machine, () => now);

        await service.ActivateAsync(Issue(signing, machine, trial: true, serial: 1));

        // 用了 100 小时，客户又来要了一个码。
        now = T0.AddHours(100);
        var again = await service.ActivateAsync(Issue(signing, machine, trial: true, serial: 2));

        // ⚠️ 这个数就是**客服最会搞错的地方**：不是 168，是 68。
        Assert.Equal(TimeSpan.FromHours(68), again.TrialRemaining);
        Assert.True(again.Activated);
    }

    [Fact]
    public async Task 试用窗口用完_重启后说试用已结束_而且机位归零()
    {
        var (signing, _) = NewKeyPair();
        var machine = Machine();

        var licensePath = Path.Combine(
            Path.GetTempPath(), "vidlog-trial-license-" + Guid.NewGuid().ToString("N"), "license.json");

        var locations = Four();
        var now = T0;

        var (first, _) = NewService(signing, machine, () => now, licensePath, locations);

        // 先确认它**本来**是能用的 —— 不然下面那条「归零」可能只是因为压根没激活过。
        var activated = await first.ActivateAsync(Issue(signing, machine, trial: true));

        Assert.True(activated.Activated);
        Assert.Equal(LicensePayload.TrialSlots, activated.Slots);

        // 过了 169 小时，重启（同一个 license 文件、同一批试用记录）。
        now = T0.AddHours(169);

        var (reopened, _) = NewService(signing, machine, () => now, licensePath, locations);
        var status = await reopened.CheckAtStartupAsync();

        Assert.False(status.Activated);
        Assert.True(status.IsTrial);
        Assert.Equal(0, status.Slots);
        Assert.Equal("试用已结束。", status.FailureReason);
    }

    [Fact]
    public async Task 终身码_机位取签发的值_试用的那条线管不着它()
    {
        var (signing, _) = NewKeyPair();
        var machine = Machine();
        var (service, locations) = NewService(signing, machine, () => T0);

        // 机器上先留一份**已经过期**的试用记录（「先用试用、后来买了」的客户就是这样）。
        foreach (var location in locations)
        {
            location.Record = new TrialRecord(T0.AddHours(-500), T0.AddHours(-300));
        }

        var status = await service.ActivateAsync(Issue(signing, machine, trial: false, slots: 8));

        // ⚠️ 那份过期记录**不许**把终身码一起毙掉 —— 试用那条线只管试用码。
        Assert.True(status.Activated, status.FailureReason);
        Assert.Null(status.FailureReason);
        Assert.False(status.IsTrial);
        Assert.Equal(8, status.Slots);
        Assert.Null(status.TrialRemaining);
    }

    [Fact]
    public async Task 机位闸门读的那个数_试用中是_4_到期是_0()
    {
        // ⚠️ 这条钉的是「试用接进现有闸门」这件事本身：录制闸门读 `Activated`、
        //    机位闸门读 `Slots`（见 `LicenseStatus` 的注释），
        //    所以这里断言的**正是那两个字段**，不是另写的一个「能不能用」。
        var (signing, _) = NewKeyPair();
        var machine = Machine();

        var licensePath = Path.Combine(
            Path.GetTempPath(), "vidlog-trial-license-" + Guid.NewGuid().ToString("N"), "license.json");

        var locations = Four();
        var now = T0;

        var (first, _) = NewService(signing, machine, () => now, licensePath, locations);
        await first.ActivateAsync(Issue(signing, machine, trial: true));

        Assert.True(first.Status.Activated);                       // 录制闸门
        Assert.Equal(4, first.Status.Slots);                       // 机位闸门

        now = T0.AddHours(169);

        var (reopened, _) = NewService(signing, machine, () => now, licensePath, locations);
        var status = await reopened.CheckAtStartupAsync();

        Assert.False(status.Activated);                            // 录制闸门关了
        Assert.Equal(0, status.Slots);                             // 机位闸门也关了

        // ⚠️ L8：关的是「能不能开始新的」，**不是已有的录像** ——
        //    已有录像这条路根本不看 `LicenseStatus`，由 `LicenseIndependenceTests` 守着。
    }
}

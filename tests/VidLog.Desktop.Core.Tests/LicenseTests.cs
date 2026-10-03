using System.Security.Cryptography;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.License;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 许可（`docs/04-许可设计.md`）：机器码、激活码、验签、机位。
/// </summary>
/// <remarks>
/// ⚠️ 测试里的**密钥对是当场生成的**（`ECDsa.Create(P256)`）——
/// 真实的私钥在**签发工具**那边，**绝不进任何仓库**（L1）。
/// 这里生成一对只活在这一次测试里的，用来验验签这条路对不对。
/// </remarks>
public class LicenseTests
{
    private static (ECDsa Signing, LicenseVerifier Verifier) NewKeyPair()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, new LicenseVerifier(key));
    }

    /// <summary>一台机器。</summary>
    private static MachineIdentity Machine(
        string motherboard = "MB-SERIAL-1234",
        string cpu = "BFEBFBFF000806EC",
        string bios = "1.2.3") => new(motherboard, cpu, bios);

    /// <summary>签发一个激活码（测试里的卖家）。</summary>
    /// <param name="version">
    /// 默认终身码。<see cref="LicensePayload.TrialVersion"/> 就是试用码
    /// （那时 <paramref name="slots"/> 必须是 <see cref="LicensePayload.TrialSlots"/>）。
    /// </param>
    private static string Issue(
        ECDsa signing, MachineIdentity machine, byte slots = 2, byte minMatch = 3, uint serial = 1,
        byte version = LicensePayload.CurrentVersion)
    {
        var payload = new LicensePayload(
            version, slots, minMatch, 100, serial, machine.AllHashes);

        var bytes = payload.ToBytes();
        var signature = signing.SignData(
            bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return LicenseVerifier.DisplayPrefix + Base32.Encode([.. bytes, .. signature]);
    }

    // ─────────────────────────────────────────────
    // 机器码
    // ─────────────────────────────────────────────

    [Fact]
    public void 机器码是_24_个字符分_6_组()
    {
        var display = Machine().Display;

        Assert.Equal(24 + 5, display.Length);   // 24 字符 + 5 个横线
        Assert.Equal(6, display.Split('-').Length);
        Assert.All(display.Split('-'), group => Assert.Equal(4, group.Length));
    }

    [Fact]
    public void 规范化_去空白_转大写_只留字母数字()
    {
        Assert.Equal("ABC123", MachineCode.Normalize("  a b c-1_2.3 "));
        Assert.Equal(string.Empty, MachineCode.Normalize(null));
        Assert.Equal(string.Empty, MachineCode.Normalize("   "));
    }

    [Fact]
    public void OEM_占位串一律当作读不到()
    {
        // ⚠️ 不排掉的话，**一批同型号的机器会算出同一个机器码** ——
        // 表现是别人的激活码能用在我这儿，比读不到更糟。
        foreach (var placeholder in new[]
                 {
                     "To be filled by O.E.M.", "Default string", "System Serial Number",
                     "None", "Not Specified", "N/A", "0", "00000000", "FFFFFFFF", "Unknown",
                 })
        {
            Assert.Equal(string.Empty, MachineCode.Normalize(placeholder));
        }
    }

    [Fact]
    public void 每段独立哈希_换一项只变那一段()
    {
        // ⚠️ 把所有标识拼起来算一个哈希的话，换任何一项整个机器码就变了 ——
        // 那是 L4 要避免的售后爆炸。
        // ⚠️ 显示时**每 4 字符一组**，所以一段 8 字符 = 两组 —— 分组边界与段边界
        // 不是一回事（24 字符正好 6 组，而每段占 2 组）。
        var baseSegments = SegmentsOf(Machine());
        var changedSegments = SegmentsOf(Machine(bios: "9.9.9"));

        Assert.Equal(baseSegments[0], changedSegments[0]);     // 主板段不变
        Assert.Equal(baseSegments[1], changedSegments[1]);     // CPU 段不变
        Assert.NotEqual(baseSegments[2], changedSegments[2]);  // BIOS 段变了
    }

    /// <summary>把显示的机器码切成三段（每段 8 个字符）。</summary>
    private static string[] SegmentsOf(MachineIdentity identity)
    {
        var compact = MachineCode.Compact(identity.Display);

        return [compact[..8], compact[8..16], compact[16..24]];
    }

    [Fact]
    public void 读不到的段是全零段_AAAAAAAA_而不是_00000000()
    {
        // Base32 的字母表里**没有数字 0**（只有 A-Z2-7），所以全零字节编出来是 8 个 A。
        var identity = new MachineIdentity(string.Empty, "CPU", "BIOS");

        Assert.Equal("AAAAAAAA", SegmentsOf(identity)[0]);
        Assert.True(identity.IsDegraded);
        Assert.False(identity.IsUnusable);
    }

    [Fact]
    public void 三段都读不到时机器码没有任何区分度_要标出来()
    {
        var identity = new MachineIdentity(string.Empty, string.Empty, string.Empty);

        Assert.True(identity.IsUnusable);
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAA", MachineCode.Compact(identity.Display));
    }

    // ─────────────────────────────────────────────
    // 激活码
    // ─────────────────────────────────────────────

    [Fact]
    public void 自己签的码能验过()
    {
        var (signing, verifier) = NewKeyPair();
        var machine = Machine();

        var check = verifier.Verify(Issue(signing, machine), machine);

        Assert.True(check.Ok, check.FailureReason);
        Assert.Equal(2, check.Payload!.Slots);
    }

    [Fact]
    public void 改机位数必须导致验签失败()
    {
        // L3 原话：**改机位数必须导致验签失败**—— 否则用户自己把 2 改成 8。
        var (signing, verifier) = NewKeyPair();
        var machine = Machine();

        var code = Issue(signing, machine, slots: 2);

        // 手工把载荷里的 slots 改掉，再按原来的签名组装 —— 模拟用户改码。
        var compact = MachineCode.Compact(LicenseVerifier.StripPrefix(code));
        var bytes = Base32.TryDecode(compact, out _)!;
        bytes[1] = 8;

        var forged = LicenseVerifier.DisplayPrefix + Base32.Encode(bytes);

        Assert.False(verifier.Verify(forged, machine).Ok);
    }

    [Fact]
    public void 换一台机器验不过_而且说得出是不适用于本机()
    {
        var (signing, verifier) = NewKeyPair();

        var code = Issue(signing, Machine());
        var otherMachine = Machine(motherboard: "完全不同的主板", cpu: "另一个CPU", bios: "别的BIOS");

        var check = verifier.Verify(code, otherMachine);

        Assert.False(check.Ok);
        Assert.Contains("不适用于本机", check.FailureReason);
    }

    [Fact]
    public void 只换主板也验不过_那是用户明确要求的()
    {
        // L4 原话：**刷 BIOS 或换主板即失效**（用户明确要求）。
        var (signing, verifier) = NewKeyPair();

        var code = Issue(signing, Machine());
        var newMotherboard = Machine(motherboard: "换过的主板序列号");

        Assert.False(verifier.Verify(code, newMotherboard).Ok);
    }

    [Fact]
    public void minMatch_是售后调节阀_降到_2_就能放宽()
    {
        // §4.3：三段全中才算通过；留着三段是为了**将来能调**（某批机器读不稳时降到 2），
        // 而**不必改格式、不必重新签发已售出的码**。
        var (signing, verifier) = NewKeyPair();

        var code = Issue(signing, Machine(), minMatch: 2);
        var oneChanged = Machine(bios: "换过BIOS");

        Assert.True(verifier.Verify(code, oneChanged).Ok);
    }

    [Fact]
    public void 别的密钥签的码验不过()
    {
        var (signing, _) = NewKeyPair();
        var (_, verifier) = NewKeyPair();   // 另一对密钥（= 别人的私钥签的）
        var machine = Machine();

        Assert.False(verifier.Verify(Issue(signing, machine), machine).Ok);
    }

    [Fact]
    public void 激活码认得出前缀_横线_空格与大小写()
    {
        var (signing, verifier) = NewKeyPair();
        var machine = Machine();

        var code = Issue(signing, machine);
        var messy = $"  {code.ToLowerInvariant()}  ";

        Assert.True(verifier.Verify(messy, machine).Ok);
    }

    [Fact]
    public void 格式不对时给的是格式不正确而不是无效()
    {
        // §4.1 的每一步失败都要给明确原因 —— 全都回激活码无效的话，
        // 用户不知道是自己抄错了还是码本身有问题。
        var (_, verifier) = NewKeyPair();
        var machine = Machine();

        Assert.Contains("格式不正确", verifier.Verify("这不是一个激活码", machine).FailureReason);
        Assert.Contains("格式不正确", verifier.Verify("VLGAAAA", machine).FailureReason);
    }

    [Fact]
    public void 载荷的字节布局逐字节钉住()
    {
        // ⚠️ 2026-09-27 踩过：短哈希写成了从**第 9** 字节起，而第 9 字节是序号
        // 那个 4 字节字段的最后一字节 —— 两段重叠。读写是**同一处错**，
        // 所以自洽地绿着：码验得过、机器码也匹配，只有序号悄悄变成垃圾
        // （签的是 77，读出来是 0xF600004D）。是**签发工具那边的固定向量**
        // 把它顶出来的（见下一条）。
        //
        // 这一条把布局逐字节钉死，让同一个错法不必再靠另一个仓才发现。
        var segments = new List<byte[]>
        {
            new byte[] { 0x11, 0x12, 0x13, 0x14, 0x15 },
            new byte[] { 0x21, 0x22, 0x23, 0x24, 0x25 },
            new byte[] { 0x31, 0x32, 0x33, 0x34, 0x35 },
        };

        var payload = new LicensePayload(1, 4, 3, 100, 77, segments);
        var bytes = payload.ToBytes();

        Assert.Equal(LicensePayload.Size, bytes.Length);   // 25

        Assert.Equal(1, bytes[0]);      // 版本
        Assert.Equal(4, bytes[1]);      // 机位数
        Assert.Equal(3, bytes[2]);      // 最少匹配段数

        Assert.Equal(100, bytes[3] | (bytes[4] << 8) | (bytes[5] << 16));   // 签发日

        // 序号 77 = 0x4D，小端 ⇒ 4D 00 00 00。**第 9 字节必须是 0** ——
        // 它要是变成了短哈希的首字节，就是那个重叠又回来了。
        Assert.Equal(0x4D, bytes[6]);
        Assert.Equal(0, bytes[7]);
        Assert.Equal(0, bytes[8]);
        Assert.Equal(0, bytes[9]);

        Assert.Equal(segments[0], bytes[10..15]);
        Assert.Equal(segments[1], bytes[15..20]);
        Assert.Equal(segments[2], bytes[20..25]);

        // 读回来每个字段都得一样 —— 单看写的那一半会漏掉读的那一半的错。
        var back = LicensePayload.FromBytes(bytes);

        Assert.Equal(payload.Version, back.Version);
        Assert.Equal(payload.Slots, back.Slots);
        Assert.Equal(payload.MinMatch, back.MinMatch);
        Assert.Equal(payload.IssuedDay, back.IssuedDay);
        Assert.Equal(payload.Serial, back.Serial);
        Assert.Equal(segments[0], back.Segments[0]);
        Assert.Equal(segments[1], back.Segments[1]);
        Assert.Equal(segments[2], back.Segments[2]);
    }

    [Fact]
    public void 版本不认识时说请升级软件而不是无效()
    {
        var (signing, verifier) = NewKeyPair();
        var machine = Machine();

        var payload = new LicensePayload(9, 2, 3, 100, 1, machine.AllHashes);
        var bytes = payload.ToBytes();
        var signature = signing.SignData(
            bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var code = LicenseVerifier.DisplayPrefix + Base32.Encode([.. bytes, .. signature]);
        var check = verifier.Verify(code, machine);

        Assert.False(check.Ok);
        Assert.Contains("升级软件", check.FailureReason);
    }

    [Fact]
    public void 机位数不在档位里时回无效()
    {
        var (signing, verifier) = NewKeyPair();
        var machine = Machine();

        Assert.Contains("无效", verifier.Verify(Issue(signing, machine, slots: 7), machine).FailureReason);
    }

    // ─────────────────────────────────────────────
    // 试用码（规格 §6）：同一个载荷，版本字节从 1 变 2
    // ─────────────────────────────────────────────

    [Fact]
    public void 版本_2_的码认得出来是试用码_而且机位是_4()
    {
        var (signing, verifier) = NewKeyPair();
        var machine = Machine();

        var check = verifier.Verify(
            Issue(signing, machine, slots: LicensePayload.TrialSlots,
                version: LicensePayload.TrialVersion), machine);

        Assert.True(check.Ok, check.FailureReason);
        Assert.True(check.Payload!.IsTrial);

        // ⚠️ 机位取的是**常量**不是字面量 4：试用机位数只在一处定义，
        //    换个地方改成 6 的时候，这条要跟着红。
        Assert.Equal(LicensePayload.TrialSlots, check.Payload.Slots);
    }

    [Fact]
    public void 试用码的机位数不是_4_时回无效()
    {
        var (signing, verifier) = NewKeyPair();
        var machine = Machine();

        // 一个签成 8 机位的试用码 —— 签发工具那边也会挡住这种（`LicenseFormat.Issue`），
        // 但**两头都该挡住**：这里漏了的话，签错的码会当场生效而不是报错。
        var check = verifier.Verify(
            Issue(signing, machine, slots: 8, version: LicensePayload.TrialVersion), machine);

        Assert.False(check.Ok);
        Assert.Contains("无效", check.FailureReason);
    }

    [Fact]
    public void 终身码不受试用那一条影响_机位照旧走_2_4_6_8()
    {
        // 反证：上面那条如果不分版本、一律要求 4，这里就会红。
        var (signing, verifier) = NewKeyPair();
        var machine = Machine();

        foreach (var slots in new byte[] { 2, 4, 6, 8 })
        {
            var check = verifier.Verify(Issue(signing, machine, slots: slots), machine);

            Assert.True(check.Ok, $"{slots} 机位：{check.FailureReason}");
            Assert.False(check.Payload!.IsTrial);
            Assert.Equal(slots, check.Payload.Slots);
        }
    }

    // ─────────────────────────────────────────────
    // Base32
    // ─────────────────────────────────────────────

    [Fact]
    public void Base32_往返一致_而且字母表里没有_0_1_8_9()
    {
        var data = new byte[] { 0, 1, 2, 250, 251, 252, 253, 254, 255 };

        var encoded = Base32.Encode(data);
        var decoded = Base32.TryDecode(encoded, out _);

        Assert.Equal(data, decoded);
        Assert.DoesNotContain(encoded, c => c is '0' or '1' or '8' or '9');
    }

    [Fact]
    public void Base32_认不出的字符直接失败_不猜()
    {
        // ⚠️ 跳过认不出的字符的话，一个抄错的码会被解成另一串字节，
        // 而错误信息会变成此激活码不适用于本机—— 把用户引向错误的方向。
        Assert.Null(Base32.TryDecode("ABC0DEF", out var reason));
        Assert.Contains("认不出的字符", reason);
    }

    // ─────────────────────────────────────────────
    // 激活与启动校验
    // ─────────────────────────────────────────────

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vidlog-license-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string File => System.IO.Path.Combine(Path, "license.json");

        public void Dispose()
        {
            // ⚠️ **宽着接**：Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`（2026-09-27 实测：会话还在
            // 收尾时删含 `.ass` / `.mkv` 的临时目录）。清理失败不该让任何一条测试红。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task 没激活时状态是还没激活_而且录不了()
    {
        using var dir = new TempDir();
        var (_, verifier) = NewKeyPair();
        var service = new LicenseService(verifier, new EntitlementStore(dir.File), Machine());

        var status = await service.CheckAtStartupAsync();

        Assert.False(status.Activated);
        Assert.False(status.CanRecord);
        Assert.Contains("还没有激活", status.FailureReason);
    }

    /// <summary>把日志收起来的假 logger（本仓测试的惯例）。</summary>
    private sealed class CapturingLogger : IAppLogger
    {
        public List<string> Messages { get; } = [];

        public void Log(LogLevel level, string category, string message) => Messages.Add(message);

        public void Log(
            LogLevel level, string category, string message,
            IReadOnlyDictionary<string, object?> data) => Messages.Add(message);
    }

    [Fact]
    public async Task 激活之后落盘_重启还在_而且是重新验签出来的()
    {
        using var dir = new TempDir();
        var (signing, verifier) = NewKeyPair();
        var machine = Machine();
        var store = new EntitlementStore(dir.File);

        var first = new LicenseService(verifier, store, machine);
        await first.ActivateAsync(Issue(signing, machine, slots: 4));

        Assert.True(first.Status.Activated);
        Assert.Equal(4, first.Status.Slots);

        // 重启：**重新验签**，不是读缓存里的 Slots。
        var reopened = new LicenseService(verifier, store, machine);
        var status = await reopened.CheckAtStartupAsync();

        Assert.True(status.Activated);
        Assert.Equal(4, status.Slots);
    }

    [Fact]
    public async Task 激活没成功_日志里要留一条_但不许把那串码记进去()
    {
        // §6.1：失败也要留痕。判据就在这句话里 —— 用户在电话里只会说
        // 「激活不了」，分不出「抄漏了一段」和「码是给别的机器的」。
        using var dir = new TempDir();
        var (signing, verifier) = NewKeyPair();
        var logger = new CapturingLogger();
        var service = new LicenseService(
            verifier, new EntitlementStore(dir.File), Machine(), logger);

        // 给**别的机器**签的码：本机一定验不过。
        var code = Issue(signing, Machine(motherboard: "ANOTHER-MB-9999"), slots: 4);

        var status = await service.ActivateAsync(code);

        Assert.False(status.Activated);
        var line = Assert.Single(logger.Messages, m => m.StartsWith("激活没成功", StringComparison.Ordinal));
        Assert.Contains(status.FailureReason!, line);
        // ⚠️ 那串码是凭据，`Sanitizer` 也不认识它 —— **一个字符都不许进日志**。
        Assert.DoesNotContain(code, line);
        Assert.DoesNotContain(code[3..20], line);
    }

    [Fact]
    public async Task 手改本地许可文件里的_slots_没有用()
    {
        // §4.4：启动时**重新完整验签**，不读缓存里的 Slots ——
        // 读缓存的话，用户手改一下这个文件就能给自己加机位。
        using var dir = new TempDir();
        var (signing, verifier) = NewKeyPair();
        var machine = Machine();
        var store = new EntitlementStore(dir.File);

        await new LicenseService(verifier, store, machine).ActivateAsync(Issue(signing, machine, slots: 2));

        // 用户手改：把 2 改成 8。
        var json = await File.ReadAllTextAsync(dir.File);
        await File.WriteAllTextAsync(dir.File, json.Replace("\"Slots\": 2", "\"Slots\": 8"));

        var status = await new LicenseService(verifier, store, machine).CheckAtStartupAsync();

        Assert.Equal(2, status.Slots);   // 还是 2 —— 因为验签是从**码**里读的
    }

    [Fact]
    public async Task 换机器之后启动校验不过_并给出原因()
    {
        using var dir = new TempDir();
        var (signing, verifier) = NewKeyPair();
        var store = new EntitlementStore(dir.File);

        await new LicenseService(verifier, store, Machine()).ActivateAsync(Issue(signing, Machine()));

        var status = await new LicenseService(verifier, store, Machine(motherboard: "换过的主板"))
            .CheckAtStartupAsync();

        Assert.False(status.Activated);
        Assert.Contains("不适用于本机", status.FailureReason);
    }

    [Fact]
    public async Task 激活失败时不动已经激活的状态()
    {
        // 一次手滑不该把已经激活的机器锁掉。
        using var dir = new TempDir();
        var (signing, verifier) = NewKeyPair();
        var machine = Machine();
        var service = new LicenseService(verifier, new EntitlementStore(dir.File), machine);

        await service.ActivateAsync(Issue(signing, machine, slots: 2));

        var after = await service.ActivateAsync("这是一个乱写的码");

        Assert.True(after.Activated);       // 还是激活着
        Assert.Equal(2, after.Slots);
        Assert.NotNull(after.FailureReason); // 但把那句失败原因带回来了
    }

    [Fact]
    public void 机位公式_手机台数加摄像头超出_1_台()
    {
        // `docs/04-许可设计.md` §5.1：机位 = 手机台数 + max(0, 摄像头数 − 1)。
        // ⚠️ 本产品**只有一个摄像头**（那一路同时喂预览、识码与录制），
        // 所以今天这个公式**恒等于手机台数**（max(0, 1-1) = 0）。
        //
        // 这一条钉住的是公式的形状而不是今天的值：
        // 将来真接了第二路相机时，它会**占一个机位**，而不会悄悄白送。
        Assert.Equal(2, SeatUsage.For(2, cameras: 1));
        Assert.Equal(2, SeatUsage.For(2, cameras: 0));
        Assert.Equal(3, SeatUsage.For(2, cameras: 2));
        Assert.Equal(4, SeatUsage.For(2, cameras: 3));
    }

    // ─────────────────────────────────────────────
    // 与签发工具之间的固定测试向量
    // ─────────────────────────────────────────────

    /// <summary>
    /// 签发工具产出的码，客户端这边**验得过**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>这是两个仓之间唯一的对齐物。</b>签发工具（独立仓，拿着私钥）与客户端
    /// 各有一份格式实现 —— 谁都没引用谁，因为私钥那边的代码**绝不能**进这个仓（L1）。
    /// 于是「两边对格式的理解有没有走岔」只能靠这一条来发现。
    /// </para>
    /// <para>
    /// 下面这三样是**签发工具真的产出过的**（`issue --machine 62OH-… --slots 4 --serial 77`）。
    /// 手改这里的任何一个字符、或者动了 <see cref="LicensePayload"/> / <see cref="Base32"/> /
    /// 显示前缀 / 哈希域，它都会红。
    /// </para>
    /// <para>
    /// ⚠️ <b>生成这个码的那把私钥已经销毁了</b>（只留下公钥）。要换向量，
    /// 就用签发工具重新签一个，再把公钥、机器标识、码三样一起换掉。
    /// </para>
    /// </remarks>
    [Fact]
    public void 签发工具产出的码_客户端验得过()
    {
        const string publicKey =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEwcJHU+/gZov5vdlf5hPvTUuEW5ZR+J/2MhzyEs5W2C+T"
            + "5tYjfYz8KnaKX+wJYOy3x4yepXykYhgLm3ROePsFVA==";

        // ⚠️ 换行是为了读得下去，粘的时候要看成一整串。
        const string code =
            "VLGAECAHHIJABGQAAAA62OHNXKOSW6E2BCBUIWIPPC2KFODMGRVF3IEIFIYVJ4Z6Y7KKUPRQAS"
            + "FDHPNLQ7JDHOVGR5FMW3UFZOENOUJPC3KILGRWLAJMNQNT3244SFDWZOLWVBLVTBTQZGBPTI";

        var verifier = LicenseVerifier.FromEmbeddedKey(publicKey);
        Assert.NotNull(verifier);

        var check = verifier.Verify(code, Machine());

        Assert.True(check.Ok, check.FailureReason);
        Assert.Equal(4, check.Payload!.Slots);
        Assert.Equal(77u, check.Payload.Serial);
        Assert.Equal(new DateOnly(2026, 9, 27), check.Payload.Issued);
        Assert.Equal(3, check.Payload.MinMatch);
        Assert.Equal(LicensePayload.CurrentVersion, check.Payload.Version);
    }

    /// <summary>
    /// 那个码**不是**随便一台机器都能用。
    /// </summary>
    /// <remarks>
    /// 这一条防的是「上面那条绿在一个巧合上」：如果签发工具根本没把机器码段放进载荷
    /// （比如解机器码时全解成了零），上面那条照样绿，而**码会变成万能码**。
    /// 所以再拿一台别的机器验一次，它必须红。
    /// </remarks>
    [Fact]
    public void 同一个码换一台机器就验不过()
    {
        const string publicKey =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEwcJHU+/gZov5vdlf5hPvTUuEW5ZR+J/2MhzyEs5W2C+T"
            + "5tYjfYz8KnaKX+wJYOy3x4yepXykYhgLm3ROePsFVA==";

        const string code =
            "VLGAECAHHIJABGQAAAA62OHNXKOSW6E2BCBUIWIPPC2KFODMGRVF3IEIFIYVJ4Z6Y7KKUPRQAS"
            + "FDHPNLQ7JDHOVGR5FMW3UFZOENOUJPC3KILGRWLAJMNQNT3244SFDWZOLWVBLVTBTQZGBPTI";

        var verifier = LicenseVerifier.FromEmbeddedKey(publicKey)!;

        // 机器标识换掉一个 —— 三段短哈希里至少一段就变了，而最少匹配段数是 3。
        var other = new MachineIdentity("MB-SERIAL-9999", "BFEBFBFF000806EC", "1.2.3");
        var check = verifier.Verify(code, other);

        Assert.False(check.Ok);
        Assert.Contains("不适用于本机", check.FailureReason);
    }

    /// <summary>
    /// 签发工具产出的**试用码**，客户端也验得过（规格 §6）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>这一条与上面那条终身码的向量是一对，是**两个仓之间唯一的对齐物**。</b>
    /// 试用码的编码只差一个版本字节（5 号位那条向量证明不了它）——
    /// 签发工具那边若把版本字节写成别的值、或者忘了强制 4 机位，
    /// **只有这一条会红**。
    /// </para>
    /// <para>
    /// ⚠️ <b>生成这个码的那把私钥已经销毁了</b>（只留下公钥）。要换向量，
    /// 就用签发工具重新签一个：
    /// <c>issue --key &lt;临时私钥&gt; --machine 62OH-NXKO-SW6E-2BCB-UIWI-PPC2 --trial --serial 501</c>
    /// （那串机器码就是本文件 <see cref="Machine"/> 的默认身份），
    /// 再把公钥、码、序号、签发日一起换掉。
    /// </para>
    /// </remarks>
    [Fact]
    public void 签发工具产出的试用码_客户端验得过()
    {
        const string publicKey =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEaI2zETmr6pLUwu1M4M/FNDHRwesNDaDuua+nm4o8pinJ"
            + "Zj7Mc9C72S4ePfXCPHezlx3JTH8U2ayOtwW+MdLNbQ==";

        // ⚠️ 换行是为了读得下去，粘的时候要看成一整串。
        const string code =
            "VLGAICAHIYJAD2QCAAA62OHNXKOSW6E2BCBUIWIPPC25EVT23JMUIBK5LMYVE3ICOSRRWPV3I5U24ZZF"
            + "KWEZVL3IIZUHY6NVCE5DFZDUGSX2VB7LDY5P7CRPSJIFFMOA324SLQFSNIDRW7HVKI";

        var verifier = LicenseVerifier.FromEmbeddedKey(publicKey);
        Assert.NotNull(verifier);

        var check = verifier.Verify(code, Machine());

        Assert.True(check.Ok, check.FailureReason);
        Assert.Equal(LicensePayload.TrialVersion, check.Payload!.Version);
        Assert.True(check.Payload.IsTrial);
        Assert.Equal(LicensePayload.TrialSlots, check.Payload.Slots);
        Assert.Equal(501u, check.Payload.Serial);
        Assert.Equal(new DateOnly(2026, 10, 3), check.Payload.Issued);
    }

    /// <summary>
    /// 内置的那把公钥是个**能用的**公钥。
    /// </summary>
    /// <remarks>
    /// ⚠️ 防的是「装配时把这一串留空 / 留了个占位符」—— 那会让**每台装出来的机器**
    /// 都激活不了，而表现是**手机上说「电脑端的机位已经满了」、电脑端一个字都不显示**
    /// （2026-10-03 报上来的正是这一条）。空串在这里会当场红。
    /// </remarks>
    [Fact]
    public void 内置公钥不是空的_而且能解析出验证器()
    {
        Assert.False(string.IsNullOrWhiteSpace(LicensePublicKey.Base64));

        // `FromEmbeddedKey` 解析不了时返回 null —— 那正是「装出来的软件激活不了」。
        Assert.NotNull(LicenseVerifier.FromEmbeddedKey(LicensePublicKey.Base64));
    }

    [Fact]
    public void 本产品只有一路摄像头_所以机位就是手机台数()
    {
        // `SeatUsage.Cameras` 是**生产代码里那个常量**，不是这里另写的一个数 ——
        // 这条钉的是「公式的形状在产品代码里没被改坏」，
        // 而不是「测试文件里那个纯函数的算术是对的」。
        Assert.Equal(1, SeatUsage.Cameras);
        Assert.Equal(3, SeatUsage.For(3, SeatUsage.Cameras));
    }
}

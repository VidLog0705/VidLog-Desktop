using VidLog.Desktop.Core.Labels;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 作废标记的判据（D1，2026-10-10）。
/// </summary>
/// <remarks>
/// ⚠️ 与 <c>EvidenceLock</c> 那一组是**镜像**的，但落法刻意**相反**：
/// 锁「认不出当锁着」（宁可少删一条），作废「认不出当没作废」
/// （宁可多显示一条）。每一条都配一条反证，见各自的注释。
/// </remarks>
public class EvidenceVoidTests
{
    private static IReadOnlyDictionary<string, string> Labels(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    [Fact]
    public void 没打过作废标签就是没作废()
    {
        // 绝大多数证据的常态。⚠️ 这一条必须单独判：少了它，
        // 「认不出来就当作废」会把整个库从检索里藏掉 —— 用户查什么都查不到。
        // 反证：把 `IsVoided` 改成 `return labelsForEvidence is null || ...` ⇒ 这条红。
        Assert.False(EvidenceVoid.IsVoided(null));
        Assert.False(EvidenceVoid.IsVoided(Labels()));
        Assert.False(EvidenceVoid.IsVoided(Labels((LabelKeys.Locked, "true"))));
    }

    [Fact]
    public void 打过true就是作废了()
        => Assert.True(EvidenceVoid.IsVoided(Labels((LabelKeys.Voided, "true"))));

    [Fact]
    public void 打过false就是没作废()
    {
        // 「取消作废 = 再追加一条 false」（标签表追加写、后者胜出）。
        Assert.False(EvidenceVoid.IsVoided(Labels((LabelKeys.Voided, "false"))));
    }

    [Fact]
    public void 认不出来的值当没作废_与锁定那边正好相反()
    {
        // ⚠️ **承重的那一条**。`EvidenceLock` 认不出时当「锁着」（朝少删落），
        // 这里当「没作废」（朝多显示落）—— 两边的代价不对称：
        // 把作废读丢了的代价是**多显示一条**（用户自己能看出来），
        // 而反过来（`'1'` 被当成作废）的代价是**用户查不到自己录过的东西**。
        // 反证：把 `bool.TryParse(raw, out var voided) && voided`
        // 改成 `!bool.TryParse(raw, out var voided) || voided` ⇒ 这条红。
        Assert.False(EvidenceVoid.IsVoided(Labels((LabelKeys.Voided, "1"))));
        Assert.False(EvidenceVoid.IsVoided(Labels((LabelKeys.Voided, ""))));
        Assert.False(EvidenceVoid.IsVoided(Labels((LabelKeys.Voided, "被人手改坏了"))));
    }

    [Fact]
    public async Task 标签表本身把后者胜出做到了_作废与取消作废共用一条键()
    {
        // 这一条钉的是**上游**：`EvidenceVoid` 只读当前值，而「当前值」是
        // `JsonLinesLabelStore` 的「后者胜出」给的（与锁定共用同一套）。
        // 走的不是内存字典 —— 真落盘、真读回来。
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vidlog-void-" + Guid.NewGuid().ToString("N") + ".jsonl");

        try
        {
            var store = new JsonLinesLabelStore(path);

            await store.SetAsync("ev-1", LabelKeys.Voided, "true");
            Assert.True(EvidenceVoid.IsVoided(await store.GetForEvidenceAsync("ev-1")));

            await store.SetAsync("ev-1", LabelKeys.Voided, "false");
            Assert.False(EvidenceVoid.IsVoided(await store.GetForEvidenceAsync("ev-1")));
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch (System.IO.IOException) { }
        }
    }
}

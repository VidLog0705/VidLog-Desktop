using System.IO;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 录像库占用统计（概览页「录像库总容量」那一格）。
/// </summary>
/// <remarks>
/// ⚠️ 这个数是给用户看盘要满没满的，**偏小比不显示更坏** ——
/// 所以除了「数得对」，还要断「数不到的时候说出来」。
/// </remarks>
public class LibraryFootprintTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-footprint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Dir(string name)
        {
            var full = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(full);
            return full;
        }

        public string File(string relative, int bytes)
        {
            var full = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllBytes(full, new byte[bytes]);
            return full;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清不掉不该让测试变红。
            }
        }
    }

    [Fact]
    public void 数得出文件个数与字节合计()
    {
        using var temp = new TempDir();
        temp.File("a.mp4", 1000);
        temp.File("b.mp4", 2000);

        var footprint = LibraryFootprintProbe.Measure(temp.Path);

        Assert.Equal(2, footprint.FileCount);
        Assert.Equal(3000, footprint.TotalBytes);
        Assert.Equal(0, footprint.UnreadableCount);
    }

    [Fact]
    public void 子目录里的也要数进去()
    {
        using var temp = new TempDir();
        temp.File("2026-09-28/a.mp4", 512);
        temp.File("2026-09-28/nested/b.mp4", 512);

        var footprint = LibraryFootprintProbe.Measure(temp.Path);

        Assert.Equal(2, footprint.FileCount);
        Assert.Equal(1024, footprint.TotalBytes);
    }

    [Fact]
    public void 空目录与不存在的目录都返回零而不是抛()
    {
        using var temp = new TempDir();
        var empty = LibraryFootprintProbe.Measure(temp.Dir("archive"));
        var missing = LibraryFootprintProbe.Measure(
            System.IO.Path.Combine(temp.Path, "根本没有这个目录"));

        Assert.Equal(new LibraryFootprint(0, 0, 0), empty);
        Assert.Equal(new LibraryFootprint(0, 0, 0), missing);
    }

    [Fact]
    public void 空字符串不算错()
    {
        Assert.Equal(new LibraryFootprint(0, 0, 0), LibraryFootprintProbe.Measure(string.Empty));
    }

    [Fact]
    public void 取消能立刻停下来()
    {
        using var temp = new TempDir();
        temp.File("a.mp4", 10);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => LibraryFootprintProbe.Measure(temp.Path, cts.Token));
    }
}

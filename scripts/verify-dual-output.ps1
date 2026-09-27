# 技术验证：**一路视频源同时喂「录制」与「识码」**会不会互相拖垮
# ══════════════════════════════════════════════════════════════════════════
#
# 它验的是规格 §3.1.3（预录缓冲）的**前置条件**。
#
# ## 为什么需要这次验证
#
# §3.1.3 要求「开始录制的动作**早于**扫码完成时刻，缓冲期内的画面也要保留」。
# 而电脑端现在是「空闲识码 → 扫到才开录」（需求方裁定，见电脑端
# `docs/实现决策.md` §24 第 3 点）—— 两件事**不重叠**。
#
# 要做到 §3.1.3，就必须**在识码的同时录着**（或有一个一直在录的环形缓冲），
# 也就是「一路源、两路输出」。而 §24 第 2 条明确否决过这个做法：
#
#   > **多一路输出就多一条会堵的管道**，而堵住管道会一路顶到 ffmpeg ——
#   > 它连 `q` 都处理不了，只能强杀，MKV 尾部就丢了（见 §16、§25）。
#
# ⚠️ 但那条否决的**理由**是「识码与录制本来就不重叠，双输出解决的是一个
# 不存在的问题」—— 也就是说：**要做 §3.1.3，它就从一个「不存在的问题」
# 变成一个真问题**。所以得重新量一次它到底会不会堵、什么条件下不堵。
#
# ## 两个场景（第二个是**对照**，不能省）
#
#   及时读   模拟识码端：每秒 1 帧、读完就丢 —— 录制**应当**正常完成
#   不读     故意**不读**管道 —— 用来证明「这个验证方法**测得出堵**」
#            ⚠️ 如果它也不堵，说明方法无效，第一场景那个「绿」不算数
#            （与「先证明会红」同一个道理）
#
# ## 两个数据源
#
#   -Source lavfi            本机就能跑（不需要相机）—— 验的是**管道背压机制**
#   -Source dshow -Device …   有摄像头的机器上跑 —— 才是**真机验证**
#                             （dshow 那一层：yuyv422、设备缓冲、打开延迟）
#
# ⚠️ **lavfi 跑绿不等于真机可行**：它验不到 dshow 那一层。两段都要跑。
# ══════════════════════════════════════════════════════════════════════════

param(
    [ValidateSet('lavfi', 'dshow')]
    [string]$Source = 'lavfi',

    # dshow 时的设备名（`ffmpeg -list_devices true -f dshow -i dummy` 里那个）
    [string]$Device = '',

    [int]$Seconds = 10,

    # 识码端多久读一帧。1 = 每秒一帧（现在那个识码进程就是低频的）
    [int]$DecodeFps = 1,

    # 识码那一帧的尺寸 —— 与 `ScannerProcess` 一致（640x480 灰度）
    [int]$DecodeWidth = 640,
    [int]$DecodeHeight = 480
)

$ErrorActionPreference = 'Stop'
$Ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $Ffmpeg) { throw 'PATH 里没有 ffmpeg' }

$frameBytes = $DecodeWidth * $DecodeHeight          # gray = 1 字节/像素
$work = Join-Path $env:TEMP ("vidlog-dual-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null

Write-Host "工作目录：$work"
Write-Host "数据源：$Source   时长：${Seconds}s   识码端：${DecodeFps} fps @ ${DecodeWidth}x${DecodeHeight}"
Write-Host ''

# ── ffmpeg 的 argv：一路源、两路输出 ────────────────────────────────────
#
# 第 ① 路：正常录制（与生产同形的编码参数）
# 第 ② 路：灰度 rawvideo 到 stdout，**降帧到识码端读得过来的频率**
#          —— 这一条是本次验证要回答的核心：给识码那一路降帧，
#          是不是就足以让它永远不成为瓶颈。
function New-Args {
    param([string]$Output)

    # ⚠️ 不叫 `$input` —— 那是 PowerShell 的自动变量。
    $inputArgs = if ($Source -eq 'lavfi') {
        # ⚠️ `-re` **必须有**：不加的话 lavfi 会**尽快**把 10 秒的源产完
        # （实测 0.8 秒），于是那些帧一股脑涌进管道 —— 那是**假的背压**，
        # 真实相机是**实时**产出的。`-re` 让它按实时读输入，才与相机同形。
        @('-re', '-f', 'lavfi', '-i', "testsrc=size=1280x720:rate=30")
    } else {
        if (-not $Device) { throw '-Source dshow 时要给 -Device' }
        # dshow 本来就是实时的 —— 不需要（也不能）加 `-re`。
        @('-f', 'dshow', '-framerate', '30', '-video_size', '1280x720', '-i', "video=$Device")
    }

    return @(
        '-hide_banner', '-nostdin'
    ) + $inputArgs + @(
        # ⚠️⚠️ **每一路输出都要各自的 `-t`** —— 这是第一次跑就踩到的坑：
        # `-t` 是**输出选项**，只作用于紧跟它的那一个输出。只给第一路的话，
        # 第二路（pipe:1）**没有时长限制**，于是 lavfi 无限生成、ffmpeg 永不退出
        # （实测挂到 600 秒超时，而且**连 OS 级 `> NUL` 也一样挂** ——
        # 说明卡的不是读取端）。
        #
        # ⇒ 这条本身就是本次验证的一条实现约束：**双输出时每一路都要各自限长**。
        #
        # ① 录制那一路
        '-t', "$Seconds",
        '-map', '0:v', '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p',
        '-f', 'matroska', $Output,
        # ② 识码那一路：降帧 + 灰度 + rawvideo 到管道
        '-t', "$Seconds",
        '-map', '0:v', '-vf', "fps=$DecodeFps,scale=${DecodeWidth}:${DecodeHeight}",
        '-pix_fmt', 'gray', '-f', 'rawvideo', 'pipe:1'
    )
}

# ── 跑一个场景 ──────────────────────────────────────────────────────────
function Invoke-Scenario {
    param(
        [string]$Name,
        # $true = 及时读管道（模拟识码）；$false = 不读（对照）
        [bool]$Drain,
        [int]$TimeoutSeconds = 90
    )

    $mkv = Join-Path $work "$Name.mkv"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Ffmpeg
    $psi.Arguments = (New-Args -Output $mkv) -join ' '
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = [System.Diagnostics.Process]::Start($psi)

    # ⚠️ **stderr 必须一直读干** —— 这一条是第一次跑就踩到的坑：
    # 设了 RedirectStandardError 却从不读，ffmpeg 的 stderr 一填满，
    # 它就**卡在写 stderr 上**，于是整个验证挂死（跑到 600 秒超时）。
    # 那正是 §24 记的同一件事（「管道要读干」）—— 只不过那次是 stdout。
    #
    # ⚠️ 而且它**必须与场景无关**地读：B 场景（不读 stdout）要验的是
    # 「堵在 **stdout** 上」，stderr 不读干的话就分不清堵在哪一路。
    $stderrTask = $proc.StandardError.ReadToEndAsync()

    $read = 0
    $buffer = New-Object byte[] 65536

    if ($Drain) {
        # 读满一帧就丢（模拟「解一帧码」），到点就停下等下一帧。
        $frameDeadline = [datetime]::Now.AddSeconds(1.0 / $DecodeFps)
        try {
            while ($true) {
                $n = $proc.StandardOutput.BaseStream.Read($buffer, 0, $buffer.Length)
                if ($n -le 0) { break }
                $read += $n
                if ($read -ge $frameBytes) {
                    $read = 0
                    $sleep = ($frameDeadline - [datetime]::Now).TotalMilliseconds
                    if ($sleep -gt 0) { Start-Sleep -Milliseconds ([int]$sleep) }
                    $frameDeadline = [datetime]::Now.AddSeconds(1.0 / $DecodeFps)
                }
            }
        } catch { }
    } else {
        # ⚠️ 故意不读 —— 管道会填满，然后 ffmpeg 应该**卡在写 stdout 上**。
        Start-Sleep -Seconds ($Seconds + 5)
    }

    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
        $proc.Kill($true)
        $sw.Stop()
        return [pscustomobject]@{
            场景 = $Name; 退出 = '超时未退出（被堵住）'; 耗时秒 = [math]::Round($sw.Elapsed.TotalSeconds, 1)
            有产物 = (Test-Path $mkv)
            产物字节 = if (Test-Path $mkv) { (Get-Item $mkv).Length } else { 0 }
            可解码 = $false
            解码错误 = ''
        }
    }

    $sw.Stop()

    # stderr 读干（上面起的那个任务）—— 顺便把它留给下面「堵在哪一路」的判断。
    $stderrText = ''
    try { $stderrText = $stderrTask.GetAwaiter().GetResult() } catch { }

    # 「可解码」= 拿 ffmpeg 真解一遍（不是看文件在不在 —— 半截的 MKV 也在）。
    $decode = & $Ffmpeg -v error -i $mkv -f null - 2>&1 | Out-String
    $size = if (Test-Path $mkv) { (Get-Item $mkv).Length } else { 0 }

    return [pscustomobject]@{
        场景 = $Name
        退出 = "退出码 $($proc.ExitCode)"
        耗时秒 = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        有产物 = $size -gt 0
        产物字节 = $size
        可解码 = [string]::IsNullOrWhiteSpace($decode)
        解码错误 = $decode.Trim()
        # 留一段 stderr 尾巴 —— 堵住的时候它通常会说出卡在哪一步。
        stderr尾 = ($stderrText -split "`n" | Select-Object -Last 2) -join ' / '
    }
}

$results = @()
$results += Invoke-Scenario -Name 'A-及时读' -Drain $true
$results += Invoke-Scenario -Name 'B-不读' -Drain $false

Write-Host ''
$results | Format-Table -AutoSize -Wrap

Write-Host @'

怎么读这张表
────────────
· A 那行必须「可解码 = True」且耗时接近源时长 —— 那是「识码端及时读就不堵」。
· B 那行**必须是堵的**（超时未退出 / 可解码 = False）—— 它证明这个验证
  方法**真的测得出堵**。⚠️ 若 B 也不堵，说明**方法无效**，A 那个绿不算数。
· lavfi 源是尽快生成的（不按实时），所以 B 会很快把管道填满 —— 正合目的。

⚠️ `-Source lavfi` 验的是**管道背压机制**，**验不到 dshow 那一层**
（相机缓冲、yuyv422、打开延迟）。真机验证要在有摄像头的机器上跑：

    pwsh -NoProfile -File scripts/verify-dual-output.ps1 -Source dshow -Device "<设备名>"
'@

Write-Host "工作目录留着：$work"

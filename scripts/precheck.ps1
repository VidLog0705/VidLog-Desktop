<#
.SYNOPSIS
    推送前的本地预检。把 CI 失败挡在推送之前。

.DESCRIPTION
    本仓是电脑端（.NET / WPF），预检覆盖：密钥泄露、大文件、还原、构建、测试。

    目的有两个：
      1. 快速反馈 —— 不用等 CI 排队
      2. 省额度 —— 私有仓库的 Windows runner 是 2× 计费

    必须先跑通这个再 push。

.PARAMETER SkipTests
    只做静态检查与构建，不跑测试。

.EXAMPLE
    pwsh -NoProfile -File scripts/precheck.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$script:Failures = @()
$script:Warnings = @()

function Section($name) {
    Write-Host ''
    Write-Host ('=' * 60) -ForegroundColor DarkGray
    Write-Host "  $name" -ForegroundColor Cyan
    Write-Host ('=' * 60) -ForegroundColor DarkGray
}

function Fail($msg) { $script:Failures += $msg; Write-Host "  [FAIL] $msg" -ForegroundColor Red }
function Warn($msg) { $script:Warnings += $msg; Write-Host "  [WARN] $msg" -ForegroundColor Yellow }
function Pass($msg) { Write-Host "  [ OK ] $msg" -ForegroundColor Green }
function Info($msg) { Write-Host "  $msg" -ForegroundColor Gray }

# ─────────────────────────────────────────────────────────────
Section '0. 环境'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if ($dotnet) {
    Info "dotnet -> $dotnet"
    Info (& dotnet --version)
} else {
    Fail 'dotnet 不在 PATH'
}

# FFmpeg 缺失不会让构建失败，但会让整组集成测试静默跳过 —— 那比失败更糟。
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if ($ffmpeg) {
    Info "ffmpeg -> $ffmpeg"
} else {
    Warn 'FFmpeg 不在 PATH —— 编码探测 / remux / 解码校验的集成测试会被跳过'
}

# ─────────────────────────────────────────────────────────────
Section '1. 密钥泄露检查'

# 扫「有真实值的密钥」，不是扫「提到了密钥这个词」。
# 关键词匹配会误伤文档（规范里当然会写 secret 这个词）。
$valuePatterns = @(
    'gh[pousr]_[A-Za-z0-9]{20,}',                                  # GitHub token
    'github_pat_[A-Za-z0-9_]{20,}',                                # GitHub fine-grained PAT
    'sk-[A-Za-z0-9]{20,}',                                         # OpenAI 风格
    'AKIA[0-9A-Z]{16}',                                            # AWS access key
    'xox[baprs]-[A-Za-z0-9-]{10,}',                                # Slack
    '-----BEGIN [A-Z ]*PRIVATE KEY-----',                          # 私钥
    '(eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})',  # JWT
    '(?i)(secret|password|passwd|pwd|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key)\s*[:=]\s*["''][^"''\s]{12,}["'']'
)

# 文档与配置元文件本来就该「提到」密钥，不参与扫描
$skipFile = '\.(md|txt|rst)$|(^|/)\.gitignore$|(^|/)\.gitattributes$|scripts/precheck\.ps1$'
$skipExt  = '\.(png|jpg|jpeg|gif|ico|pdf|zip|7z|exe|dll|so|dylib|a|aar|jar|mp4|mp3|woff2?)$'

# ⚠️ `-c core.quotepath=false` 是**承重的**：git 默认把非 ASCII 的文件名
# 转义成 `"\346\226\207…"` 那种八进制串，而下面那句 `Test-Path $f` 认不出来
# ⇒ 一个个中文名的文件会被 **`continue` 静默跳过**，等于这道闸对它们全开。
# 本仓的文档大半是中文名，所以这一条不是洁癖。
$staged = @(& git -c core.quotepath=false diff --cached --name-only --diff-filter=ACM 2>$null)
if ($staged.Count -eq 0) { $staged = @(& git -c core.quotepath=false diff --name-only --diff-filter=ACM HEAD 2>$null) }

if ($staged.Count -gt 0) {
    $hits = @()
    foreach ($f in $staged) {
        if (-not $f) { continue }
        if (-not (Test-Path $f)) { continue }
        if ($f -match $skipExt) { continue }
        if ($f -match $skipFile) { continue }
        foreach ($pat in $valuePatterns) {
            $m = Select-String -Path $f -Pattern $pat -AllMatches -ErrorAction SilentlyContinue
            if ($m) { $hits += $m }
        }
    }
    if ($hits.Count -gt 0) {
        Fail "疑似真实密钥出现在待提交内容里（$($hits.Count) 处）"
        $hits | Select-Object -First 10 | ForEach-Object {
            $t = $_.Line.Trim()
            Info ("    {0}:{1}  {2}" -f $_.Path, $_.LineNumber, $t.Substring(0, [Math]::Min(90, $t.Length)))
        }
        Info '    确认误报就忽略；是真密钥，改用环境变量或 GitHub Secrets，并立刻轮换。'
    } else { Pass '未发现明文密钥' }
} else {
    Pass '没有待提交的改动'
}

# ─────────────────────────────────────────────────────────────
Section '2. 大文件检查'

$maxMB = 20

# ⚠️ 同第 1 节：不加 `core.quotepath=false` 的话，中文名的文件会以八进制转义
# 出现在这里，`Test-Path` 报「路径里有非法字符」，整条检查**看起来在跑**
# 却一个中文名文件都没量到（2026-10-02 实测，四个警告刷了满屏）。
$big = & git -c core.quotepath=false ls-files 2>$null | Where-Object { $_ } | ForEach-Object {
    if (Test-Path $_) {
        $i = Get-Item $_ -Force -ErrorAction SilentlyContinue
        if ($i -and $i.Length -gt ($maxMB * 1MB)) {
            [pscustomobject]@{ Path = $_; MB = [Math]::Round($i.Length / 1MB, 1) }
        }
    }
}
if ($big) {
    Fail "有超过 ${maxMB}MB 的已跟踪文件"
    $big | ForEach-Object { Info ("    {0}  {1} MB" -f $_.Path, $_.MB) }
} else { Pass "无超过 ${maxMB}MB 的已跟踪文件" }

# ─────────────────────────────────────────────────────────────
Section '3. .NET'

$sln = @(Get-ChildItem -Path $root -Filter *.sln -File -ErrorAction SilentlyContinue)
$target = if ($sln.Count -gt 0) { $sln[0].FullName } else { $root }

Info 'restore...'
& dotnet restore $target --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail 'dotnet restore 失败' }

Info 'build...'
& dotnet build $target -c Debug --no-restore --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail 'dotnet build 失败' } else { Pass 'dotnet build 通过' }

if (-not $SkipTests) {
    Info 'test...'
    $testOutput = & dotnet test $target -c Debug --no-build --nologo `
        --logger "trx;LogFileName=precheck.trx" 2>&1 | Out-String
    Write-Host $testOutput

    if ($LASTEXITCODE -ne 0) {
        Fail 'dotnet test 失败'
    } else {
        Pass 'dotnet test 通过'

        # 跳过的测试会让「全绿」变成一种虚假的安心 —— 尤其是没装 FFmpeg 时
        # 整组集成测试会静默跳过，而它们正是验编码探测与成品可播性的那些。
        #
        # ⚠️ **必须只认汇总行那个数**。原来写的是 `(Skipped|已跳过)\D+(\d+)`，
        # 它会**先匹配到上面逐条打印的 "已跳过 <测试名> [1 ms]"**，把耗时 1 当成
        # 跳过数 —— 明明跳了 12 个，却报「有 1 个测试被跳过」。
        # 一条守卫报错了数，比没有守卫更坏：它让人以为只差一点点。
        #
        # ⚠️ 但也不能按「> 0 就警惕」一刀切：有一类跳过是**这台机器本来就没有**的
        # 东西（摄像头 / 内网 RTSP 源）。所以分开报 —— 允许的说明一下，其余才值得查。
        #
        # ⚠️ 判据与 CI **逐字同源**：读 TRX 里被跳过测试的 **Skip 原因**，
        # 带 `[允许跳过]` 标记的才放行（见 tests/…/SkipMarker.cs）。
        # 以前这里与 CI 各写一份「按类名列名单」—— 那句「判据与 CI 保持一致」
        # 其实**名不副实**（两套实现），而 2026-09-29 一天就走岔两次。
        $trx = Get-ChildItem -Path $root -Recurse -Filter 'precheck.trx' -ErrorAction SilentlyContinue |
               Select-Object -First 1
        if (-not $trx) {
            Warn '找不到 precheck.trx —— 没法判定有没有意外跳过（CI 那边会判）'
        } else {
            [xml]$results = Get-Content -LiteralPath $trx.FullName
            $skipped = @($results.TestRun.Results.UnitTestResult |
                         Where-Object { $_.outcome -eq 'NotExecuted' })

            if ($skipped.Count -gt 0) {
                $allowMarker = [regex]::Escape('[允许跳过]')
                $unexpected = @($skipped | Where-Object {
                    "$($_.Output.ErrorInfo.Message)" -notmatch $allowMarker
                })

                if ($unexpected.Count -gt 0) {
                    # ⚠️ 只 Warn 不 Fail：CI 那一步才是**门**。本机要与 CI 同判据，
                    # 但没必要因为一条自己已经知道原因的环境跳过挡住推送。
                    $names = ($unexpected | ForEach-Object { $_.testName }) -join '; '
                    Warn "有 $($unexpected.Count) 个测试被异常跳过（CI 上会红）：$names"
                } else {
                    Info "$($skipped.Count) 个测试因本机环境限制跳过（无摄像头 / 无内网 RTSP 源），已按标记放行"
                }
            }
        }
    }
}

# ─────────────────────────────────────────────────────────────
Section '4. 不变量自查（母仓 docs/01-行为规格书.md 第 7 节）'

Info '不变量主要靠单元测试保证，此处仅作提醒：'
Info '  I1  归档回执前不清理本地      I7  导出件不算归档层那一份'
Info '  I2  任何时刻至少一份副本       I8  清理前必须回查归档层'
Info '  I3  上传失败必须可见           I9  录制收尾只有一条路径'
Info '  I4  坏配置不得影响录制         I11 改系统时间不得伪造时间'
Info '  I5  单号是唯一事实标识         I12 静止判定按已录时长封顶'
Info '  I10 已校准之后的无网可用（未校准不得录制）'

# ─────────────────────────────────────────────────────────────
Section '结果'

if ($script:Warnings.Count -gt 0) {
    Write-Host ("  警告 {0} 项" -f $script:Warnings.Count) -ForegroundColor Yellow
}
if ($script:Failures.Count -gt 0) {
    Write-Host ''
    Write-Host ("  预检未通过 —— {0} 项失败：" -f $script:Failures.Count) -ForegroundColor Red
    $script:Failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    Write-Host ''
    Write-Host '  修完再推送。CI 排队比这里慢得多。' -ForegroundColor Yellow
    exit 1
}

Write-Host ''
Write-Host '  预检全部通过，可以推送。' -ForegroundColor Green
exit 0

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Cloud;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Import;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Live;
using VidLog.Desktop.Core.Playback;
using VidLog.Desktop.Core.Search;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.Core.Web;

public sealed partial class PlaybackServer : IAsyncDisposable
{
    /// <summary>回放页面。刻意保持单文件、零外部依赖 —— 局域网里没有 CDN 可访问。</summary>
    /// <remarks>
    /// <para>
    /// 外观照需求方的设计图 <c>_36</c> / <c>_37</c> / <c>_38</c> 做（豁免留档见
    /// <c>docs/实现决策.md</c> §86）：浅色单档、三张统计卡、筛选卡、录像列表卡、
    /// 齿轮弹出的「播放兼容」、以及「用手机打开」的二维码。
    /// </para>
    /// <para>
    /// ⚠️ <b>图上能点、而本仓没这个能力的每一颗按钮一律 <c>disabled</c>，
    /// 并且把原因写在悬停提示里</b>（踩坑 #13）。渲染一个按下去什么都不发生的按钮，
    /// 用户会以为是网络卡了，接着反复点 —— 那比干脆没有这颗按钮更糟。
    /// </para>
    /// <para>
    /// ⚠️ <b>不引任何外部资源</b>（图标是内联 SVG，二维码是 canvas 现画的）：
    /// 这一页跑在局域网里，多半连不上外网，一个 CDN 图标就能让它整页错位。
    /// </para>
    /// </remarks>
    private static string BuildPage() => """
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>快递打包录像回放</title>
        <style>
          /* 设计图 `_36` 的令牌（Tailwind 色板）。⚠️ 只做浅色一档 —— 图上没有深色版，
             而 `color-scheme: light dark` 会让浏览器把输入框、滚动条自己刷成深色，
             于是同一张卡上出现两套配色。 */
          :root {
            --page: #F3F4F6; --surface: #FFFFFF; --muted-surface: #F8FAFC;
            --border: #E2E8F0; --border-strong: #CBD5E1;
            --text: #1E293B; --muted: #64748B; --disabled: #94A3B8;
            --accent: #3B82F6; --accent-weak: #DBEAFE; --success: #10B981;
            --video: #0F172A; --warn: #C2410C;
            color-scheme: light;
          }
          * { box-sizing: border-box; }
          body { margin: 0; background: var(--page); color: var(--text);
            font: 14px/1.6 system-ui, "Segoe UI", "Microsoft YaHei", sans-serif; }
          .page { max-width: 1120px; margin: 0 auto; padding: 20px 16px 48px; }

          .topbar { display: flex; align-items: flex-start; gap: 12px; flex-wrap: wrap; margin-bottom: 16px; }
          .topbar h1 { font-size: 22px; margin: 0; flex: 1 1 auto; }
          .tools { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }

          button { font: inherit; cursor: pointer; border-radius: 8px;
            border: 1px solid var(--border-strong); background: var(--surface);
            color: var(--text); padding: 6px 12px; }
          button:hover:not(:disabled) { border-color: var(--accent); color: var(--accent); }
          button:disabled { color: var(--disabled); background: var(--muted-surface);
            border-color: var(--border); cursor: not-allowed; }
          .tool-btn { display: flex; flex-direction: column; align-items: flex-start;
            gap: 1px; padding: 6px 12px; text-align: left; }
          .tool-btn b { font-size: 13px; font-weight: 600; }
          .tool-btn small { font-size: 11px; color: var(--muted); font-weight: 400; }
          .tool-btn:disabled small { color: var(--disabled); }
          .icon-btn { width: 34px; height: 34px; padding: 0; display: grid; place-items: center; }
          .icon-btn svg { width: 18px; height: 18px; }

          input, select { font: inherit; padding: 7px 10px; width: 100%;
            border: 1px solid var(--border-strong); border-radius: 8px;
            background: var(--surface); color: var(--text); }
          input:focus, select:focus { outline: 2px solid var(--accent-weak); border-color: var(--accent); }

          .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
            gap: 12px; margin-bottom: 12px; }
          .card { background: var(--surface); border: 1px solid var(--border);
            border-radius: 12px; padding: 16px; }
          .card h2 { font-size: 13px; font-weight: 600; color: var(--muted); margin: 0 0 6px; }
          .stat { font-size: 24px; font-weight: 700; line-height: 1.3; }
          .stat.weak { font-size: 16px; font-weight: 600; color: var(--muted); }
          .foot { font-size: 12px; color: var(--muted); margin-top: 6px; }
          .bar { height: 8px; border-radius: 999px; background: var(--border);
            margin-top: 12px; overflow: hidden; }
          .bar i { display: block; height: 100%; width: 0; background: var(--accent); }

          .filters { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr));
            gap: 12px; align-items: end; }
          .field { display: flex; flex-direction: column; gap: 4px; }
          .field label { font-size: 12px; color: var(--muted); }
          .field button { height: 36px; }

          .list-card { padding: 0; overflow: hidden; margin-top: 12px; }
          .card-head { display: flex; align-items: center; gap: 12px;
            padding: 14px 16px; border-bottom: 1px solid var(--border); }
          .card-head h2 { margin: 0; font-size: 15px; font-weight: 600; color: var(--text); }
          .card-head .foot { margin: 0 0 0 auto; }

          table { width: 100%; border-collapse: collapse; }
          th, td { text-align: left; padding: 10px 12px; font-size: 13px;
            border-bottom: 1px solid var(--border); }
          th { color: var(--muted); font-weight: 600; background: var(--muted-surface); }
          tbody tr:last-child td { border-bottom: none; }
          tr.hit { cursor: pointer; }
          tr.hit:hover { background: var(--accent-weak); }
          .thumb { display: block; width: 96px; height: 54px; border-radius: 6px;
            object-fit: cover; background: var(--video); }

          .empty { padding: 40px 16px; text-align: center; }
          .empty b { display: block; font-size: 15px; margin-bottom: 4px; }
          .empty span { font-size: 13px; color: var(--muted); }

          #punches { display: flex; gap: 6px; flex-wrap: wrap; margin-top: 12px; align-items: center; }
          #punches button { padding: 4px 10px; }
          #player { display: none; width: 100%; margin-top: 16px;
            background: #000; border-radius: 12px; }
          .note { margin-top: 24px; font-size: 12px; color: var(--muted); }
          .muted { color: var(--muted); }

          .modal { position: fixed; inset: 0; z-index: 10; padding: 16px;
            background: #0F172A99; display: grid; place-items: center; }
          .modal[hidden] { display: none; }
          .modal-box { width: 100%; max-width: 460px; background: var(--surface);
            border-radius: 12px; padding: 20px; }
          .modal-box h3 { margin: 0 0 6px; font-size: 16px; }
          .radio { display: flex; align-items: center; gap: 8px; padding: 8px 0; }
          .radio input { width: auto; }
          .radio.off { color: var(--disabled); }
          .hint { margin: 0 0 10px; font-size: 12px; color: var(--muted); }
          .warn { margin: 10px 0; padding: 8px 10px; font-size: 12px;
            color: var(--warn); background: #FFF7ED; border: 1px solid #FED7AA; border-radius: 8px; }
          .modal-actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; }
          #qrCanvas { display: block; margin: 12px auto; background: #FFFFFF;
            image-rendering: pixelated; }
        </style>
        </head>
        <body>
        <div class="page">
        <header class="topbar">
          <h1>快递打包录像回放</h1>
          <div class="tools">
            <!-- ⚠️ 图上这一颗是按得动的，而订单联动要一个**至今未开工的服务端**
                 （母仓 docs/实现决策.md 记着，规格 §3.8 把它挂在 M6 上）。
                 所以这里禁用 + 悬停写明原因：渲染一颗按下去什么都不发生的按钮，
                 用户会以为是网络卡了，接着反复点。 -->
            <button class="tool-btn" disabled
              title="订单联动需要一个订单服务端把订单信息推给录像设备，而那个服务端还没开工。这一颗现在按下去不会有任何反应。">
              <b>安装订单联动</b>
              <small>群发订单信息到录像设备</small>
            </button>

            <!-- 齿轮 = 「播放兼容」（设计图 `_37`）。这一颗是真能打开的。 -->
            <button id="openCompat" class="icon-btn" title="播放兼容" aria-label="播放兼容">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6"
                   stroke-linecap="round">
                <circle cx="12" cy="12" r="3.4"/>
                <circle cx="12" cy="12" r="6.8"/>
                <path d="M12 2.6v4.6M12 16.8v4.6M2.6 12h4.6M16.8 12h4.6"/>
                <path d="M5.4 5.4l3.2 3.2M15.4 15.4l3.2 3.2M18.6 5.4l-3.2 3.2M8.6 15.4l-3.2 3.2"/>
              </svg>
            </button>

            <!-- ⚠️ 面板图标在图上对应的是那个产品的「侧栏/分栏」开关。
                 这一页只有一栏，没有可收起的第二栏 —— 禁用。 -->
            <button class="icon-btn" disabled
              title="这一页只有单栏布局，没有可以展开或收起的第二栏。">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6"
                   stroke-linejoin="round">
                <rect x="3.5" y="4.5" width="17" height="15" rx="2"/>
                <path d="M9.5 4.5v15"/>
              </svg>
            </button>

            <button id="refresh" class="icon-btn" title="重新查询" aria-label="重新查询">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6"
                   stroke-linecap="round" stroke-linejoin="round">
                <path d="M20.2 12a8.2 8.2 0 1 1-2.8-6.1"/>
                <path d="M20.5 4.4v4.2h-4.2"/>
              </svg>
            </button>

            <!-- ⚠️ 地球图标 = 界面语言。本仓只做中文（英文项禁用，见
                 docs/实现决策.md 里那条裁决），所以这一颗禁用 + 写明原因。 -->
            <button class="icon-btn" disabled
              title="这一页现在只有中文。界面语言切换在电脑端的设置里，而且英文还没做。">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6"
                   stroke-linecap="round">
                <circle cx="12" cy="12" r="8.5"/>
                <path d="M3.6 12h16.8"/>
                <path d="M12 3.6c2.6 2.7 2.6 14.1 0 16.8M12 3.6c-2.6 2.7-2.6 14.1 0 16.8"/>
              </svg>
            </button>

            <!-- ⚠️ **刻意加的一颗、图上没有入口的按钮**：设计图 `_38` 那个「用手机打开」
                 模态框在 `_36` 上找不到触发它的东西。功能是图上的，入口是我加的，
                 留档在 docs/实现决策.md §86。 -->
            <button id="openQr" class="tool-btn">
              <b>用手机打开</b>
              <small>扫二维码在手机上看</small>
            </button>
          </div>
        </header>

        <section class="cards">
          <div class="card">
            <h2>当前可追溯到</h2>
            <div id="statSpan" class="stat weak">读取中…</div>
            <div id="statSpanFoot" class="foot"></div>
          </div>
          <div class="card">
            <h2>预计可保留</h2>
            <div id="statRetention" class="stat weak">读取中…</div>
            <div id="statRetentionFoot" class="foot"></div>
          </div>
          <div class="card">
            <h2>存储空间</h2>
            <div id="statSpace" class="stat weak">读取中…</div>
            <div id="statSpaceFoot" class="foot"></div>
            <div class="bar"><i id="spaceBar"></i></div>
          </div>
        </section>

        <form class="card filters" id="f">
          <div class="field">
            <label for="from">开始日期</label>
            <input type="date" id="from">
          </div>
          <div class="field">
            <label for="to">结束日期</label>
            <input type="date" id="to">
          </div>
          <div class="field">
            <label for="source">录像来源</label>
            <select id="source"><option value="">全部设备</option></select>
          </div>
          <div class="field">
            <label for="q">订单号或文件名</label>
            <input type="text" id="q" placeholder="输入订单号关键词搜索" autocomplete="off">
          </div>
          <div class="field">
            <label for="mode">匹配方式</label>
            <select id="mode">
              <option value="exact">精确</option>
              <option value="prefix">前缀</option>
              <option value="contains">模糊</option>
            </select>
          </div>
          <div class="field">
            <label for="type">类型</label>
            <select id="type">
              <option value="">全部</option>
              <option value="outbound">发货</option>
              <option value="return">退货</option>
            </select>
          </div>
          <div class="field">
            <button type="submit" id="searchBtn">查询</button>
          </div>
        </form>

        <section class="card list-card">
          <div class="card-head">
            <h2>录像列表</h2>
            <div id="listSummary" class="foot"></div>
          </div>
          <table>
            <thead><tr>
              <th>类型</th><th>画面</th><th>录像</th><th>录制时间</th><th>时长</th><th>归档</th>
            </tr></thead>
            <tbody id="rows"></tbody>
          </table>
          <div id="empty" class="empty" hidden>
            <b>没有找到匹配的录像</b>
            <span>请调整日期范围或订单号关键词</span>
          </div>
        </section>

        <div id="punches"></div>
        <video id="player" controls playsinline></video>
        <!-- 录制规格的如实告知（规格 §3.1.7 的连带项）。默认藏着，点开一条 H.265 的才出现。 -->
        <div id="codecNote" class="note" hidden></div>
        <p class="note">
          本页由 VidLog 电脑端提供，仅供内网回放。<b>它不是交付渠道</b> ——
          要交给别人请用电脑端上的【导出原视频】（导出的是原件本身，不转码、不压缩），
          然后把那个文件发出去。这一页的视频来自本机副本，而本机副本可能已被生命周期清理。
        </p>
        </div>

        <!-- ── 「播放兼容」（设计图 `_37`）─────────────────────────────── -->
        <div id="compatModal" class="modal" hidden>
          <div class="modal-box">
            <h3>播放兼容</h3>

            <label class="radio">
              <input type="radio" name="compat" checked>
              <span>自动（推荐）</span>
            </label>
            <p class="hint">
              现在是这一条路：回放页把归档里的<b>原片</b>直接发给浏览器，<b>不转码</b>。
              H.265 的浏览器支持不一致 —— 放不出来时页面会提示你用系统播放器打开那个文件
              （规格 §3.1.7 的如实告知）。
            </p>

            <!-- ⚠️ 这两档**禁用**：本机不做转码（设计图上那一档要有转码链才有意义），
                 而「始终直连原片」就是上面「自动」现在做的事。按下去什么都不会变的开关，
                 比没有这个开关更糟（踩坑 #13）。 -->
            <label class="radio off">
              <input type="radio" name="compat" disabled>
              <span>始终转码为 H.264</span>
            </label>
            <p class="hint">本机不做转码 —— 库里存的是什么就发什么。这一档要有转码链才有意义，先禁用。</p>

            <label class="radio off">
              <input type="radio" name="compat" disabled>
              <span>始终直连原片</span>
            </label>
            <p class="hint">这一档就是「自动」现在唯一在做的事，选了也看不出区别。</p>

            <div class="modal-actions">
              <button id="closeCompat">关闭</button>
            </div>
          </div>
        </div>

        <!-- ── 「用手机打开」（设计图 `_38`）───────────────────────────── -->
        <div id="qrModal" class="modal" hidden>
          <div class="modal-box">
            <h3>用手机打开</h3>
            <p class="hint">手机与监控端连接同一个局域网后，扫描二维码即可打开录像网页。</p>
            <canvas id="qrCanvas" width="240" height="240"></canvas>
            <input id="qrUrl" type="text" readonly>
            <p id="qrProblem" class="hint" hidden></p>
            <p class="warn">
              这个网址<b>没有密码</b> —— 同一个局域网里谁拿到它，都能看到全部录像。
              请不要发给无关人员，也不要发到群里。它指向的又是这台电脑上的副本，
              副本会被生命周期清理，所以它也不该被当成一条长期有效的链接。
            </p>
            <div class="modal-actions">
              <button id="closeQr">关闭</button>
              <button id="copyUrl">复制网址</button>
            </div>
          </div>
        </div>

        <script>
        const rows = document.getElementById('rows');
        const player = document.getElementById('player');
        const punchBox = document.getElementById('punches');
        const empty = document.getElementById('empty');

        // 打点是**会话内偏移**，一次打包可能横跨多个分段文件 ——
        // 所以「跳到打点」要服务端换算成「哪个文件、第几秒」。
        async function loadPunches(evidenceId) {
          punchBox.innerHTML = '';
          const res = await fetch('/api/punches?evidenceId=' + encodeURIComponent(evidenceId));
          const targets = await res.json();

          if (targets.length === 0) {
            const span = document.createElement('span');
            span.className = 'muted';
            span.textContent = '这段录像没有打点';
            punchBox.appendChild(span);
            return;
          }

          const label = document.createElement('span');
          label.className = 'muted';
          label.textContent = '打点：';
          punchBox.appendChild(label);

          for (const t of targets) {
            const b = document.createElement('button');
            b.textContent = t.waybillNumber + ' · ' + Math.round(t.sessionOffsetSeconds) + 's';
            b.onclick = () => {
              player.style.display = 'block';
              player.src = '/media/' + encodeURIComponent(t.evidenceId);
              player.onloadedmetadata = () => {
                player.currentTime = t.offsetSeconds;
                player.play();
              };
            };
            punchBox.appendChild(b);
          }
        }

        function openRecording(evidenceId, codec) {
          player.style.display = 'block';
          player.src = '/media/' + encodeURIComponent(evidenceId);
          player.play();
          loadPunches(evidenceId);
          showCodecNote(codec);
        }

        // 发货 / 退货那两个字。服务端回的是枚举名（`Outbound` / `Return`），
        // 而界面上要写中文 —— 这个映射**只在这里**，与手机端那两处同一个口径。
        function businessLabel(type) {
          if (type === 'Outbound') return '发货';
          if (type === 'Return') return '退货';
          return '未标注';
        }

        // ⚠️ 规格 §3.1.7 的连带项：**浏览器对 H.265 的支持不一致**，
        // 所以网页**可能播不了 H.265 录的那条**。口径是「**如实告知**……**不得承诺
        // 做不到的事**，**不为此砍掉 H.265 选项**」—— 于是这里把话说出来，
        // 而不是让用户对着一个转圈的播放器自己猜。
        //
        // 只提一句「用系统播放器打开」：那是**肯定**能播的路径
        // （文件本来就在这台机器上），不承诺浏览器能播。
        function showCodecNote(codec) {
          const note = document.getElementById('codecNote');
          if (codec === 'H265') {
            note.textContent = '这条录像是 H.265 编码。浏览器对它的支持不一致 —— '
              + '要是这里放不出来，请用系统播放器打开这个文件（在上面那台电脑上，'
              + '或者把它下载下来）。录像本身没问题。';
            note.hidden = false;
          } else {
            note.hidden = true;
          }
        }

        // ── 三张统计卡（设计图 `_36`）──────────────────────────────────

        // 字节 → 「GB」。⚠️ 只取一位小数：这三张卡上的数**都是估算的**
        // （底下是 CleanupPlanner 那套系数），印成 `12.3456789GB`
        // 会让用户以为它是量出来的一个精确值。
        function gb(bytes) {
          const v = (bytes || 0) / (1024 * 1024 * 1024);
          return (v >= 100 ? Math.round(v) : Math.round(v * 10) / 10) + 'GB';
        }

        function dayText(iso) {
          const d = new Date(iso);
          return d.getFullYear() + '-'
            + String(d.getMonth() + 1).padStart(2, '0') + '-'
            + String(d.getDate()).padStart(2, '0');
        }

        async function loadOverview() {
          const o = await (await fetch('/api/overview')).json();

          // ① 当前可追溯到
          const span = document.getElementById('statSpan');
          const spanFoot = document.getElementById('statSpanFoot');
          if (o.earliest) {
            const days = Math.max(0, Math.floor((Date.now() - new Date(o.earliest).getTime()) / 86400000));
            span.textContent = dayText(o.earliest) + ' 起（' + days + ' 天）';
            spanFoot.textContent = '库里一共 ' + o.count + ' 条录像，' + o.waybillCount + ' 个单号';
          } else {
            span.textContent = '暂无录像数据';
            spanFoot.textContent = '当前存储库未找到可用录像';
          }

          // ② 预计可保留 —— **算不出来时报「暂无法估算」，绝不编一个数**
          //（规格 §13.1：不能给用户一个看起来像事实的猜测）。
          const ret = document.getElementById('statRetention');
          const retFoot = document.getElementById('statRetentionFoot');
          if (o.retentionDays === null) {
            ret.textContent = '暂无法估算';
            retFoot.textContent = '历史数据不足或平均每日占用为 0';
          } else {
            ret.textContent = '约 ' + Math.floor(o.retentionDays) + ' 天';
            retFoot.textContent = '按最近 ' + o.retentionWindowDays
              + ' 天的平均占用估算，剩余空间用尽之前';
          }

          // ③ 存储空间
          document.getElementById('statSpace').textContent =
            '已用约 ' + gb(o.estimatedUsedBytes) + ' / ' + gb(o.totalBytes);
          document.getElementById('statSpaceFoot').textContent =
            '共 ' + o.directoryCount + ' 个存储目录（' + o.volumeCount + ' 块盘），剩余 '
            + gb(o.freeBytes);
          const pct = o.totalBytes > 0
            ? Math.min(100, (o.estimatedUsedBytes / o.totalBytes) * 100)
            : 0;
          document.getElementById('spaceBar').style.width = pct.toFixed(1) + '%';

          // ④ 「录像来源」下拉。
          //
          // ⚠️ `option.value` 用的是**原样值**（`source.id`）而不是给人看的那几个字
          // —— 服务端比的是索引里存的那个串，拿「外部导入」去筛会一条都筛不出来。
          const sel = document.getElementById('source');
          const kept = sel.value;
          sel.length = 1;
          for (const s of o.sources) {
            const opt = document.createElement('option');
            opt.value = s.id;
            opt.textContent = s.label + '（' + s.count + '）';
            sel.appendChild(opt);
          }
          sel.value = kept;
          // 只有一处来源时把它禁掉：一颗只有一个可选项的下拉没有信息量，
          // 而它占着筛选区一整格。**不禁用整格**，值仍然会跟着查询发出去。
          sel.disabled = o.sources.length <= 1;
        }

        async function run() {
          const p = new URLSearchParams();
          const q = document.getElementById('q').value.trim();
          if (q) p.set('q', q);
          p.set('mode', document.getElementById('mode').value);
          const t = document.getElementById('type').value;
          if (t) p.set('type', t);
          const src = document.getElementById('source').value;
          if (src) p.set('source', src);
          const from = document.getElementById('from').value;
          if (from) p.set('from', from + 'T00:00:00');
          const to = document.getElementById('to').value;
          if (to) p.set('to', to + 'T23:59:59');

          const res = await fetch('/api/search?' + p);
          const items = await res.json();

          rows.innerHTML = '';
          for (const it of items) {
            const tr = document.createElement('tr');
            tr.className = 'hit';
            // 规格 §3.4.3 的七项：标签 / 缩略图+播放 / `单号.mp4` / 时间 / 时长 / 归档。
            tr.innerHTML = `<td></td><td></td><td></td><td></td><td></td><td></td>`;

            // ① 标签（发货 / 退货）
            tr.children[0].textContent = businessLabel(it.businessType);

            // ② 缩略图 + ③ 播放按钮（**同一格**：缩略图就是播放入口）
            const thumbCell = tr.children[1];
            const img = document.createElement('img');
            img.className = 'thumb';
            img.alt = '';
            img.src = '/api/thumbnail/' + encodeURIComponent(it.evidenceId);
            // ⚠️ 404（还没抽过帧）时**把那格清空**，而不是显示一个浏览器的破图图标 ——
            // 破图图标看起来像「这一段坏了」，而其实只是还没生成缩略图。
            img.onerror = () => { img.remove(); };
            thumbCell.appendChild(img);

            // ④ 列表里显示的名字：`单号.mp4`
            //
            // ⚠️ **这只是显示名**：磁盘上的文件名与归档路径一律不动
            //（改了会波及索引、检索、归档回查，还要迁移已经录好的那些）。
            tr.children[2].textContent = it.waybill ? (it.waybill + '.mp4') : '（无单号）';

            // ⑤ 录制时间 ⑥ 时长
            tr.children[3].textContent = new Date(it.startedAt).toLocaleString();
            tr.children[4].textContent = Math.round(it.durationSeconds) + ' 秒';

            // ⑦ 归档层这一份在哪儿 —— **本机磁盘那一档要明说「仅本机」**：
            // 那时盘上这份是唯一副本（规格 §3.5.1），用户必须知道。
            tr.children[5].textContent = it.archiveLabel;

            tr.onclick = () => openRecording(it.evidenceId, it.codec);
            tr.title = '点这一行播放';
            rows.appendChild(tr);
          }

          // 空状态照设计图：表格收起来，换成那两句话。
          empty.hidden = items.length > 0;
          document.querySelector('.list-card table').hidden = items.length === 0;
          document.getElementById('listSummary').textContent =
            items.length === 0 ? '没有找到匹配记录' : '共 ' + items.length + ' 条';
        }

        // ── 「播放兼容」弹出框（设计图 `_37`）──────────────────────────
        const compatModal = document.getElementById('compatModal');
        document.getElementById('openCompat').onclick = () => { compatModal.hidden = false; };
        document.getElementById('closeCompat').onclick = () => { compatModal.hidden = true; };
        compatModal.onclick = e => { if (e.target === compatModal) compatModal.hidden = true; };

        // ── 「用手机打开」的二维码（设计图 `_38`）─────────────────────
        const qrModal = document.getElementById('qrModal');
        const qrCanvas = document.getElementById('qrCanvas');
        const qrUrlBox = document.getElementById('qrUrl');
        const copyBtn = document.getElementById('copyUrl');

        document.getElementById('openQr').onclick = async () => {
          qrModal.hidden = false;

          const qr = await (await fetch('/api/qr')).json();
          const problem = document.getElementById('qrProblem');

          if (!qr.url) {
            // ⚠️ 挑不到局域网地址时**画不出来就别画**：
            // 一张扫不出来的假码比一句实话糟糕得多。
            qrCanvas.hidden = true;
            qrUrlBox.value = '';
            copyBtn.disabled = true;
            problem.textContent = qr.problem || '没挑到局域网地址。';
            problem.hidden = false;
            return;
          }

          qrCanvas.hidden = false;
          problem.hidden = true;
          copyBtn.disabled = false;
          qrUrlBox.value = qr.url;

          // ⚠️ 逐模块画方块、**不做任何缩放插值**。二维码一糊就变成
          // 「看着正常、扫不出来」—— 那是最难排查的一种坏法
          //（`EnrollWindow` 的 NearestNeighbor 那条红线是同一个道理）。
          const scale = Math.max(2, Math.floor(280 / qr.width));
          qrCanvas.width = qr.width * scale;
          qrCanvas.height = qr.height * scale;
          qrCanvas.style.width = '240px';
          qrCanvas.style.height = '240px';

          const ctx = qrCanvas.getContext('2d');
          ctx.fillStyle = '#FFFFFF';
          ctx.fillRect(0, 0, qrCanvas.width, qrCanvas.height);
          ctx.fillStyle = '#000000';

          for (let y = 0; y < qr.rows.length; y++) {
            const line = qr.rows[y];
            for (let x = 0; x < line.length; x++) {
              if (line[x] === '1') {
                ctx.fillRect(x * scale, y * scale, scale, scale);
              }
            }
          }
        };

        document.getElementById('closeQr').onclick = () => { qrModal.hidden = true; };
        qrModal.onclick = e => { if (e.target === qrModal) qrModal.hidden = true; };

        copyBtn.onclick = async () => {
          try {
            if (navigator.clipboard) {
              await navigator.clipboard.writeText(qrUrlBox.value);
            } else {
              // ⚠️ 这一页走 http（不是 https），浏览器多半不给 clipboard API ——
              // 退回选中 + execCommand，那条路在 http 下仍然能用。
              qrUrlBox.select();
              document.execCommand('copy');
            }
            copyBtn.textContent = '已复制';
          } catch (err) {
            // 复制不上时**别装作复制上了**（地址就在旁边的框里，让用户自己选中）。
            copyBtn.textContent = '复制不了，请手动选中';
            qrUrlBox.select();
          }
          setTimeout(() => { copyBtn.textContent = '复制网址'; }, 2000);
        };

        document.getElementById('refresh').onclick = () => { loadOverview(); run(); };
        document.getElementById('f').addEventListener('submit', e => { e.preventDefault(); run(); });

        loadOverview();
        run();
        </script>
        </body>
        </html>
        """;

}

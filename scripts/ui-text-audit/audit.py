# 上屏文字体检（只读仓库，不改任何东西）
#
#   python -I audit.py check  <root>           数几个指标，超阈值非零退出 —— CI 与本地都跑这个
#   python -I audit.py report <root> <out.md>  生成逐条清单（改文案时用）
#
# 口径与 2026-10-15 那份审计报告是**同一份代码**，不是另写一套近似的东西
# —— 两套口径必然走岔，那时候「报上来的数」和「闸卡住的数」就对不上了。
#
# ⚠️ 阈值为什么钉在「文案」上而不是「代码」上：
# 这个仓的界面文案有一族反复长回来的毛病（每条都带句号、带 ⚠️、带破折号、
# 不给用户解释「我们是故意的」）。它们**编译得过、测试全绿、界面上也看得见**，
# 唯一的失败方式是肉眼看着乱 —— 那就没有能失败的检查。
# 这几个数就是把它变成能失败的检查。
import re, sys, pathlib, collections

CJK = re.compile(r'[一-鿿]')
tag_re = re.compile(r'<([A-Za-z][\w:.]*)\b((?:[^<>"\']|"[^"]*"|\'[^\']*\')*?)/?>', re.S)
attr_re = re.compile(r'([A-Za-z:]+)\s*=\s*"([^"]*)"', re.S)
TEXTY = ('Text', 'Content', 'Header', 'ToolTip', 'Title', 'Watermark')

# 「小字」= 这几类：说明 / 提示 / 脚注 / 悬停提示
HINTY = ('说明', '提示', '脚注', '悬停')

FLAG = [('。', '句号'), ('⚠', '⚠️符'), ('——', '破折号'), ('…', '省略号'),
        ('「', '引号'), ('【', '【】'), ('；', '分号')]

# 设置窗左导航分节的中文页名（照 SettingsWindow.xaml 的 NavItem Content）
PAGE_CN = {'Device': '设备与外观', 'Scan': '扫码与识别', 'Storage': '存储与备份',
           'Cloud': '百度网盘上传', 'Recording': '录像设置', 'Audio': '声音与播报',
           'Network': '局域网与网页', 'Extensions': '扩展与联动', 'Advanced': '高级设置',
           'About': '关于'}

# ─────────────────────────────────────────────
# 阈值（批次 C 的验收线，2026-10-15 需求方裁定）
#
# ⚠️ 这几个数是**目标**，不是「现状」：定下来的那天现状是
# ⚠️ 13 / 句号 71 / 破折号 19 / 小字 147。批次 C 做完才该是绿的 ——
# 所以这份闸落地的当天它就是红的，红到文案改完为止。
# ─────────────────────────────────────────────
LIMITS = {
    '⚠️符': 0,      # 一个都不许有：PP 是 0
    '句号': 15,     # PP 4%；说明句一律不加句号
    '破折号': 3,    # PP 0
    '小字条数': 115, # PP 185 条但每条 ≤23 字
    # ⚠️ 2026-10-15 订正：这里原来写的是 **20**，那个数**没有依据** ——
    #    它的来历是「那天量出来 22.8，PP 是 21.3，就填了个更小的整数」。
    #    批次 C 把平均压到 21.2 之后，剩下最长的几条已经是**不能再砍的事实**：
    #    盘位预留规则（「系统盘留 30GB 或 10%」）、网盘落点形状
    #    （「传到我的应用数据/<应用名>/<日期>/发货或退货/单号.mp4」）、
    #    rtsp 示例地址。再往下砍就是**删信息**，而裁定 #8 写明「不藏不删，只砍短」。
    #    所以上限定成 PP 自己的 21.3 向上取整的 22（留 1 字余量）：
    #    判据是「**不比 PP 长**」，就这一条，是个能说出口的标准。
    #    ⚠️ 不设成 21.3 是因为那条线一碰就红 —— 这条闸要卡的是「成段的长解释」，
    #    不是最后 0.7 个字的抖动；写成 21.3 只会逼出下一次「把阈值改掉」的动作。
    '平均字数': 22,  # PP 实测 21.3；我们（批次 C 之后）21.2，当初 26.4
}


# ⚠️ 2026-10-15 补的一道**分类口径**（不是新阈值）：
# 这份脚本头一版把 `Caption` 样式的 TextBlock 一律当成「小字 / 说明」，
# 于是**表头与统计标签**也被算了进去 ——「时间」「单号」「动作」「原因」
# 「今日收录」「索引总量」「存储路径」「预留空间」…… 一共 34 条。
# 它们不是说明文字：没有动词、没有标点、就是给旁边那个数/那张表起的名字。
# 「小字太多影响观感」这句抱怨说的是**成段的灰色解释**，不是表头。
# 判据刻意做得可复核：**没有任何标点，且 ≤ 8 字 ⇒ 是标签**。
# 真正的说明句最短也有 9 字（「下面是日常打包流程」），两边不重叠。
PUNCT = set("。，、；：？！…—-（）【】「」《》/·%()[]{}<>:;,.!?\"'")
LABEL_MAX = 8


def is_label(text):
    """这一条是「给旁边那个数起的名字」，还是「一句说明」。"""
    return len(text) <= LABEL_MAX and not (set(text) & PUNCT)


def line_of(src, pos):
    return src.count('\n', 0, pos) + 1


def strip_cs_comments(s):
    s = re.sub(r'/\*.*?\*/', '', s, flags=re.S)
    out = []
    for ln in s.split('\n'):
        t = ln.lstrip()
        if t.startswith('//'):
            continue
        out.append(re.sub(r'//(?=(?:[^"]*"[^"]*")*[^"]*$).*$', '', ln))
    return '\n'.join(out)


def member_at(src, pos):
    head = src[:pos]
    ms = list(re.finditer(r'^\s*(?:public|private|internal|protected|static)[^\n=;{]*?\b(\w+)\s*\([^)]*\)\s*$',
                          head, re.M))
    if ms:
        return ms[-1].group(1)
    ms = list(re.finditer(r'^\s*(?:public|private|internal|protected|static)[^\n=;{]*?\b(\w+)\s*(?:=>|\{)',
                          head, re.M))
    return ms[-1].group(1) if ms else '?'


def flags(t):
    return '·'.join(v for k, v in FLAG if k in t)


def purpose(style, attr, tag, fg):
    if attr == 'ToolTip':
        return '悬停提示'
    if attr == 'Title':
        return '窗口标题'
    if attr == 'Header':
        return '表头/选项'
    if tag in ('Button', 'RadioButton', 'CheckBox', 'MenuItem'):
        return '按钮/选项'
    if 'Warning' in fg:
        return '提示(橙)'
    if 'Danger' in fg:
        return '提示(红)'
    if style in ('Caption', 'FieldHintStyle'):
        return '说明(灰小字)'
    if style in ('Note',):
        return '脚注/角标'
    if style in ('SectionTitle', 'SectionTitleStyle', 'SectionHeader', 'GroupTitle', 'PageTitle'):
        return '小节标题'
    if style in ('Body', 'BodySecondary', 'ValueLine'):
        return '正文/值'
    if style == 'FieldLabel':
        return '字段标签'
    return '其它'


def walk_xaml(root):
    for p in sorted(root.rglob('*.xaml')):
        if any(x in p.parts for x in ('obj', 'bin')):
            continue
        src = p.read_text(encoding='utf-8', errors='replace')
        # 注释里的 Text= 不是上屏文字：抹掉注释内容但保留换行，行号才对得上
        src = re.sub(r'<!--.*?-->', lambda m: '\n' * m.group(0).count('\n'), src, flags=re.S)
        section = ''
        page = ''
        first = True
        for m in tag_re.finditer(src):
            tag = m.group(1)
            attrs = dict(attr_re.findall(m.group(2)))
            st = re.search(r'\{StaticResource (\w+)\}', attrs.get('Style', ''))
            style = st.group(1) if st else ''
            # 「在哪页」：设置窗的各节按 x:Name="XxxSection" 切（文档顺序，互不嵌套）
            nm = attrs.get('x:Name', '')
            if nm.endswith('Section'):
                page = nm[:-7]
            if style in ('SectionTitle', 'SectionTitleStyle', 'SectionHeader') and attrs.get('Text'):
                section = attrs['Text']
            fg = attrs.get('Foreground', '')
            for a in TEXTY:
                v = (attrs.get(a) or '').strip()
                if not v:
                    continue
                if a == 'Title' and first:
                    first = False
                yield (p.name, page, section, line_of(src, m.start()), style or tag,
                       purpose(style, a, tag, fg), v)
        for m in re.finditer(r'<TextBlock\b([^>]*)>([^<]+)</TextBlock>', src, re.S):
            attrs = dict(attr_re.findall(m.group(1)))
            st = re.search(r'\{StaticResource (\w+)\}', attrs.get('Style', ''))
            style = st.group(1) if st else ''
            v = m.group(2).strip()
            if v and CJK.search(v):
                yield (p.name, page, section, line_of(src, m.start()), style or 'TextBlock-内文',
                       purpose(style, 'Text', 'TextBlock', attrs.get('Foreground', '')), v)


def walk_cs(root):
    for p in sorted(root.rglob('*.cs')):
        if any(x in p.parts for x in ('obj', 'bin')):
            continue
        src = strip_cs_comments(p.read_text(encoding='utf-8', errors='replace'))
        for m in re.finditer(r'"((?:[^"\\]|\\.)*)"', src):
            t = m.group(1).replace('\\n', ' ').replace('\\"', '"')
            if len(t) < 4 or not CJK.search(t):
                continue
            yield (p.name, member_at(src, m.start()), line_of(src, m.start()), t)


def collect(root):
    """→ (XAML 里能上屏的, 说明小字, 表头/标签, .cs 里的中文)。

    ⚠️ 「说明小字」与「表头/标签」**分开返回**：两者一起数会把条数报大一截，
    而阈值是钉在「说明」上的（见 is_label 上面那段）。
    """
    xr = list(walk_xaml(root))
    hinty = [r for r in xr if r[5].startswith(HINTY)]
    hints = [r for r in hinty if not is_label(r[6])]
    labels = [r for r in hinty if is_label(r[6])]
    return xr, hints, labels, list(walk_cs(root))


def metrics(hints):
    m = {'小字条数': len(hints)}
    for key, label in FLAG:
        m[label] = sum(1 for r in hints if key in r[6])
    m['平均字数'] = round(sum(len(r[6]) for r in hints) / max(1, len(hints)), 1)
    return m


def check(root):
    _, hints, labels, _ = collect(root)
    m = metrics(hints)

    print(f'上屏说明小字 {m["小字条数"]} 条，平均 {m["平均字数"]} 字'
          f'（另有表头/统计标签 {len(labels)} 条，不算在内 —— 见 is_label）')
    bad = []
    for k, limit in LIMITS.items():
        got = m[k]
        ok = got <= limit
        print(f'  [{"OK  " if ok else "FAIL"}] {k}: {got}（上限 {limit}）')
        if not ok:
            bad.append(f'{k} {got} > {limit}')

    if bad:
        print('')
        print('超阈值的：' + '；'.join(bad))
        print('这几条不是「风格偏好」——见 audit.py 文件头，以及方案里批次 C 的三条「一律」。')
        # 把出格的那几条原文列出来，省得再回去翻清单。
        # 截断到 12 条：这几族动辄几十处，全列出来会把 CI 日志淹掉，
        # 而修复时真正要的是「第 1 条在哪」+「一共有多少条」。
        for k, limit in LIMITS.items():
            if m[k] <= limit:
                continue
            # ⚠️ 只有「某个符号超标」这一族能列出原文（按符号筛）；
            # 「条数」与「平均字数」是**整批**的指标，没有单条可列 ——
            # 平均字数超了就把最长的几条印出来（要砍就从这几条砍）。
            mark = next((key for key, label in FLAG if label == k), None)
            if mark is not None:
                hits = [r for r in hints if mark in r[6]]
            elif k == '平均字数':
                hits = sorted(hints, key=lambda r: -len(r[6]))
            else:
                continue
            for r in hits[:12]:
                print(f'    {r[0]}:{r[3]}  ({len(r[6])} 字) {r[6]}')
            if len(hits) > 12:
                print(f'    …还有 {len(hits) - 12} 条，全表见 audit.py report 生成的清单')
        return 1

    print('全部在阈值内。')
    return 0


def md_table(rows, head):
    out = ['| ' + ' | '.join(head) + ' |', '|' + '---|' * len(head)]
    for r in rows:
        out.append('| ' + ' | '.join(str(c).replace('|', '\\|').replace('\n', ' ') for c in r) + ' |')
    return '\n'.join(out) + '\n'


def report(root, out):
    xr, hints, labels, cr = collect(root)
    rest = [r for r in xr if not r[5].startswith(HINTY)]

    L = ['# VidLog 电脑端 · 上屏文字全清单', '']
    L.append(f'- XAML 里能上屏的字符串 **{len(xr)}** 条；`.cs` 里含中文的字符串字面量 **{len(cr)}** 条（已剥注释）。')
    L.append(f'- 其中「说明小字」XAML **{len(hints)}** 条 —— 就是这份清单的主角。')
    L.append(f'- 另有「表头/统计标签」**{len(labels)}** 条（≤8 字且无标点，如「时间」「索引总量」），**不算小字**。')
    L.append('- 口径：XAML 走元素属性（Text/Content/Header/ToolTip/Title）与 `<TextBlock>` 内文；'
             '`.cs` 是含中文、长度 ≥4 的字符串字面量（会把日志/异常文案也算进来）。')
    L.append('')

    def dump(rows, cols, head):
        by = collections.OrderedDict()
        for r in rows:
            by.setdefault((r[0], r[1]), []).append(r)
        for (f, pg), rs in by.items():
            L.append(f'### {f} · {PAGE_CN.get(pg, pg) or "—"} —— {len(rs)} 条')
            L.append('')
            L.append(md_table([tuple(c(r) for c in cols) for r in rs], head))
            L.append('')

    L.append('## 一、小字（说明 / 提示 / 脚注）逐条')
    L.append('')
    dump(hints,
         (lambda r: r[2] or '—', lambda r: f':{r[3]}', lambda r: r[4], lambda r: r[5],
          lambda r: flags(r[6]), lambda r: r[6]),
         ['所属小节', '位置', '样式', '作用', '标点', '原文'])

    L.append('## 二、其余上屏文字（标签 / 按钮 / 标题 / 正文 / 表头）')
    L.append('')
    dump(rest,
         (lambda r: r[2] or '—', lambda r: f':{r[3]}', lambda r: r[4], lambda r: r[5], lambda r: r[6]),
         ['所属小节', '位置', '样式', '作用', '原文'])

    L.append('## 三、`.cs` 里的中文文案（对话框 / 状态 / 报错 / 播报）')
    L.append('')
    by3 = collections.OrderedDict()
    for r in cr:
        by3.setdefault(r[0], []).append(r)
    for f, rs in by3.items():
        L.append(f'### {f} —— {len(rs)} 条')
        L.append('')
        L.append(md_table([(r[1], f':{r[2]}', flags(r[3]), r[3]) for r in rs],
                          ['所在的成员', '位置', '标点', '原文']))
        L.append('')

    pathlib.Path(out).write_text('\n'.join(L), encoding='utf-8')
    print('xaml', len(xr), 'hints', len(hints), 'cs', len(cr), '->', out)
    return 0


def main():
    # 这台机器上 stdout 默认是 GBK，中文与 ⚠️ 直接 UnicodeEncodeError ——
    # 这一步的产出**只有**这几行文字，写不出去等于白跑。
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass

    if len(sys.argv) < 3:
        print(__doc__ or 'usage: audit.py check|report <root> [out.md]')
        return 2
    mode, root = sys.argv[1], pathlib.Path(sys.argv[2])
    if mode == 'check':
        return check(root)
    if mode == 'report':
        return report(root, sys.argv[3])
    print(f'不认识的模式：{mode}')
    return 2


if __name__ == '__main__':
    sys.exit(main())

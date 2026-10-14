namespace Parrot.Core.TextProcessing;

/// <summary>一页 OCR 的产出：句尾发音热点 + 原图遮挡的释义列边界（页宽分数；null = 推算不出，不盖）。
/// MaskRows = 罩层覆盖区的整行框（页坐标 pt），悬停"整行露出"用；文本层页按词框、OCR 页按行框推算。
/// MaskBand = 罩层的竖直内容带（页高分数，y-up：Top 大 = 靠上），罩层只盖正文、页眉页码留在外面；null = 盖整高。</summary>
public sealed record PageOcrData(
    IReadOnlyList<SpeakSpot> Spots, double? MaskLeftFraction,
    IReadOnlyList<PdfRowBox>? MaskRows = null,
    (double TopFraction, double BottomFraction)? MaskBand = null,
    IReadOnlyList<(double Top, double Bottom)>? TitleSpans = null);

/// <summary>页面上检出的一条水平线（讲义表格的分隔线，pt 页坐标 y-up）。
/// Top/Bottom = 线的竖直带（厚 ≤4px），Left/Right = 横向范围；Left 深居主词列的是词条分隔线。</summary>
public sealed record MaskRule(double Top, double Bottom, double Left, double Right);

/// <summary>
/// 原图遮挡模式的边界推算：从"释义列起始 X"盖到页右缘，主词列留在左边。
/// 依据 = OCR 行框里以中文字符开头的行（释义/例句译文的行首特征，英文词头行天然进不来），
/// 按 8pt 桶取众数——众数能抗少量离群（页眉水印、词头列下的【频次】标签）；
/// 候选太少或结果落在可疑区间就放弃：宁可不盖，也不把该看着回忆的主词盖住。
/// 主词与释义同行的"行内式"书推不出竖直边界（返回 null），这类书由文本视图的遮挡负责。
/// </summary>
public static class MaskLayout
{
    public static double? ComputeMaskLeftFraction(
        IReadOnlyList<PdfLine> lines, double pageWidthPt, double pageHeightPt)
    {
        if (pageWidthPt <= 0 || pageHeightPt <= 0 || lines.Count == 0) return null;
        double topKeep = pageHeightPt * 0.94, bottomKeep = pageHeightPt * 0.06; // 页眉水印/页码不参与
        var xs = new List<double>();
        foreach (var ln in lines)
        {
            if (ln.Top > topKeep || ln.Top < bottomKeep) continue;
            if (!StartsCjk(ln.Text)) continue;
            xs.Add(ln.Left);
        }
        if (xs.Count < 3) return null;

        double modeX = xs.GroupBy(x => Math.Round(x / 8))
            .OrderByDescending(g => g.Count())
            .First()
            .Average(); // 同桶内取均值，抹平桶内抖动
        double f = (modeX - 6) / pageWidthPt; // 边界略往左收一点，别切到释义首字
        return f is >= 0.12 and <= 0.70 ? f : null;
    }

    /// <summary>
    /// 罩层覆盖区的"整行"框（悬停露词以行为单位，露就露整行）：边界右侧有词的行收一个框，
    /// 左缘裁到罩层边界（别把罩外的主词列带出来），上下取整行文字带。仅文本层页有词框。
    /// </summary>
    public static IReadOnlyList<PdfRowBox>? ComputeMaskRows(
        IReadOnlyList<PdfLine> lines, double? maskLeftFraction, double pageWidthPt)
    {
        if (maskLeftFraction is not { } f || pageWidthPt <= 0) return null;
        double boundary = f * pageWidthPt;
        double body = LayoutBuilder.BodyFontSize(lines);
        var rows = new List<PdfRowBox>();
        foreach (var ln in lines)
        {
            if (ln.Words is null) continue;
            if (!IsMaskContentLine(ln, boundary, body)) continue; // 跨栏大标题不进罩层行
            List<PdfWord>? covered = null;
            foreach (var w in ln.Words)
                if ((w.Left + w.Right) / 2 >= boundary)
                    (covered ??= new List<PdfWord>()).Add(w);
            if (covered is null) continue;
            rows.Add(new PdfRowBox(
                string.Join(' ', covered.Select(w => w.Text)),
                Math.Max(covered.Min(w => w.Left), boundary),
                covered.Max(w => w.Right),
                ln.Top, ln.Bottom));
        }
        return rows.Count > 0 ? rows : null;
    }

    /// <summary>
    /// OCR 页的"整行"框：无词框，按"行右缘伸进罩层"判被盖行（主词与释义同行合并的 OCR 行也算），
    /// 左缘同样裁到罩层边界，上下取整行文字带。
    /// </summary>
    public static IReadOnlyList<PdfRowBox>? ComputeOcrMaskRows(
        IReadOnlyList<PdfLine> lines, double? maskLeftFraction, double pageWidthPt)
    {
        if (maskLeftFraction is not { } f || pageWidthPt <= 0) return null;
        double boundary = f * pageWidthPt;
        double body = LayoutBuilder.BodyFontSize(lines);
        var rows = new List<PdfRowBox>();
        foreach (var ln in lines)
        {
            if (ln.Right <= boundary + 1) continue; // 没伸进罩层的行（主词列/频次标签/空白）
            if (!IsMaskContentLine(ln, boundary, body)) continue; // Unit 标题等跨栏大标题不进罩层行
            rows.Add(new PdfRowBox(ln.Text, Math.Max(ln.Left, boundary), ln.Right, ln.Top, ln.Bottom));
        }
        return rows.Count > 0 ? rows : null;
    }

    /// <summary>
    /// 罩层分块：把被盖行归并成若干覆盖块，块与内容带求交（页眉页码不进块）。
    /// 空档只在"含标题行"处切开露出（换单元的 Unit 标题、跨栏节标题整块留白）；
    /// 其余空档一律照盖——思维导图等无文字行的大空档盖住，别把答案图漏给回忆中的用户。
    /// 返回 (Top, Bottom) 页坐标 pt（y-up），自上而下。
    /// </summary>
    public static IReadOnlyList<(double Top, double Bottom)> ComputeCoverBlocks(
        IReadOnlyList<PdfRowBox> rows, (double TopFraction, double BottomFraction)? band,
        double pageHeightPt, IReadOnlyList<(double Top, double Bottom)>? titleSpans = null, double maxGapPt = 12)
    {
        double topPt = band is { } bd ? bd.TopFraction * pageHeightPt : double.PositiveInfinity;
        double botPt = band is { } bd2 ? bd2.BottomFraction * pageHeightPt : double.NegativeInfinity;
        var merged = new List<(double Top, double Bottom)>();
        foreach (var r in rows.OrderBy(r => r.Bottom)) // y-up：自下而上归并
        {
            double t = Math.Min(r.Top, topPt), b = Math.Max(r.Bottom, botPt);
            if (t - b <= 1) continue; // 与内容带无交集（页眉/页码行）或退化块
            if (merged.Count > 0 && b - merged[^1].Top <= maxGapPt)
            {
                MergeUp(t);
                continue;
            }
            // 空隙 = 本行底 到 块顶（y-up）；±3pt 容差吸收 OCR/取行抖动
            bool titleInGap = merged.Count > 0 && titleSpans is { Count: > 0 }
                && titleSpans.Any(s => s.Bottom >= merged[^1].Top - 3 && s.Top <= b + 3);
            if (merged.Count > 0 && !titleInGap)
            {
                MergeUp(t); // 空档里没有标题行：再大也盖（思维导图/大留白不是要回忆的分界）
                continue;
            }
            merged.Add((t, b));
        }
        merged.Reverse(); // 自下而上归并，返回自上而下
        return merged;

        void MergeUp(double t)
        {
            var (pt, pb) = merged[^1];
            merged[^1] = (Math.Max(pt, t), pb);
        }
    }

    /// <summary>
    /// 罩层里应当露出的"标题行"竖直区间（页坐标 pt，y-up）：单元小标题（Unit 11——在主词列
    /// 够不到罩层）与跨栏大标题（上策词（高频词）——伸进罩层但字号大、起点深居主词列，
    /// 进不了罩层行）。给 ComputeCoverBlocks 当切分依据；页眉/页码仍由保留带排除在外。
    /// </summary>
    public static IReadOnlyList<(double Top, double Bottom)> ComputeTitleSpans(
        IReadOnlyList<PdfLine> lines, double? maskLeftFraction, double pageWidthPt, double pageHeightPt)
    {
        var spans = new List<(double Top, double Bottom)>();
        if (maskLeftFraction is not { } f || pageWidthPt <= 0 || pageHeightPt <= 0 || lines.Count == 0)
            return spans;
        double boundary = f * pageWidthPt;
        double topKeep = pageHeightPt * 0.94, bottomKeep = pageHeightPt * 0.06;
        double body = LayoutBuilder.BodyFontSize(lines);
        foreach (var ln in lines)
        {
            if (ln.Top > topKeep || ln.Top < bottomKeep) continue; // 页眉水印/页码
            bool unit = OverlayLayout.IsUnitTitle(ln.Text);
            bool banner = !unit && ReachesMask(ln, boundary) && !IsMaskContentLine(ln, boundary, body);
            if (unit || banner) spans.Add((ln.Top, ln.Bottom));
        }
        return spans;
    }

    /// <summary>
    /// 从渲染位图检出水平分隔线（讲义是"主词条表格"版式：全宽横线围出词条框，框内短线分隔释义/真题/词形联想小节）。
    /// 逐行扫暗像素游程（≥ 30pt），纵向 ≤3px 合并、厚度 ≤4px 的才是线——填充色块（思维导图节点 16px）与
    /// 文字行的碎游程都被排除。两种书通吃：文本层渲染与扫描件里线都是可见像素，扫描倾斜在 1px 线宽下不影响。
    /// 返回 pt 页坐标（y-up）自上而下；ppp = 每 pt 像素数（dpi/72）。
    /// </summary>
    public static IReadOnlyList<MaskRule> DetectUnderlines(
        byte[] bgra, int widthPx, int heightPx, int rowBytes, double pixelsPerPoint)
    {
        var rules = new List<MaskRule>();
        if (widthPx <= 0 || heightPx <= 0 || rowBytes < widthPx * 4 || pixelsPerPoint <= 0) return rules;
        int minRunPx = Math.Max(40, (int)(30 * pixelsPerPoint));

        // 每行收集暗像素长游程
        var runs = new List<(int Y, int X0, int X1)>();
        for (int y = 0; y < heightPx; y++)
        {
            int baseOff = y * rowBytes;
            int x = 0;
            while (x < widthPx)
            {
                if (IsDark(baseOff, x))
                {
                    int s = x;
                    while (x < widthPx && IsDark(baseOff, x)) x++;
                    if (x - s >= minRunPx) runs.Add((y, s, x - 1));
                }
                else x++;
            }
        }
        if (runs.Count == 0) return rules;

        // 纵向合并相邻行（≤3px 且横向搭接）成线候选
        var cands = new List<(int YTop, int YBot, int X0, int X1)>();
        foreach (var r in runs) // runs 本就按 y 产出
        {
            int hit = -1;
            for (int i = cands.Count - 1; i >= 0 && r.Y - cands[i].YBot <= 3; i--)
                if (r.X1 >= cands[i].X0 - 20 && r.X0 <= cands[i].X1 + 20) { hit = i; break; }
            if (hit >= 0)
            {
                var c = cands[hit];
                cands[hit] = (c.YTop, r.Y, Math.Min(c.X0, r.X0), Math.Max(c.X1, r.X1));
            }
            else cands.Add((r.Y, r.Y, r.X0, r.X1));
        }

        double ptPerPx = 1.0 / pixelsPerPoint;
        foreach (var c in cands.Where(c => c.YBot - c.YTop + 1 <= 4).OrderBy(c => c.YTop)) // y-down 升序 = y-up 自上而下
            rules.Add(new MaskRule(
                (heightPx - c.YTop) * ptPerPx,
                (heightPx - c.YBot - 1) * ptPerPx,
                c.X0 * ptPerPx,
                (c.X1 + 1) * ptPerPx));
        return rules;

        bool IsDark(int baseOff, int x)
        {
            int off = baseOff + x * 4; // BGRA：B G R A
            return (bgra[off] + bgra[off + 1] + bgra[off + 2]) / 3 < 200;
        }
    }

    /// <summary>
    /// 词条级覆盖行框（下划线版）：讲义主词条是表格框——跨主词列+释义列的全宽横线（Left 在边界左侧）
    /// 围出的每个区域就是"与主词对应的全部数据"（释义/例句/真题/词形联想/思维导图）；
    /// 框内起线（Left ≥ 边界−10）只是小节分隔，不参与分组。组的 Top/Bottom 直接取线内缘——
    /// 悬停露出整框（含框内无文字行的思维导图区），行只用来合并文本与过滤（页码行随内容带排除）。
    /// 全宽线不足两条或没有命中区域 → null，退回主词锚点分组。
    /// </summary>
    public static IReadOnlyList<PdfRowBox>? ComputeEntryRowsByUnderlines(
        IReadOnlyList<PdfRowBox> rows, IReadOnlyList<MaskRule> rules, double? maskLeftFraction, double pageWidthPt,
        double pageHeightPt = 0, (double TopFraction, double BottomFraction)? band = null)
    {
        if (maskLeftFraction is not { } f || pageWidthPt <= 0 || rows.Count == 0 || rules.Count == 0) return null;
        double boundary = f * pageWidthPt;
        var delims = rules.Where(r => r.Left < boundary - 10)
            .OrderByDescending(r => r.Top).ToList(); // y-up：自上而下
        if (delims.Count < 2) return null;

        if (band is { } bd && pageHeightPt > 0)
        {
            // 与 ComputeEntryRows 同一过滤：页码/带外行不进词条组
            double topPt = bd.TopFraction * pageHeightPt, botPt = bd.BottomFraction * pageHeightPt;
            rows = rows.Where(r => r.Bottom >= botPt && r.Top <= topPt).ToList();
            if (rows.Count == 0) return null;
        }

        var sorted = rows.OrderByDescending(r => r.Bottom).ToList();
        var boxes = new List<PdfRowBox>();
        for (int i = 0; i < delims.Count - 1; i++)
        {
            double top = delims[i].Bottom, bottom = delims[i + 1].Top; // 线内缘之间的区域
            if (top <= bottom) continue;
            var slice = sorted.Where(r => (r.Top + r.Bottom) / 2 > bottom && (r.Top + r.Bottom) / 2 < top).ToList();
            if (slice.Count == 0) continue;
            boxes.Add(new PdfRowBox(
                string.Join(" / ", slice.Select(r => r.Text)),
                slice.Min(r => r.Left), slice.Max(r => r.Right), top, bottom));
        }
        return boxes.Count > 0 ? boxes : null;
    }

    /// <summary>
    /// 词条级覆盖行框：把被盖行按词条归并——悬停时露的是"整个词条的所有行"（释义/例句/真题一起），
    /// 而不是只有光标所在那一行（多义词的第二释义往往在隔几行的别处，逐行露出看不到）。
    /// 分界依据 = 左列发音热点（主词与 Unit 大点都在主词列，句尾热点在释义列会被 RightPt 排除）：
    /// 每个热点 y 自上而下切一刀，两刀之间（到下一热点行顶为止）的行同属一个词条；
    /// 页首热点之上的行是上一页词条的延续，单独成组。推不出热点（无主词锚点）→ null，退回逐行露出。
    /// </summary>
    public static IReadOnlyList<PdfRowBox>? ComputeEntryRows(
        IReadOnlyList<PdfRowBox> rows, IReadOnlyList<SpeakSpot> spots, double? maskLeftFraction, double pageWidthPt,
        double pageHeightPt = 0, (double TopFraction, double BottomFraction)? band = null)
    {
        if (maskLeftFraction is not { } f || pageWidthPt <= 0 || rows.Count == 0) return null;
        double boundary = f * pageWidthPt;
        if (band is { } bd && pageHeightPt > 0)
        {
            // 页码行不进词条组：它混进末组会把组底拖到内容带外（跨页联动的贴边判据失准），
            // 悬停页码附近的露出也会拖到页码区。带 = 内容行 ±2pt，行底低于带底的就是页码/带外行。
            double topPt = bd.TopFraction * pageHeightPt, botPt = bd.BottomFraction * pageHeightPt;
            rows = rows.Where(r => r.Bottom >= botPt && r.Top <= topPt).ToList();
            if (rows.Count == 0) return null;
        }
        var anchors = spots.Where(s => s.RightPt < boundary - 2)
            .Select(s => s.MidYPt).ToList();
        // OCR 页常把词头与释义并成一行（「exchange［iks'tʃeɪndʒ］ v./n.交换；交流」），热点锚在行尾
        // （RightPt 深入罩层）过不了左列过滤——按文本补锚：词头行 = 拉丁字母开头 + 头词后紧跟音标
        // 方括号（讲义版式词头必带音标；例句/搭配行如「climate change 气候变化」没有）。分界取行底：
        // 行底 +8pt 容差够不到上一行底（行高≈13pt），GroupStart 恰从本行起算。
        anchors.AddRange(rows.Where(r => IsHeadwordRow(r.Text)).Select(r => r.Bottom));
        var ordered = anchors.OrderByDescending(y => y).ToList(); // y-up：自上而下
        if (ordered.Count == 0) return null;

        var sorted = rows.OrderByDescending(r => r.Bottom).ToList();
        int GroupStart(double y)
        {
            for (int i = 0; i < sorted.Count; i++)
                if (sorted[i].Bottom <= y + 8) return i; // 8pt 容差：中策文本层的释义行底比主词热点中线高约 4-5pt
                                                         // （释义列从词头格顶起排）；上一词条末行隔着格边框 ≥10pt，不会误吞
            return sorted[0].Top <= y ? 0 : sorted.Count; // 热点在顶行带内 → 首行起；低于所有行 → 空组
        }

        // 只有真词头才切得动分组。真词头的中点落在自己词条首行的行带内（行内式词头），
        // 或首行上/下方的空隙里（释义行悬挂版式，行带按 [Bottom,Top) 半开理解——上策词 p3
        // according to 的词头行中点压进例句行顶缘 0.08pt，几何抖动给 0.5pt 容差；再宽会误伤
        // favour 词条的 "(= favor)" 变体词头，它的中点距同行例句顶缘只 0.9pt）；
        // 落在首行之后某行带内的热点是同一词条的变体词头（favour 词条左列的 "(= favor)" 与
        // 第二释义的例句同行），不能把多义词条拦腰切开
        bool IsBoundary(double y)
        {
            int gs = GroupStart(y);
            for (int i = gs; i < sorted.Count; i++)
            {
                if (y >= sorted[i].Top - 0.5) return true;     // 行间空隙（含行顶缘 0.5pt）→ 真词头
                if (y >= sorted[i].Bottom) return i == gs; // 落在某行带内：恰为首行才算词头
            }
            return true; // 低于所有行 → 尾组词头
        }

        var groups = new List<(int Start, int End)>();
        int prev = 0;
        foreach (var y in ordered)
        {
            if (!IsBoundary(y)) continue;
            int start = Math.Max(prev, GroupStart(y));
            if (prev < start) groups.Add((prev, start));
            prev = start;
        }
        if (prev < sorted.Count) groups.Add((prev, sorted.Count));
        if (groups.Count == 0) return null;

        var boxes = new List<PdfRowBox>();
        foreach (var (s, e) in groups)
        {
            var slice = sorted.Skip(s).Take(e - s).ToList();
            boxes.Add(new PdfRowBox(
                string.Join(" / ", slice.Select(r => r.Text)), // 合并文本保住"按内容找行"的调试入口
                slice.Min(r => r.Left), slice.Max(r => r.Right),
                slice.Max(r => r.Top), slice.Min(r => r.Bottom)));
        }
        return boxes;
    }

    /// <summary>
    /// 罩层的竖直内容带：只统计罩层真正要盖的行——释义列起头的行，以及"有内容伸进罩层、
    /// 起点只悬挂在边界左侧 ≤40pt"的行（中策词的词性尾巴 n./v. 挂过边界，整行起点随之偏左）；
    /// 再放宽到"起点深居主词列但字号是正文级"的行——词条版式里词头与释义同行被 PDF 拼成
    /// 一条跨栏行（favour L=73「favour 【释义】 ① 帮助」），它就是释义首行，不算则罩层顶边切掉上半截。
    /// 大字号仍排除：跨栏标题（−100pt 级、字号大）不是释义列内容，算进去罩层顶边会压上标题。
    /// 再叠"页眉/页码不参与"过滤（视觉上 6%~94% 之外是页眉水印/页码）。
    /// 取命中行的最高/最低文字带，上下各放 2pt 余量，换算成页高分数（y-up）。推不出 → null（盖整高）。
    /// </summary>
    public static (double TopFraction, double BottomFraction)? ComputeMaskVerticalBand(
        IReadOnlyList<PdfLine> lines, double? maskLeftFraction, double pageWidthPt, double pageHeightPt)
    {
        if (pageHeightPt <= 0 || pageWidthPt <= 0 || lines.Count == 0) return null;
        if (maskLeftFraction is not { } f) return null;
        double boundary = f * pageWidthPt;
        double topKeep = pageHeightPt * 0.94, bottomKeep = pageHeightPt * 0.06;
        double body = LayoutBuilder.BodyFontSize(lines);
        double? top = null, bottom = null;
        foreach (var ln in lines)
        {
            if (ln.Top > topKeep || ln.Top < bottomKeep) continue; // 页眉水印/页码
            if (!IsMaskContentLine(ln, boundary, body))
                continue; // 起头深居主词列的大字号行 = 跨栏标题，不是释义列内容
            top = Math.Max(top ?? ln.Top, ln.Top);
            bottom = Math.Min(bottom ?? ln.Bottom, ln.Bottom);
        }
        if (top is not { } t || bottom is not { } b || t <= b) return null;
        return ((t + 2) / pageHeightPt, Math.Max(b - 2, 0) / pageHeightPt);
    }

    /// <summary>
    /// 行算不算罩层要盖的释义列内容：起点已在边界附近（≥ 边界−10）自然是；起点深居主词列的行
    /// 只认两种例外——词性尾巴悬挂在边界 ≤40pt 处（中策词 n./v.），或"词头+释义"跨栏合并的正文级行
    /// （favour L=73「favour 【释义】① 帮助」，行框略高：中策 p17 favour 7.1 vs 正文 6.0）；
    /// 其余（Unit 标题、跨栏大标题，字号 2× 正文起）不算——它们不是释义列内容，混进罩层行会把
    /// 单元标题也盖上、还能被悬停露出。
    /// </summary>
    private static bool IsMaskContentLine(PdfLine ln, double boundary, double body)
    {
        if (ln.Left >= boundary - 10) return true;
        bool hang = ln.Left >= boundary - 40 && ReachesMask(ln, boundary); // 词性尾巴悬挂在边界附近
        bool mergedBody = ReachesMask(ln, boundary) && ln.FontSize <= body * 1.25; // 词头+释义跨栏合并正文行
        return hang || mergedBody;
    }

    /// <summary>行里有没有内容伸进罩层（与 ComputeMaskRows/ComputeOcrMaskRows 的收行判据一致）。</summary>
    private static bool ReachesMask(PdfLine ln, double boundary)
        => ln.Words is { } ws
            ? ws.Any(w => (w.Left + w.Right) / 2 >= boundary)
            : ln.Right > boundary + 1;

    /// <summary>
    /// 词头行（OCR 把词头与释义并成一行的行）：拉丁字母开头，音标方括号紧跟在头词后（前 24 字符内）。
    /// 例句/搭配行（「climate change 气候变化」）与纯译文行不带音标，不误判。
    /// </summary>
    private static bool IsHeadwordRow(string text)
    {
        var t = text.TrimStart();
        if (t.Length == 0 || t[0] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z')) return false;
        return t.IndexOfAny(['［', '['], 0, Math.Min(24, t.Length)) >= 0;
    }

    /// <summary>行首是否中文系字符（释义列的行首特征）：汉字 / 全角标点（【】、。）/ 全角形式 / 带圈数字。</summary>
    private static bool StartsCjk(string text)
    {
        string t = text.TrimStart();
        if (t.Length == 0) return false;
        return t[0] is >= '一' and <= '鿿'      // CJK 统一表意文字
            or >= '　' and <= '〿'              // CJK 标点与符号 U+3000-303F
            or >= '＀' and <= '￯'              // 全角形式 U+FF00-FFEF
            or >= '①' and <= '⑳';              // 带圈数字 U+2460-2473
    }
}

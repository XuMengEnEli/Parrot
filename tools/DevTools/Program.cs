// Parrot 鹦鹉 构建工具集（替代 gen-icons.ps1 / make-mac-apps.ps1，输出路径与参数一致）
//   dotnet run --project tools/DevTools -c Release -- icons    # 生成 app.ico / AppIcon.icns / app-256.png / tray.png
//   dotnet run --project tools/DevTools -c Release -- bundle   # publish\stage-* → Parrot-mac-*.zip
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SkiaSharp;

var cmd = args.Length > 0 ? args[0] : "icons";
switch (cmd)
{
    case "icons": GenIcons(); break;
    case "bundle": GenBundles(); break;
    case "all": GenIcons(); GenBundles(); break;
    default:
        Console.Error.WriteLine("usage: DevTools [icons|bundle|all]");
        return 1;
}
return 0;

// ============================ icons ============================

static void GenIcons()
{
    var assets = Path.Combine(RepoRoot(), "src", "Parrot.App", "Assets");
    Directory.CreateDirectory(Path.Combine(assets, "mac"));

    // ---- ico（PNG 编码条目，Vista+）----
    int[] icoSizes = [16, 24, 32, 48, 64, 128, 256];
    var pngs = new List<byte[]>();
    foreach (var px in icoSizes)
    {
        using var bmp = DrawIcon(px);
        pngs.Add(PngOf(bmp));
    }
    using (var ms = new MemoryStream())
    using (var bw = new BinaryWriter(ms))
    {
        bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)icoSizes.Length);
        uint off = (uint)(6 + 16 * icoSizes.Length);
        for (int i = 0; i < icoSizes.Length; i++)
        {
            byte wb = (byte)(icoSizes[i] == 256 ? 0 : icoSizes[i]);
            bw.Write(wb); bw.Write(wb); bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((ushort)1); bw.Write((ushort)32);
            bw.Write((uint)pngs[i].Length); bw.Write(off);
            off += (uint)pngs[i].Length;
        }
        foreach (var p in pngs) bw.Write(p);
        var ico = ms.ToArray();
        File.WriteAllBytes(Path.Combine(assets, "app.ico"), ico);
        Console.WriteLine($"wrote app.ico ({icoSizes.Length} entries, {ico.Length} bytes)");
    }

    // ---- icns: 'icns' + BE 总长，随后 4cc + BE 长度(含头) + png ----
    (string T, int Px)[] map =
    [
        ("icp4", 16), ("icp5", 32), ("ic11", 32), ("ic12", 64),
        ("ic07", 128), ("ic08", 256), ("ic13", 256), ("ic14", 512), ("ic10", 1024),
    ];
    var chunks = new List<byte[]>();
    int total = 8;
    foreach (var (t, px) in map)
    {
        using var bmp = DrawIcon(px);
        var png = PngOf(bmp);
        var chunk = new byte[8 + png.Length];
        Encoding.ASCII.GetBytes(t).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), (uint)(8 + png.Length));
        png.CopyTo(chunk.AsSpan(8));
        chunks.Add(chunk);
        total += chunk.Length;
    }
    using (var ms2 = new MemoryStream())
    using (var bw2 = new BinaryWriter(ms2))
    {
        bw2.Write(Encoding.ASCII.GetBytes("icns"));
        Span<byte> be = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(be, (uint)total);
        bw2.Write(be);
        foreach (var c in chunks) bw2.Write(c);
        File.WriteAllBytes(Path.Combine(assets, "mac", "AppIcon.icns"), ms2.ToArray());
    }
    Console.WriteLine($"wrote mac/AppIcon.icns ({total} bytes)");

    // ---- 窗体图标 + 托盘 ----
    SavePng(256, Path.Combine(assets, "app-256.png"));
    SavePng(32, Path.Combine(assets, "tray.png"));
    Console.WriteLine("saved app-256.png, tray.png");
}

static void SavePng(int px, string file)
{
    using var bmp = DrawIcon(px);
    File.WriteAllBytes(file, PngOf(bmp));
}

static byte[] PngOf(SKBitmap bmp)
{
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// 1024 设计空间：圆角渐变磁贴 + 白色「鹦」（鹦鹉学舌，跟读练嘴即是语言学习之路）+ 右下喇叭徽章
static SKBitmap DrawIcon(int px)
{
    var bmp = new SKBitmap(new SKImageInfo(px, px, SKColorType.Bgra8888, SKAlphaType.Premul));
    using var canvas = new SKCanvas(bmp);
    canvas.Clear(SKColors.Transparent);
    float s = px / 1024f;
    var blue = new SKColor(0x2F, 0x5A, 0xFD);
    var teal = new SKColor(0x14, 0xC0, 0xA8);
    var navy = new SKColor(0x17, 0x33, 0x8C);

    // 圆角渐变磁贴（52° 对角，左上蓝 → 右下青）
    double rad = 52 * Math.PI / 180;
    float gx = (float)Math.Cos(rad), gy = (float)Math.Sin(rad);
    float gl = px * (Math.Abs(gx) + Math.Abs(gy)) / 2f;
    using (var grad = SKShader.CreateLinearGradient(
               new SKPoint(px / 2 - gx * gl, px / 2 - gy * gl),
               new SKPoint(px / 2 + gx * gl, px / 2 + gy * gl),
               [blue, teal], null, SKShaderTileMode.Clamp))
    using (var tile = new SKPaint { Shader = grad, IsAntialias = true })
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(0, 0, px, px), 196 * s), tile);

    // 大「词」字居中偏上（旧版 660em@(732,710) 会被画布底边裁切、且与徽章互相压字）
    using (var tf = GlyphFont())
    using (var font = new SKFont(tf, Math.Max(6, 560 * s)))
    using (var tp = new SKPaint { Color = SKColors.White, IsAntialias = true })
    {
        var fm = font.Metrics;
        canvas.DrawText("\u9E66", 512 * s, 435 * s - (fm.Ascent + fm.Descent) / 2,
                        SKTextAlign.Center, font, tp);
    }

    // 右下白色圆徽章 + 蓝色喇叭 + 两道声波弧
    float bcx = 824 * s, bcy = 824 * s, br = 150 * s, sp = br / 100f;
    using (var white = new SKPaint { Color = SKColors.White, IsAntialias = true })
        canvas.DrawCircle(bcx, bcy, br, white);
    float bx = bcx - 62 * sp;
    using (var cone = new SKPath())
    using (var bp = new SKPaint { Color = blue, IsAntialias = true })
    {
        cone.MoveTo(bx, bcy - 26 * sp);
        cone.LineTo(bx + 34 * sp, bcy - 26 * sp);
        cone.LineTo(bx + 72 * sp, bcy - 62 * sp);
        cone.LineTo(bx + 72 * sp, bcy + 62 * sp);
        cone.LineTo(bx + 34 * sp, bcy + 26 * sp);
        cone.LineTo(bx, bcy + 26 * sp);
        cone.Close();
        canvas.DrawPath(cone, bp);
    }
    float cx2 = bx + 60 * sp;
    using (var a1 = new SKPaint
    {
        Color = navy, IsAntialias = true, Style = SKPaintStyle.Stroke,
        StrokeWidth = Math.Max(2, 16 * sp), StrokeCap = SKStrokeCap.Round,
    })
    using (var a2 = new SKPaint
    {
        Color = navy.WithAlpha(200), IsAntialias = true, Style = SKPaintStyle.Stroke,
        StrokeWidth = Math.Max(2, 14 * sp), StrokeCap = SKStrokeCap.Round,
    })
    {
        canvas.DrawArc(new SKRect(cx2 - 64 * sp, bcy - 64 * sp, cx2 + 64 * sp, bcy + 64 * sp), -55, 110, false, a1);
        canvas.DrawArc(new SKRect(cx2 - 92 * sp, bcy - 92 * sp, cx2 + 92 * sp, bcy + 92 * sp), -50, 100, false, a2);
    }
    return bmp;
}

// 优先「Microsoft YaHei UI」粗体；拿不到（或不含该字）就按字符回退匹配
static SKTypeface GlyphFont()
{
    var tf = SKTypeface.FromFamilyName("Microsoft YaHei UI", new SKFontStyle(
        SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright));
    if (tf is not null)
    {
        using var probe = new SKFont(tf, 10);
        if (probe.GetGlyph('\u9E66') != 0)
        {
            Console.WriteLine($"icon font: {tf.FamilyName}");
            return tf;
        }
        tf.Dispose();
    }
    var fb = SKFontManager.Default.MatchCharacter('\u9E66') ?? SKTypeface.Default;
    Console.WriteLine($"icon font: fallback {fb.FamilyName}");
    return fb;
}

// ============================ bundle ============================

/// <summary>仓库根：自当前目录上溯找 Parrot.slnx（克隆到任意路径都能跑）。</summary>
static string RepoRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Parrot.slnx"))) return dir.FullName;
    }
    throw new InvalidOperationException("请在 Parrot 仓库目录内运行 DevTools（未找到 Parrot.slnx）");
}

static void GenBundles()
{
    var root = RepoRoot();
    var pub = Path.Combine(root, "publish");
    var notes = Path.Combine(root, "packaging", "使用说明.txt");
    if (!File.Exists(notes)) throw new InvalidOperationException($"缺少 {notes}（包内《使用说明》）");
    var brandCn = "鹦鹉"; // 中文品牌名（CFBundleDisplayName）

    const string plistTemplate = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>CFBundleName</key><string>Parrot</string>
            <key>CFBundleDisplayName</key><string>__CN__</string>
            <key>CFBundleIdentifier</key><string>com.parrot.app</string>
            <key>CFBundleExecutable</key><string>Parrot.App</string>
            <key>CFBundlePackageType</key><string>APPL</string>
            <key>CFBundleShortVersionString</key><string>1.3.0</string>
            <key>CFBundleVersion</key><string>1</string>
            <key>LSMinimumSystemVersion</key><string>11.0</string>
            <key>LSApplicationCategoryType</key><string>public.app-category.education</string>
            <key>CFBundleIconFile</key><string>AppIcon.icns</string>
            <key>NSHighResolutionCapable</key><true/>
            <key>NSPrincipalClass</key><string>NSApplication</string>
        </dict>
        </plist>
        """;

    foreach (var r in new[] { "arm64", "x64" })
    {
        var stage = Path.Combine(pub, $"stage-{r}");
        var baseDir = Path.Combine(pub, $"mac-{r}");
        var contents = Path.Combine(baseDir, "Parrot.app", "Contents");
        var macos = Path.Combine(contents, "MacOS");
        var res = Path.Combine(contents, "Resources");
        if (!File.Exists(Path.Combine(stage, "Parrot.App")))
            throw new InvalidOperationException($"先 dotnet publish 到 {stage}（缺 Parrot.App 主程序）");
        if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
        Directory.CreateDirectory(macos);
        Directory.CreateDirectory(res);

        File.Copy(Path.Combine(stage, "Parrot.App"), Path.Combine(macos, "Parrot.App"), true);
        foreach (var dll in Directory.GetFiles(stage, "*.dylib"))
            File.Copy(dll, Path.Combine(macos, Path.GetFileName(dll)), true);
        File.Copy(Path.Combine(root, "src", "Parrot.App", "Assets", "mac", "AppIcon.icns"),
                  Path.Combine(res, "AppIcon.icns"), true);
        File.WriteAllText(Path.Combine(contents, "Info.plist"),
                          plistTemplate.Replace("__CN__", brandCn), new UTF8Encoding(false));
        File.Copy(notes, Path.Combine(baseDir, Path.GetFileName(notes)), true);

        var zip = Path.Combine(pub, $"Parrot-mac-{r}.zip");
        if (File.Exists(zip)) File.Delete(zip);
        using (var fs = File.Create(zip))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var f in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
            {
                var rel = f[(baseDir.Length + 1)..].Replace('\\', '/'); // mac 解压安全：'/' 分隔
                var e = za.CreateEntry(rel, CompressionLevel.Optimal);
                using var es = e.Open();
                using var input = File.OpenRead(f);
                input.CopyTo(es);
            }
        }
        Console.WriteLine($"== mac-{r}.zip -> {new FileInfo(zip).Length / 1048576.0:F1}MB");
    }

    // 自检：列出条目 + 反斜杠残留计数
    using (var z = ZipFile.OpenRead(Path.Combine(pub, "Parrot-mac-arm64.zip")))
    {
        Console.WriteLine("entries of Parrot-mac-arm64.zip:");
        foreach (var e in z.Entries) Console.WriteLine("  " + e.FullName);
        Console.WriteLine($"backslash entries: {z.Entries.Count(e => e.FullName.Contains('\\'))}");
    }
}

# Parrot mac bundle assembler (run on Windows after dotnet publish to publish\stage-arm64|x64).
# ASCII-only source; CJK strings by codepoint. Zips use '/' entry separators (mac-safe).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$pub  = "$root\publish"
$brandCn = [string][char]0x9E66 + [char]0x9E49          # brand CJK display name, by codepoint
$ver = (Select-String -Path "$root\Directory.Build.props" -Pattern '<Version>(.*?)</Version>').Matches[0].Groups[1].Value
$notes = (Get-ChildItem "$pub\*.txt" | Select-Object -First 1).FullName

$plistTemplate = @'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Parrot</string>
    <key>CFBundleDisplayName</key><string>__CN__</string>
    <key>CFBundleIdentifier</key><string>com.parrot.app</string>
    <key>CFBundleExecutable</key><string>Parrot.App</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>__VER__</string>
    <key>CFBundleVersion</key><string>1</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.education</string>
    <key>CFBundleIconFile</key><string>AppIcon.icns</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
'@

foreach ($r in 'arm64', 'x64') {
    $stage = "$pub\stage-$r"
    $base  = "$pub\mac-$r"
    $app   = "$base\Parrot.app"
    if (Test-Path $base) { Remove-Item $base -Recurse -Force }
    New-Item -ItemType Directory -Path "$app\Contents\MacOS" | Out-Null
    New-Item -ItemType Directory -Path "$app\Contents\Resources" | Out-Null
    Copy-Item "$stage\Parrot.App" "$app\Contents\MacOS\"
    Copy-Item "$stage\*.dylib" "$app\Contents\MacOS\"
    Copy-Item "$root\src\Parrot.App\Assets\mac\AppIcon.icns" "$app\Contents\Resources\"
    $plist = $plistTemplate.Replace('__CN__', $brandCn).Replace('__VER__', $ver)
    [System.IO.File]::WriteAllText("$app\Contents\Info.plist", $plist, (New-Object System.Text.UTF8Encoding($false)))
    Copy-Item $notes "$base\"
    $zip = "$pub\Parrot-mac-$r.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    $fs = [System.IO.File]::Open($zip, [System.IO.FileMode]::Create)
    $za = [System.IO.Compression.ZipArchive]::new($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    Get-ChildItem $base -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($base.Length + 1).Replace('\', '/')
        $e = $za.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
        $es = $e.Open(); $in = [System.IO.File]::OpenRead($_.FullName); $in.CopyTo($es); $in.Dispose(); $es.Dispose()
    }
    $za.Dispose(); $fs.Dispose()
    "== mac-$r.zip -> $([math]::Round((Get-Item $zip).Length / 1MB, 1))MB"
}
'entries of Parrot-mac-arm64.zip:'
$z = [System.IO.Compression.ZipFile]::OpenRead("$pub\Parrot-mac-arm64.zip")
$z.Entries | Select-Object -ExpandProperty FullName
"backslash entries: $(($z.Entries | Where-Object { $_.FullName.Contains('\') }).Count)"
$z.Dispose()

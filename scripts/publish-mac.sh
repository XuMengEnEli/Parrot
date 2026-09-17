#!/usr/bin/env bash
# macOS 发布：双架构（Apple 芯片 + Intel）各自组装 Parrot.app。
# 用法：bash scripts/publish-mac.sh
#       签名/公证步骤见 README「mac 分发」一节（本项目未做付费开发者证书，默认产出未签名包）。
# 图标资源（src/Parrot.App/Assets/mac/AppIcon.icns）缺失时先生成：
#       dotnet run --project tools/DevTools -- icons
set -euo pipefail
cd "$(dirname "$0")/.."

BRAND_CN="鹦鹉"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)"

for RID in osx-arm64 osx-x64; do
  STAGE="publish/stage-${RID#osx-}"
  OUT="publish/mac-${RID#osx-}"
  dotnet publish src/Parrot.App -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true -o "$STAGE"

  APP="$OUT/Parrot.app"
  rm -rf "$OUT"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  mv "$STAGE/Parrot.App" "$APP/Contents/MacOS/Parrot.App"
  find "$STAGE" -maxdepth 1 -name '*.dylib' -exec mv {} "$APP/Contents/MacOS/" \;
  cp src/Parrot.App/Assets/mac/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
  # README 让用户"按包内《使用说明.txt》"放行 Gatekeeper，缺了它就是误导
  cp packaging/使用说明.txt "$OUT/使用说明.txt"

  cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Parrot</string>
    <key>CFBundleDisplayName</key><string>${BRAND_CN}</string>
    <key>CFBundleIdentifier</key><string>com.parrot.app</string>
    <key>CFBundleExecutable</key><string>Parrot.App</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>${VERSION}</string>
    <key>CFBundleVersion</key><string>1</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.education</string>
    <key>CFBundleIconFile</key><string>AppIcon.icns</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
PLIST

  # ad-hoc 签名：本机可直接运行；对外分发需换 Developer ID 证书并走公证
  codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || true
  # 打 $OUT 而非 $APP：压缩包根 = Parrot.app + 使用说明.txt（README 承诺包内有说明）。
  # --noextattr --norsrc 去掉 ditto 默认写入的 ._ AppleDouble 条目
  ditto --noextattr --norsrc -c -k "$OUT" "publish/Parrot-mac-${RID#osx-}.zip"

  echo "完成 → $APP 与 publish/Parrot-mac-${RID#osx-}.zip（v${VERSION}，未做 Developer ID 签名/公证）"
done

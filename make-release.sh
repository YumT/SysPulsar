#!/usr/bin/env bash
# SysPulsar リリーススクリプト
# 使い方: ./make-release.sh v1.2.1
# csproj の <Version> 一致チェック → publish → zip+sha256 → gh release create まで一発。
set -e
cd "$(dirname "$0")"

tag="$1"
[ -z "$tag" ] && { echo "usage: $0 vX.Y.Z"; exit 1; }
ver="${tag#v}"

# csproj の <Version> 一致チェック(不一致だと自動更新が無限提示される)
csproj_ver=$(grep -oPm1 '(?<=<Version>)[^<]+' src/SysPulsar.App/SysPulsar.App.csproj)
if [ "$csproj_ver" != "$ver" ]; then
  echo "error: csproj <Version> ($csproj_ver) != tag ($ver)" >&2
  exit 1
fi

# 自己完結 single-file を publish/ に生成
# (Git Bash で ProgramFiles 系が未設定の環境でも動くよう env を付ける。
#  setx で永続化済みの環境では env 部分は無害な上書きでしかない)
env PATH="$PATH:/c/Program Files/dotnet" \
  ProgramData='C:\ProgramData' \
  ProgramFiles='C:\Program Files' \
  'ProgramFiles(x86)=C:\Program Files (x86)' \
  CommonProgramFiles='C:\Program Files\Common Files' \
  'CommonProgramFiles(x86)=C:\Program Files (x86)\Common Files' \
  ProgramW6432='C:\Program Files' \
  CommonProgramW6432='C:\Program Files\Common Files' \
  dotnet publish src/SysPulsar.App -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -o publish -nr:false

# zip + sha256(Git Bash に zip が無いので PowerShell の Compress-Archive を使う)
zip="SysPulsar-$tag.zip"
rm -f "$zip" "$zip.sha256"
powershell -NoProfile -Command "Compress-Archive -Force -Path 'publish\\SysPulsar.exe','publish\\config.json','autostart-register.bat','autostart-unregister.bat' -DestinationPath '$zip'"
hash=$(sha256sum "$zip" | cut -d' ' -f1)
printf '%s  %s\n' "$hash" "$zip" > "$zip.sha256"

# GitHub Releases へ公開
gh release create "$tag" "$zip" "$zip.sha256" --title "$tag" --generate-notes

echo "released: $tag ($zip, sha256=$hash)"

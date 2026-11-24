#!/usr/bin/env bash
set -euo pipefail

target="${1:?Specify windows-x64, macos-arm64 or macos-x64}"
mkdir -p "${2:?Specify the output directory}"
output="$(cd "$2" && pwd)"
script="$(cd "$(dirname "$0")" && pwd)/$(basename "$0")"
build="$(mktemp -d)"
trap 'rm -rf "$build"' EXIT
cd "$build"

case "$target" in
  windows-x64)
    host=x86_64-w64-mingw32
    compiler="$host-gcc"
    platform=(--enable-cross-compile --cross-prefix="$host-" --target-os=mingw32 --arch=x86_64)
    binary=ffmpeg.exe
    ;;
  macos-arm64|macos-x64)
    if [[ "$target" == macos-arm64 ]]; then arch=arm64; host=aarch64; else arch=x86_64; host=x86_64; fi
    host="$host-apple-darwin"
    compiler="clang -arch $arch -mmacosx-version-min=13.0"
    platform=(--enable-cross-compile --target-os=darwin --arch="$arch" --cc="$compiler")
    binary=ffmpeg
    ;;
  *) echo "Unsupported media tool target: $target" >&2; exit 1 ;;
esac

ffmpeg=ffmpeg-9.1.tar.xz
lame=lame_3.100.orig.tar.gz
curl -fsSL --retry 3 "https://ffmpeg.org/releases/$ffmpeg" --output "$ffmpeg"
curl -fsSL --retry 3 "https://deb.debian.org/debian/pool/main/l/lame/$lame" --output "$lame"
printf '%s  %s\n' \
  1a4d394ae63d190a526cd052eb4415c156b5c5e2df37efab41a7de0b8fde2433 "$ffmpeg" \
  ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e "$lame" | shasum -a 256 -c
tar -xJf "$ffmpeg"
tar -xzf "$lame"
jobs="$(getconf _NPROCESSORS_ONLN)"
prefix="$build/lame-install"

(
  cd lame-3.100
  CC="$compiler" CFLAGS="-O2 -Wno-error=implicit-function-declaration" ./configure --host="$host" \
    --prefix="$prefix" --enable-static --disable-shared --disable-frontend --disable-decoder --disable-nasm
  make -j "$jobs"
  make install
)

(
  cd ffmpeg-9.1
  ./configure "${platform[@]}" \
    --disable-autodetect --disable-everything --disable-doc --disable-network --disable-hwaccels \
    --disable-ffplay --disable-ffprobe --disable-debug --disable-x86asm \
    --enable-protocol=file,pipe --enable-demuxer=mpegts --enable-muxer=mp4,mp3 \
    --enable-parser=aac,h264,hevc --enable-decoder=aac,aac_latm,h264,hevc \
    --enable-libmp3lame --enable-encoder=libmp3lame --enable-filter=aresample \
    --enable-bsf=aac_adtstoasc --extra-cflags="-I$prefix/include" --extra-ldflags="-L$prefix/lib"
  make -j "$jobs" "$binary"
  cp "$binary" COPYING.LGPLv2.1 "$output/"
)

cp "$ffmpeg" "$lame" "$script" "$output/"
cp lame-3.100/COPYING "$output/COPYING.LAME"
printf '%s\n' "FFmpeg and LAME use LGPL 2.1 or later. The source archives are unmodified." "Build with: bash build-media-tool.sh $target <output>" > "$output/BUILD.txt"

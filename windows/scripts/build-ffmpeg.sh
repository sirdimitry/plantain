#!/usr/bin/env bash
set -euo pipefail

version=9.1
archive="ffmpeg-$version.tar.xz"
output="$(realpath -m "${1:?Pass the output directory}")"
build="$(mktemp -d)"
trap 'rm -rf "$build"' EXIT
cd "$build"

curl --fail --silent --show-error --location --retry 3 "https://ffmpeg.org/releases/$archive" --output "$archive"
echo "1a4d394ae63d190a526cd052eb4415c156b5c5e2df37efab41a7de0b8fde2433  $archive" | sha256sum --check
tar -xJf "$archive"
cd "ffmpeg-$version"

options=(
  --enable-cross-compile --cross-prefix=x86_64-w64-mingw32- --target-os=mingw32 --arch=x86_64
  --disable-autodetect --disable-everything --disable-doc --disable-network --disable-hwaccels
  --disable-ffplay --disable-ffprobe --disable-debug --disable-x86asm
  --enable-protocol=pipe --enable-demuxer=mpegts --enable-muxer=mp4
  --enable-parser=aac,h264,hevc --enable-decoder=aac,h264,hevc --enable-bsf=aac_adtstoasc
)
./configure "${options[@]}"
make -j "$(nproc)" ffmpeg.exe

mkdir -p "$output"
cp ffmpeg.exe COPYING.LGPLv2.1 "$output/"
cp "$build/$archive" "$output/"
printf '%s\n' 'FFmpeg is provided under LGPL 2.1 or later. Source code is unmodified.' > "$output/BUILD.txt"
printf './configure %s\n' "${options[*]}" >> "$output/BUILD.txt"
printf '%s\n' 'make ffmpeg.exe' >> "$output/BUILD.txt"

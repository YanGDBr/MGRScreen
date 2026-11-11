#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 2 || ${MSYSTEM:-} != UCRT64 ]]; then
    echo 'Uso no MSYS2 UCRT64: bash build.sh <pasta-dos-arquivos-fonte> <nova-pasta-de-build>' >&2
    exit 2
fi

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
archives=$(cd -- "$1" && pwd)
(cd "$archives" && sha256sum -c "$script_dir/SHA256SUMS")
mkdir -- "$2"
cd -- "$2"
build_root=$PWD
prefix="$build_root/deps"
native_prefix=$(cygpath -m "$prefix")
output="$build_root/ffmpeg"
mkdir -p "$prefix" "$output"
jobs=${JOBS:-$(nproc)}

while read -r _ archive; do
    tar -xf "$archives/$archive"
done < "$script_dir/SHA256SUMS"

x264=x264-31e19f92f00c7003fa115047ce50978bc98c3a0d
nvcodec=nv-codec-headers-n12.2.72.0
amf=AMF-1.4.36
vpl=libvpl-2.15.0
ffmpeg=ffmpeg-8.1.3

(
    cd "$x264"
    ./configure --prefix="$native_prefix" --host=x86_64-w64-mingw32 --enable-static --disable-cli \
        --disable-opencl --bit-depth=8
    make -j"$jobs"
    make install
)
make -C "$nvcodec" PREFIX="$native_prefix" install
mkdir -p "$prefix/include/AMF"
cp -R "$amf/amf/public/include/." "$prefix/include/AMF"

# The legacy MSVC macros conflict with secure wide-string functions in MinGW UCRT.
sed -i 's/^#if _MSC_VER < 1400$/#if defined(_MSC_VER) \&\& _MSC_VER < 1400/' \
    "$vpl/libvpl/src/windows/mfx_dispatcher_defs.h"
cmake -S "$vpl" -B vpl-build -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$native_prefix" -DCMAKE_INSTALL_LIBDIR=lib \
    -DCXX_LIB="-lstdc++ -lwinpthread" \
    -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTS=OFF -DBUILD_EXAMPLES=OFF -DBUILD_EXPERIMENTAL=OFF
cmake --build vpl-build --parallel "$jobs"
cmake --install vpl-build

export PKG_CONFIG_PATH="$native_prefix/lib/pkgconfig"
(
    cd "$ffmpeg"
    ./configure --target-os=mingw32 --arch=x86_64 --enable-gpl --enable-static --disable-shared \
        --disable-autodetect --disable-debug --disable-doc --disable-ffplay --disable-ffprobe \
        --disable-network --disable-everything --enable-libx264 --enable-libvpl \
        --enable-amf --enable-ffnvcodec --enable-nvenc --enable-d3d11va --enable-w32threads \
        --enable-encoder=libx264,h264_amf,h264_nvenc,h264_qsv,rawvideo,wrapped_avframe \
        --enable-decoder=h264,rawvideo,wrapped_avframe --enable-hwaccel=h264_d3d11va,h264_d3d11va2 \
        --enable-demuxer=h264,rawvideo --enable-parser=h264 --enable-muxer=h264,rawvideo,null,mp4 \
        --enable-indev=lavfi --enable-filter=color,scale,hwdownload --enable-protocol=file,pipe \
        --enable-bsf=dump_extradata,h264_metadata --pkg-config-flags=--static \
        --extra-cflags=-I../deps/include \
        --extra-ldflags=-static
    for component in H264_AMF_ENCODER H264_NVENC_ENCODER H264_QSV_ENCODER LIBX264_ENCODER \
        H264_D3D11VA2_HWACCEL H264_DECODER H264_PARSER DUMP_EXTRADATA_BSF H264_METADATA_BSF; do
        if ! grep -qx "#define CONFIG_$component 1" config_components.h; then
            echo "Componente obrigatório indisponível: $component" >&2
            exit 1
        fi
    done
    make -j"$jobs" ffmpeg.exe
    cp ffmpeg.exe "$output/"
)

"$output/ffmpeg.exe" -L > "$output/LICENSE.txt" 2>&1
cp "$ffmpeg/COPYING.GPLv2" "$output/FFmpeg-COPYING.GPLv2"
cp "$x264/COPYING" "$output/x264-COPYING"
for header in "$nvcodec"/include/ffnvcodec/*.h; do
    sed -n '1,/^ \*\//p' "$header"
    echo
done > "$output/nv-codec-headers-LICENSE"
cp "$amf/LICENSE.txt" "$output/AMF-LICENSE"
cp "$vpl/LICENSE" "$output/libvpl-LICENSE"
runtime_licenses=/ucrt64/share/licenses
cp "$runtime_licenses/libgcc/COPYING3" "$output/GCC-COPYING3"
cp "$runtime_licenses/libgcc/COPYING.RUNTIME" "$output/GCC-COPYING.RUNTIME"
cp "$runtime_licenses/crt/COPYING.MinGW-w64-runtime.txt" "$output/MinGW-w64-runtime-LICENSE"
cp "$runtime_licenses/winpthreads/COPYING" "$output/winpthreads-COPYING"
{
    pacman -Q mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-binutils mingw-w64-ucrt-x86_64-nasm \
        mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-ninja mingw-w64-ucrt-x86_64-pkgconf \
        mingw-w64-ucrt-x86_64-crt mingw-w64-ucrt-x86_64-winpthreads make
    echo
    objdump -p "$output/ffmpeg.exe" | awk '/DLL Name:/ {print $NF}'
} > "$output/TOOLCHAIN.txt"

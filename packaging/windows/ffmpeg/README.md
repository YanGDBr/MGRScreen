# Código-fonte do FFmpeg distribuído com o MGRScreen

O MGRScreen usa `ffmpeg.exe` como processo externo. O binário incluído nos pacotes Windows é compilado sob GPL v2 ou posterior. Ele inclui o encoder x264, os cabeçalhos de NVENC e AMF e o dispatcher Intel VPL.

| Componente | Origem |
| --- | --- |
| FFmpeg 8.1.3 | [Arquivo oficial](https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz) |
| x264 | [Commit 31e19f9](https://github.com/mirror/x264/tree/31e19f92f00c7003fa115047ce50978bc98c3a0d) |
| nv-codec-headers | [Tag n12.2.72.0](https://github.com/FFmpeg/nv-codec-headers/tree/n12.2.72.0) |
| AMF | [Tag v1.4.36](https://github.com/GPUOpen-LibrariesAndSDKs/AMF/tree/v1.4.36) |
| Intel VPL | [Tag v2.15.0](https://github.com/intel/libvpl/tree/v2.15.0) |

O arquivo de AMF contém os cabeçalhos públicos usados na compilação e a licença, extraídos da tag indicada. O script aplica um ajuste no Intel VPL para limitar as macros de compatibilidade do Visual C++ antigo ao compilador correspondente. Os drivers e runtimes de GPU são fornecidos pelos fabricantes.

No [MSYS2 UCRT64](https://www.msys2.org/), instale GCC, NASM, CMake, Ninja e pkgconf da variante `mingw-w64-ucrt-x86_64`, além de `make`. Extraia o ZIP de fontes da release e execute o comando na pasta extraída.

```bash
bash build.sh . build
```

O executável e os avisos de licença são gerados em `build/ffmpeg/`. O arquivo `TOOLCHAIN.txt` registra as ferramentas usadas na compilação da release.

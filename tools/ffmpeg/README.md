# FFmpeg no Windows

O instalador e o ZIP portátil da [release](https://github.com/YanGDBr/MGRScreen/releases/latest) já incluem o FFmpeg. Para executar o projeto a partir do código-fonte, você também pode instalar o [FFmpeg Essentials](https://www.gyan.dev/ffmpeg/builds/) com o Windows Package Manager.

```powershell
winget install --id Gyan.FFmpeg.Essentials -e
ffmpeg -version
```

Abra um novo terminal se o comando ainda não estiver no PATH. Na instalação manual, extraia `ffmpeg.exe` para esta pasta durante o desenvolvimento ou para `ffmpeg/ffmpeg.exe` ao lado de `MGRScreen.exe` na versão publicada.

O MGRScreen chama o FFmpeg como processo externo. Use um build que ofereça o encoder `libx264` para o fallback por software. O `ffprobe.exe` é opcional para diagnóstico. [Compilação do FFmpeg distribuído](../../packaging/windows/ffmpeg/README.md)

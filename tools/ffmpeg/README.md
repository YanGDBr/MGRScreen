# FFmpeg no Windows

Instale o [FFmpeg Essentials](https://www.gyan.dev/ffmpeg/builds/) com o Windows Package Manager:

```powershell
winget install --id Gyan.FFmpeg.Essentials -e
ffmpeg -version
```

Abra um novo terminal se o comando ainda não estiver no PATH. Na instalação manual, extraia `ffmpeg.exe` para esta pasta durante o desenvolvimento ou para `ffmpeg/ffmpeg.exe` ao lado de `MGRScreen.exe` na versão publicada.

O MGRScreen chama o FFmpeg como processo externo. Use um build que ofereça o encoder `libx264` para o fallback por software. O `ffprobe.exe` é opcional para diagnóstico. Consulte a [licença do FFmpeg](https://ffmpeg.org/legal.html) para a compilação que instalou.

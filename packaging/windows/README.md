# Pacotes para Windows

O instalador em português registra o MGRScreen para o usuário atual, cria um atalho no menu Iniciar e oferece um atalho opcional na área de trabalho. O ZIP contém os mesmos arquivos do aplicativo. Ambos funcionam sem baixar dependências durante a instalação ou a primeira execução.

Use o [Inno Setup](https://jrsoftware.org/isdl.php) e o .NET 8 SDK para gerar os pacotes. Publique o aplicativo com o runtime Windows x64 e inclua os avisos das dependências na pasta de publicação conforme os [avisos de terceiros](../../THIRD_PARTY_NOTICES.md).

```powershell
dotnet publish src/MGRScreen.App -c Release -r win-x64 --self-contained true -o out/windows-app
```

Baixe o ZIP de fontes do FFmpeg na release e siga o [guia de compilação](ffmpeg/README.md). Depois, aponte o script para a publicação do aplicativo, o FFmpeg compilado, os arquivos-fonte extraídos e o compilador do Inno Setup.

```powershell
.\packaging\windows\New-WindowsPackages.ps1 `
    -AppDirectory out/windows-app `
    -FfmpegDirectory out/ffmpeg-build/ffmpeg `
    -SourceArchives out/ffmpeg-source `
    -InnoCompiler "C:\Program Files\Inno Setup 7\ISCC.exe" `
    -OutputDirectory out/windows-packages
```

Publique o instalador, o ZIP portátil e o ZIP de código-fonte correspondente do FFmpeg como arquivos da mesma release.

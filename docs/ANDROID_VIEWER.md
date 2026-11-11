# MGRScreen Viewer

O Android entra como espectador em uma sala criada no Windows. Use Android 10 ou superior, um Windows com o protocolo 2 do MGRScreen e a mesma rede local ou uma conexão pelo Tailscale.

## Entrar na sala

1. Abra a versão atualizada do MGRScreen no Windows, crie a sala e comece a compartilhar. Para o primeiro teste, selecione 720p30.
2. Instale o APK no Android e abra o **MGRScreen Viewer**.
3. Digite seu apelido. As salas aparecem automaticamente na rede local. Toque em **Atualizar** para refazer a busca ou em **Entrar por IP ou nome** para informar o endereço manualmente. As portas TCP padrão são 51900 e 51901.
4. Escolha uma transmissão. O menu **Orientação** oferece os modos automático, retrato e paisagem. Em tela cheia, toque no vídeo para mostrar ou ocultar os controles. Use **Silenciar** e o controle de volume para ajustar o áudio da tela. A câmera toca sem áudio.

Para usar o Tailscale, conecte o celular e o computador do anfitrião à rede Tailscale e use o MGRScreen para Windows atualizado. O anfitrião envia anúncios aos dispositivos da rede pela porta UDP 51902. A descoberta na primeira visita exige que o celular permita receber esse tráfego. Quando ele estiver bloqueado, entre pelo IP Tailscale ou nome MagicDNS do anfitrião. A busca automática também consulta os endereços recentes por TCP.

Se o servidor não aparecer na busca, tente a entrada manual. Confira as duas portas TCP no firewall do anfitrião se o endereço estiver correto e a conexão falhar. Clientes Windows antigos ainda podem entrar, mas o publicador precisa estar atualizado para aparecer como reproduzível no Android.

## Compilar

Use JDK 17, Android SDK 35, Build Tools 34.0.0 e Gradle 8.9. Na pasta `android`:

```powershell
.\gradlew.bat assembleDebug lintDebug
```

O APK de teste sai em `android/app/build/outputs/apk/debug/app-debug.apk`. Para distribuição direta, use `assembleRelease` e assine o APK com sua chave de distribuição. Preserve a mesma chave em atualizações.

No Windows, compile a solução e mantenha o FFmpeg no caminho esperado pelo aplicativo. O teste de integração abaixo abre sockets locais e usa o FFmpeg real.

```powershell
dotnet build MGRScreen.sln
dotnet run --project tests/MGRScreen.ProtocolSmoke -- tools/ffmpeg/ffmpeg.exe libx264
```

## Limites da primeira versão

- Uma transmissão por vez em primeiro plano. Ao voltar ao aplicativo, a sala e a seleção são restauradas quando disponíveis.
- A resolução e o bitrate são escolhidos pelo publicador. Um aparelho sem suporte ao formato pode precisar que o transmissor reduza a qualidade.
- Use LAN confiável ou VPN. O transporte TCP usa o controle de acesso da rede e o token só associa o socket de mídia à sessão. Convites via WebRTC estão previstos como evolução.
- Os tempos de início e sincronização dependem da rede e do aparelho. Use a matriz de testes do [plano Android](ANDROID_VIEWER_PLAN.md) para a validação de bateria, latência e estabilidade em celulares físicos.

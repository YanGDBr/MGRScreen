# Revisão do código — defeitos e melhorias (11/09/2026)

Leitura completa das ~7.400 linhas de `src/`. Só identificação, nada foi alterado. Ordenado pelo
que mais afeta o uso real. "Verificado" = confirmei lendo o caminho inteiro do código; "provável"
= depende de condição que não reproduzi.

## A. Defeitos que afetam o uso

| # | Onde | O quê | Efeito |
| --- | --- | --- | --- |
| A1 | `RoomServer` / `RoomClient` | **Nenhum keepalive nem heartbeat.** O `Ping` existe no enum e nunca é usado; o socket não tem `KeepAlive`. Verificado. | Wi-Fi caindo, cabo puxado, PC hibernando: nenhum lado recebe erro de leitura. O servidor mantém um participante fantasma "compartilhando"; o cliente nunca dispara `Disconnected`, então a **reconexão automática só funciona em queda limpa** (app fechado, RST). Na queda que mais acontece, os dois lados ficam pendurados até o timeout do TCP (minutos). |
| A2 | `RoomClient.ReceiveMediaLoopAsync` | O loop de mídia terminar **não** dispara `Disconnected` (só o de controle dispara). Verificado. | Canal de mídia morre com o de controle vivo → o cliente fica na sala sem receber nada, sem aviso e sem reconectar. No servidor, `HandleMediaClientAsync` só fecha o socket; o participante continua com `Stream != null` e os outros veem um tile "não está enviando vídeo" pra sempre. |
| A3 | `RoomWindow.ReconnectLoopAsync` | Quando o anfitrião fecha a sala, os clientes recebem RST → reconexão a cada 3 s **pra sempre**, banner "Reconectando... (tentativa N)" sem fim. Não existe mensagem `RoomClosed`. Verificado. | O evento mais normal do app (o dono sai) vira um estado de erro permanente pra todo mundo. |
| A4 | `RoomWindow.OnLoaded` + `MainWindow` | Pra quem hospeda, `LastRoomAddress` é salvo como `127.0.0.1`. O botão "Voltar pra sua sala" tenta **entrar** em 127.0.0.1 como cliente — e não há servidor. Verificado. | Botão que sempre falha ("Não consegui conectar") depois de você ter hospedado. Deveria recriar a sala. |
| A5 | `ScreenCaptureService.Stop` vs `OnFrameArrived` | `Stop()` (thread de UI) dispõe `_d3dContext`/`_stagingTexture` e zera `_frameBuffer` enquanto `OnFrameArrived` (thread do WGC) pode estar no meio da cópia. Tirar a assinatura do evento não espera o callback em andamento. Provável. | Exceção dentro de um callback WinRT nativo — o handler global não segura isso. Crash ocasional ao parar de compartilhar, dependendo do timing. |
| A6 | `RoomWindow.OnLoaded` | `_audioMixer.Start()` abre o `WaveOutEvent` sem try/catch, antes do `ConnectAsync`. | Máquina sem dispositivo de saída de áudio (ou dispositivo em uso exclusivo): `MmException` num `async void` → MessageBox de erro e **a sala não conecta**. Deveria degradar pra "sem áudio". |
| A7 | `RoomWindow.ReconnectLoopAsync` | Só captura `SocketException/IOException/InvalidDataException`. `ConnectAsync` também pode lançar `JsonException` (resposta malformada, versão diferente), `ObjectDisposedException`. | Exceção não prevista mata a task silenciosamente; o banner fica preso em "Reconectando..." e nunca mais tenta. |
| A8 | `RoomClient.ConnectAsync` | Sem timeout próprio. `TcpClient.ConnectAsync` pra um IP que não responde leva ~20 s (retentativas de SYN do Windows). | "Conectando na sala..." por 20 s sem cancelar; na reconexão, cada tentativa pode levar 20 s em vez de 3. |
| A9 | `FfmpegEncoder.Stop`, `FfmpegDecoder.Stop`, `ProcessLoopbackCapture.Stop` | `WaitForExit(2000)` + `Thread.Sleep(200)` / `Join(2000)`, todos chamados **no thread de UI** (`StopWatching`, `RemoveTile`, `ClearRemoteTiles`, `StopSharing`, `Shutdown`, troca de qualidade). | Sair da sala com 3 telas, reconectar, ou trocar qualidade pode travar a janela por segundos. Normalmente os processos saem rápido, mas o pior caso é 2 s por decoder. |
| A10 | `SystemAudioCapture.TryStartMixed` | A lista de PIDs excluídos (Discord) é um snapshot no início. O rescan a cada 3 s só adiciona apps novos — não reavalia o Discord. | Abrir o Discord **depois** de começar a compartilhar: o áudio dele entra na transmissão. |
| A11 | `RoomWindow.OnLocalFrameCaptured` | Quando a resolução muda, cada frame que chega antes do dispatcher processar o primeiro `RestartStreamingForNewSize` enfileira **outro** restart. | Vários restarts seguidos do encoder numa troca de resolução (ex.: jogo entrando em tela cheia) — engasgo maior que o necessário. |
| A12 | `RoomWindow.StartAudioCapture` | `_audioCapture.Notice += ...` é assinado toda vez que começa a compartilhar e nunca dessassinado. | Handler acumula a cada compartilhamento (vazamento pequeno; avisos escritos N vezes). |
| A13 | `RoomWindow.OnLoaded` | `_bestEncoder` só é detectado depois do connect (`DetectBestVideoEncoderAsync`). | Clicar "Compartilhar" nos primeiros ~300 ms usa `libx264` (CPU) mesmo com NVENC disponível. |
| A14 | `RoomServer.HandleControlClientAsync` | Tokens em `_pendingMediaTokens` nunca expiram; um participante que faz o handshake de controle e nunca conecta a mídia fica na sala e recebe mídia numa fila sem leitor (até 8 MB). | Vazamento pequeno por conexão pela metade; a busca por TCP (`Discover`) não passa por aqui, então é raro. |
| A15 | `RoomServer.HandleControlClientAsync` / `RoomClient.ReceiveControlLoopAsync` | `RoomEnvelope.FromJsonBytes` lança `JsonException`, que **não** está nos filtros de `catch` (só `InvalidDataException`). | Dado malformado no canal de controle (versão diferente, lixo) mata o loop sem `Disconnected` no cliente; no servidor é exceção não observada. |

## B. Defeitos de comportamento / qualidade percebida

| # | Onde | O quê |
| --- | --- | --- |
| B1 | `FfmpegEncoder.PumpStdOutAsync` | Os "chunks" são leituras de 64 KB do pipe, **não frames**. Todo o descarte alinhado a keyframe (`H264Inspector`, `MediaQueue`, resync) trabalha com granularidade de chunk: um descarte pode cortar um frame no meio (inclusive um keyframe → corrupção até o próximo), e o "10 frames perdidos por janela" do monitor de congestionamento é na verdade "10 chunks" (um keyframe de 1080p60 são ~4 chunks). Alinhar a saída por Access Unit (start code + tipo de NAL) deixaria descarte, contagem e resync exatos. |
| B2 | Áudio (`RoomClient`, `RemoteAudioMixer`) | **Áudio vai em PCM 16 bits sem compressão: 1,5 Mbps por stream.** Com o fan-out do relay, 4 pessoas compartilhando = 3 streams × 3 destinos × 1,5 Mbps = 13,5 Mbps de upload do anfitrião só de áudio. Opus a 96 kbps corta isso 16×. Foi uma decisão razoável no começo (evitar mux no ffmpeg); com a topologia de relay virou o ganho mais barato pro upload do anfitrião. |
| B3 | `FfmpegEncoder.BuildVideoEncoderArgs` | O comentário do NVENC explica que usa `-rc vbr` pra evitar filler em tela parada; **o código usa `-rc cbr`**. Documentação contradiz o código, e CBR gasta o bitrate cheio com a tela parada — multiplicado pelo fan-out. VBR com `-maxrate` merece ser medido. |
| B4 | Vídeo + áudio | Não há sincronização A/V. Vídeo e áudio têm buffers independentes (fila de 300 ms + decoder; áudio corta em 400 ms). Podem derivar até ~0,5 s um do outro, e nada corrige. |
| B5 | `RoomWindow.StatusText` | É o único canal de feedback: avisos de congestionamento, "fulano entrou", linhas do ffmpeg (`[ffmpeg] ...`), log do servidor, tudo na mesma linha. O aviso importante some em 1 s quando alguém entra. |
| B6 | `RoomProtocol` | Sem número de versão no handshake. Build diferente nos dois lados dá sintomas confusos ("Handshake inválido", tile sem vídeo) em vez de "atualize o app". Já aconteceu na prática (a sala "app desatualizado" do diagnóstico). |
| B7 | `FfmpegDecoder` | `FfmpegLogLine` do decoder nunca é assinado pela janela. Erros de decode (d3d11va falhando, "non-existing PPS") são invisíveis — o usuário vê "Recebendo dados, mas não consegui decodificar" sem motivo. |
| B8 | `FfmpegDecoder` | Lê frames de `Width×Height×4` bytes fixos, mas o ffmpeg emite no tamanho do stream. Se anunciado e real divergirem (corrida no reanúncio, SPS mudando no meio), a imagem vira lixo. `-s WxH` na saída do decoder tornaria isso robusto. |
| B9 | `RoomWindow.RelayoutTiles` | Tile limitado a 760 px de largura. Num monitor 4K com uma pessoa só compartilhando, a imagem fica pequena; o teto deveria escalar com a janela. |
| B10 | `RoomServer` (topologia) | Sem adaptação de bitrate. O aviso existe, a ação é manual. Combinado com B2/B3 é o que define se o anfitrião aguenta a sala. |
| B11 | Renderização (`PushFrameToTile`/`RenderTile`) | Três cópias de 8 MB por frame por tile (decoder → `PendingFrame` → `WritePixels` → GPU), a do meio no thread de UI e segurando `tile.Sync`, que bloqueia o thread do decoder enquanto a UI copia. Com 3 tiles a 60 fps é ~1,4 GB/s no thread de UI. É o item `D3DImage`/NV12 do plano de performance. |

## C. Robustez / higiene

| # | Onde | O quê |
| --- | --- | --- |
| C1 | Projeto | **Não é um repositório git.** Sem histórico, sem como voltar atrás, sem diff do que mudou entre um `.exe` e outro. É a melhoria mais barata de todas. |
| C2 | Projeto | Todas as validações (latência, mixer, descoberta, reconexão, rajada) vivem em harnesses fora do repositório. Um projeto de testes com esses mesmos cenários evitaria regressões como a das filas de 8 pedaços. |
| C3 | `RoomServer`, `RoomClient` | Listener e conexão só IPv4 (`IPAddress.Any`). Nome MagicDNS do Tailscale pode resolver pra IPv6 → falha. Listener dual-stack resolve. |
| C4 | `SessionBrowser` | `tailscale status --json` é spawnado a **cada 3 s** enquanto o menu está aberto. Cache de 30 s bastaria. |
| C5 | `RoomWindow` (volume) | `_settings.Save()` escreve o JSON no disco a cada tick do slider (dezenas de vezes por arraste). Debounce. |
| C6 | `App.OnDispatcherUnhandledException` | MessageBox por exceção, sem throttle. Uma exceção repetitiva (por frame) empilha diálogos modais. |
| C7 | `RoomWindow.Shutdown` | `_shuttingDown = true` é setado **depois** de `StopSharing()` e do dispose dos tiles; um `Disconnected` nessa janela inicia uma reconexão numa janela que está fechando. |
| C8 | `RoomServer.Participant.TryEnqueueMedia` | `_awaitingKeyframe`/`_resyncStartedAt` sem sincronização, chamados pelos loops de leitura de **vários** publicadores ao mesmo tempo. Efeito é só uma transição perdida, mas é corrida. |
| C9 | `RoomServer` | Sem limite de participantes (o brief original falava em 4–5). Sem controle de acesso, por decisão — mas nem um teto existe. |
| C10 | Firewall | O app nunca cria regra; depende do prompt do Windows na primeira execução, que costuma liberar só "Privada". Um helper que cria a regra (com elevação) ou pelo menos detecta a ausência dela evitaria o diagnóstico manual. |
| C11 | `FfmpegEncoder`/`ScreenCaptureService` | Dois pacers (captura descarta acima de `TargetFps`, encoder repete a `Fps`) → batimento entre os dois relógios; frames duplicados/pulados de vez em quando mesmo com fonte estável. |
| C12 | `MessageFraming` | Comentário fala em "stream MPEG-TS" — obsoleto (é H.264 cru). `RestartStreamingWithCurrentQuality` tem dois `<summary>`. `_resyncCount` nunca é lido. |

## D. O que eu faria primeiro

1. **Heartbeat no canal de controle** (A1) e `Disconnected` também quando a mídia cai (A2) — sem isso a reconexão automática não cobre o caso real.
2. **`RoomClosed`** quando o anfitrião sai (A3) + teto de tentativas com botão "voltar ao menu".
3. **Áudio em Opus** (B2) e **VBR** (B3) — os dois maiores cortes no upload do anfitrião, sem tocar na topologia.
4. **Chunks alinhados a frame** (B1) — deixa todo o mecanismo de descarte exato.
5. **git + projeto de testes** (C1, C2).
6. Os pequenos: A4, A6, A7, A8, A10, A12, C5.

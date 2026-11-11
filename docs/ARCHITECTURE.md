# MGRScreen — Arquitetura

> Projeto pessoal/hobby de screen+audio sharing em C#/.NET 8 para um grupo pequeno de amigos.
> Prioridade: simplicidade > escalabilidade. Sem backend hospedado, sem contas.

## 0-A. Mudança de modelo: de "1 Host → N Viewers" para **sala**

O desenho original era unidirecional: um Host transmitia e os outros só assistiam. Isso mudou —
agora é uma **sala**, onde qualquer participante pode compartilhar a tela a qualquer momento
(inclusive várias pessoas ao mesmo tempo), tipo uma call só de compartilhamento de tela.

Quem "cria a sala" sobe um <see>RoomServer</see> e **entra nela como um participante comum**
(o cliente dele conecta em 127.0.0.1). Não existe caminho de código especial pro anfitrião: a
única diferença é que o processo dele também hospeda o servidor, e é o IP dele que os amigos
usam pra entrar.

O servidor é um repassador burro: recebe os bytes de quem está publicando, carimba o id de quem
originou e manda pra todos os outros (nunca de volta pra quem enviou). Cada participante mantém
um decoder por stream remoto e mostra tudo numa grade.

Consequências práticas:
- Ainda é preciso **uma** pessoa acessível na rede (port forwarding ou Tailscale) — a da sala.
- Cada tela compartilhada é um encode independente na máquina de quem compartilha, e um decode
  independente na máquina de quem assiste. Com 4-5 pessoas compartilhando ao mesmo tempo isso
  pesa; na prática 1-2 telas simultâneas é o uso normal.

## 0. O que foi realmente validado nesta sessão

Esta máquina não tinha o .NET SDK nem ffmpeg instalados quando começamos — instalei o SDK
(`winget install Microsoft.DotNet.SDK.8`) e usei o FFmpeg que você já tinha via winget
(`Gyan.FFmpeg`) pra poder **compilar e testar de verdade**, não só escrever código "no papel".
Isso mudou o plano em pontos concretos — resumo do que foi validado e do que foi corrigido:

- ✅ **Compila**: solução inteira (`MGRScreen.Core` + `MGRScreen.App`, WPF) compila limpo,
  incluindo o interop WGC (`Windows.Graphics.Capture`) e o Direct3D11 bridging.
- ✅ **FFmpeg encoder**: `FfmpegLocator` detecta corretamente hardware disponível (nesta máquina,
  `h264_nvenc`) e `FfmpegEncoder` produz vídeo H.264 válido (confirmado com `ffprobe`).
- ✅ **Rede**: handshake do control channel (aceitar/rejeitar entrada), fan-out do media
  channel pra múltiplos Viewers, e desconexão — tudo testado com sockets reais em loopback.
- ⚠️ **Corrigido — container do stream ao vivo**: o plano original usava MPEG-TS pro media
  channel. Testes de ponta a ponta mostraram que o *demuxer* mpegts do ffmpeg, quando alimentado
  **incrementalmente via pipe** (em vez de um arquivo completo), tem probing pouco confiável —
  em alguns testes o decoder simplesmente nunca decodificava nada. Trocamos para **H.264
  elementary stream cru (Annex-B)**, que decodificou de forma muito mais previsível nesses
  mesmos testes. Efeito colateral: elementary stream não carrega áudio, então **vídeo e áudio
  não são mais muxados juntos** — ver seção 3 pro novo desenho.
- ⚠️ **Corrigido — bug real de ordenação em `FfmpegDecoder.Stop()`**: a implementação inicial
  cancelava o `CancellationToken` *antes* de fechar o stdin do processo. Decoders H.264 retêm os
  últimos frames num buffer interno e só os liberam ao ver EOF — cancelar antes de fechar o
  stdin abortava a leitura do stdout bem no momento em que esses frames finais chegariam,
  perdendo-os silenciosamente. Um teste de ponta a ponta (60 frames enviados) capturou isso
  concretamente: só 44 decodificavam antes do fix, 58 depois (os 2 finais se perdem por motivo
  não relacionado — o encoder é interrompido antes de finalizar o último GOP, efeito esperado
  ao cortar um stream ao vivo abruptamente). `Stop()` agora fecha o stdin primeiro, espera o
  processo sair, *depois* cancela.
- ⚠️ **Descoberto — latência residual de ~1s no decode**: mesmo com o fix acima, o decoder fica
  sistematicamente ~14-16 frames atrás do encoder durante o streaming contínuo (não é só um
  efeito de encerramento). Isso não impede o funcionamento, mas conflita com o objetivo de
  "menor latência possível" — ver seção 6 (gargalos) pra hipótese mais provável e próximo passo
  de tuning. **Não foi resolvido nesta sessão**, fica como item concreto do checklist.
- 🔲 **Não testado**: captura de tela real (WGC) e captura de áudio real (WASAPI loopback) —
  ambas exigem uma janela WPF interativa (o picker de tela é uma UI nativa do Windows) e não dá
  pra automatizar numa sessão de terminal. O código compila e segue os padrões documentados da
  API, mas é o primeiro lugar a testar manualmente (checklist item 1).

Ou seja: a "espinha dorsal" (rede + encode + decode, a parte mais arriscada de interação entre
processos) foi validada com testes reais de ponta a ponta e teve bugs reais corrigidos. A
captura de tela/áudio em si (mais simples de validar, só requer clicar em botões) fica pro seu
primeiro teste manual — comece pelo checklist, item 1.

## 0-B. Correções da rodada "sala + áudio + FPS"

Quatro problemas reportados em uso real e o que foi feito em cada um:

1. **Borda amarela em volta da tela capturada** — é o Windows sinalizando captura ativa. Removida
   com `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` + `session.IsBorderRequired = false`
   (API só do Windows 11, daí o TFM ter subido pra `10.0.22621.0`; o `SupportedOSPlatformVersion`
   continua em 19041 e as chamadas são guardadas por `try/catch`, então segue rodando no Win10 —
   só que lá a borda permanece). Verificado nesta máquina: a permissão volta `Allowed`.

2. **Áudio não saía** — duas causas prováveis, ambas corrigidas:
   - O combo de dispositivos vinha com `SelectedIndex = 0`, ou seja, o **primeiro da lista**, não
     o **padrão do Windows**. Nesta máquina o primeiro era "Alto-falantes (Steam Streaming
     Speakers)" — capturar loopback dele dava silêncio. Agora seleciona o marcado como padrão.
   - O `WasapiLoopbackCapture` entrega **IEEE Float 32-bit**, e esses bytes eram mandados crus pra
     o `WaveOutEvent` do outro lado, que frequentemente não toca float. Agora o áudio é convertido
     pra **PCM 16-bit** na captura (metade da banda, e toca em qualquer lugar).
   - Extra: a UI ganhou um **medidor de nível** ao lado do seletor, pra dar pra ver na hora se o
     dispositivo escolhido está captando alguma coisa.

3. **FPS baixo pros outros (20) apesar de mostrar 60 aqui** — o contador antigo media *frames
   capturados*, não *frames que saíam do encoder*; a diferença toda estava sendo engolida. As
   causas e o que mudou:
   - Cada frame alocava um `byte[]` de ~8 MB. A 60 fps são ~500 MB/s indo pro **Large Object
     Heap**, o que provoca coletas Gen2 constantes. Agora captura e decoder **reutilizam buffers**
     (zero alocação por frame).
   - As escritas no stdin do ffmpeg eram disparadas com `_ = WriteVideoFrameAsync(...)` sem
     serialização: várias escritas de 8 MB podiam ficar pendentes/intercaladas no mesmo pipe.
     Agora existe **um único writer** alimentando o ffmpeg num ritmo fixo (`PeriodicTimer` na taxa
     alvo), com buffer duplo — se a captura vier mais rápido, descarta; se vier mais devagar,
     repete o último frame pra manter a taxa constante que o `-r` do ffmpeg espera.
   - No lado de quem assiste, cada frame decodificado fazia um `Dispatcher.BeginInvoke`. Se a UI
     não acompanhasse, a fila crescia sem limite (atraso crescente). Agora há **no máximo um
     render agendado por vez** e os frames intermediários são sobrescritos.
   - A UI agora mostra o **FPS realmente codificado** ("Enviando X fps"), que é o número que
     corresponde ao que os amigos veem.
   - O seletor de qualidade passou a **reduzir a resolução de verdade** (`-vf scale`), não só o
     bitrate — antes "720p" mandava a resolução nativa mesmo assim.

4. **Múltiplas pessoas compartilhando** — ver seção 0-A.

## 0-C. Rodada "tela preta + áudio do Discord + parar de assistir"

- **Áudio do Discord**: agora é excluído de verdade pelo Windows (ver o aviso no início da seção
  3). Nada de cabo virtual.

- **Tela preta ao assistir os outros** — não achei uma causa única reproduzível, então ataquei
  todas as que consegui identificar no código, e adicionei diagnóstico pro caso de sobrar alguma:
  - **Corrida de threads real**: o dicionário de tiles era um `Dictionary` comum lido pela thread
    de rede a cada chunk enquanto a thread de UI adicionava/removia tiles. Essa corrida pode fazer
    a busca falhar silenciosamente e o vídeo nunca chegar ao decoder. Virou `ConcurrentDictionary`.
  - **Mudança de resolução no meio da transmissão**: o ffmpeg é iniciado com `-s LARGURAxALTURA`
    fixo. Se a tela muda de resolução depois (é exatamente o que um jogo faz ao entrar em tela
    cheia), os frames passam a ter outro tamanho e o stream vira lixo pra quem assiste — enquanto
    o preview de quem compartilha continua perfeito, porque ele usa o tamanho do frame. Agora
    detectamos a mudança, reiniciamos o encoder e reavisamos a sala com a nova resolução.
  - **Encoder de hardware que falha**: se o ffmpeg morre (ex: NVENC listado mas indisponível), o
    sintoma pro resto da sala era só "tela preta". Agora o processo é monitorado e cai
    automaticamente pra `libx264` (CPU).
  - **Jogo em tela cheia exclusiva**: o Windows não deixa a WGC capturar — vêm frames pretos.
    Não há como resolver por código; agora o app **detecta** (amostra ~400 pixels por segundo) e
    avisa quem está compartilhando pra trocar para "tela cheia sem bordas".
  - **Cabeçalhos H.264 pra quem entra depois**: adicionado `-bsf:v dump_extra=freq=keyframe` e GOP
    de 1s, pra que quem chega no meio da transmissão consiga "engatar" rápido. Medido: primeiro
    frame em <1s tanto com NVENC quanto com libx264. (Nos testes daqui o stream já se recuperava
    sem isso, então não era *a* causa — mas é proteção barata pra encoders que não repetem os
    cabeçalhos sozinhos.)
  - **Diagnóstico na UI**: cada tile mostra `fps · KB/s`. Isso separa "não está chegando nada pela
    rede" de "está chegando mas não decodifica" — os dois pareciam a mesma tela preta.

### Eco entre telas e modos de áudio

**O eco**: se eu estou assistindo a tela do amigo, o MGRScreen toca o áudio dele no meu PC. Como
a captura de áudio pegava *tudo* que sai do meu PC, ela recapturava esse áudio e mandava de volta
— o amigo se ouvia com atraso.

A correção é excluir o **próprio MGRScreen** da captura. A API do Windows aceita **uma única**
árvore de processos por captura (incluir OU excluir) — então "tudo menos o MGRScreen e menos o
Discord" não sai de uma captura só. Sai de várias: abrimos uma captura em modo <i>incluir</i>
para <b>cada app que está tocando algo</b> e somamos os streams
([MixedProcessLoopbackCapture.cs](../src/MGRScreen.Core/Audio/MixedProcessLoopbackCapture.cs)).

| Modo | Eco? | Discord? | Como |
|---|---|---|---|
| **Tudo, menos MGRScreen e Discord** (padrão) | não | não | N capturas (uma por app) somadas |
| Tudo, menos o MGRScreen | não | entra | 1 captura, excluindo nosso processo |
| Tudo, menos o Discord | sim, se estiver assistindo alguém | não | 1 captura, excluindo o Discord |
| Apenas um app (o jogo) | não | não | 1 captura, incluindo só aquele app |
| Sem áudio | — | — | — |

Detalhes do modo misturado:
- Os apps são redescobertos a cada 3 s, então algo que começa a tocar depois entra com esse atraso.
- É preciso descartar processos cuja **árvore já está coberta** por outro que vamos capturar
  (navegadores e apps Electron têm vários processos) — senão o mesmo áudio entraria duas vezes,
  dobrando o volume. O parentesco vem de `CreateToolhelp32Snapshot`
  ([ProcessTreeHelper.cs](../src/MGRScreen.Core/Audio/ProcessTreeHelper.cs)).
- O mesmo mecanismo tira o `ffmpeg` da conta automaticamente: ele é processo filho nosso, e
  qualquer descendente de um PID excluído é descartado.

Verificação do modo misturado: um app separado (`tonegen.exe`, iniciado destacado pra não ser
filho do processo de teste) tocava 880 Hz enquanto o processo de teste tocava 440 Hz.

| Sinal | Energia medida |
|---|---|
| 440 Hz (o próprio processo — fonte do eco) | 0,0001 |
| 880 Hz (o outro app) | 0,2933 |

6 apps foram capturados e misturados simultaneamente nesse teste. A exclusão do Discord usa
exatamente o mesmo caminho de código da exclusão do próprio processo (que está comprovada); não
deu pra exercitá-la de ponta a ponta porque o Discord não estava aberto na máquina de teste.

Verificação: medir o pico do áudio não servia (havia música tocando na máquina de teste, o que
mascarava tudo). Medimos então a energia **exatamente em 440 Hz** (filtro de Goertzel) enquanto o
processo de teste tocava um tom nessa frequência, a partir da thread STA:

| Cenário | Energia em 440 Hz |
|---|---|
| Sem eco, sem tocar o tom (referência) | 0,0017 |
| Sem eco, tocando o tom | 0,0042 (piso de ruído — não entrou) |
| Apenas um app, tocando o tom | 0,2874 (68× maior) |

### Qualidade de transmissão

Mudanças com efeito real na imagem, todas validadas rodando os 5 presets × 2 encoders
(NVENC e libx264) com encode+decode de verdade — um argumento errado mataria o ffmpeg e daria
"tela preta" pros outros:

- **NVENC: preset `p1` → `p4`.** O p1 é o mais rápido e o de pior qualidade. Como o NVENC roda em
  hardware dedicado, subir para p4 melhora a imagem (principalmente texto) praticamente de graça.
- **CBR → VBR com teto.** Em CBR o encoder injeta *filler data* pra manter a taxa mesmo com a tela
  parada — banda desperdiçada. Com VBR limitado ao mesmo teto, esses bits sobram pros momentos de
  movimento.
- **Redimensionamento `fast_bilinear` → `bicubic`.** Ao reduzir (ex: monitor 1440p compartilhando
  em 1080p), o fast_bilinear borrava texto a ponto de atrapalhar a leitura.
- **Novo preset 720p60** (6 Mbps): metade dos pixels do 1080p, então rende movimento fluido com
  bem menos banda — boa opção pra jogo quando a rede não aguenta 1080p60.
- libx264 continua em `ultrafast` de propósito: quem cai nele não tem encoder de hardware e já
  está com a CPU no limite.

### Crash ao compartilhar (apartamento COM) — corrigido

O primeiro uso real da exclusão do Discord derrubava o app inteiro ao clicar em "Compartilhar
minha tela". O log (`%TEMP%\mgrscreen_crash.log`, gravado pelo handler global em `App.xaml.cs`)
apontou direto:

```
InvalidCastException: QueryInterface para IAudioCaptureClient falhou (E_NOINTERFACE)
   at ProcessLoopbackCapture.CaptureLoop
```

Causa: os objetos COM do WASAPI **não são "agile"** — não podem ser usados de um apartamento COM
diferente daquele em que foram criados. O app criava o `IAudioClient`/`IAudioCaptureClient` na
thread da UI (**STA**) e depois os usava na thread de captura (**MTA**); a primeira chamada
cruzando apartamentos falhava e matava o processo.

Isso **não aparecia no teste automatizado** porque um app de console roda em MTA — a criação e o
uso caíam no mesmo apartamento. A lição prática: para interop COM, o teste precisa reproduzir o
apartamento real do chamador. O teste foi reescrito como app WPF chamando da thread STA.

Correção: `ProcessLoopbackCapture` agora cria uma **thread MTA dedicada** e faz TODO o ciclo de
vida COM nela (ativação, `Initialize`, `GetService`, loop de leitura e liberação dos objetos).
A thread de UI só dispara e espera o resultado da inicialização.

Verificação diferencial (rodando da thread STA, como o app real):

| Cenário | Pico captado |
|---|---|
| Excluindo o próprio processo, sem tocar nada | 0,823 (áudio de outros apps) |
| Excluindo o próprio processo, tocando um tom | 0,013 (o tom não vazou) |
| Incluindo o próprio processo, tocando o tom | 0,251 (tom captado) |

Também corrigido no mesmo log: `ObjectDisposedException` em `FfmpegDecoder.WriteEncodedChunkAsync`
— corrida normal entre chunks em trânsito e o `Stop()` do decoder (acontece toda vez que alguém
clica em "parar de assistir"). Agora é tratada em vez de virar exceção não observada.

- **Parar de assistir**: cada tile tem um botão. Ao parar, o decoder e o áudio daquele
  participante são encerrados (economiza CPU de verdade, não é só esconder a imagem) e ele
  continua compartilhando normalmente pros outros. Ao voltar, o decoder é recriado e a imagem
  reaparece no próximo keyframe (~1s). O preview local também pode ser ocultado.

---

## 1. Arquitetura geral (diagrama)

```
┌──────────────────────────────── HOST ──────────────────────────────────┐
│                                                                          │
│  ┌────────────────┐      ┌──────────────────┐        ┌────────────────┐│
│  │ ScreenCapture   │ BGRA │  FfmpegEncoder    │ H.264  │  HostServer    ││
│  │ (Windows.Graphics│─────▶│  (processo ffmpeg│ cru   │  - control TCP ││
│  │  .Capture, WGC) │frames│   stdin=vídeo raw │(Annex-B)│   (JSON,      ││
│  │  60fps 1080p    │      │   stdout=stream)  │        │    handshake, ││
│  └────────────────┘      └──────────────────┘        │    stats)     ││
│                                                         │  - media TCP   ││
│  ┌────────────────┐                              PCM   │   (vídeo H.264 ││
│  │ SystemAudio     │ cru (sem passar pelo ffmpeg) ────▶│   + áudio PCM  ││
│  │ Capture (NAudio │                                    │   multiplexados││
│  │ WASAPI loopback)│                                    │   por tag,     ││
│  └────────────────┘                                    │   relay p/ N   ││
│                                                          │   viewers)     ││
└──────────────────────────────────────────────────────────────┬──────────┘
                                                                │ Internet
                                                  (port forward │ TCP)
                    ┌───────────────────────────────────────────┴──┐
                    │                    ...N viewers               │
┌───────────────────▼──────────────────┐    ┌───────────────────────▼──┐
│              VIEWER #1                │    │        VIEWER #2 ...     │
│  ┌───────────┐   ┌────────────────┐  │    │        (idem)            │
│  │ ViewerClient│─┬▶│ FfmpegDecoder  │  │    └──────────────────────────┘
│  │ (TCP recv, │ │H.264│(processo ffmpeg│  │
│  │ desmultiplexa│ │  stdin=H.264 cru│  │
│  │ tag vídeo/  │ │  stdout=BGRA raw│  │
│  │ áudio)      │ └───────┬────────┘  │
│  │            │          │ frames     │
│  │            │  ┌───────▼────────┐  │
│  │            │  │WPF WriteableBitmap│ (janela + overlay fps/latência)
│  │            │  └────────────────┘  │
│  │            └PCM──────────────────┐│
│  │                    ┌──────────────▼┐
│  └────────────────────▶ NAudio        │ (WaveOutEvent, toca direto — sem
│                        │ BufferedWave  │  passar pelo ffmpeg)
│                        │ Provider      │
│                        └───────────────┘
└────────────────────────────────────────┘
```

**Ideia central:** o Host encoda **uma única vez** (um processo `ffmpeg`), e o `HostServer` em
C# faz o fan-out dos bytes já encodados para cada Viewer conectado via um socket TCP dedicado
("media channel"). Isso evita reencodar por espectador e é a peça que responde ao requisito de
suportar vários amigos assistindo ao mesmo tempo.

Há dois canais TCP separados por simplicidade de protocolo:
- **Control channel**: mensagens JSON pequenas e esporádicas (handshake com nickname/senha da
  sala, resolução/fps acordados, estatísticas de fps/latência). Enquadradas com prefixo de
  tamanho (4 bytes).
- **Media channel**: carrega vídeo (H.264 cru, saindo do `ffmpeg` do Host) e áudio (PCM cru,
  saindo direto do `SystemAudioCapture`, **sem passar pelo ffmpeg**) multiplexados na mesma
  conexão TCP por Viewer. Cada um dos dois é enquadrado com um cabeçalho leve (1 byte de
  tipo + 4 bytes de tamanho — ver `MediaFraming.cs`) só pra o lado do Viewer saber pra onde
  encaminhar os bytes (vídeo → `FfmpegDecoder`, áudio → `NAudio.BufferedWaveProvider` direto).

  **Por que vídeo e áudio não são muxados num único container (tipo MPEG-TS) como o plano
  original previa:** validado nesta sessão com testes de ponta a ponta reais — o demuxer
  MPEG-TS do ffmpeg, quando alimentado incrementalmente via pipe (stream "ao vivo"), tem
  comportamento de probing pouco confiável (chegou a não decodificar nada em alguns testes).
  H.264 elementary stream cru decodificou de forma muito mais previsível, mas não carrega
  áudio — daí o áudio ir separado, sem overhead nenhum de encode/decode (é só PCM relayado).
  Custo: mais bytes na rede (PCM cru é maior que AAC), o que é uma troca aceitável pra um
  projeto hobby que prioriza confiabilidade sobre eficiência de banda.

---

## 2. Bibliotecas / pacotes NuGet por parte

| Parte                         | Pacote(s) NuGet                                   | Observação |
|-------------------------------|----------------------------------------------------|------------|
| Captura de tela               | *(nenhum extra)* — `Windows.Graphics.Capture` vem de graça ao usar `TargetFramework=net8.0-windows10.0.19041.0` | Só precisa do TFM certo no `.csproj` |
| Interop D3D11 p/ WGC          | `Vortice.Direct3D11` + `Vortice.DXGI` (3.8.x)      | Substitui SharpDX (descontinuado); não existe mais um pacote "Vortice.Windows" guarda-chuva |
| Captura de áudio               | `NAudio` (2.2.x)                                  | `WasapiLoopbackCapture` já pronto |
| Execução do FFmpeg             | *(nenhum)* — `System.Diagnostics.Process` puro    | Binário `ffmpeg.exe` fica em `tools/ffmpeg/` |
| UI                             | WPF (`UseWPF=true`, incluso no SDK)                | Ver decisão na seção 4 |
| MVVM helpers (opcional)        | `CommunityToolkit.Mvvm`                           | `ObservableObject`, `RelayCommand` |
| JSON do protocolo de controle  | `System.Text.Json` (já vem no BCL)                | Sem pacote extra |

Não uso `FFmpeg.AutoGen` nem bindings in-process de FFmpeg — ver justificativa na seção 4.

---

## 3. Áudio: como excluir o Discord do "mix geral do sistema"

Fato técnico importante que decide isso: a API nativa de "captura de loopback por processo" do
Windows (`AUDIOCLIENT_ACTIVATION_PARAMS` com `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`,
usada via `ActivateAudioInterfaceAsync`) é **allow-list, não deny-list**: ela permite capturar
"só o áudio do processo X (e filhos)", mas **não existe** um modo "todo mundo, exceto o processo
X". Pra imitar "sistema inteiro menos Discord" com essa API você precisaria enumerar todos os
outros processos que emitem áudio, ativar uma sessão de captura por processo para cada um, e
mixar tudo isso manualmente em tempo real — e re-detectar processos novos que abrirem depois
(navegador, Spotify, um jogo). É frágil e muito trabalho de manutenção para um projeto hobby.

> ⚠️ **CORREÇÃO (rodada posterior): o parágrafo acima estava ERRADO.** A Application Loopback API
> do Windows **tem sim** modo de exclusão: `AUDIOCLIENT_ACTIVATION_PARAMS.ProcessLoopbackMode`
> aceita `PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE`, não só o modo de inclusão. Ou seja,
> dá pra pedir "o mix do sistema inteiro, exceto a árvore de processos do Discord" — sem cabo
> virtual e sem configuração manual.
>
> Isso está implementado em [ProcessLoopbackCapture.cs](../src/MGRScreen.Core/Audio/ProcessLoopbackCapture.cs)
> e é o caminho padrão quando a caixa "Não compartilhar o áudio do Discord" está marcada
> (`SystemAudioCapture` cai pro loopback normal se o Discord não estiver aberto ou se a API não
> existir, como no Windows 10 antigo).
>
> **Validado com teste real**: capturamos o áudio do sistema excluindo o *próprio processo de
> teste* enquanto ele tocava um tom de 440 Hz. Excluindo → pico 0.000 (mas 743 KB capturados, ou
> seja, o stream estava vivo, só sem aquele áudio). Incluindo → pico 0.281. A exclusão funciona.
>
> A abordagem do VB-Cable abaixo continua documentada como alternativa manual (útil se você quiser
> excluir algum outro app específico, ou no Windows 10 antigo), mas não é mais necessária.

**Abordagem manual alternativa: VB-Audio Virtual Cable como dispositivo de saída do Discord.**

Motivo: zero código de interop COM frágil, configuração de ~2 minutos, extremamente confiável
(é o que a maioria dos streamers já faz manualmente). O app só precisa deixar o usuário escolher
*qual dispositivo de gravação* usar para o loopback — a "mágica" de excluir o Discord acontece
inteiramente na configuração do Windows, fora do nosso código.

Passo a passo (documentar isso no README/setup do app):

1. Instalar [VB-Audio Virtual Cable](https://vb-audio.com/Cable/) (gratuito).
2. Windows → Configurações de Som → **Volume do app e preferências de dispositivo**:
   - Defina a saída do **Discord** especificamente para os seus fones/caixa de som reais
     (dispositivo físico normal).
   - Deixe todos os outros apps (jogo, navegador, Spotify, etc.) na saída **padrão**, que você
     vai trocar para "CABLE Input (VB-Audio Virtual Cable)" nas configurações de som padrão.
3. (Opcional, só pra você continuar ouvindo os outros apps) Painel de Som clássico → aba
   *Gravação* → **CABLE Output** → Propriedades → aba *Escutar* → "Escutar este dispositivo",
   roteando para seus fones reais.
4. No MGRScreen (Host), na tela de configuração de áudio, selecione **"CABLE Input (VB-Audio
   Virtual Cable)"** como dispositivo de **reprodução** a capturar via loopback — pronto, é o
   mix do sistema sem o Discord, porque o Discord nunca passou por lá.
   (Reparar: `WasapiLoopbackCapture` sempre opera sobre um dispositivo de **saída/reprodução**,
   nunca sobre um dispositivo de gravação — por isso é "CABLE Input", o lado "alto-falante"
   do cabo virtual, e não "CABLE Output".)

O `SystemAudioCapture` no código ([SystemAudioCapture.cs](../src/MGRScreen.Core/Audio/SystemAudioCapture.cs))
é **agnóstico**: ele só recebe um `MMDevice` de reprodução para fazer loopback. Não há nenhuma
lógica "exclua o Discord.exe" hardcoded — é só uma questão de qual dispositivo de saída o
usuário escolhe no dropdown. Isso também significa: se o usuário não usa Discord, ou não se
importa, ele simplesmente escolhe o dispositivo de saída padrão normal (seus fones/caixas) e
captura tudo.

**Limitações da abordagem (a):**
- Precisa instalar um driver de áudio virtual (VB-Cable) — é gratuito e leve, mas é uma
  instalação extra fora do nosso app.
- Se o usuário trocar o dispositivo de saída padrão do Windows sem querer, o roteamento quebra
  até reconfigurar.
- Adiciona ~alguns ms de latência de áudio (irrelevante pro nosso caso, streaming já tem
  latência de rede maior que isso).

Se um dia quiser evoluir para (b)/(c) (per-process include-mode, capturando *só* apps
específicos ao invés de excluir o Discord), o ponto de partida é `ActivateAudioInterfaceAsync`
com `AUDIOCLIENT_ACTIVATION_PARAMS.ProcessLoopbackParams.TargetProcessId` — mas para o objetivo
atual (tudo, exceto Discord) isso é estritamente pior que a abordagem (a).

---

## 4. Decisões de arquitetura (com justificativa)

### WPF vs. WinUI 3 → **WPF**
WinUI 3 tem visual mais moderno, mas empacotamento é mais chato (depende do Windows App SDK
runtime, MSIX ou bundling extra) — ruim pro requisito "baixar pasta zip e rodar". WPF publica
como `self-contained` de forma trivial (`dotnet publish -r win-x64 --self-contained`), roda em
Win10/11 sem instalar nada além do próprio app, e tem WriteableBitmap/Image nativos que
encaixam perfeitamente no pipeline de frames raw que vamos gerar. Decisão: **WPF**.

### Um único app vs. `MGRScreen.Host` + `MGRScreen.Viewer` separados → **um único app**
A sugestão original do pedido tinha 3 projetos (`Host`, `Viewer`, `Core`). Decidi simplificar
para **2 projetos**: `MGRScreen.Core` (biblioteca, sem UI) e `MGRScreen.App` (WPF, com uma tela
inicial "Hospedar" / "Conectar"). Motivo: reduz a fricção de distribuição — cada amigo baixa
**uma pasta, um `.exe`**, sem confusão sobre qual dos dois rodar; e Host/Viewer compartilham
99% do código de rede e protocolo de qualquer forma, então não haveria ganho real em separá-los
em processos/executáveis distintos — só duplicação de shell de UI.

### FFmpeg cuidando do transporte (RTP/UDP direto) vs. C# distribuindo os bytes encodados
Prós de deixar o FFmpeg mandar via `-f rtp udp://viewer-ip:porta` diretamente:
- Menos código de rede pra escrever.
Contras (motivo pelo qual **não** escolhi essa opção):
- Pra 1 Host → N Viewers, o FFmpeg reencodaria (ou pelo menos reempacotaria) por destino, ou
  você precisaria de truques de multicast/tee que complicam o comando e não funcionam bem pela
  Internet (multicast não atravessa a maioria dos roteadores domésticos).
- UDP puro sem qualquer retransmissão tende a degradar muito mal em redes domésticas com jitter
  (perda de pacote = corrupção visível imediata, sem chance de re-pedido).

Decisão: **(b) C# recebe os bytes já encodados do FFmpeg (stdout) e distribui via TCP** para
cada Viewer. Um único encode, fan-out em memória, e TCP nos dá entrega confiável (sem
implementar retransmissão própria) — troca-se um pouco de latência teórica por robustez e
simplicidade, o que é a prioridade declarada do projeto. Se no futuro a latência do TCP incomodar
(cabeça-de-fila em perda de pacote), migrar o media channel pra UDP com um FEC simples é a
evolução natural — mas não é o MVP.

### Player: FFmpeg/FFplay como processo filho vs. FFmpeg.AutoGen in-process
`FFmpeg.AutoGen` dá mais controle (zero-copy, menos overhead de pipe), mas o custo de
aprendizado/manutenção é alto: gerenciamento manual de `AVFrame`/`AVPacket`, ponteiros não
gerenciados, e quebra a cada bump de versão do FFmpeg. Para um projeto hobby que quer "ficar
pronto rápido e fácil de manter", **processo filho `ffmpeg` fazendo decode, entregando frames
BGRA raw via stdout, renderizados em `WriteableBitmap`** é muito mais simples de debugar (dá pra
literalmente rodar o mesmo comando `ffmpeg` manualmente no terminal pra isolar problemas) e não
exige lidar com ponteiros/interop do libav. Decisão: **processo filho**, não `ffplay` puro (que
abriria sua própria janela fora do nosso controle) — usamos `ffmpeg -f h264 -i pipe:0 ... -f
rawvideo pipe:1` e desenhamos nós mesmos, o que também permite overlay de fps/latência na nossa
janela.

### NAT / Internet
MVP: **port forwarding manual** no roteador do Host (2 portas TCP: control + media, ambas
configuráveis, default sugerido 51900/51901). É a opção mais simples e é o "modo padrão".
Melhoria futura (não faz parte do MVP): hole punching UDP simples com um pequeno servidor de
rendezvous (nem precisa ser STUN de verdade — para um grupo fixo de amigos, um "sinalizador"
minúsculo rodando em qualquer VPS gratuita, ou até compartilhar IP:porta por WhatsApp, já
resolve 90% dos casos sem gastar tempo implementando ICE/STUN/TURN completo).

---

## 5. Estrutura de pastas da solução

```
MGR-Screen/
├── MGRScreen.sln
├── docs/
│   └── ARCHITECTURE.md          (este arquivo)
├── tools/
│   └── ffmpeg/                  (colocar ffmpeg.exe + ffprobe.exe aqui — ver tools/ffmpeg/README.md)
└── src/
    ├── MGRScreen.Core/           (biblioteca, sem UI, TFM net8.0-windows10.0.19041.0)
    │   ├── Capture/
    │   │   ├── CaptureInterop.cs         (P/Invoke + interfaces COM p/ WGC)
    │   │   ├── Direct3D11Helpers.cs      (criação de device D3D11 + bridge IDirect3DDevice)
    │   │   ├── MonitorEnumerator.cs      (lista monitores via EnumDisplayMonitors)
    │   │   └── ScreenCaptureService.cs   (Direct3D11CaptureFramePool -> byte[] BGRA)
    │   ├── Audio/
    │   │   ├── AudioDeviceHelper.cs      (lista dispositivos de saída p/ loopback via NAudio)
    │   │   └── SystemAudioCapture.cs     (WasapiLoopbackCapture -> byte[] PCM)
    │   ├── Encoding/
    │   │   ├── FfmpegLocator.cs          (acha ffmpeg.exe, detecta NVENC/AMF/QSV)
    │   │   ├── FfmpegEncoder.cs          (stdin=vídeo cru, stdout=H.264 cru; áudio só é
    │   │   │                              muxado via named pipe no modo StartToFile)
    │   │   └── FfmpegDecoder.cs          (stdin=H.264 cru, stdout=BGRA raw — só vídeo)
    │   ├── Networking/
    │   │   ├── MessageFraming.cs         (control channel: prefixo de tamanho + JSON)
    │   │   ├── MediaFraming.cs           (media channel: [tipo][id de quem publicou][tamanho],
    │   │   │                              multiplexa vídeo H.264 e áudio PCM de VÁRIOS
    │   │   │                              participantes na mesma conexão)
    │   │   ├── RoomProtocol.cs           (mensagens JSON: Join/Accepted/Rejected, entrou/saiu,
    │   │   │                              começou/parou de compartilhar)
    │   │   ├── RoomServer.cs             (2 TcpListeners; repassa o stream de cada publicador
    │   │   │                              pra todos os outros participantes)
    │   │   ├── RoomClient.cs             (publica o próprio stream e recebe os dos outros pelo
    │   │   │                              mesmo socket; todo participante usa esta classe)
    │   │   ├── H264Inspector.cs          (acha início de keyframe, pra ressincronizar quem
    │   │   │                              fica pra trás sem embaralhar a imagem)
    │   │   ├── SessionDiscovery.cs       (acha salas na rede: broadcast nas placas +
    │   │   │                              pergunta unicast pras máquinas do Tailscale)
    │   │   └── LocalNetworks.cs          (que redes esta máquina alcança e como se chamam)
    │   └── Models/
    │       └── SessionSettings.cs        (resolução, fps, bitrate, encoder)
    └── MGRScreen.App/            (WPF, TFM net8.0-windows10.0.22621.0)
        ├── App.xaml / App.xaml.cs        (tema + handler global de exceção)
        ├── Theme/Theme.xaml              (paleta, estilos de botão/campo/combo/cartão)
        ├── AppSettings.cs                (nickname e último IP, em %APPDATA%)
        ├── MainWindow.xaml / .cs         (nickname + criar sala + lista de salas encontradas)
        └── Views/
            └── RoomWindow.xaml / .cs     (grade de telas compartilhadas, botão de
                                           compartilhar, seletor de áudio + medidor de nível)
```

---

## 6. Gargalos de performance conhecidos e mitigação

1. **Cópia GPU→CPU por frame (staging texture + `Map`)** — é o custo mais alto do pipeline de
   captura. Mitigação: manter o `StagingTexture` já alocado e reaproveitado entre frames (não
   recriar por frame); se sobrar CPU, considerar futuramente encodar direto na GPU (NVENC aceita
   textura D3D11 diretamente, evitando o round-trip pra CPU) — fora do escopo do MVP mas é o
   próximo passo natural de otimização.
2. **Pipe stdin/stdout do FFmpeg** — pipes anônimos do `Process` no .NET têm buffer limitado;
   se o C# não ler o stdout rápido o suficiente, o FFmpeg trava esperando espaço no buffer,
   e isso propaga latência pra trás até a captura. Mitigação: sempre ler stdout numa thread
   dedicada em loop apertado (nunca compartilhar a thread que também escreve stdin), e usar um
   `BufferedStream`/tamanho de buffer generoso.
3. **GC pressure de `byte[]` por frame** — a 60fps e 1920x1080x4 bytes (~8MB/frame), alocar um
   array novo por frame gera bastante lixo. Mitigação futura: usar `ArrayPool<byte>.Shared`
   pros buffers de frame ao invés de `new byte[...]` — deixei como comentário no código, não
   implementado no MVP pra manter simples primeiro, otimizar depois de medir.
4. **Head-of-line blocking do TCP no media channel** — se um Viewer com internet ruim atrasar a
   leitura, o `Socket.Send` daquele Viewer específico pode bloquear; isso **não** deve travar o
   encode nem os outros Viewers. Mitigação: cada Viewer tem sua própria fila de envio numa thread
   própria (implementado no `HostServer` — ver `ViewerConnection.EnqueueMediaChunk`), e se a fila
   crescer demais (viewer não acompanha), descartamos aquele viewer específico ao invés de travar
   o Host.
5. **NVENC/AMF/QSV não detectado corretamente** — `ffmpeg -encoders` pode listar o encoder mas a
   GPU não suportar de fato (ex: driver desatualizado). Mitigação: `FfmpegEncoder` deve capturar
   o `stderr` do processo nos primeiros segundos; se o FFmpeg morrer/reclamar do encoder
   escolhido, cair automaticamente pro próximo da lista de prioridade (NVENC → AMF → QSV →
   libx264 ultrafast).
6. **[Descoberto nesta sessão, não resolvido] ~1s de latência de decode** — em testes de ponta
   a ponta reais, o `FfmpegDecoder` fica sistematicamente ~14-16 frames atrás do que o encoder
   já produziu, mesmo durante streaming contínuo (não é só um efeito de encerramento — só fica
   óbvio no encerramento porque aí os frames retidos finalmente aparecem de uma vez). Suspeita
   mais provável: os flags `-probesize 32k -analyzeduration 500000` fazem o ffmpeg esperar por
   esse tanto de dado/tempo antes de começar a decodificar de verdade. Próximo passo de tuning
   (não feito nesta sessão): baixar `-analyzeduration` ainda mais agora que o container é H.264
   cru (não MPEG-TS) — um teste rápido nesta sessão com `-analyzeduration 100000` sem
   `-probesize` chegou a reduzir um pouco o atraso, mas não eliminou. Vale medir com `ffprobe`/
   timestamps antes de assumir que está resolvido.

---

## 7. Checklist de implementação incremental

Siga nesta ordem — cada item é testável isoladamente antes de integrar ao próximo:

- [ ] **0. Preparar ambiente**: instalar .NET 8 SDK, baixar `ffmpeg.exe`/`ffprobe.exe` (build
      "essentials" do gyan.dev ou BtbN) e colocar em `tools/ffmpeg/`. Rodar `dotnet build`.
- [ ] **1. Captura de tela + preview local** (`ScreenCaptureService` + `HostWindow`): abrir o
      app, escolher monitor, ver o próprio monitor sendo exibido ao vivo numa `Image` da janela.
      Critério de sucesso: fps estável, sem memory leak visível ao rodar por alguns minutos.
- [ ] **2. Encoding via FFmpeg + salvar em arquivo** (`FfmpegEncoder` apontando `-f mpegts` para
      um arquivo `.ts` local ao invés de stdout): validar qualidade visual e fps reais alcançados
      com NVENC/AMF/QSV/libx264 antes de mexer em rede. Critério: o `.ts` abre no VLC com boa
      qualidade e sem soluços.
- [ ] **3. Transporte simples em LAN**: rodar Host e Viewer nas duas instâncias do app na mesma
      rede (sem port forwarding ainda), Host encoda e manda pro Viewer via `HostServer`/
      `ViewerClient`, Viewer decoda com `FfmpegDecoder` e mostra na tela. Critério: vídeo ao vivo
      de uma máquina pra outra na mesma rede, com latência percebida razoável.
- [ ] **4. Áudio** (`SystemAudioCapture` — PCM cru direto pro media channel via
      `HostServer.BroadcastAudioChunk`, sem passar pelo ffmpeg — + configurar VB-Cable pra
      excluir o Discord conforme seção 3): validar que o áudio chega sincronizado com o vídeo
      no Viewer, e que a voz do Discord realmente não aparece no stream.
- [ ] **5. Internet de verdade**: configurar port forwarding manual no roteador do Host pras 2
      portas (control + media), testar com um amigo fora da rede local. Ajustar bitrate/fps
      fallback (1080p60 → 720p30) se a rede não aguentar.
- [ ] **6. Investigar a latência de decode** (item 6 da seção "gargalos" acima): medir com que
      atraso real o Viewer vê o que o Host está fazendo, e experimentar reduzir
      `-analyzeduration`/`-probesize` no `FfmpegDecoder` até achar o ponto onde ainda decodifica
      de forma confiável mas sem acumular atraso.
- [ ] **7. Polimento**: código de sala com senha simples, overlay de fps/latência no Viewer,
      múltiplos Viewers simultâneos, tratamento de desconexão/reconexão.

Só depois de tudo isso funcionando de ponta a ponta é que vale a pena considerar as melhorias
"futuras" mencionadas ao longo deste documento (hole punching, encode direto da GPU, `ArrayPool`,
media channel em UDP).

---

## Rodada de baixa latência (09/09/2026)

Detalhes e números em [PERFORMANCE.md](PERFORMANCE.md), seção 5. O resumo das decisões:

- **A latência era o problema, não a cópia GPU→CPU.** A leitura da textura custa ~1,6 ms/frame a
  1080p; a latência ponta a ponta era ~540 ms, quase toda em buffer do decoder. Por isso o
  trabalho foi todo em flags de baixa latência, e não no "zero-copy" que parecia ser o caminho.

- **`-fflags nobuffer` fica proibido no decoder.** Não melhora a latência e faz quem entra no meio
  da transmissão esperar ~2 GOPs pela primeira imagem. Há um comentário no `FfmpegDecoder`
  avisando — é o tipo de flag que alguém adiciona "porque parece low latency".

- **`BoundedChannelFullMode.DropWrite` mente.** O `Channel` descarta o item e `TryWrite` retorna
  `true` mesmo assim. As filas de mídia (`RoomServer.Participant` e `RoomClient`) usam
  `FullMode.Wait` + `TryWrite` justamente pra `TryWrite` retornar `false` quando cheia e o
  descarte ser explícito, contado e tratado. **Nunca usar `WriteAsync` nessas filas** — em modo
  `Wait` ela bloquearia a captura.

- **Fila curta é feature.** 60 pedaços (~1 s a 60 fps) em vez de 256. Fila cheia significa
  espectador atrasado; deixar acumular 4 s de atraso é pior que descartar e ressincronizar.

- **Descarte de vídeo é alinhado a keyframe** (`H264Inspector`), nos dois lados: quem transmite
  (fila de saída) e o relay (fila por espectador). Áudio nunca é segurado — som cortado incomoda
  mais que imagem congelada. Limite de 3 s esperando keyframe, pra nunca travar de vez.

- **Aviso de congestionamento com histerese.** Duas janelas seguidas de 4 s com mais de 10 frames
  perdidos, e no máximo um aviso a cada 30 s por pessoa. Sem a histerese, um engasgo isolado
  viraria aviso; medido: sala saudável com tráfego real por 6 s dá zero falso positivo.

- **Ainda não verificado**: rede real entre máquinas, e as flags de AMF/QSV (não tenho essas
  placas aqui). NVENC e libx264 estão testados.

---

## Entrar na sala sem código (09/09/2026)

O código de sala saiu. `RoomCode` foi deletado, `RoomEnvelope.Join` carrega só o nickname, e o
servidor não rejeita mais ninguém por código. No lugar entrou descoberta automática na rede.

**Como funciona.** `SessionBeacon` (dentro do `RoomServer`) escuta UDP na porta 51902 e responde a
quem pergunta se tem sala ali. `SessionBrowser.ScanAsync` pergunta e junta as respostas. São dois
caminhos, porque um só não cobre tudo:

- **Broadcast dirigido em cada placa de rede** — pega rede local, Radmin VPN, Hamachi e ZeroTier,
  que emulam uma LAN e entregam broadcast. Mandar só pra 255.255.255.255 não bastaria: isso sai
  apenas pela placa da rota padrão, e quem tem Radmin junto ficaria de fora.
- **Pergunta unicast pra cada máquina do Tailscale**, lida de `tailscale status --json`. O
  Tailscale é roteamento puro e **não entrega broadcast** — lá não existe "perguntar pra todos".
  Sem o Tailscale instalado, essa parte volta vazia e o resto funciona igual.

É pergunta-e-resposta, não anúncio periódico: o anfitrião não fica falando sozinho na rede, só
responde quando alguém está de fato procurando sala.

**Rótulo da rede.** `LocalNetworks.LabelFor` diz por onde cada sala apareceu ("Rede local",
"Tailscale", "Radmin VPN"...) comparando o IP de quem respondeu com as sub-redes das placas locais.
O Tailscale é caso especial e é reconhecido pela faixa 100.64.0.0/10: no Windows a placa dele tem
máscara /32, então comparação de sub-rede nunca casaria. A mesma sala aparece uma vez por rede que
alcança ela — é intencional, é o que deixa claro por onde você está entrando.

**Quem pode entrar.** Sem código, qualquer um que alcance a porta 51900 entra na sala. Na prática
isso é a sua rede local e as VPNs em que você já está autenticado (o Tailscale e o Radmin já
autenticam quem está lá dentro). O buraco existe se as portas estiverem abertas pra internet no
roteador — nesse caso, quem souber o IP entra. Se um dia isso incomodar, o caminho é o anfitrião
aprovar quem entra, não voltar com o código.

**Nickname.** `AppSettings` (App) guarda nickname e último IP digitado à mão em
`%APPDATA%\MGRScreen\settings.json`. Arquivo solto, não registro, pra continuar sendo "copia a
pasta e usa".

---

## Usabilidade: mixer, reconexão e atritos (09/09/2026)

### Mixer de áudio (`RemoteAudioMixer`)

Antes: um `WaveOutEvent` por participante — 4 pessoas compartilhando = 4 saídas de áudio abertas,
sem controle de volume, e "silenciar" era jogar as amostras fora na chegada. Agora existe uma saída
só, um `MixingSampleProvider` (48 kHz, estéreo, float) e um `VolumeSampleProvider` por pessoa.

- **Volume por pessoa de 0 a 200%**, no botão direito em cima da tela dela. Acima de 100% pode
  distorcer: o `SampleToWaveProvider16` do NAudio satura em ±1.0 (não dá wraparound, mas corta).
- **Silenciar = volume 0** e não enfileirar mais nada dessa pessoa. Sem a gambiarra de limpar
  buffer pra não voltar atrasado.
- **Cada entrada é adaptada pro formato do mixer** (mono → estéreo, reamostragem) — os amigos
  podem estar com taxas de amostragem diferentes.
- **Volume e mudo são guardados pelo NICKNAME**, não pelo id de participante: o id muda a cada
  conexão, então guardar por id perderia tudo no primeiro reconnect.

**Bug corrigido junto:** o buffer de áudio era de 2 s com `DiscardOnBufferOverflow`. Um engasgo de
rede empurrava o áudio pra trás e ele **nunca voltava** — ficava permanentemente atrás da imagem
até silenciar e reativar. Agora, passou de 400 ms acumulados, o excesso é descartado. O corte é
feito DEPOIS de enfileirar: cortando antes, o pedaço recém-chegado ficava por cima do limite e o
atraso real virava 400 ms + um pedaço (medido: 500 ms).

### Reconexão automática

`RoomClient.Disconnected` só escrevia uma linha na barra de status; as telas congelavam e a única
saída era fechar o app. Agora aparece uma faixa de aviso e a sala tenta voltar a cada 3 s, pra
sempre, enquanto a janela estiver aberta.

A fiação dos eventos virou `WireClientEvents(client)` porque a reconexão cria um `RoomClient` novo
do zero. As telas remotas são apagadas antes de reconectar: os ids de participante mudam a cada
conexão, então as antigas nunca mais receberiam vídeo — o `ConnectAsync` reavisa quem está
compartilhando e elas voltam. Quem estava transmitindo reanuncia o próprio stream (`_myStreamInfo`).

Detalhe que faz isso funcionar: os handlers de saída (`EncodedChunkReady`, áudio capturado) leem o
**campo** `_client` na hora de enviar, então trocar o campo pela conexão nova já redireciona tudo.

### Decodificação por hardware

`-hwaccel d3d11va` no `FfmpegDecoder`. É best-effort de propósito: onde não houver suporte, o
ffmpeg avisa no stderr e decodifica em software — não precisa de detecção prévia nem de caminho de
fallback nosso. Medido aqui (720p60, ponta a ponta): 89 ms em software contra 60 ms com hardware.

### "Compartilhar de novo"

O WGC **não expõe o HMONITOR** do que o usuário escolheu no seletor. Pra saber qual monitor foi,
comparo o tamanho do alvo com o dos monitores conectados: se exatamente um bate, guardo o nome de
dispositivo dele e ofereço "compartilhar de novo" (via `GraphicsCaptureItem.CreateForMonitor`, sem
seletor). Se dois monitores tiverem a mesma resolução, ou se foi uma janela, não guardo nada —
compartilhar a tela errada seria pior que abrir o seletor. Janela não tem identificador estável
entre execuções do app, então não dá pra oferecer nesse caso.

### Aviso de congestionamento pros dois lados

O servidor já avisava quem transmite. Agora avisa também o **próprio espectador** afetado
(`ViewerCongested` com o id dele; o cliente compara com o seu e escolhe a mensagem). Sem isso a
pessoa vê a imagem congelar e conclui que o app trava.

---

## Descoberta por TCP (09/09/2026) — correção

A busca de salas não funcionava entre máquinas de verdade, embora entrar digitando o IP
funcionasse. A descoberta era só UDP, e UDP é justamente o que não passa nos dois casos que
importam aqui:

- **Tailscale não entrega broadcast** (é roteamento puro), então lá a única forma era o probe
  unicast UDP pra cada peer — e UDP de entrada costuma estar bloqueado, mesmo quando a porta
  TCP da sala foi liberada.
- Pior: `BroadcastAddresses()` incluía a placa do Tailscale, cuja máscara é /32 — a conta do
  endereço de broadcast dava o **próprio IP da máquina**. A pergunta ia pra nós mesmos.

**Agora a descoberta usa dois caminhos em paralelo:**

| Caminho | Cobre | Por quê |
| --- | --- | --- |
| UDP broadcast em cada placa | Rede local, Radmin, Hamachi, ZeroTier | Não dá pra enumerar quem está na rede; broadcast é a única forma de perguntar "pra todos" |
| TCP na porta de controle, peer a peer | Tailscale, e qualquer IP já usado antes | É o **mesmo caminho da entrada manual**: se digitar o IP funciona, descobrir funciona |

O TCP pergunta com uma mensagem `Discover` no canal de controle; o servidor responde
`DiscoverReply` (nome do anfitrião + quantos estão na sala) e fecha, **sem criar participante**
(testado: 5 buscas seguidas deixam a sala com 0 participantes). Os alvos são os peers online do
`tailscale status --json` mais os endereços que o app já conhece — o último IP digitado à mão e a
última sala. Ou seja: digitou o IP do amigo uma vez, a sala dele passa a aparecer sozinha depois.

**Compatibilidade:** uma versão anterior do app não conhece `Discover` e responde `JoinRejected`.
Isso é tratado como "tem sala aqui, mas o app do outro lado está desatualizado" e a sala aparece na
lista assim mesmo, com esse nome — dá pra entrar do mesmo jeito.

**Diagnóstico:** o botão "Por que não aparece?" na tela inicial roda
`SessionBrowser.DiagnoseAsync` e mostra redes locais, alvos do broadcast, peers do Tailscale e o
resultado de cada pergunta TCP. Serve pra separar as três causas que dão o mesmo sintoma: o amigo
não está com sala aberta, a porta está bloqueada, ou o app dele é de outra versão. Foi esse
relatório que achou a causa real aqui: um peer com sala aberta respondendo `JoinRejected`, ou seja,
rodando um build antigo.

---

## Câmera (23/09/2026)

Cada participante pode publicar **dois streams independentes**: tela e câmera. Dá pra ter os dois
ligados (o caso típico: o jogo + o rosto), só um, ou nenhum.

**Identificação.** Tudo que antes era chaveado por participante agora é por (participante, tipo):
`StreamKind { Screen, Camera }` no `StreamInfo` e no `RoomEnvelope` (StopStream/StreamStopped), um
tipo de chunk novo `MediaChunkType.CameraVideo = 3` no canal de mídia, dois slots por participante
no servidor (`ScreenStream`/`CameraStream`), e na janela da sala `TileKey(participante, tipo)`.
O estado de ressincronização por keyframe no cliente também é por tipo: um keyframe da câmera não
pode destravar a tela, que está num GOP completamente diferente.

**Captura.** `CameraCaptureService` usa o `MediaCapture` do Windows com `MediaFrameReader` pedindo
BGRA8 — o próprio Windows converte de NV12/YUY2/MJPG, e o frame sai no mesmo formato da captura de
tela, então o resto (encoder, relay, decoder, tile) é o mesmo pipeline. `MemoryPreference = Cpu` é
obrigatório: no padrão o frame pode vir só como superfície de GPU e o `SoftwareBitmap` chega null.
Tenta controle exclusivo (pra escolher 720p30); se outro programa já estiver com a câmera — o
Discord numa chamada de vídeo —, cai pro modo compartilhado e usa o formato que ele escolheu.

**Custos.** 720p30 a 2 Mbps (1,2 Mbps abaixo de 720p), sem áudio próprio (a voz já vai pelo
Discord). Pra quem hospeda, cada câmera ligada é mais 2 Mbps de upload por espectador — ver a
seção 7.2 do PERFORMANCE.md.

**Detalhes que parecem bobos e não são:**
- Parar de assistir a câmera de alguém **não** pode tirar o áudio dessa pessoa do mixer: o mixer é
  indexado por participante, e só o tile da tela tem áudio. Checado em `StopWatching`/`RemoveTile`.
- O preview da própria câmera é espelhado (como em todo app de vídeo); quem assiste recebe normal.
- Medido aqui (câmera integrada do notebook): 1280×720 a ~28 fps na captura, 30 fps e ~244 KB/s
  chegando no outro lado.

### Duas correções que o teste da câmera revelou

**A sala não abria quando a porta de descoberta caía numa faixa reservada.** O Windows reserva
faixas de porta pra Hyper-V/WSL/Docker, e elas mudam a cada boot; nesta máquina a UDP 51808–51907
ficou reservada e o bind da 51902 falhou com "acesso proibido". A exceção subia do `RoomServer.Start`
e a sala inteira não abria, com mensagem culpando 51900/51901. Agora o beacon é opcional: sem ele a
sala só deixa de responder ao broadcast, e a busca por TCP continua achando.

**O convite mostrava `169.254.x.x`.** Com o Tailscale instalado mas desconectado, a placa dele fica
"ligada" com o endereço automático do Windows (APIPA), que nunca alcança outra máquina.
`LocalNetworks` passou a ignorar 169.254.0.0/16.

**Os botões de ação foram pro rodapé.** Com o botão da câmera, a soma dos blocos do cabeçalho passou
da largura da janela e a coluna de participantes foi espremida a ~0 px: o texto quebrava uma letra
por linha e o cabeçalho crescia até cobrir a janela inteira. Câmera/compartilhar/sair agora ficam
numa barra embaixo (como em Meet/Discord), a coluna de participantes tem largura mínima, e a
largura mínima da janela subiu de 820 pra 1000 px — abaixo disso o cabeçalho já não cabia antes.

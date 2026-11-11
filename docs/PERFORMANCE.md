# Até onde dá pra melhorar a transmissão — análise com medições

> Investigação feita a pedido: "dá pra melhorar ainda mais a transmissão, diminuir travamentos,
> aumentar qualidade e FPS?". Resposta curta: **dá, e bastante — mas o gargalo não é onde parece.**

Tudo abaixo foi **medido nesta máquina** (RTX 2050, monitor 1080p60), não estimado.

---

## 1. O que eu medi

### 1.1 Custo da leitura GPU → CPU (o que o "zero-copy" eliminaria)

Captura de 1920x1080 BGRA, 120 iterações:

| Etapa | Tempo por frame |
|---|---|
| `CopySubresourceRegion` (GPU→staging) | 0,00 ms (assíncrono) |
| `Map` (é aqui que se espera a GPU) | 1,09 ms |
| `memcpy` para o array gerenciado | 0,90 ms |
| **Total** | **1,99 ms** → teto de ~500 fps só nesta etapa |

A 60 fps isso consome **~12% de um núcleo**. Somando o `SubmitFrame` (mais um memcpy de 8,3 MB,
medido em **1,66 ms**) e a escrita no pipe, o caminho CPU inteiro custa algo como **25–30% de um
núcleo**.

> **Conclusão que contraria a premissa**: a cópia GPU→CPU é real, mas **não é o gargalo** a 60 fps.
> Zero-copy compraria folga de CPU, não os travamentos que você sente.

### 1.2 O pipeline aguenta 1080p60?

Alimentando ruído aleatório (pior caso possível pro encoder) por 8 s:

- **64,9 frames/s realmente codificados** (o número que o espectador recebe)
- 11,0 Mbps reais
- Ou seja: **o encode não é o gargalo**. O NVENC dá conta de 1080p60 folgado.

### 1.3 Latência — aqui está o problema

Método: fico enviando frames escuros, disparo um "flash" branco e cronometro até ele aparecer
na saída decodificada. Mede encode + pipes + decode (**sem rede**), 720p60, 5 repetições:

| Configuração | Latência (mediana) |
|---|---|
| **Como está hoje** | **540 ms** |
| + decoder com `-probesize 32 -analyzeduration 0 -flags low_delay -fflags nobuffer` | 169 ms |
| + encoder com `-delay 0 -zerolatency 1` | 81 ms |
| + `-flush_packets 1` nos dois | **72 ms** |
| (libx264 `zerolatency`, sem GPU, mesma config) | 77 ms |

**De 540 ms para 72 ms — 7,5× melhor — só trocando argumentos do ffmpeg.** Nenhuma reescrita.

Onde estavam os 540 ms:
- **~370 ms no decoder**: o `-analyzeduration 500000` (meio segundo!) que eu mesmo coloquei
  quando resolvi o problema da tela preta. Funcionou, mas cobrou caro em latência.
- **~88 ms no encoder**: o NVENC segura alguns frames por padrão; `-delay 0 -zerolatency 1` corta.
- **~9 ms** de buffer dos pipes (`-flush_packets`).

### 1.4 O contra desse ganho (medido também)

Com a configuração agressiva, quem **entra depois** que a transmissão já começou demora mais pra
ver a primeira imagem: **1882 ms**, contra ~900 ms hoje. Perda de frames em fluxo contínuo:
desprezível (658 de ~720, e a diferença é a cauda cortada no fim do teste).

Ou seja, é um ajuste com trade-off — precisa de afinação, não é só colar as flags. Ver plano.

---

## 2. Avaliação honesta das três ideias pesquisadas

### 2.1 Zero-copy (textura D3D11 direto no NVENC)

**Viável em C#?** Sim. As bibliotecas existem e estão vivas:
`FFmpeg.AutoGen 9.0.1.1`, `Vortice.MediaFoundation 3.8.3`. (Não existe binding oficial do NVIDIA
Video Codec SDK em NuGet — seria P/Invoke na mão do `nvEncodeAPI64.dll`.)

**Vale a pena agora?** **Não como primeiro passo.** Pelas medições, isso economiza ~25–30% de um
núcleo — não resolve latência nem travamento. E o custo é alto:
- Sai de "processo filho isolado" para libav **dentro do processo**: um bug no decoder deixa de
  matar um `ffmpeg.exe` e passa a derrubar o app inteiro.
- Ponteiros `unsafe`, gerência manual de `AVFrame`/`AVBufferRef`, versão do FFmpeg travada.
- Distribuição passa a exigir DLLs do FFmpeg casadas com a versão do binding.

É a mudança de maior risco e menor retorno **no seu caso atual**.

### 2.2 WebRTC / SIPSorcery (UDP)

**Viável?** Sim, `SIPSorcery 10.0.16` é maduro. **Resolve o quê?** Perda de pacote sem travar a
fila (o TCP de hoje trava tudo quando um pacote se perde), controle de congestionamento e FEC.

**Mas**: a promessa de "sub-50ms" é sobre *transporte*. Nós medimos **72 ms sem rede nenhuma** —
o transporte não é o que está custando. Numa LAN ou via Tailscale, TCP se comporta bem. WebRTC
vira SFU, sinalização SDP, ICE, DTLS-SRTP: semanas de trabalho para resolver um problema que só
aparece em rede ruim pela internet.

**Veredito**: guardar para depois, e só se aparecer travamento com perda de pacote real.

### 2.3 Captura por Windows.Graphics.Capture

**Já é o que usamos** e está correto. Uma alternativa (DXGI Desktop Duplication) daria *dirty
rects* — saber quais retângulos mudaram — o que permitiria não recodificar tela parada. Mas ela
não captura janela específica com a mesma facilidade e é mais chata de manter.

---

## 3. Plano recomendado, por ordem de retorno

### Nível 1 — barato, ganho grande, risco baixo

1. **Baixar a latência de 540 ms para ~80 ms** com as flags medidas acima, **afinando o
   trade-off do "entrar depois"**: testar `-probesize` intermediário (4k em vez de 32) e medir de
   novo latência × tempo de primeira imagem, escolhendo o ponto onde os dois ficam bons.
   *É de longe o maior ganho de "qualidade percebida" do projeto todo.*

2. **Parar de duplicar frames com a tela parada.** Hoje o pacer reescreve o último frame 60×/s
   mesmo sem nada mudar: 500 MB/s de memcpy + pipe à toa e banda gasta. Enviar só quando muda
   (com PTS) economiza CPU e banda justamente em quem está lendo texto/parado.

3. **Pedir keyframe quando alguém fica pra trás.** Hoje, se a fila de um espectador enche, a gente
   descarta bytes e ele fica com imagem corrompida por até 1 s (até o próximo keyframe). Forçar um
   keyframe logo após o descarte encurta isso para milissegundos.

4. **Bitrate adaptativo simples**: se a fila de um espectador vive cheia, baixar o bitrate em vez
   de descartar. Hoje não existe nenhuma adaptação.

### Nível 2 — esforço médio, ganho real de CPU e banda

5. **Converter BGRA→NV12 na GPU** (shader D3D11) e mandar NV12 pelo pipe em vez de BGRA:
   8,3 MB → 3,1 MB por frame. Corta 2,7× o tráfego na leitura, no memcpy e no pipe, **e** elimina
   a conversão que o ffmpeg faz na CPU hoje. Consegue quase todo o benefício do zero-copy
   mantendo o `ffmpeg.exe` isolado em processo separado.

6. **Do lado de quem assiste**: decodificar para NV12 e exibir via `D3DImage` (GPU) em vez de
   BGRA + `WriteableBitmap`. Hoje cada tile custa duas cópias de 8,3 MB por frame na CPU — com
   4 pessoas compartilhando isso pesa.

7. **Decodificação por hardware** (`-hwaccel d3d11va`) em quem assiste.

### Nível 3 — só se o Nível 1 e 2 não bastarem

8. Zero-copy real com `FFmpeg.AutoGen` + D3D11 hwframes (ou Media Foundation via Vortice).
9. Transporte WebRTC com SIPSorcery.

---

## 4. O que eu NÃO consegui verificar

- **Rede real entre máquinas diferentes** — tudo aqui é loopback. O quanto o TCP atrapalha na
  prática de vocês, só medindo com os amigos conectados.
- **Hardware dos seus amigos** — `-zerolatency` e `-delay` são opções do **NVENC**. AMD (AMF) e
  Intel (QSV) têm equivalentes diferentes, que precisariam ser testados em máquinas com essas
  placas. Hoje eu só posso testar NVENC e libx264 aqui.
- Se a latência alta é o que vocês percebem como "travamento" — vale você confirmar a sensação
  depois do Nível 1.

---

## 5. O que foi implementado (e medido depois)

Implementado o Nível 1, exceto o item 2. Todos os números abaixo saíram de um teste que usa as
**classes reais do app** (`FfmpegEncoder` → `FfmpegDecoder`, `RoomServer` → `RoomClient`), não um
comando de ffmpeg isolado.

| Medida | Antes | Depois |
| --- | --- | --- |
| Latência ponta a ponta, 720p60 (mediana) | ~540 ms | **~70 ms** (min 52, max 94) |
| Primeira imagem correta pra quem entra depois | ~900 ms | 145–800 ms (varia com o keyframe) |
| Atraso máximo de um espectador antes de descartar | ~4 s | ~1 s |
| Espectador travado gera aviso pra quem transmite | não existia | sim, em ~10 s |

### 5.1 Flags de baixa latência (item 1)

Decoder: `-probesize 32 -analyzeduration 0 -flags low_delay -flush_packets 1`.
Encoder: `-flush_packets 1` mais, por encoder, `-delay 0 -zerolatency 1` (NVENC),
`-usage ultralowlatency` (AMF), `-async_depth 1` (QSV).

**`-fflags nobuffer` foi testado e descartado.** Medido: latência igual com ou sem (68 ms vs
70 ms), mas com ele quem entra no meio da transmissão espera ~2 GOPs pela primeira imagem
(1834 ms contra 71 ms). Não compensa. Está anotado como comentário no `FfmpegDecoder` pra ninguém
"reativar a otimização" depois.

O tempo da primeira imagem varia bastante (145–800 ms) porque depende de quando o próximo keyframe
cai — com GOP de 1 s, qualquer valor até ~1 s é esperado.

### 5.2 Ressincronizar por keyframe (item 3)

`H264Inspector.ContainsKeyframeStart` procura o início de um NAL de SPS/PPS/IDR. Quando a fila de
um espectador enche, o servidor para de mandar vídeo pra ele até aparecer um keyframe (limite de
3 s, pra nunca travar de vez) — a imagem congela e volta limpa, em vez de embaralhar. O mesmo vale
na saída de quem transmite, se o upload dele não der conta. Áudio nunca é segurado.

Não implementei "**forçar** um keyframe no encoder após o descarte" como o plano sugeria: com GOP
de 1 s, o próximo keyframe chega rápido, e forçar IDR sob demanda a partir do lado que assiste
exigiria um canal de volta e mexeria no bitrate de todo mundo por causa de um só.

### 5.3 O bug que só apareceu no teste

A fila de mídia de cada participante usava `BoundedChannelFullMode.DropWrite`. Nesse modo o
`Channel` descarta o item **e `TryWrite` ainda retorna `true`** — ou seja, o descarte era
silencioso: a contagem de perdas nunca subia, o resync por keyframe nunca disparava e o aviso de
congestionamento nunca seria enviado. As duas filas passaram para `FullMode.Wait` + `TryWrite`
(nunca `WriteAsync`, que bloquearia), que retorna `false` quando está cheia, e o descarte passou a
ser nosso, contado e tratado.

A fila também encolheu de 256 para 60 pedaços (~1 s a 60 fps). Com 256, um espectador podia
acumular ~4 s de atraso antes de qualquer descarte — o oposto do que este trabalho todo busca.

### 5.4 Aviso de congestionamento

O servidor mede frames perdidos por janela de 4 s. Acima de 10 perdas em duas janelas seguidas,
avisa quem está transmitindo (no máximo um aviso a cada 30 s por pessoa). Testado nos dois
sentidos: sala saudável com tráfego real por 6 s → **0 avisos**; espectador que não consome nada →
avisa uma vez, sem repetir.

### 5.5 O que ficou de fora, e por quê

**Item 2 (parar de duplicar frames com a tela parada) não foi implementado.** Olhando o código de
perto, o ganho é menor do que eu estimei neste documento: quando a tela não muda, o pacer reusa o
mesmo buffer e **não** há memcpy de 8,3 MB — sobra só a escrita no pipe. E enviar de verdade em
VFR exige PTS explícito por frame, o que na prática pede a reescrita em processo (Nível 3). O
custo real hoje é banda com tela parada, que o encoder já reduz bastante sozinho.

Itens 4 a 9 (bitrate adaptativo, NV12 na GPU, `D3DImage`, decodificação por hardware, zero-copy,
WebRTC) continuam como estavam: não implementados.

---

## 6. Atraso de vários segundos na transmissão (09/09/2026)

Relato: "o delay chega a 10 segundos". Reproduzido e corrigido — as medições de latência da seção 5
(~70 ms) valiam para o caso em que a rede e o PC de quem assiste dão conta. Quando **não** dão, o
atraso crescia sem limite.

### O que estava errado

**1. As filas limitadas eram as nossas; os buffers do Windows não.** O `SendBufferSize` de um
socket TCP no Windows cresce sozinho até vários MB. Com o link de quem assiste abaixo do bitrate da
transmissão, o atraso ia todo pra dentro do kernel: a nossa fila de 60 pedaços nunca enchia, o
descarte nunca rodava, e o vídeo ficava segundos atrás. Medido no loopback com só 2,5× de falta de
banda: **4,6 s**, e ainda subindo.

**2. A entrada do decoder era uma fila infinita.** `OnRemoteVideoChunk` fazia
`_ = decoder.WriteEncodedChunkAsync(chunk)` — fire-and-forget, um por chunk que chegava da rede. Se
o ffmpeg não acompanhava (o PC do amigo está ocupado com o jogo), as escritas se empilhavam sem
limite nenhum. Era também uma corrida: várias escritas simultâneas no **mesmo** stdin, que podiam
se intercalar e corromper o H.264.

### O que mudou

| Onde | Antes | Agora |
| --- | --- | --- |
| Buffer de envio dos sockets de mídia | automático (MB) | 32 KB |
| Buffer de recepção do cliente | automático (MB) | 128 KB |
| Fila por espectador no relay | 60 pedaços | 8 pedaços |
| Fila de saída de quem transmite | 60 pedaços | 15 pedaços |
| Entrada do decoder | ilimitada, escritas concorrentes | 8 pedaços, um escritor só, descarte alinhado a keyframe |

**Cuidado com a conta das filas** (errei isso na primeira tentativa e a medição me corrigiu): 8
pedaços não são "8 frames de atraso". O atraso que uma fila causa é *bytes parados ÷ velocidade que
o espectador consegue baixar*. Para quem está com metade da banda necessária, cada pedaço parado
custa o dobro do tempo. Por isso as filas ficaram tão pequenas.

### Resultado medido

| Cenário | Antes | Agora |
| --- | --- | --- |
| Espectador com 3,2 Mbps recebendo uma transmissão de 8 Mbps | 4,6 s e subindo | 0,9 s (pico 1,4 s) |
| Decoder que não acompanha | crescia pra sempre | limitado, descarta e volta no keyframe |
| Caso normal, 720p60 ponta a ponta | ~70 ms | 58 ms, zero descartes |

### O que ainda sobra, e por quê

Com **falta sustentada de banda** ainda sobra ~1 s. Isso não é buffer mal dimensionado: é
consequência de mandar mais dados do que o link aguenta — a única cura de verdade é mandar menos.
Por isso a troca de qualidade no meio da transmissão passou a valer na hora (antes só valia depois
de parar e começar de novo, o que tornava inútil o aviso "considere trocar para 720p"). Trocar o
preset reinicia o encoder: dá um engasgo de ~1 s e volta.

Redução automática de qualidade quando alguém está congestionado continua **não implementada** — o
aviso aparece e a decisão é de quem transmite.

---

## 7. Fila por tempo, não por quantidade (11/09/2026)

Relato: "o dono da sala perde fps". Duas coisas, uma delas causada por mim na seção 6.

### 7.1 A fila contada em pedaços derrubava frames a cada keyframe

Ao apertar as filas pra 8 pedaços, criei sensibilidade a rajada: um keyframe de 1080p60 tem
~220 KB e leva ~135 ms pra passar num link de 13 Mbps. Nesse tempo 8 frames se acumulam, a fila
enche, descarta, e o espectador espera o próximo keyframe. Medido: **60 → 33 fps a cada keyframe
com só 8% de folga de banda**. Com 15% de folga não acontecia — por isso os testes da seção 6 não
pegaram.

Nova `MediaQueue`: descarta só quando o pedaço **mais velho está esperando há mais de 300 ms**.
É exatamente o que queremos limitar (o atraso que a pessoa sente), e uma rajada passa desde que
seja drenada a tempo. Usada nos três lugares: relay por espectador, saída de quem transmite e
entrada do decoder.

| Link do espectador (transmissão de 12 Mbps) | Fila de 8 pedaços | Fila por tempo (300 ms) |
| --- | --- | --- |
| 14 Mbps (15% de folga) | 100%, 62 fps | 100%, 62 fps |
| 13 Mbps (8% de folga) | 92%, quedas pra 33 fps | 93%, 58–60 fps estáveis, 279 ms |
| 12 Mbps (sem folga) | — | 86%, 292 ms |
| 8 Mbps (falta 33%) | — | 57%, ~33 fps, 480 ms |

Testei 500 ms primeiro: fps igual, mas o atraso em regime estabilizava em 471 ms (na beirada do
limite, porque com 8% de folga o excesso de um keyframe demora mais pra drenar do que o intervalo
até o próximo). 300 ms ficou no ponto.

### 7.2 O upload do anfitrião carrega a sala inteira

Isto não é bug, é a topologia: o relay roda na máquina de quem criou a sala, e **cada stream sai
de lá uma vez para cada outro participante**. Três amigos assistindo o anfitrião a 8 Mbps são
24 Mbps saindo da casa dele. Se os amigos também compartilham, os streams deles entram no
anfitrião e saem de novo para os demais — com 4 pessoas todas a 8 Mbps, são 72 Mbps de upload.

A maioria das conexões residenciais não tem isso. Por isso o anfitrião é quem perde fps: o upload
dele satura e TODOS os streams que passam por ele sofrem, o dele inclusive. Agora a sala mostra
"Saída da sala pela sua internet: X Mbps" para o anfitrião, medido em tempo real, pra isso deixar
de ser invisível.

O que ajuda: quem hospeda ser quem tem o melhor upload; presets menores (720p30 = 4 Mbps, metade do
1080p30); menos gente compartilhando ao mesmo tempo. O que resolveria de verdade seria um servidor
fora de casa fazendo o relay — fora do escopo "sem servidor" do projeto.

### 7.3 Mandar mais de 60 fps pra "garantir 60"?

Não. Os frames que se perdem se perdem por falta de banda ou de CPU no caminho; mandar mais frames
adiciona carga exatamente onde já falta. Com bitrate constante, 90 fps significa cada frame com
menos bits (pior imagem) pela mesma banda; com mais bitrate, mais descarte. O que garante 60 fps
chegando é folga de banda, o pipeline tolerar rajada (7.1) e decodificação por hardware em quem
assiste — tudo já feito.

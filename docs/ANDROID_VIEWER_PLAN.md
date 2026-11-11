# Plano de implementação — MGRScreen Viewer para Android

Data: 07/11/2026. Status: proposta; implementação não iniciada.

## 1. Objetivo e premissas

Criar um APK Android que entre nas salas do MGRScreen Windows, liste as transmissões e reproduza vídeo e áudio. O Windows continua criando a sala e capturando/publicando tela e câmera. O Android envia apenas controle, assinaturas e estatísticas, nunca captura ou publica mídia.

Premissas propostas para a primeira versão:

- Android 10+ (API 29); confirmar os aparelhos reais do grupo antes de fixar o mínimo.
- Uma transmissão visível por vez, com troca entre telas/câmeras. Lista com nomes e estado, sem miniaturas de vídeo continuamente decodificadas.
- Áudio da tela selecionada; ao selecionar câmera, silêncio por padrão, com opção explícita de ouvir a tela do mesmo participante se estiver disponível.
- Retrato e paisagem, tela cheia, mute/volume, reconexão e diagnóstico simples.
- Reprodução somente enquanto o app está visível. Ao ir para segundo plano ou bloquear a tela, suspender assinatura e reprodução; ao voltar, buscar estado atual e um novo ponto de recuperação. PiP e áudio em segundo plano ficam para outra versão.
- Distribuição direta por APK assinado. Publicação em loja é uma etapa independente.
- Primeira entrega via LAN ou Tailscale já configurado. A conexão sem VPN exige uma etapa própria de WebRTC e sinalização; não é consequência automática de criar o APK.

Se eliminar a VPN for requisito para o primeiro APK, executar a prova WebRTC da seção 11 antes de implementar o transporte TCP Android. Isso evita investir em dois mecanismos de mídia completos.

## 2. Estado confirmado no código

- Interface WPF e Core direcionado a net8.0-windows10.0.22621.0; não podem ser referenciados diretamente por um aplicativo Android.
- Core inclui dependências de Direct3D, captura Windows e NAudio.
- Controle TCP com tamanho int32 little-endian seguido de JSON UTF-8. O serializador atual usa propriedades PascalCase e enums numéricos.
- Porta de controle usual 51900, mídia 51901 e descoberta UDP 51902. Tratar portas como configuráveis.
- JoinAccepted entrega id, participantes, streams e token de mídia. O socket de mídia começa com 32 bytes ASCII desse token.
- Mídia atual: tipo uint8, publicador int32 little-endian, tamanho int32 little-endian e payload. Tipos: tela 1, áudio 2, câmera 3.
- Vídeo H.264 Annex-B, fragmentado pelas leituras do stdout do FFmpeg. Um payload não corresponde necessariamente a um frame ou NAL completo.
- Áudio PCM16; taxa e canais são anunciados em StreamInfo. Não assumir sempre 48 kHz/estéreo sem validar o anúncio.
- Servidor retransmite todos os streams a todos os outros participantes. Não há assinatura seletiva implementada.
- Não há timestamps de apresentação no cabeçalho de mídia.
- Existe valor Ping no enum, mas sua presença não equivale a heartbeat completo com resposta e timeout.
- Não há projeto Android no conjunto de fontes examinado.

Arquivos principais: RoomProtocol.cs, MessageFraming.cs, MediaFraming.cs, RoomServer.cs, RoomClient.cs, H264Inspector.cs e FfmpegEncoder.cs, dentro de src/MGRScreen.Core.

## 3. Tecnologia proposta

Android nativo em Kotlin, Jetpack Compose para a interface, coroutines para concorrência, StateFlow para estado e DataStore para preferências. Usar MediaCodec com saída para SurfaceView integrada ao Compose; AudioTrack em streaming para PCM.

Motivo da escolha: acesso direto ao decoder, Surface, áudio e ciclo de vida do Android. É uma recomendação de arquitetura, não uma afirmação de que outras stacks sejam incapazes. .NET para Android é uma alternativa caso manter C# seja prioritário, mas exigiria separar o código portátil e implementar as mesmas integrações nativas; a interface WPF não seria reaproveitada.

Não embarcar ffmpeg.exe nem reproduzir o caminho desktop de conversão para BGRA na CPU. No Android, entregar vídeo comprimido ao decoder e renderizar diretamente na Surface. Consultar capacidades reais de codec/perfil/resolução/FPS. Habilitar baixa latência apenas quando suportada; Android 11 introduziu essa capacidade, mas o driver também precisa implementá-la.

## 4. Estrutura planejada

Manter os projetos Windows atuais e adicionar android/ com Gradle Wrapper, um módulo app e organização interna por responsabilidade:

- ui/: conexão, lista de streams, player, estados de erro.
- session/: coordenador da sala e máquina de estados.
- protocol/: envelopes, mensagens binárias, codecs e testes com fixtures.
- transport/: sockets TCP e descoberta; ponto de extensão para outro transporte.
- playback/: decoder H.264, saída de áudio, sincronização e buffers.
- diagnostics/: métricas locais e exportação de relatório sanitizado.

Evitar criar muitos módulos Gradle antes de existir necessidade. A abstração de sessão deve expor entrar/sair, estado da sala, assinar stream e renderizar mídia; WebRTC pode usar seu próprio pipeline, sem fingir que é apenas mais um socket TCP.

Documentação complementar a produzir na implementação: docs/PROTOCOL_V2.md, fixtures binárias compartilhadas e instruções de build/instalação Android. Fixar versões compatíveis de JDK, Android Gradle Plugin, Kotlin, Compose e SDK após validar o ambiente.

## 5. Fase 0 — preparação e medições

1. Confirmar modelos/versões Android dos aparelhos de teste e se a primeira entrega pode usar Tailscale.
2. Conferir instruções locais, estado de versionamento e preservar uma referência recuperável do Windows antes das alterações.
3. Registrar comportamento atual com um transmissor e um espectador Windows: resolução, bitrate, FPS exibido, atraso, CPU/GPU, quedas e áudio.
4. Preparar conteúdo sintético: contador visual, movimento contínuo, texto pequeno e flash acompanhado de pulso de áudio.
5. Verificar SDK/JDK/adb e selecionar pelo menos dois aparelhos físicos. Emulador serve para UI/protocolo, mas não certifica desempenho de decoder e temperatura.
6. Criar fixtures do handshake e mídia, documentando números dos enums e endianness.

Saída: decisões de escopo, baseline e ambiente reproduzível. Nenhuma meta de desempenho deve ser apresentada como já atingida.

## 6. Fase 1 — preparar o Windows e o protocolo

### 6.1 Compatibilidade e negociação

- Acrescentar ProtocolVersion, ClientRole e capabilities ao handshake, com valores ausentes tratados como legado.
- Fixar explicitamente os valores existentes dos enums; acrescentar novos valores sem renumerar mensagens anteriores.
- Negociar v2 na resposta. Não enviar cabeçalho v2 para um cliente v1.
- Novo Windows aceita clientes Windows legados por um adaptador de framing. APK inicial exige servidor com suporte v2 e mostra instrução de atualização ao detectar v1.
- Papel Viewer impede publicação no servidor; não apenas ocultar botão no Android.
- Validar versão, dimensões, FPS, formatos, limites de mensagem, timeouts e tipos desconhecidos antes de alocar memória ou criar decoder.

### 6.2 Mídia com limites claros

- Transformar a saída do encoder em access units H.264 completas, com parser incremental limitado em memória. Preservar bytes parciais entre leituras.
- Uma NAL não é sempre um frame: suportar múltiplas slices por imagem. Avaliar AUD inserido pelos encoders, sem depender de cada read do pipe como delimitador.
- Mensagem de mídia v2 deve carregar: tipo, publicador, tipo do stream, geração/configuração, sequência, PTS em microssegundos, flags de keyframe/configuração, tamanho e payload.
- Definir posições e tamanhos exatos no documento de protocolo antes de programar ambos os lados.
- SPS/PPS e metadados de codec devem estar disponíveis antes do primeiro IDR, na entrada tardia e após mudança de resolução.
- Usar geração do stream para rejeitar mídia antiga após reinício ou troca de formato. Como controle e mídia têm sockets separados, projetar explicitamente a corrida em que mídia chega antes do anúncio de configuração.
- PTS deve representar a linha do tempo da mídia, não o instante em que um fragmento chegou à rede. Áudio usa contagem de amostras; vídeo precisa associação com os frames efetivamente fornecidos ao encoder, inclusive repetidos.
- Validar uma forma confiável de preservar PTS na saída FFmpeg. Se Annex-B no pipe não permitir associação robusta, usar saída interna com timestamps e demux, ou integração de encoder que retorne pacotes com PTS. Esta é uma prova técnica obrigatória, não um detalhe resolvido.

### 6.3 Assinatura seletiva

- Adicionar SubscribeStream e UnsubscribeStream por participante + tipo de stream; áudio tem seleção explícita.
- Cliente legado mantém comportamento anterior. Viewer v2 começa sem assinatura e recebe só o que escolheu.
- Descartar filas da assinatura antiga na troca, sem destruir o canal de controle.
- Retomar com configuração + IDR; inicialmente pode aguardar o próximo GOP de aproximadamente um segundo. Pedido de keyframe imediato depende de suporte real do encoder e não deve ser prometido apenas por criar uma mensagem.
- Limitar memória e frequência de solicitações; não retransmitir áudio de participantes não selecionados.

### 6.4 Estabilidade

- Recuperação independente por publicador/tipo de stream, com sincronização de concorrência.
- Heartbeat Ping/Pong com tempo monotônico, timeout e motivo de desconexão. Não tratar ausência de stream ativo como conexão morta.
- Queda de qualquer canal encerra a sessão de forma coordenada e aciona uma única reconexão.
- Encerramento assíncrono de processos/decoders fora da thread da interface Windows.
- Descartar somente unidades adequadas; ao perder referência H.264, aguardar configuração/IDR em vez de retomar bytes arbitrários após um prazo.

Saída: Windows novo continua interoperando com Windows legado, aceita viewer v2 e entrega somente o stream solicitado, com configuração e timestamps testados.

## 7. Fase 2 — prova vertical Android

Antes de construir todas as telas, implementar um caminho mínimo em um aparelho real:

1. Conectar por endereço conhecido, nickname e portas configuradas.
2. Executar handshake, validar versão, conectar canal de mídia e apresentar a lista de streams.
3. Assinar uma tela 720p30, receber configuração H.264 e access units completas.
4. Configurar MediaCodec AVC com SPS/PPS e encaminhar buffers com PTS para SurfaceView.
5. Tocar áudio PCM16 através de AudioTrack; tratar escritas parciais e erros de inicialização.
6. Testar entrada no meio da transmissão, parada/reinício e mudança de resolução.

Primeiro testar NVENC e libx264; incluir AMF e QSV quando houver equipamento correspondente. Garantir saída H.264 8-bit 4:2:0 compatível em todos os caminhos, verificando o bitstream efetivo. Hoje apenas o caminho libx264 explicita yuv420p.

Saída: vídeo e áudio reais Windows → Android durante 15 minutos, com logs de configuração, frames e falhas. Se codec ou timestamps falharem, resolver antes de ampliar a UI.

## 8. Fase 3 — reprodução contínua e qualidade

- Recepção, parser, decoder e áudio fora da thread principal; filas com limites por duração e bytes.
- Nenhuma coroutine por frame sem limite. Cancelamento estruturado e apenas um dono por codec/socket/saída.
- Usar timestamps relativos do publicador e mapear para relógio local; não comparar diretamente relógios de máquinas diferentes.
- Áudio como referência de reprodução quando presente; vídeo acompanha com tolerância calibrada. Sem áudio, usar relógio monotônico local.
- Ajustar atraso gradualmente e medir drift. Descartar frames já decodificados/atrasados quando apropriado; não descartar aleatoriamente frames comprimidos de referência.
- Tratar destruição/recriação da Surface e do codec, mudança de orientação, erro do decoder e falta de recursos.
- Tentar um decoder compatível alternativo se necessário, informando limitação. Não manter fallback de software aquecendo o aparelho silenciosamente.
- Perfil inicial de validação: 720p30. Evoluir para 1080p30 e depois 1080p60 nos aparelhos capazes. 4K e HDR fora da primeira versão.
- Um botão de qualidade individual exige uma representação menor gerada pelo publicador ou transcodificação. O relay atual só repassa bytes; reduzir a Surface não reduz a banda nem o custo do decoder.
- MVP pode receber a qualidade global compatível escolhida no Windows. Uma segunda representação móvel é evolução separada, medida quanto a GPU e upload.
- PCM primeiro para reduzir mudanças simultâneas. Opus depois, com negociação e testes de decoder/packetização; não pressupor suporte uniforme de baixa latência em todos os aparelhos.

Saída: sessão longa com atraso limitado, áudio sincronizado e recursos liberados corretamente.

## 9. Fase 4 — experiência, conexão e ciclo de vida

Interface:

- Entrada por endereço e nickname; histórico de servidores e favorito.
- Lista de participantes com tela/câmera disponível, inclusive sala sem transmissões.
- Player com proporção preservada, tela cheia, rotação, mute/volume, seletor de stream e voltar à sala.
- Estados distintos: conectando, aguardando transmissão, aguardando primeiro frame, reproduzindo, reconectando, incompatível e sala encerrada.
- Indicadores opcionais de resolução, FPS apresentado, bitrate e congestionamento. Não chamar RTT de latência de vídeo.

Conectividade:

- LAN: entrada manual primeiro; descoberta UDP compatível com o Windows depois, com timeout e fallback manual.
- Tailscale: conectar ao IP/nome alcançável com o app Tailscale configurado. Não portar a invocação desktop de tailscale status --json para Android nem presumir acesso à lista de peers do outro app.
- Convite/QR posterior pode carregar endereço e portas; isso evita digitação, mas não atravessa NAT sozinho. O scanner de QR, caso incluído, adiciona uma permissão de câmera para essa finalidade específica.
- Suportar resolução de nomes/IPv4/IPv6 onde o servidor também oferecer suporte. Testar VPN e rede local sem forçar binding ao Wi-Fi que ignore o túnel.
- Mudança Wi-Fi/dados móveis invalida sessão antiga e tenta reconectar com backoff limitado, cancelável pelo usuário. Restaurar assinatura só depois do novo handshake.

Android:

- INTERNET e ACCESS_NETWORK_STATE; solicitar permissão local quando exigida pela combinação de OS/target. A documentação atual exige ACCESS_LOCAL_NETWORK para apps direcionados ao Android 17/API 37+.
- Sem permissões de captura de tela, microfone ou câmera no MVP sem QR.
- Manter tela acordada apenas durante reprodução visível. Liberar codecs, áudio e sockets de forma idempotente.
- Tratar audio focus, chamadas e desconexão de fones. Definir comportamento com Discord em uso e validá-lo em aparelho real; Bluetooth pode adicionar atraso fora do pipeline do MGRScreen.
- Persistir somente endereço, nickname e preferências. Não guardar token de sessão em logs/histórico.

## 10. Fase 5 — testes e critérios de aceite

Testes automatizados:

- Fixtures C#/Kotlin com JSON, enums, little-endian, token e cabeçalhos v1/v2.
- Leituras TCP com prefixos/payloads divididos e várias mensagens numa mesma leitura; EOF no meio; tamanho inválido; mensagem desconhecida.
- Parser H.264 com start codes divididos, múltiplas NALs/slices, SPS/PPS e IDR, configuração alterada e dados truncados.
- Assinaturas: celular recebe só a seleção; recuperar câmera A não libera tela B; clientes legados continuam funcionando.
- Reconexão: queda só de mídia, controle silencioso, host encerrado, cancelamento e eventos atrasados de uma sessão anterior.
- Reinício de geração/resolução sem entregar frames antigos ao novo decoder.

Testes em aparelhos:

- Pelo menos dois dispositivos com GPUs/SoCs diferentes; preferir um intermediário e um aparelho mais antigo dentro do suporte.
- LAN estável, Wi-Fi congestionado, Tailscale direto e rota com relay quando disponível, além de Wi-Fi → dados móveis.
- 720p30, 1080p30, 1080p60 quando suportado; texto, vídeo, jogo, tela estática, silêncio e som contínuo.
- 30 ciclos de entrar/sair/trocar stream e 60 minutos contínuos; rotação, lock/unlock, segundo plano e troca de fones.
- Validar Windows novo/legado e múltiplos espectadores simultâneos, medindo upload do anfitrião.

Metas iniciais de engenharia, a confirmar na prova vertical:

- Primeiro frame em até 2 s depois de assinar, em LAN estável e com publicador ativo.
- Em movimento contínuo 720p30, pelo menos 95% da taxa nominal apresentada durante 10 min no aparelho de referência.
- Latência visual p95 abaixo de 300 ms em LAN estável e áudio/vídeo com diferença absoluta abaixo de 80 ms durante reprodução contínua, medidos com flash/pulso. Registrar separadamente resultados pela internet e Bluetooth.
- Nenhum congelamento superior a 1 s na sessão de 30 min em LAN estável com banda suficiente; registrar todos os incidentes em condições degradadas, sem prometer ausência de travamentos em qualquer rede.
- Sem ANR, crash, processos/decoders abandonados ou crescimento sustentado de memória nos ciclos de uso.
- Após a rede voltar, recuperar a sessão em até 10 s nas condições de teste; distinguir host encerrado de falha transitória.
- Medir bateria e temperatura por 60 min; definir orçamento final a partir dos aparelhos, sem prometer um consumo universal.

## 11. Evolução para conexão sem VPN

WebRTC continua sendo a proposta de longo prazo para Internet. Ela demanda alterações também no Windows:

1. Prova Windows → Android com mídia H.264/Opus, renderização por hardware, entrada tardia e reconexão.
2. Escolher integração WebRTC mantida, verificar procedência/licenças e testar build, codec, controle de congestionamento e compatibilidade nos dois sistemas antes de fixar a dependência.
3. Adicionar serviço de sinalização autenticado por convite com expiração e proteção contra tentativas em massa.
4. ICE/STUN para conexão direta e TURN para fallback. Credenciais TURN temporárias emitidas pelo backend; nunca chave administrativa no APK.
5. Definir topologia: conexão por publicador/assinante aumenta upload de cada publicador conforme espectadores. Manter relay/SFU muda custo e infraestrutura. Nenhuma opção elimina multiplicação de tráfego por mágica.
6. Usar canais próprios de mídia WebRTC e feedback de congestionamento. Não apenas colocar o protocolo TCP inteiro num DataChannel confiável.
7. Medir uso de relay e limites gratuitos na data da implementação. Se custo zero for obrigatório, definir limite de uso e comportamento quando a franquia acabar; não prometer relay gratuito ilimitado.

O aplicativo TCP atual não oferece TLS nem autenticação de sala robusta. A primeira versão deve restringir seu uso a LAN confiável/VPN. Exposição pública exige desenho de autenticação, criptografia, limites e validação de acesso; o token atual de mídia apenas associa canais.

## 12. Fase 6 — entrega do APK

- Build debug para desenvolvimento; release assinado para distribuição, com versionCode crescente e identidade de aplicativo estável.
- Chave de assinatura protegida e com backup fora do repositório; segredos não embutidos no Gradle/APK.
- Testar instalação limpa e atualização por cima da versão anterior, preservando preferências.
- Verificar APK final após otimização/minificação. Se houver bibliotecas nativas, validar ABIs e compatibilidade de páginas de memória, incluindo 16 KB onde aplicável.
- Entregar APK, SHA-256, notas de versão, requisitos de versão do Windows e guia de conexão.
- Exportação de diagnóstico deve omitir tokens, conteúdo de tela e áudio; estatísticas locais bastam para o MVP.

## 13. Ordem de execução e entregáveis

| Marco | Dependência | Entrega verificável |
|---|---|---|
| A — decisões e baseline | Nenhuma | Aparelhos, escopo, transporte inicial, fixtures e medidas atuais |
| B — servidor e protocolo v2 | A | Assinaturas, mídia completa/PTS, estabilidade e compatibilidade Windows |
| C — prova Android | B | Vídeo/áudio em aparelho físico por endereço conhecido |
| D — qualidade e ciclo de vida | C | Buffers, sincronização, recuperação e testes prolongados |
| E — experiência completa | C e D | UI, histórico, descoberta LAN e diagnóstico |
| F — release | B a E aprovados nos testes | APK assinado e instruções |
| G — WebRTC sem VPN | Prova própria; pode substituir B/C TCP se for requisito inicial | Convites, sinalização, ICE/TURN e Windows compatível |

Não estimar um prazo fechado antes de B/C: timestamping no encoder e comportamento dos decoders reais são as principais incertezas. Após a prova vertical, dividir o restante em tarefas e estimar com base no que foi medido.

## 14. Fontes técnicas consultadas

- MediaCodec: https://developer.android.com/reference/android/media/MediaCodec
- Baixa latência e suporte do driver: https://source.android.com/docs/core/media/low-latency-media
- AudioTrack: https://developer.android.com/reference/android/media/AudioTrack
- Permissão de rede local: https://developer.android.com/privacy-and-security/local-network-permission
- Tailscale no Android: https://tailscale.com/docs/install/android
- Conexões WebRTC e sinalização: https://webrtc.org/getting-started/peer-connections

O plano não altera código executável e não certifica desempenho em Android; isso depende das provas e testes previstos acima.

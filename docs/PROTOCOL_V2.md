# MGRScreen protocolo 2

Os valores existentes são preservados. Inteiros binários usam little-endian.

## Controle

O canal TCP padrão 51900 usa um comprimento int32 seguido de JSON UTF-8, com campos em PascalCase e enums numéricos. O cliente envia Join (0) com `Nickname`, `ProtocolVersion: 2` e, se for espectador, `ViewerOnly: true`. JoinAccepted (1) confirma a versão e informa `ParticipantId`, `MediaToken`, `Participants` e `Streams`. O Android exige a confirmação explícita da versão 2.

| Kind | Mensagem | Campos adicionais |
|---|---|---|
| 12 | Ping | — |
| 13 | Pong | — |
| 14 | Subscribe | `ParticipantId` do publicador, `StreamKind` (0 tela, 1 câmera), `IncludeAudio` |
| 15 | Unsubscribe | — |

Cada descrição de stream inclui `Generation` positiva para publicadores atualizados. Ela muda quando o encoder é recriado. Uma assinatura substitui a anterior e começa no próximo IDR. O servidor rejeita publicação de mídia por espectadores. Mensagens legadas de descoberta e de início/parada mantêm seus valores.

## Mídia

O canal TCP padrão 51901 começa com os 32 caracteres ASCII do token. Depois vêm mensagens com o mesmo envelope de nove bytes do protocolo 1. A versão 2 acrescenta um prefixo de 25 bytes ao payload e liga o bit mais alto do tipo.

| Offset | Tamanho | Significado |
|---:|---:|---|
| 0 | 1 | Tipo, 0x81 tela, 0x82 áudio, 0x83 câmera |
| 1 | 4 | Id do publicador, preenchido pelo servidor |
| 5 | 4 | Comprimento do restante, incluindo prefixo v2 |
| 9 | 8 | Geração do stream, int64 |
| 17 | 8 | Sequência de mídia por conexão, int64 |
| 25 | 8 | Timestamp de apresentação em microssegundos, int64 |
| 33 | 1 | Flag de IDR (1), ou 0 |
| 34 | variável | Uma unidade H.264 Annex B completa ou amostras PCM16LE intercaladas |

Os timestamps usam a origem monotônica do publicador. O timestamp do vídeo é registrado na escrita do frame cru e consumido na ordem das unidades produzidas. Os encoders são configurados sem B-frames, com AUD inserido e sem alteração de cadência na saída. O áudio estima a origem a partir do instante do callback e avança pela duração das amostras. Uma lacuna superior a 100 ms ajusta a origem. A precisão depende da captura WASAPI e deve ser medida em dispositivos reais.

O formato do áudio vem na descrição da tela, incluindo canais e taxa de amostragem. As unidades de recuperação de vídeo trazem SPS/PPS antes das slices do IDR. Ao assinar ou se recuperar de congestionamento, o espectador espera por esse IDR. Mídia de gerações anteriores é ignorada.

O servidor envia framing antigo a clientes desktop. Publicadores antigos não fornecem geração, unidades completas nem PTS e seus streams continuam disponíveis pelo caminho desktop.

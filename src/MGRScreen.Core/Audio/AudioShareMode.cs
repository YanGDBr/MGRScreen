namespace MGRScreen.Core.Audio;

/// <summary>
/// De onde vem o áudio que eu compartilho.
///
/// A API de loopback por processo do Windows aceita <b>uma única</b> árvore de processos por
/// captura (incluir OU excluir). Por isso não existe um modo "tudo menos o MGRScreen e menos o
/// Discord" — é preciso escolher, ou então capturar só o app que interessa.
/// </summary>
public enum AudioShareMode
{
    /// <summary>
    /// Tudo do sistema, menos o MGRScreen <b>e</b> o Discord — sem eco e sem a call.
    ///
    /// Como a API só aceita uma árvore de processos por captura, isto é feito abrindo uma captura
    /// para CADA app que está tocando algo e somando os streams
    /// (<see cref="MixedProcessLoopbackCapture"/>). Custo: um app que começa a tocar depois entra
    /// com alguns segundos de atraso.
    /// </summary>
    SystemExceptSelfAndDiscord,

    /// <summary>Tudo do sistema, menos o próprio MGRScreen. Elimina o eco (o áudio das telas que
    /// EU estou assistindo não volta pros outros), mas o Discord continua entrando.</summary>
    SystemExceptSelf,

    /// <summary>Tudo do sistema, menos o Discord. Não duplica a call, mas se você estiver
    /// assistindo a tela de alguém, o áudio dela volta pra essa pessoa (eco).</summary>
    SystemExceptDiscord,

    /// <summary>Só o áudio de um app específico (o jogo, por exemplo). Não tem eco nem Discord —
    /// é o modo mais indicado quando as duas coisas incomodam.</summary>
    SingleApp,

    /// <summary>Nenhum áudio.</summary>
    None,
}

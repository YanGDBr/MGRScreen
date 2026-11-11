namespace MGRScreen.Core.Networking;

/// <summary>
/// Leitura mínima do fluxo H.264 (Annex-B) para saber se um pedaço de bytes começa um keyframe.
///
/// Serve pra recuperar um espectador que ficou pra trás: em vez de continuar mandando bytes do
/// meio de um GOP (o que produz imagem corrompida até o próximo keyframe, ~1 s), o servidor
/// segura tudo até aparecer um keyframe e retoma dali — a imagem congela por um instante e volta
/// limpa, em vez de embaralhar.
/// </summary>
public static class H264Inspector
{
    private const byte NalTypeIdr = 5;
    private const byte NalTypeSps = 7;
    private const byte NalTypePps = 8;

    public static bool ContainsIdr(byte[] data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && (data[i + 3] & 31) == NalTypeIdr)
                return true;
        return false;
    }

    /// <summary>
    /// Procura, dentro de <paramref name="data"/> a partir de <paramref name="offset"/>, um
    /// start code seguido de SPS, PPS ou IDR — o começo de um ponto de sincronização.
    /// </summary>
    public static bool ContainsKeyframeStart(byte[] data, int offset)
    {
        for (var i = offset; i + 3 < data.Length; i++)
        {
            // start code: 00 00 01  (o 00 00 00 01 cai neste mesmo caso, um byte adiante)
            if (data[i] != 0x00 || data[i + 1] != 0x00 || data[i + 2] != 0x01)
            {
                continue;
            }

            var nalType = (byte)(data[i + 3] & 0x1F);
            if (nalType is NalTypeIdr or NalTypeSps or NalTypePps)
            {
                return true;
            }
        }

        return false;
    }
}

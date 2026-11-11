using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MGRScreen.Core.Networking;

/// <summary>Uma placa de rede IPv4 ativa desta máquina.</summary>
public sealed record LocalNic(string Label, string InterfaceName, IPAddress Address, IPAddress Mask);

/// <summary>
/// Descobre por quais redes esta máquina está falando e dá nome a elas ("Rede local",
/// "Tailscale", "Radmin VPN"...). É o que permite mostrar pro usuário de ONDE veio cada sala
/// encontrada — quem usa várias VPNs ao mesmo tempo precisa saber qual é qual.
/// </summary>
public static class LocalNetworks
{
    /// <summary>Faixa CGNAT que o Tailscale usa (100.64.0.0/10).</summary>
    internal static bool IsTailscaleRange(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 169 && b[1] == 254;
    }

    public static IReadOnlyList<LocalNic> Enumerate()
    {
        var result = new List<LocalNic>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var ip in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    // 169.254.x.x é o endereço que o Windows inventa quando a placa NÃO conseguiu
                    // um IP de verdade — nunca alcança outra máquina. Acontece, por exemplo, com o
                    // Tailscale instalado mas desconectado: a placa aparece "ligada" com esse
                    // endereço, e o convite chegou a mostrar 169.254.83.107 como "IP (Tailscale)".
                    if (IsLinkLocal(ip.Address))
                    {
                        continue;
                    }

                    var mask = ip.IPv4Mask ?? IPAddress.Parse("255.255.255.0");
                    result.Add(new LocalNic(LabelForInterface(nic, ip.Address), nic.Name, ip.Address, mask));
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return result;
    }

    private static string LabelForInterface(NetworkInterface nic, IPAddress address)
    {
        var name = $"{nic.Name} {nic.Description}";

        if (name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) || IsTailscaleRange(address))
        {
            return "Tailscale";
        }

        if (name.Contains("Radmin", StringComparison.OrdinalIgnoreCase))
        {
            return "Radmin VPN";
        }

        if (name.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase))
        {
            return "ZeroTier";
        }

        if (name.Contains("Hamachi", StringComparison.OrdinalIgnoreCase)
            || name.Contains("LogMeIn", StringComparison.OrdinalIgnoreCase))
        {
            return "Hamachi";
        }

        return "Rede local";
    }

    /// <summary>
    /// Por qual rede este endereço remoto está falando com a gente. Repare que o Tailscale entra
    /// pela faixa e não pela máscara: no Windows a placa dele tem máscara /32, então comparar
    /// sub-rede nunca casaria.
    /// </summary>
    public static string LabelFor(IPAddress remote)
    {
        if (IPAddress.IsLoopback(remote))
        {
            return "Esta máquina";
        }

        if (IsTailscaleRange(remote))
        {
            return "Tailscale";
        }

        foreach (var nic in Enumerate())
        {
            if (SameSubnet(nic.Address, remote, nic.Mask))
            {
                return nic.Label;
            }
        }

        return "Internet";
    }

    private static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var ba = a.GetAddressBytes();
        var bb = b.GetAddressBytes();
        var bm = mask.GetAddressBytes();

        if (ba.Length != 4 || bb.Length != 4 || bm.Length != 4)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            if ((ba[i] & bm[i]) != (bb[i] & bm[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// O endereço que os amigos devem usar pra entrar. Se tem VPN mesh ativa, é o dela — o IP da
    /// rede local só serve pra quem está na mesma casa. Retorna também o nome da rede, pra deixar
    /// claro na tela qual é.
    /// </summary>
    public static (string Address, string Network) PreferredInviteAddress()
    {
        var nics = Enumerate();

        // Ordem fixa em vez da ordem das placas (que varia por máquina): quem tem Tailscale e
        // Radmin ao mesmo tempo vê sempre o mesmo endereço aqui, e não um a cada dia.
        var vpn = nics.FirstOrDefault(n => n.Label == "Tailscale")
                  ?? nics.FirstOrDefault(n => n.Label != "Rede local");

        if (vpn is not null)
        {
            return (vpn.Address.ToString(), vpn.Label);
        }

        try
        {
            // Sem VPN: o IP da placa que atende a rota padrão pra internet.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return (((IPEndPoint)socket.LocalEndPoint!).Address.ToString(), "rede local");
        }
        catch (SocketException)
        {
            var any = nics.FirstOrDefault();
            return any is null ? ("(não identificado)", "rede local") : (any.Address.ToString(), any.Label);
        }
    }

    /// <summary>
    /// Todos os endereços por onde dá pra chegar nesta máquina, com o nome da rede de cada um.
    /// O convite lista todos porque um amigo pode estar no Radmin e outro no Tailscale — cada um
    /// precisa do endereço da rede DELE.
    /// </summary>
    public static IReadOnlyList<(string Address, string Network)> AllInviteAddresses()
    {
        var preferred = PreferredInviteAddress();

        var all = Enumerate()
            .Select(n => (Address: n.Address.ToString(), Network: n.Label))
            .ToList();

        if (all.All(a => a.Address != preferred.Address))
        {
            all.Insert(0, preferred);
        }

        // O endereço preferido primeiro; o resto na ordem em que apareceu.
        return all.OrderByDescending(a => a.Address == preferred.Address).ToList();
    }

    /// <summary>Endereços de broadcast dirigido de cada placa. Mandar só pra 255.255.255.255 sai
    /// apenas pela placa da rota padrão — quem tem Radmin/Hamachi junto ficaria de fora.</summary>
    public static IReadOnlyList<IPAddress> BroadcastAddresses()
    {
        var result = new List<IPAddress> { IPAddress.Broadcast };

        foreach (var nic in Enumerate())
        {
            var ip = nic.Address.GetAddressBytes();
            var mask = nic.Mask.GetAddressBytes();

            // Máscara /32 (o caso do Tailscale) não tem broadcast: a conta daria o próprio IP da
            // placa, ou seja, mandaríamos a pergunta pra nós mesmos. Lá a descoberta é por TCP,
            // peer a peer.
            if (mask is [255, 255, 255, 255])
            {
                continue;
            }
            var broadcast = new byte[4];

            for (var i = 0; i < 4; i++)
            {
                broadcast[i] = (byte)(ip[i] | (byte)~mask[i]);
            }

            var address = new IPAddress(broadcast);
            if (!result.Contains(address))
            {
                result.Add(address);
            }
        }

        return result;
    }
}

# Sala numa VPS

O MGRScreen normalmente não precisa de servidor: quem cria a sala hospeda, e os outros entram pela
rede local ou pelo Tailscale. Isso continua igual.

O `mgrscreen-server` é uma alternativa pra quem tem uma VPS: a sala fica lá, com endereço fixo, e
todo mundo entra por "Entrar por IP". Cada pessoa ou grupo roda o seu; não existe servidor central
do MGRScreen.

## Quando vale a pena

- O upload de quem hospedaria em casa não aguenta: hoje cada stream sai da casa do anfitrião uma
  vez pra cada espectador. Na VPS, cada um só envia o próprio stream.
- Instalar Tailscale em todo mundo é um atrito.

O servidor só repassa o que recebe. Não decodifica nem recodifica, então a qualidade da imagem é a
mesma e ele quase não usa CPU. O que ele gasta é **banda**: cada espectador de um stream a 12 Mbps
consome cerca de 5,4 GB por hora. Escolha uma VPS perto de vocês (no Brasil, São Paulo); um
servidor em outro continente soma mais de 100 ms de atraso.

## Instalar (Linux)

Baixe o binário da sua arquitetura (`mgrscreen-server-linux-x64` na maioria das VPS;
`linux-arm64` nas ARM) e mande pra VPS. Ele já inclui o .NET; não precisa instalar nada.

```bash
sudo install -m 755 mgrscreen-server-linux-x64 /usr/local/bin/mgrscreen-server
```

Teste na mão primeiro:

```bash
MGRSCREEN_PASSWORD='troque-esta-senha' mgrscreen-server --nome "Sala da turma"
```

Libere as duas portas TCP no firewall da VPS **e** no painel do provedor (grupo de segurança):

```bash
sudo ufw allow 51900:51901/tcp
```

No MGRScreen, cole o IP ou o nome da VPS em **Entrar por IP ou link** e digite a senha. Depois da
primeira vez a sala aparece sozinha na lista e no botão "Voltar pra…".

### Deixar rodando sempre (systemd)

```bash
sudo useradd --system --no-create-home mgrscreen
sudo tee /etc/mgrscreen.env >/dev/null <<'EOF'
MGRSCREEN_NAME=Sala da turma
MGRSCREEN_PASSWORD=troque-esta-senha
EOF
sudo chmod 600 /etc/mgrscreen.env

sudo tee /etc/systemd/system/mgrscreen.service >/dev/null <<'EOF'
[Unit]
Description=Sala do MGRScreen
After=network-online.target
Wants=network-online.target

[Service]
User=mgrscreen
EnvironmentFile=/etc/mgrscreen.env
ExecStart=/usr/local/bin/mgrscreen-server
Restart=on-failure
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable --now mgrscreen
```

Acompanhar: `journalctl -u mgrscreen -f`. A cada 30 s com gente na sala, ele registra quantos
estão conectados e quanta banda está saindo.

## Windows

`mgrscreen-server-windows-x64.exe` funciona igual, com as mesmas opções. Libere as portas 51900 e
51901 (TCP) no Firewall do Windows.

## Opções

| Opção | Variável | Padrão | |
| --- | --- | --- | --- |
| `--nome` | `MGRSCREEN_NAME` | Sala na VPS | Nome que aparece na lista de salas. |
| `--senha` | `MGRSCREEN_PASSWORD` | (sem senha) | Prefira a variável: na linha de comando a senha aparece na lista de processos. |
| `--porta` | `MGRSCREEN_PORT` | 51900 | A porta de mídia é a seguinte. O MGRScreen só conecta na 51900. |

## O que saber antes de expor na internet

- **Use senha.** Sem ela, qualquer pessoa que descobrir o endereço entra e assiste. Só quem tem o
  MGRScreen 1.1.0 ou mais novo consegue entrar numa sala com senha.
- **O vídeo não é criptografado.** A senha protege a entrada (ela nunca trafega; o servidor desafia
  e o cliente prova que sabe), mas vídeo, áudio, voz e chat passam em claro entre vocês e a VPS.
  Com o Tailscale, quem criptografa é ele. Não compartilhe pela VPS uma tela com dados sensíveis.
- **Não há limite de tentativas de senha.** Use uma senha longa.
- **O atraso depende da distância** até a VPS, somada nos dois sentidos (quem transmite → VPS →
  quem assiste).
- O transporte continua sendo TCP: perda de pacote na conexão de alguém congela a imagem dessa
  pessoa por instantes, como já acontece hoje.

## Compilar

```powershell
dotnet publish src/MGRScreen.Server -c Release -r linux-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o out/server
```

Troque `linux-x64` por `linux-arm64` ou `win-x64` conforme a máquina.

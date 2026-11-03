# Concorde

Call de voz, transmissão de tela em até Quad HD 60 fps e chat com os amigos, parecido com o Discord.
Tudo vai direto de PC pra PC (P2P, WebRTC): não tem servidor próprio guardando mensagens ou chamadas.
Feito para a comunidade **Família Advoliseiros**.

*Voice calls, screen sharing up to 1440p 60 fps and text chat with friends, peer-to-peer (WebRTC), for Windows.*

## Baixar / Download

- **Instalador para Windows:** [concorde-setup.exe](https://justinocosta007.github.io/concorde/concorde-setup.exe)
- **Sem instalar (pelo navegador):** https://justinocosta007.github.io/concorde/

O instalador não precisa de administrador: instala em `%LOCALAPPDATA%\Programs\Concorde`, cria atalhos e aparece em
*Configurações do Windows → Aplicativos instalados*. O programa se atualiza sozinho a partir deste repositório.

## O que ele faz

- Canais de texto e de voz, mensagens privadas, amigos por `@usuário`, cargos (igual Discord)
- Transmissão de tela até 2560×1440 a 60 fps, com o som de um programa só ou do PC inteiro (sem as vozes da call)
- Contas com usuário e senha (a conta fica criptografada com a sua senha nos PCs dos amigos, pra você entrar em outro PC)
- Mostra "Jogando ..." quando um jogo está aberto (dá pra desligar)

## Como compilar

Só precisa do Windows (usa o compilador C# do .NET Framework que já vem nele):

```powershell
powershell -ExecutionPolicy Bypass -File fonte\build.ps1
```

Isso gera `Concorde.exe` e a pasta `site\` (o que fica publicado aqui). Cada versão publicada é compilada pelo
GitHub Actions a partir deste código ([.github/workflows/build.yml](.github/workflows/build.yml)).
O mesmo `Concorde.exe` é o programa e o instalador: com "setup" no nome do arquivo ele instala.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [justinocosta007](https://github.com/justinocosta007)
- Approvers: [justinocosta007](https://github.com/justinocosta007)

Only binaries built by GitHub Actions from the source code in this repository are signed, and every signing request
is manually approved. See the privacy policy below.

## Privacidade

**Privacy policy:** this program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it. Concorde has no server of its own and the
developers receive no data from it: no telemetry, no ads, no analytics.

O que sai do seu PC, e só para fazer o que você pediu:

- **Mensagens, voz e tela:** vão direto para os amigos que você adicionou ou que estão no mesmo canal, criptografadas pelo
  WebRTC (DTLS/SRTP). Os amigos guardam as mensagens dos canais e das conversas com você para entregar a quem estava offline.
- **Achar os amigos online:** o servidor público do [PeerJS](https://peerjs.com) (0.peerjs.com) recebe o seu código
  (ex.: `ABC123`) e o seu `@usuário` para ligar um PC no outro. Os servidores STUN do Google e da Twilio veem o seu IP
  para abrir a conexão. Sem isso não dá pra conectar.
- **Senha:** a conta é criptografada com a sua senha (PBKDF2 + AES-GCM) e uma cópia criptografada fica nos PCs dos
  amigos, pra você conseguir entrar de novo. Ninguém consegue abrir sem a senha.
- **"Jogando ...":** o programa vê se algum jogo das pastas da Steam, Epic, Riot etc. está aberto e mostra só o nome
  dele para os amigos. Desliga em *Configurações → Sistema → Mostrar o jogo que estou jogando*.
- **Busca de jogos no perfil:** quando você busca um jogo, a busca vai para a loja da Steam.
- **Prévia de links:** quando chega um link numa mensagem, o seu PC abre esse link para mostrar o título e a imagem.
- **Atualizações:** ao abrir (e a cada 30 minutos) o programa confere neste site (GitHub Pages) se tem versão nova.
- **Cópia de segurança da conta:** fica no seu PC (`%LOCALAPPDATA%\TelaCall`) e em `Documentos\Concorde`. Se o
  OneDrive estiver ligado nessa pasta, a Microsoft guarda essa cópia na sua conta do OneDrive.

Abrir junto com o Windows é opcional (pergunta na instalação e dá pra mudar em *Configurações → Sistema*).

## Desinstalar

*Configurações do Windows → Aplicativos → Aplicativos instalados → Concorde → Desinstalar.*
Os seus dados ficam em `%LOCALAPPDATA%\TelaCall`; apague essa pasta se quiser tirar tudo.

## Licença

[MIT](LICENSE). Usa a biblioteca [PeerJS](https://github.com/peers/peerjs) (MIT), carregada do unpkg.com.

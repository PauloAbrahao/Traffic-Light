# Traffic Light

Mostra o estado de cada sessão do Claude Code aberta na máquina, pra você saber qual precisa de você:

- 🟡 **WAITING FOR YOU / AGUARDANDO VOCE**: pediu permissão ou fez uma pergunta
- 🔴 **BUSY / ATIVO**: trabalhando
- 🟢 **IDLE / LIVRE**: terminou, esperando o próximo prompt

Quem está aguardando aparece primeiro, uma linha por sessão (pasta, estado e horário da última mudança).

Existem duas versões com a mesma lógica. Use **uma ou outra** (as duas escutam a porta 4545):

| Versão | Arquivo | Precisa de |
|---|---|---|
| **App Windows** (recomendado) | `traffic_light.exe` | nada: é autossuficiente |
| **Node** | `traffic_light.js` | Node.js (Edge só pro `--app`) |

O que as duas fazem igual:

| | exe | Node |
|---|---|---|
| Estados, ordem e eventos | ✓ | ✓ |
| Pasta + estado + horário por sessão | widget | terminal / janela |
| Resumo (`1 waiting · 2 busy · 0 idle`) | tooltip do ícone | topo do painel |
| Detecta Esc pelo transcript | em até 1s | na hora |
| Confere os hooks ao abrir | pergunta se pode instalar | avisa embaixo do painel |
| Instalar hooks | ao abrir / menu | `--install` |
| Verificar instalação | menu | `--doctor` |
| Porta 4545 em uso | avisa e não abre | avisa e sai |
| `--ptbr` | ✓ | ✓ |

## App Windows (`traffic_light.exe`)

Dê dois cliques. Pode copiar só o `.exe` pra qualquer pasta ou máquina Windows (usa o .NET Framework 4, que já vem no Windows 10/11).

- **Instalação dos hooks:** ao abrir, ele confere o `~/.claude/settings.json`. Se faltar algum hook, mostra o que falta e pergunta se pode instalar (sem apagar os seus hooks; faz backup em `settings.json.bak`). Depois, reinicie as sessões abertas do Claude Code.
- **Ícone na bandeja:** cor de quem mais precisa de você (amarelo > vermelho > verde; cinza = nenhuma sessão). Passe o mouse pra ver o resumo.
- **Widget:** sempre por cima, no canto de baixo à direita. Arraste pra mover.
- **Menu** (botão direito no ícone ou no widget): mostrar/ocultar widget, **verificar instalação**, sair. Clique simples no ícone mostra/oculta o widget.

Em inglês por padrão; `traffic_light.exe --ptbr` pra pt-br. Pra abrir em pt-br no duplo clique, crie um atalho e coloque `--ptbr` no final do campo "Destino".

Se já tiver outro semáforo rodando (o exe ou o `.js`), ele avisa que a porta está em uso e não abre.

## Versão Node (`traffic_light.js`)

| Comando | O que faz |
|---|---|
| `node traffic_light.js` | Painel no terminal. Se faltar hook, mostra o aviso embaixo. |
| `node traffic_light.js --app` | Painel numa janela (Edge em modo app). Fechar a janela encerra. Se já tiver um semáforo `.js` rodando, só abre outra janela pra ele. |
| `node traffic_light.js --install` | Instala os hooks (com backup) e confere |
| `node traffic_light.js --doctor` | Confere `settings.json`, cada hook e matcher, `disableAllHooks`, Edge (só pro `--app`) e se o semáforo (exe ou `.js`) está rodando. Sai com código 1 se achar problema. |

`--ptbr` funciona em todos. As flags podem vir em qualquer ordem.

## Hooks (referência)

O exe e o `--install` já fazem isso. O semáforo recebe os eventos via hooks HTTP em `http://127.0.0.1:4545`; em `~/.claude/settings.json`, cada um destes eventos precisa de um hook apontando pra essa URL:

`SessionStart`, `UserPromptSubmit`, `PostToolUse`, `PreToolUse` (matcher `AskUserQuestion`), `PermissionRequest`, `Notification` (matcher `permission_prompt|elicitation_dialog|idle_prompt`), `Stop`, `SessionEnd`

```json
"Stop": [{ "hooks": [{ "type": "http", "url": "http://127.0.0.1:4545", "timeout": 2 }] }]
```

Com o semáforo fechado, os hooks só falham em silêncio. Não atrapalha o Claude Code.

## Observações

- O estado fica só na memória: abra o semáforo antes das sessões, ou espere o próximo evento de cada uma pra ela aparecer.
- O Esc (interromper) não dispara hook nenhum. O semáforo detecta a interrupção lendo o transcript da sessão (no exe, em até 1s).

## Recompilar o exe

Só é preciso se mudar o `traffic_light.cs`. Feche o exe antes (menu → Sair):

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /out:traffic_light.exe traffic_light.cs
```

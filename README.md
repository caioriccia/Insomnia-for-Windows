# Insomnia para Windows

Porte para Windows 11 do Insomnia (app de barra de menus do macOS). Fica na bandeja do
sistema e mantém o notebook **acordado por uma sessão com hora de fim, inclusive de tampa
fechada**. C# / .NET 10 / WPF (tema Fluent do Windows 11; o ícone da bandeja é o
`NotifyIcon` do WinForms), sem administrador.

## O que ele faz

- **Sessão com hora de fim**: presets (30 min a 3 dias) ou duração livre (`1h30m`, `3d`,
  `45`); contagem numa pílula no topo da tela (`3h 42m`, `4m 59s`) e no tooltip da
  bandeja; `+15 min`, `+1 h`, `+4 h`; encerrar **segurando** o botão (0,6 s). Lembrete 5
  minutos antes do fim.
- **Tampa fechada sem dormir**: durante a sessão, a ação da tampa (`LIDACTION`) do plano de
  energia ativo vai para "nada" em AC e na bateria, e o app segura um pedido de energia
  (`PowerSetRequest` SystemRequired + ExecutionRequired) que morre junto com ele.
- **Ao fechar a tampa** (cada uma desligável em Configurações > Tampa): apaga o monitor,
  muta a saída de áudio padrão, congela os apps da lista (ou todos os que têm janela, fora
  os protegidos e a lista de agentes), liga a Economia de energia e, opcionalmente, pausa o
  Docker Desktop se ele estiver ocioso. Ao abrir a tampa, tudo volta.
- **Pisos de bateria**: liga a Economia de energia abaixo de N% e encerra a sessão abaixo de
  M% (0 desliga o fim por bateria).
- **Hotspot de reserva**: se o Wi-Fi cair durante a sessão, tenta conectar ao perfil Wi-Fi
  salvo do celular (5 s, depois 5/10/20/30 s, sem desistir); quedas vão para
  `Logs\handoffs.log`.
- **Navegadores**: avisa quando Chrome, Edge ou Brave rodam sem
  `--disable-backgrounding-occluded-windows --disable-renderer-backgrounding` e oferece
  fechar e relançar com as flags, preservando o perfil.

### Recuperação (o que impede o notebook de ficar sem dormir para sempre)

Toda mudança que sobrevive ao app (`LIDACTION`, Economia de energia, apps congelados, mudo)
é escrita **antes** num diário (`%LOCALAPPDATA%\Insomnia\state.json`). Uma tarefa do
Agendador do usuário, **`Insomnia Backstop`**, roda `Insomnia.exe --backstop` a cada
minuto (inclusive na bateria): se não há sessão válida e o diário está sujo, ela desfaz
tudo. Assim, matar o app, travar ou reiniciar no meio de uma sessão não deixa a tampa
desarmada além do fim da sessão. Diário ilegível nunca é sobrescrito: fica como evidência e
bloqueia novas mudanças até alguém olhar.

## Interface

Segue o visual do Windows 11 (tipografia Segoe UI Variable, glifos Segoe Fluent Icons,
claro/escuro e cor de destaque do sistema, trocados ao vivo). Onde o Windows aceita, pílula,
painel e menu usam Acrylic e as Configurações usam Mica; se o DWM recusar (efeitos de
transparência desligados, alto contraste, Windows 10), a janela cai para um fundo sólido do tema — nunca fica
preta nem invisível. Com "Efeitos de animação" desligado em Acessibilidade, nada anima.

- **Pílula**: durante uma sessão, uma pílula de 32 px de altura fica centrada no topo do
  monitor principal com o tempo restante (`2d 4h`, `3h 42m`, `42m`, `4m 59s`, `42s`) e um
  ponto de estado. O glifo diz o estado: olho (normal), pausa (tampa fechada), aviso em
  âmbar (recuperação pendente), relógio em âmbar (faltam menos de 5 min) e em vermelho
  (menos de 1 min). Sem sessão, ela some. Nunca aparece na barra de tarefas nem no Alt+Tab
  e nunca toma o foco.
- **Painel**: clicar na pílula, ou **arrastá-la para baixo** (48 px ou um puxão rápido),
  abre o painel no mesmo lugar. Com sessão: contagem grande, `termina às HH:MM`, chips
  `+15 min`/`+1 h`/`+4 h`/`Outro…`, cartão de estado (tampa, bateria, Wi-Fi, ações, última
  queda), faixa `Tampa fechada` quando for o caso, avisos com ação (`Relançar Chrome`,
  `Parar de esperar`) e `Segure para encerrar`. Sem sessão (aberto pela bandeja): chips dos
  presets, campo `Outra duração (ex.: 45, 1h30m, 3d)` e **Iniciar**. Depois de iniciar, o
  painel mostra a contagem por 1,5 s e recolhe para a pílula. Fecha com clique fora, `Esc`,
  clique na alça de baixo ou arrastando-a para cima.
- **Bandeja**: o ícone é um glifo monocromático (olho fechado ocioso, olho em sessão, olho
  com selo de pausa com a tampa fechada, olho com ponto âmbar quando há aviso), branco na
  barra escura e preto na clara; o tooltip tem a contagem. Clique esquerdo abre/fecha o
  painel; clique direito abre o menu (sessão e estado da máquina, avisos e suas ações,
  `Abrir painel` ou `Iniciar sessão de …`, `Configurações…`, `Sair`), no monitor do
  cursor. Sair só existe no menu, para não sair por acidente.
- **Configurações**: janela no estilo das Configurações do Windows 11, com navegação à
  esquerda (Sessão, Tampa, Agentes, Bateria, Rede, App). Toda mudança grava na hora; fechar
  só esconde a janela.
- **Teclado**: `Esc` fecha o painel e o menu; `Enter` no campo de duração inicia (campo
  vazio = o preset selecionado) ou estende; `Tab` percorre chips, campo, botões, links e o
  botão de encerrar; `Espaço`/`Enter` **segurado** no botão de encerrar conta como segurar.
  Nas Configurações, `Enter` nos campos de adicionar (preset, app, SSID) adiciona.
- **Tela cheia**: com um jogo, vídeo em tela cheia ou apresentação em primeiro plano a
  pílula some e volta quando ele sai. O painel aberto não é afetado.
- **Vários monitores**: pílula, painel e Configurações ficam no monitor principal (onde
  está a bandeja); o menu abre no monitor do cursor. Trocar o monitor principal, desconectar
  um monitor ou mudar a escala reposiciona a pílula. A escala é por monitor (DPI
  PerMonitorV2): a UI fica nítida ao atravessar monitores com escalas diferentes.

## Arquivos

| O quê | Onde |
|---|---|
| Programa | `%LOCALAPPDATA%\Programs\Insomnia\Insomnia.exe` |
| Config | `%APPDATA%\Insomnia\config.json` |
| Diário, sessão, lock | `%LOCALAPPDATA%\Insomnia\` (`state.json`, `session.json`, `.recovery.lock`) |
| Log | `%LOCALAPPDATA%\Insomnia\Logs\insomnia.log` (+ `.1`, 1 MiB) |
| Tarefa | `Insomnia Backstop`, raiz do Agendador de Tarefas |
| Início com o Windows | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Insomnia` |

`INSOMNIA_HOME=<pasta>` relocaliza config, diário, sessão e logs para dentro de `<pasta>`
(testes e ensaios).

## Instalar, diagnosticar, desinstalar

Requer o SDK do .NET 10 para compilar e o runtime do .NET 10 Desktop para rodar.

```powershell
cd insomnia-windows
.\installer\instalar.ps1            # publica, copia, cria atalho e roda o diagnóstico
.\installer\desinstalar.ps1         # desfaz tudo, remove tarefa/atalho/programa (mantém config e log)
.\installer\desinstalar.ps1 -Purge  # idem, apagando também config, diário e logs
```

Diagnóstico a qualquer momento (o `| Out-Host` faz o PowerShell esperar e mostrar a saída):

```powershell
& "$env:LOCALAPPDATA\Programs\Insomnia\Insomnia.exe" --diagnostico | Out-Host
$LASTEXITCODE   # 0 = nada impede o app; 1 = há problema (listado no [Resumo])
```

Ele mostra caminhos, config, sessão, diário (cada entrada), `LIDACTION` AC/DC e esquema,
o **pré-voo de permissão** (relê e reescreve o mesmo `LIDACTION`, sob o lock de
recuperação, para provar que você consegue mudar o plano de energia — `--sem-pre-voo`
pula), `ESBATTTHRESHOLD`, bateria/AC, tampa, estado da tarefa do backstop
(Armed/Missing/Different/Unknown), perfil do hotspot, navegadores e flags, e se a bandeja
está aberta.

Outros modos: `Insomnia.exe --backstop [--force]` (recuperação; `--force` age mesmo com
sessão válida) e `Insomnia.exe --remover-backstop`.

### Compilar e testar

```powershell
dotnet build Insomnia.sln
dotnet test Insomnia.sln
```

A saída de build vai para `%LOCALAPPDATA%\InsomniaBuild\artifacts` (ver
`Directory.Build.props`), fora do OneDrive, que trava arquivos durante a sincronização.
Os testes nunca tocam o sistema: todo efeito está atrás de interface e os testes usam fakes.

## Diferenças em relação ao Mac

| Mac | Windows | O que muda |
|---|---|---|
| `pmset disablesleep 1` (sudo) | `LIDACTION=0` em AC e DC no plano ativo + `PowerSetRequest` do processo | Sem sudo. Só o `LIDACTION` persiste e é diarizado; o pedido de energia some quando o app morre. Aparece em `powercfg /requests`. |
| sudoers | nada | Pré-voo no início e no `--diagnostico`: sem permissão de mudar o plano (política de grupo), a sessão não começa. |
| Brilho de tela/teclado salvos | Apaga o monitor (`SC_MONITORPOWER`) ao fechar; acorda ao abrir | Nada é salvo; monitores externos também apagam; teclado fica de fora. |
| `pmset -b lowpowermode` | `ESBATTTHRESHOLD=100` só na bateria | Igual. Em AC não tem efeito. |
| SIGSTOP/SIGCONT | `NtSuspendProcess`/`NtResumeProcess`, identidade por hora de criação + BootId | Apps são identificados pelo nome do exe (`slack.exe`). Apps da Store podem ser encerrados pelo Windows quando suspensos. |
| Docker (`com.docker.docker`) | `Docker Desktop.exe` + `com.docker.backend.exe` | A VM do WSL2 (`vmmemWSL`) é do sistema e **não** congela; só a interface e o backend pausam. Opcional, desligado por padrão. |
| launchd + codesign | Tarefa `Insomnia Backstop` no Agendador do usuário | Sem assinatura de código; "armado" = a tarefa registrada bate com a esperada. |
| Keychain + senha do hotspot | Perfil Wi-Fi salvo pelo Windows | **O app não guarda senha**: conecte-se ao hotspot uma vez pelo Windows; o app só pede para conectar pelo perfil. |
| Barra de menus | Pílula no topo da tela + painel (clique ou arrasto) + ícone da bandeja com menu (clique direito) | A contagem fica na pílula; o ícone da bandeja é só o glifo de estado. |
| Login Items | `HKCU\...\Run` | Sem aprovação do sistema. |

Fora do porte: regras de temperatura, tmux, App Nap, brilho salvo, senha do hotspot no app.

## Teste no aparelho (Checkpoint 2)

Nada abaixo roda em teste automatizado: é o que só o notebook prova. Antes de começar,
anote o estado original:

```powershell
powercfg /q SCHEME_CURRENT SUB_BUTTONS LIDACTION       # "Índice da Configuração de Energia AC/DC atual"
powercfg /q SCHEME_CURRENT SUB_ENERGYSAVER ESBATTTHRESHOLD
```

Valores de `LIDACTION`: `0x0` nada, `0x1` suspender, `0x2` hibernar, `0x3` desligar.

1. **Instalar**: `.\installer\instalar.ps1`. O diagnóstico deve sair 0, com o pré-voo
   `ok` e a tarefa `ausente (Missing)` (normal antes da primeira sessão).
2. **Iniciar sessão** curta (ex.: 10 min) pelo painel (clique esquerdo na bandeja).
   Conferir:
   - a pílula com a contagem no topo da tela, o ícone da bandeja com o olho aberto e o
     tooltip `Insomnia · … · termina HH:MM`;
   - `powercfg /q SCHEME_CURRENT SUB_BUTTONS LIDACTION` mostra `0x0` em AC e DC;
   - Agendador de Tarefas: `Insomnia Backstop` existe, a cada 1 minuto, "Iniciar a tarefa
     somente se o computador estiver ligado na energia CA" **desmarcado**;
   - `--diagnostico`: sessão `válida até`, diário `sujo` com a entrada `LIDACTION`,
     backstop `armada (Armed)`.
3. **Fechar a tampa** por 2–3 min (com um monitor externo ou ouvindo um áudio tocando,
   ou deixando um download/ping rodando para ver que não parou). Ao abrir:
   - o notebook não dormiu (o download/ping seguiu; a contagem da pílula continuou);
   - a tela apagou ao fechar e voltou ao abrir; o som voltou ao volume de antes;
   - apps da lista de congelados (ex.: Slack) voltaram a responder;
   - com o carregador fora, a Economia de energia ligou com a tampa fechada e desligou
     depois de abrir.
4. **+tempo**: no painel, `+15 min` empurra o fim; `termina às` e o tooltip mostram o novo
   horário.
5. **Encerrar segurando** (`Segure para encerrar`, no painel): soltar antes de 0,6 s não
   encerra; segurar encerra, o painel recolhe e a pílula some.
   `LIDACTION` volta ao valor anotado; diagnóstico com diário `limpo`.
6. **Matar o app** no meio de uma sessão curta (5 min): Gerenciador de Tarefas >
   Insomnia > Finalizar tarefa. Até o fim da sessão o `LIDACTION` continua `0x0` (por
   desenho). **Esperar 1–2 min depois da hora de fim** e conferir:
   - `powercfg /q … LIDACTION` voltou ao valor anotado;
   - `insomnia.log` tem a linha do backstop restaurando;
   - `session.json` sumiu e o diário está limpo (`--diagnostico`).
7. **Matar com a tampa fechada** (apps congelados e mudo): iniciar uma sessão curta
   (5 min) com um app da lista de congelamento aberto (ex.: WhatsApp) e um áudio
   tocando. Num PowerShell, deixar o encerramento agendado **antes** de fechar a tampa
   (abrir a tampa desfaz as ações, então o app tem de morrer com ela fechada):
   `Start-Sleep 30; Stop-Process -Name Insomnia -Force`. Fechar a tampa logo em seguida
   e mantê-la fechada por ~1 min — o app morre com o app congelado suspenso e o som
   mudo. Abrir a tampa e **não** reabrir o Insomnia.
   **Esperar 1–2 min depois da hora de fim** e conferir: o app congelado volta a
   responder, o áudio volta e `LIDACTION` volta ao valor anotado — tudo pelo backstop.
8. **Reiniciar no meio da sessão**: depois do login, ao abrir o app a sessão continua
   (se ainda válida) ou é desfeita (se expirou). Sem abrir o app, o backstop desfaz até
   1–2 min depois do fim.
9. **Bateria**: com o carregador fora e o piso "encerrar abaixo de" acima do percentual
   atual, a sessão encerra sozinha e restaura tudo.
10. **Hotspot**: configure o SSID do celular em Configurações > Rede (depois de conectar a
    ele uma vez pelo Windows). Com a sessão ativa, desligue o roteador: em segundos o
    notebook entra no hotspot; `Logs\handoffs.log` registra a queda.
11. **Navegador**: com o Chrome aberto normalmente, o menu e o painel mostram o aviso de
    que o Chrome está limitado;
    "Relançar Chrome sem limitação" fecha e reabre com as mesmas janelas/perfil; o
    diagnóstico passa a mostrar `sem limitação`.
12. **Sair pela bandeja** com sessão ativa restaura tudo antes de fechar.
13. **Interface** (o que nenhum teste automatizado vê):
    - **Pílula ao iniciar**: abrir o app com uma sessão ainda válida (ou iniciar uma) faz a
      pílula aparecer, com um leve deslizar, centrada no topo do monitor principal; sem
      sessão, só o ícone da bandeja (olho fechado). A pílula não aparece na barra de
      tarefas nem no Alt+Tab, e ficar visível não tira o foco da janela em uso.
    - **Arrastar abre**: arrastar a pílula para baixo faz o painel crescer dela, seguindo o
      ponteiro; soltar cedo volta, soltar além de ~48 px (ou num puxão rápido) abre. `Esc`,
      clique fora e arrastar a alça para cima fecham.
    - **Tampa fechada mostra a faixa**: fechar e reabrir a tampa durante a sessão; enquanto
      fechada (com monitor externo), a pílula mostra o glifo de pausa e o painel a faixa
      `Tampa fechada` com o que foi feito (`2 apps congelados · áudio mudo · tela apagada`).
    - **Fim próximo muda a cor**: numa sessão de 6 min, abaixo de 5 min a pílula passa a
      relógio e texto âmbar com segundos (`4m 59s`), o `+15 min` vira botão de destaque e
      a sublinha mostra `faltam N min`; abaixo de 1 min, vermelho.
    - **Aviso aparece**: com o Chrome aberto sem as flags, a pílula troca o olho pelo aviso
      âmbar, o ícone da bandeja ganha o ponto âmbar e o painel mostra o aviso com
      `Relançar Chrome`.
    - **Configurações grava**: `Configurações…` no menu (ou a engrenagem do painel) abre a
      janela; mudar um preset e um interruptor de Tampa, fechar e reabrir o app: as
      mudanças estão em `%APPDATA%\Insomnia\config.json` e continuam lá.
    - **Claro/escuro ao vivo**: com pílula, painel e Configurações abertos, trocar o modo em
      Configurações do Windows > Personalização > Cores: tudo troca na hora, sem reiniciar;
      o ícone da bandeja acompanha a cor da barra. Com "Efeitos de transparência" desligado
      as janelas ficam com fundo sólido (nunca pretas ou invisíveis).
    - **DPI 150 % no segundo monitor**: com um monitor externo a 150 % e o notebook a
      100 % (ou o inverso), tornar o externo o principal: a pílula vai para ele, nítida e
      centrada; o menu da bandeja abre no monitor do cursor e as Configurações abrem
      nítidas. Desconectar o monitor reposiciona a pílula no que sobrou.
    - **Tela cheia**: um vídeo em tela cheia ou apresentação faz a pílula sumir; ao sair, ela
      volta.
14. **Desinstalar**: `.\installer\desinstalar.ps1`. `LIDACTION` igual ao anotado, tarefa
    `Insomnia Backstop` removida, atalho e pasta do programa removidos.

Se algo ficar errado depois de um teste: `Insomnia.exe --backstop --force` desfaz o que
estiver no diário; se o diário estiver corrompido, restaure à mão com
`powercfg /setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 1`,
`powercfg /setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 1` e
`powercfg /setactive SCHEME_CURRENT` (use os valores anotados).

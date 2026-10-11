<#
.SYNOPSIS
  Desinstala o Insomnia do usuário atual, desfazendo antes tudo o que ele mudou no sistema.

.DESCRIPTION
  Ordem (a recuperação vem ANTES de remover qualquer coisa):
  1. se a bandeja estiver aberta, pede para sair por ela (Sair restaura tudo); com -Forcar,
     encerra o processo (o passo 2 desfaz o que ficou);
  2. Insomnia.exe --backstop --force: restaura LIDACTION, Economia de energia, apps
     congelados e áudio a partir do diário, mesmo com sessão válida. Se falhar, PARA aqui:
     sem a recuperação completa, remover o backstop deixaria o notebook sem dormir com a
     tampa fechada;
  3. Insomnia.exe --remover-backstop (tarefa "Insomnia Backstop"; se o exe não existir,
     schtasks /delete);
  4. remove o início com o Windows (HKCU\...\Run\Insomnia) e o atalho do Menu Iniciar;
  5. apaga %LOCALAPPDATA%\Programs\Insomnia.

  Config (%APPDATA%\Insomnia) e diário/log (%LOCALAPPDATA%\Insomnia) ficam, salvo -Purge.

.PARAMETER Purge
  Apaga também config, diário, sessão e logs.

.PARAMETER Forcar
  Encerra a bandeja aberta em vez de pedir para sair por ela.
#>
[CmdletBinding()]
param(
    [switch]$Purge,
    [switch]$Forcar
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$destino   = Join-Path $env:LOCALAPPDATA 'Programs\Insomnia'
$exe       = Join-Path $destino 'Insomnia.exe'
$atalho    = Join-Path ([Environment]::GetFolderPath('Programs')) 'Insomnia.lnk'
$runKey    = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$tarefa    = 'Insomnia Backstop'
$config    = Join-Path $env:APPDATA 'Insomnia'
$dados     = Join-Path $env:LOCALAPPDATA 'Insomnia'
$temExe    = Test-Path $exe

function Invoke-Insomnia([string[]]$argumentos) {
    $p = Start-Process -FilePath $exe -ArgumentList $argumentos -Wait -PassThru -NoNewWindow
    return $p.ExitCode
}

# 1. Bandeja aberta
$abertos = @(Get-Process -Name 'Insomnia' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ($_.Path -ieq $exe) })
if ($abertos.Count -gt 0) {
    if (-not $Forcar) {
        Write-Host 'O Insomnia está aberto. Saia pela bandeja (botão direito > Sair) e rode de novo,' -ForegroundColor Yellow
        Write-Host 'ou use -Forcar para encerrá-lo (a recuperação abaixo desfaz o que ele deixou).' -ForegroundColor Yellow
        exit 1
    }
    Write-Host '==> Encerrando a bandeja'
    $abertos | Stop-Process -Force
    $abertos | ForEach-Object { $_.WaitForExit(10000) | Out-Null }
}

# 2. Recuperação
if ($temExe) {
    Write-Host '==> Desfazendo mudanças de energia (--backstop --force)'
    $codigo = 75
    for ($tentativa = 1; $tentativa -le 5 -and $codigo -eq 75; $tentativa++) {
        $codigo = Invoke-Insomnia @('--backstop', '--force')
        if ($codigo -eq 75) { Write-Host '    lock de recuperação ocupado; tentando de novo em 3 s'; Start-Sleep -Seconds 3 }
    }
    if ($codigo -ne 0) {
        Write-Host "A recuperação não terminou (código $codigo). Nada foi removido." -ForegroundColor Red
        Write-Host "Veja $dados\Logs\insomnia.log e rode '$exe --diagnostico | Out-Host'." -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "Insomnia.exe não encontrado em ${destino}: pulando a recuperação." -ForegroundColor Yellow
    Write-Host "Se o diário $dados\state.json existir e estiver sujo, confira o LIDACTION com 'powercfg /q'." -ForegroundColor Yellow
}

# 3. Tarefa do backstop
Write-Host "==> Removendo a tarefa '$tarefa'"
$removida = $false
if ($temExe) { $removida = ((Invoke-Insomnia @('--remover-backstop')) -eq 0) }
if (-not $removida) {
    & schtasks.exe /delete /tn $tarefa /f 2>$null | Out-Null
}

# 4. Início com o Windows e atalho
Write-Host '==> Removendo início com o Windows e atalho'
Remove-ItemProperty -Path $runKey -Name 'Insomnia' -ErrorAction SilentlyContinue
if (Test-Path $atalho) { Remove-Item $atalho -Force }

# 5. Programa
if (Test-Path $destino) {
    Write-Host "==> Apagando $destino"
    Remove-Item $destino -Recurse -Force
}

if ($Purge) {
    Write-Host '==> -Purge: apagando config, diário e logs'
    foreach ($pasta in @($config, $dados)) {
        if (Test-Path $pasta) { Remove-Item $pasta -Recurse -Force }
    }
} else {
    Write-Host "Mantidos: $config (config) e $dados (diário e logs). Use -Purge para apagar."
}

Write-Host 'Insomnia desinstalado.' -ForegroundColor Green
exit 0

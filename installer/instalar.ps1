<#
.SYNOPSIS
  Compila e instala o Insomnia para o usuário atual (sem administrador).

.DESCRIPTION
  1. dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
     (saída fora do OneDrive, em %LOCALAPPDATA%\InsomniaBuild\publish);
  2. copia Insomnia.exe para %LOCALAPPDATA%\Programs\Insomnia;
  3. cria o atalho "Insomnia" no Menu Iniciar do usuário;
  4. roda "Insomnia.exe --diagnostico" e mostra o relatório.

  Não registra o início com o Windows (isso é em Configurações > App) nem instala a tarefa do
  backstop: o próprio app instala a tarefa "Insomnia Backstop" ao iniciar a primeira sessão.
  Requer o SDK do .NET 10 para compilar e o runtime do .NET 10 Desktop para rodar.

.PARAMETER SemDiagnostico
  Pula o passo 4.

.PARAMETER SemPreVoo
  Roda o diagnóstico com --sem-pre-voo (não reescreve o LIDACTION).
#>
[CmdletBinding()]
param(
    [switch]$SemDiagnostico,
    [switch]$SemPreVoo
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$raiz       = Split-Path -Parent $PSScriptRoot
$projeto    = Join-Path $raiz 'src\Insomnia.App\Insomnia.App.csproj'
$publicacao = Join-Path $env:LOCALAPPDATA 'InsomniaBuild\publish'
$destino    = Join-Path $env:LOCALAPPDATA 'Programs\Insomnia'
$exe        = Join-Path $destino 'Insomnia.exe'
$atalho     = Join-Path ([Environment]::GetFolderPath('Programs')) 'Insomnia.lnk'

if (-not (Test-Path $projeto)) {
    throw "Projeto não encontrado: $projeto (rode este script de dentro de insomnia-windows\installer)"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet não encontrado no PATH: instale o SDK do .NET 10.'
}

# O exe em uso fica travado: atualizar com o app aberto falharia no meio da cópia.
$abertos = @(Get-Process -Name 'Insomnia' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ($_.Path -ieq $exe) })
if ($abertos.Count -gt 0) {
    Write-Host 'O Insomnia está aberto. Saia pela bandeja (botão direito > Sair) e rode este script de novo.' -ForegroundColor Yellow
    exit 1
}

Write-Host "==> Compilando ($projeto)"
# Compila numa pasta própria e limpa: reaproveitar a saída dos builds de desenvolvimento
# e de teste já deixou o publish com estado inconsistente (NETSDK1047, MSB3030).
$artefatos = Join-Path $env:LOCALAPPDATA 'InsomniaBuild\instalador'
foreach ($p in @($publicacao, $artefatos)) { if (Test-Path $p) { Remove-Item $p -Recurse -Force } }
& dotnet publish $projeto -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true "-p:ArtifactsPath=$artefatos" -o $publicacao
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou (código $LASTEXITCODE)" }

$publicado = Join-Path $publicacao 'Insomnia.exe'
if (-not (Test-Path $publicado)) { throw "Insomnia.exe não apareceu em $publicacao" }

Write-Host "==> Copiando para $destino"
New-Item -ItemType Directory -Path $destino -Force | Out-Null
# Single-file: o exe é o essencial; o resto (pdb, configs de runtime, se houver) vai junto.
Get-ChildItem -Path $publicacao -File | Copy-Item -Destination $destino -Force

Write-Host "==> Atalho no Menu Iniciar: $atalho"
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($atalho)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $destino
$lnk.Description = 'Insomnia: mantém o notebook acordado por uma sessão, inclusive de tampa fechada'
$lnk.IconLocation = "$exe,0"
$lnk.Save()
[void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)

Write-Host ''
Write-Host "Instalado em $exe" -ForegroundColor Green

if ($SemDiagnostico) { exit 0 }

Write-Host ''
Write-Host '==> Diagnóstico'
$argumentos = @('--diagnostico')
if ($SemPreVoo) { $argumentos += '--sem-pre-voo' }
# O pipe faz o PowerShell esperar o exe (WinExe) e mostrar a saída.
& $exe @argumentos | Out-Host
$codigo = $LASTEXITCODE
if ($codigo -ne 0) {
    Write-Host "O diagnóstico encontrou problema(s) (código $codigo): veja o resumo acima." -ForegroundColor Yellow
} else {
    Write-Host 'Diagnóstico sem problemas. Abra o Insomnia pelo Menu Iniciar.' -ForegroundColor Green
}
exit $codigo

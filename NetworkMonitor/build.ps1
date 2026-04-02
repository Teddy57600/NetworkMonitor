<#
.SYNOPSIS
    Script de build Docker pour le projet MoviesFollower2025.

.DESCRIPTION
    Ce script construit l'image Docker avec une version spécifique, crée un tag "latest", et pousse les deux images vers le registre Docker.

.PARAMETER Version
    Numéro de version à utiliser pour le build (format suggéré: x.y.z, par exemple 1.0.0).

.EXAMPLE
    .\build.ps1 -Version 1.0.0
#>

[CmdletBinding()]
Param(
    [Parameter(Mandatory = $true, HelpMessage = "Numéro de version au format x.y.z, par exemple 1.0.0.")]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

# Fonction pour exécuter les commandes et vérifier les erreurs
function Execute-Command {
    param (
        [string]$Command
    )

    Write-Host "Exécution de: $Command" -ForegroundColor Cyan
    Invoke-Expression $Command

    if ($LASTEXITCODE -ne 0) {
        Write-Error "La commande a échoué: $Command"
        exit $LASTEXITCODE
    }
}

function Update-DockerfileVersion {
    param (
        [string]$DockerfilePath,
        [string]$Version
    )

    if (-not (Test-Path -Path $DockerfilePath)) {
        Write-Error "Dockerfile introuvable: $DockerfilePath"
        exit 1
    }

    $dockerfileContent = Get-Content -Path $DockerfilePath -Raw
    $updatedContent = [regex]::Replace(
        $dockerfileContent,
        '(?m)^ENV\s+APP_VERSION="[^"]*"\s*$',
        "ENV APP_VERSION=`"$Version`"",
        1
    )

    $updatedContent = [regex]::Replace(
        $updatedContent,
        '(?m)^LABEL\s+Version="[^"]*"\s*$',
        "LABEL Version=`"$Version`"",
        1
    )

    if ($updatedContent -eq $dockerfileContent) {
        Write-Error "Aucun ENV APP_VERSION ou LABEL Version n'a été trouvé dans le Dockerfile: $DockerfilePath"
        exit 1
    }

    Set-Content -Path $DockerfilePath -Value $updatedContent
    Write-Host "Version du Dockerfile mise à jour: $Version" -ForegroundColor Yellow
}

# Définir le nom de l'image
$imageName = "alethi/networkmonitor-tal"
$dockerfilePath = Join-Path $PSScriptRoot "Dockerfile"

# Mettre à jour la version dans le Dockerfile avant le build
Update-DockerfileVersion -DockerfilePath $dockerfilePath -Version $Version

# Construire l'image Docker avec le numéro de version
$solutionRoot = Split-Path $PSScriptRoot -Parent
$buildCommand = "docker build --network=host -t ${imageName}:${Version} -f `"$dockerfilePath`" `"$solutionRoot`""
Execute-Command -Command $buildCommand

# Tagger l'image avec "latest"
$tagCommand = "docker tag ${imageName}:${Version} ${imageName}:latest"
Execute-Command -Command $tagCommand

# Pousser l'image avec la version spécifique
$pushVersionCommand = "docker push ${imageName}:${Version}"
Execute-Command -Command $pushVersionCommand

# Pousser l'image avec le tag "latest"
$pushLatestCommand = "docker push ${imageName}:latest"
Execute-Command -Command $pushLatestCommand

Write-Host "Build et push des images Docker réussis pour la version $Version." -ForegroundColor Green
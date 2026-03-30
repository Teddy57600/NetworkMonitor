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

# Définir le nom de l'image
$imageName = "alethi/networkmonitor-tal"

# Construire l'image Docker avec le numéro de version
$solutionRoot = Split-Path $PSScriptRoot -Parent
$buildCommand = "docker build --network=host -t ${imageName}:${Version} -f `"$PSScriptRoot\Dockerfile`" `"$solutionRoot`""
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
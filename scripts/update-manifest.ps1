[CmdletBinding()]
param (
  [Parameter(Mandatory=$false)]
  [string]
  $Version,

  # Store identity reserved for THIS fork in Partner Center. Leave both empty for
  # sideload builds; the manifest's own identity is used instead.
  #
  # Do not set these to the original Screenbox Store identity (18496Starpine.Screenbox).
  # That name is reserved by the upstream author, and a package carrying it would present
  # itself to Windows as their app.
  [Parameter(Mandatory=$false)]
  [string]
  $IdentityName,

  [Parameter(Mandatory=$false)]
  [string]
  $Publisher
)

# Update package manifest for store upload
$ProjectPath = "$PSScriptRoot\..\Screenbox"
$ManifestPath = "$ProjectPath\Package.appxmanifest"
[xml]$xmlDoc = Get-Content $ManifestPath

if ($IdentityName -or $Publisher) {
  if (-not ($IdentityName -and $Publisher)) {
    throw "Provide both -IdentityName and -Publisher, or neither."
  }
  $xmlDoc.Package.Identity.Name = $IdentityName
  $xmlDoc.Package.Identity.Publisher = $Publisher
}

$xmlDoc.Package.Properties.DisplayName="Screenbox Enhanced"
$xmlDoc.Package.Applications.Application.VisualElements.DisplayName="ms-resource:ManifestResources/AppDisplayName"
if ($Version -match '^(\d+.\d+.\d+)$') {
  $Version = $Version + ".0"
  $xmlDoc.Package.Identity.Version = $Version
}

$xmlDoc.Save($ManifestPath)

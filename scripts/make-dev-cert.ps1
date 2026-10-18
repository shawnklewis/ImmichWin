# Creates a self-signed dev certificate (Subject must match Publisher in Package.appxmanifest) for local MSIX builds.
$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=ImmichWinDev" -KeyUsage DigitalSignature `
  -FriendlyName "ImmichWin dev" -CertStoreLocation "Cert:\CurrentUser\My" `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
$pwd = ConvertTo-SecureString -String "immichwin-dev" -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath "$PSScriptRoot\..\ImmichWinDev.pfx" -Password $pwd | Out-Null
Export-Certificate -Cert $cert -FilePath "$PSScriptRoot\..\ImmichWinDev.cer" | Out-Null
Write-Host "Created ImmichWinDev.pfx (git-ignored). To install the app, trust ImmichWinDev.cer: Import-Certificate -FilePath ImmichWinDev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople  (as admin)"

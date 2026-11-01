# Builds dist\easy_installer.exe. Needs only Windows (the C# compiler ships with .NET Framework 4.x) and internet
# for the first run (downloads the two pinned prerequisite zips and verifies their SHA-256).
$ErrorActionPreference = 'Stop'
$root  = $PSScriptRoot
$cache = Join-Path $root 'build\cache'
$tmp   = Join-Path $root 'build'
$dist  = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $cache, $dist | Out-Null

$fw  = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc (install .NET Framework 4.x)" }

# Pinned prerequisites (bundled into the installer)
$deps = @(
  @{ File='cleo.zip'; Sha='d9aa5c2350fd215645eaed69249cd9e2ac7a1effce704b50b661c5465cc6eb90';
     Url='https://github.com/cleolibrary/CLEO-Redux/releases/download/1.4.2/cleo_redux_1.4.2.x64.zip' },
  @{ File='asi.zip';  Sha='a99e188e1f40d7aac9db9c0339990332152d372e0359faa06a52fa339e8b218f';
     Url='https://github.com/ThirteenAG/Ultimate-ASI-Loader/releases/download/v10.0.2/Ultimate-ASI-Loader_x64.zip' }
)
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
foreach ($d in $deps) {
  $p = Join-Path $cache $d.File
  if (-not (Test-Path $p)) { Write-Host "Downloading $($d.Url)"; Invoke-WebRequest -Uri $d.Url -OutFile $p -UseBasicParsing }
  $h = (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash.ToLower()
  if ($h -ne $d.Sha) { Remove-Item $p; throw "SHA-256 mismatch for $($d.File): got $h" }
  Write-Host "OK  $($d.File) $h"
}

# 1) the background helper
$helper = Join-Path $tmp 'CustomRadio.exe'
& $csc /nologo /target:winexe /optimize+ /out:$helper `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll `
  /r:"$fw\WPF\PresentationCore.dll" /r:"$fw\WPF\WindowsBase.dll" /r:System.Xaml.dll `
  (Join-Path $root 'src\CustomRadio.cs')
if ($LASTEXITCODE -ne 0) { throw 'helper compile failed' }

# 2) the installer, with everything embedded
$exe = Join-Path $dist 'easy_installer.exe'
& $csc /nologo /target:winexe /optimize+ /out:$exe `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll `
  "/resource:$helper,CustomRadio.exe" `
  "/resource:$(Join-Path $root 'src\custom_radio[fs].js'),custom_radio.js" `
  "/resource:$(Join-Path $cache 'cleo.zip'),cleo.zip" `
  "/resource:$(Join-Path $cache 'asi.zip'),asi.zip" `
  "/resource:$(Join-Path $root 'third_party\CLEO-Redux-LICENSE.txt'),CLEO-Redux-LICENSE.txt" `
  "/resource:$(Join-Path $root 'third_party\Ultimate-ASI-Loader-LICENSE.txt'),Ultimate-ASI-Loader-LICENSE.txt" `
  (Join-Path $root 'src\EasyInstaller.cs')
if ($LASTEXITCODE -ne 0) { throw 'installer compile failed' }

$sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash.ToLower()
"$sha *easy_installer.exe" | Set-Content -Encoding ascii (Join-Path $dist 'easy_installer.exe.sha256')
Write-Host "Built $exe ($([math]::Round((Get-Item $exe).Length/1MB,1)) MB)`nSHA-256 $sha"

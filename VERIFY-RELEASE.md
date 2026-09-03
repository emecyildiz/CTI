# Verify a release download

Each release contains a ZIP archive, a Windows x64 installer, a Linux installer, their `.sha256` checksum files, and a JSON manifest. Verify the applicable checksum before extracting the ZIP or running either installer.

## Linux

Place the ZIP and checksum file in the same directory:

```sh
sha256sum -c cti-self-hosted-0.1.0-rc.5.zip.sha256
```

The command must report `OK`.

## Windows PowerShell

```powershell
$expected = (Get-Content ./cti-self-hosted-0.1.0-rc.5.zip.sha256).Split()[0]
$actual = (Get-FileHash ./cti-self-hosted-0.1.0-rc.5.zip -Algorithm SHA256).Hash
$actual.Equals($expected, [StringComparison]::OrdinalIgnoreCase)
```

The final result must be `True`. Do not continue when the checksum differs.

### Windows installer

```powershell
$expected = (Get-Content ./CTI-Setup-0.1.0-rc.5-win-x64.exe.sha256).Split()[0]
$actual = (Get-FileHash ./CTI-Setup-0.1.0-rc.5-win-x64.exe -Algorithm SHA256).Hash
$actual.Equals($expected, [StringComparison]::OrdinalIgnoreCase)
```

The final result must be `True`. The installer is currently unsigned, so checksum verification is especially important when Windows SmartScreen displays an unrecognized-app warning.

### Linux installer

```sh
sha256sum -c CTI-Setup-0.1.0-rc.5-linux.sh.sha256
```

The command must report `OK`. The Linux installer then performs a second checksum verification on the release ZIP before extracting it.

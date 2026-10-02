# Verify a release download

Each release contains a ZIP archive, a Windows x64 installer, a Linux installer, their `.sha256` checksum files, and a JSON manifest. Verify the applicable checksum before extracting the ZIP or running either installer.

Download matching assets from the [official releases page](https://github.com/emecyildiz/CTI/releases). Use the exact version in their filenames; unreleased source changes on `main` are not necessarily included in the latest published installer. The examples below use rc.9, not a promise that a newer candidate has been published.

A matching checksum detects corruption or a mismatch with the supplied checksum file. It does **not** authenticate the publisher if both files have been replaced by an attacker. Obtain both from the official repository; the Windows installer is currently not Authenticode-signed.

## Linux

Place the ZIP and checksum file in the same directory:

```sh
version=0.1.0-rc.9 # Replace with the downloaded release version.
sha256sum -c "cti-self-hosted-$version.zip.sha256"
```

The command must report `OK`.

## Windows PowerShell

```powershell
$version = '0.1.0-rc.9' # Replace with the downloaded release version.
$file = "cti-self-hosted-$version.zip"
$expected = (Get-Content -LiteralPath "$file.sha256").Split()[0]
$actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
$actual.Equals($expected, [StringComparison]::OrdinalIgnoreCase)
```

The final result must be `True`. Do not continue when the checksum differs.

### Windows installer

```powershell
$version = '0.1.0-rc.9' # Replace with the downloaded release version.
$file = "CTI-Setup-$version-win-x64.exe"
$expected = (Get-Content -LiteralPath "$file.sha256").Split()[0]
$actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
$actual.Equals($expected, [StringComparison]::OrdinalIgnoreCase)
```

The final result must be `True`. A checksum does not remove a Windows SmartScreen warning or replace a publisher signature. Do not disable Windows protection to make an untrusted download run.

Maintainers can run a **trusted, locally built or verified official** installer with `--verify-payload` to check its embedded package without installing CTI. This still executes the EXE; it is not a safe way to inspect an untrusted executable. Candidate user testing is documented in [the Windows test checklist](WINDOWS-TEST-CHECKLIST.md).

### Linux installer

```sh
version=0.1.0-rc.9 # Replace with the downloaded release version.
sha256sum -c "CTI-Setup-$version-linux.sh.sha256"
```

The command must report `OK`. The Linux installer then performs a second checksum verification on the release ZIP before extracting it.

# Verify a release download

Each release contains a ZIP archive, a `.sha256` checksum file, and a JSON manifest. Verify the checksum before extracting or running any script.

## Linux

Place the ZIP and checksum file in the same directory:

```sh
sha256sum --check cti-self-hosted-0.1.0-rc.3.zip.sha256
```

The command must report `OK`.

## Windows PowerShell

```powershell
$expected = (Get-Content ./cti-self-hosted-0.1.0-rc.3.zip.sha256).Split()[0]
$actual = (Get-FileHash ./cti-self-hosted-0.1.0-rc.3.zip -Algorithm SHA256).Hash
$actual.Equals($expected, [StringComparison]::OrdinalIgnoreCase)
```

The final result must be `True`. Do not continue when the checksum differs.

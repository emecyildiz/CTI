# Windows dialog component acceptance

Run `dotnet run --project tests/installer-ui/InstallerUi.Tests.csproj -c Release` on Windows. The suite instantiates the production AI, Telegram and preparation dialogs without installing CTI or contacting Docker/providers. It checks wrapped label sizing, action rows, keyboard contracts, unique mnemonics, accessible input names, secret masking/clearing and explicit optional-check selection at 100%, 150% and 200% component/font scales.

For manual visual/keyboard inspection, build `tests/installer-ui-preview/InstallerUi.Preview.csproj` and launch its EXE. Select a scale and open a dialog. These dialogs have no connection to the installer's backend; even a successful form closes without preparing anything. Do not enter actual credentials. The preview temporarily shows each dialog in the taskbar to make it separately targetable; production dialogs remain owned modal windows.

Component/font scaling is a layout stress check, **not actual monitor DPI emulation**. Physical multi-monitor/DPI changes, small displays, released installer wiring, clean Windows installation and real provider/Telegram operation require separate acceptance. The production installer remains unsigned.

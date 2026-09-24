## Download

| System | File |
|---|---|
| Windows 10/11 (most PCs) | `PhotoTag-win-x64-Setup.exe` |
| Windows on ARM (Snapdragon laptops) | `PhotoTag-win-arm64-Setup.exe` |
| Mac with Apple silicon (M1 and later) | `PhotoTag-osx-arm64-Setup.pkg` |
| Mac with an Intel processor | `PhotoTag-osx-x64-Setup.pkg` |
| Linux (64-bit) | `PhotoTag.AppImage` from the linux-x64 set |

Everything PhotoTag needs, including ExifTool, is included. Once installed, PhotoTag checks for new versions when it starts and offers to update (you can turn this off under ⚙ Settings).

**The first time you open it, you'll see a warning.** PhotoTag isn't code-signed yet, so Windows and macOS don't recognise it:

- **Windows:** "Windows protected your PC". Click **More info**, then **Run anyway**.
- **macOS:** "PhotoTag can't be opened". Open **System Settings → Privacy & Security**, scroll down, and click **Open Anyway** next to PhotoTag.
- **Linux:** make the AppImage executable (`chmod +x PhotoTag.AppImage`, or tick "Allow executing" in its properties), then run it.


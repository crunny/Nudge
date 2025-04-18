<p align="center">
  <img src="Nudge/Assets/NudgeBanner.png" width="600" alt="Nudge Banner">
</p>

A minimal, elegant clipboard notification utility for Windows.

## ✨ Overview

Nudge is a lightweight clipboard monitor that elegantly notifies you whenever content is copied to your clipboard. With subtle animations and a clean design inspired by iOS, Nudge stays out of your way while keeping you informed.

## 🔍 Features

- **Elegant Notifications** — Subtle, animated notifications appear when content is copied
- **Clipboard Monitoring** — Automatically detects text, images, files, and audio in clipboard
- **System Tray Integration** — Runs silently in background with easy access via system tray
- **Startup Control** — Option to run automatically when Windows starts
- **Resource Efficient** — Minimal CPU and memory usage

## 📥 Installation

1. Download the latest release from the [Releases](https://github.com/crunny/Nudge/releases) page
2. Extract the ZIP file to a location of your choice
3. Run `Nudge.exe` to start the application
4. To enable automatic startup, right-click the system tray icon and select "Enable Startup with Windows"

## 🚀 Usage

Nudge runs silently in your system tray. When you copy content to your clipboard:

- A notification appears briefly with a preview of the copied content
- The notification automatically fades away after a moment
- No configuration needed — it just works

To exit or configure Nudge, right-click its icon in the system tray.

## 🗺️ Future Improvements

The focus for future development is on enhancing the UI/UX experience.

## 🛠️ Building from Source

Nudge is built with .NET 8.0 and Windows Forms.

```
git clone https://github.com/crunny/Nudge.git
cd Nudge
dotnet build
```

## 📄 License

This project is licensed under the GNU General Public License v3.0 (GPL-3.0) - see the [LICENSE](LICENSE) file for details.

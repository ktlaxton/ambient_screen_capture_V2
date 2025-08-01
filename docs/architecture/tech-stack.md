# Tech Stack

**Cloud Infrastructure**

Provider: Not Applicable. This is a standalone desktop application.

**Technology Stack Table**
| Category | Technology | Version | Purpose | Rationale |
| :--- | :--- | :--- | :--- | :--- |
| Language | C# | 12.0 (.NET 8 SDK) | Primary development language | Modern, powerful, and the standard for building native Windows apps. |
| Framework | WPF | .NET 8 | The core UI and application framework | Proven, stable technology for Windows desktop applications with excellent .NET 8 support. |
| Build Tool | MSBuild | via dotnet CLI | Compiling and packaging the application | Integrated standard for the .NET ecosystem, accessible via command line. |
| Screen Capture| Windows.Graphics.Capture | Windows 10, v1803+ | Capturing the screen content efficiently | A modern, high-performance, OS-level API for screen capture. |
| Audio Capture | NAudio | 2.2.1 | Capturing system audio output | A popular, robust, and feature-rich audio library for .NET. |
| Unit Testing | xUnit | 2.8.0 | Framework for writing and running tests | A flexible and widely-used testing framework in the .NET community. |
| Mocking | Moq | 4.20.70 | Creating mock objects for testing | The industry standard for mocking in .NET. |
| UI Testing | WinAppDriver | 1.2.1 | Automating E2E tests for the UI | Microsoft's standard for UI test automation on Windows. |
| System Tray | Windows Forms | .NET 8 | System tray icon functionality | Provides NotifyIcon for system tray integration in WPF applications. |

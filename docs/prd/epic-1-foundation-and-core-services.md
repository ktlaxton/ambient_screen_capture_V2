# Epic 1: Foundation & Core Services

**Expanded Goal:** The objective of this epic is to establish the core technical foundation of the Windows application. This includes creating the main project, implementing the background services that can capture screen and audio data, and building the basic user interface shell with non-functional controls. By the end of this epic, we will have a runnable application that is ready for the effects engine to be built on top of it.

* **Story 1.1: Initial Project Setup**
    * **As a** developer, **I want** a new, properly configured C# WinUI 3 project, **so that** I have a stable foundation to start building the application's features.
    * **Acceptance Criteria:**
        1.  A new C# WinUI 3 project is created in the repository.
        2.  The project can be compiled and launched successfully on a Windows machine.
        3.  A basic, empty main window appears when the application is run.
        4.  The application includes a system tray icon for future use.
* **Story 1.2: Screen Capture Service**
    * **As a** system, **I want** to continuously capture the visuals of the primary monitor in a highly efficient manner, **so that** this visual data is available for the effects engine.
    * **Acceptance Criteria:**
        1.  A background service is implemented that can capture the primary monitor's screen content.
        2.  The capture process is optimized for low latency and minimal GPU impact.
        3.  The captured data is accessible within the application for other services to use.
        4.  The service can be programmatically started and stopped.
* **Story 1.3: Audio Capture Service**
    * **As a** system, **I want** to continuously capture the system's default audio output, **so that** this audio data is available for the effects engine.
    * **Acceptance Criteria:**
        1.  A background service is implemented that can capture the system's default audio output mix.
        2.  The audio capture process is optimized for low CPU impact.
        3.  The captured audio level/data is accessible within the application.
        4.  The service can be programmatically started and stopped.
* **Story 1.4: Basic Settings UI Shell**
    * **As a** user, **I want** to see the main control panel with all the settings components, **so that** I can understand how I will control the application.
    * **Acceptance Criteria:**
        1.  The main application window displays a settings panel.
        2.  The panel contains a master On/Off toggle switch.
        3.  The panel contains a placeholder for a dropdown/list to select effect styles.
        4.  The panel contains a slider for adjusting audio sensitivity.
        5.  The UI elements are laid out according to the high-level design, but do not need to be functional yet.
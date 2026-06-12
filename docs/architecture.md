# Ambient Effects Engine Technical Architecture Document

## Introduction

This document outlines the overall project architecture for the Ambient Effects Engine, including backend systems, shared services, and non-UI specific concerns. Its primary goal is to serve as the guiding architectural blueprint for AI-driven development, ensuring consistency and adherence to chosen patterns and technologies.

**Change Log**
| Date | Version | Description | Author |
| :--- | :--- | :--- | :--- |
| 2025-07-19 | 1.0 | Initial Architecture Design | Winston (Architect) |
| 2025-07-30 | 1.1 | Added Epic 4 PoC Integration Architecture | Winston (Architect) |

---
## Starter Template or Existing Project

The architecture will be based on the standard "Blank App, Packaged (WinUI 3 in Desktop)" template provided within Visual Studio. This provides a best-practice foundation for a modern Windows desktop application using C# and WinUI 3.

---
## High Level Architecture

**Technical Summary**
This project will be architected as a monolithic Windows desktop application using the standard Model-View-ViewModel (MVVM) pattern to ensure a clean separation between the user interface and the core logic. It will feature background services for efficient, real-time screen and audio capture. A central processing module will analyze this data to drive a flexible rendering engine, which will use a "strategy" pattern to allow for easy selection and addition of new visual effects.

**High Level Overview**
* **Architectural Style:** Monolith. The application will be a single, self-contained executable.
* **Repository Structure:** Monorepo. All code for the application will reside in a single repository.

**High Level Project Diagram**
```mermaid
graph TD
    subgraph User Interaction
        A[User] --> B[Settings UI (View)];
    end

    subgraph Core Application
        B -- manipulates --> C[Settings Logic (ViewModel)];
        C -- controls --> D[Effects Manager];
        D --> E[Screen Capture Service];
        D --> F[Audio Capture Service];
        E -- raw video --> G[Data Processor];
        F -- raw audio --> G;
        G -- processed data --> H[Effects Renderer];
    end

    subgraph Output
        H -- renders to --> I[Secondary Monitors];
    end
```

## Architectural and Design Patterns

**Model-View-ViewModel (MVVM):** The standard pattern for WinUI 3, cleanly separating UI (View) from logic (ViewModel).

**Singleton Pattern:** For core Screen Capture and Audio Capture services to ensure only one instance is running.

**Strategy Pattern:** For selectable visual effects, allowing new effects to be added easily.

## Tech Stack

**Cloud Infrastructure**

Provider: Not Applicable. This is a standalone desktop application.

**Technology Stack Table**
| Category | Technology | Version | Purpose | Rationale |
| :--- | :--- | :--- | :--- | :--- |
| Language | C# | 12.0 (.NET 8 SDK) | Primary development language | Modern, powerful, and the standard for building native Windows apps. |
| Framework | WinUI 3 | Windows App SDK 1.5 | The core UI and application framework | The latest Microsoft technology for building modern, performant desktop apps. |
| Build Tool | MSBuild | 17.0 | Compiling and packaging the application | Integrated standard for the .NET and Visual Studio ecosystem. |
| Screen Capture| Windows.Graphics.Capture | Windows 10, v1803+ | Capturing the screen content efficiently | A modern, high-performance, OS-level API for screen capture. |
| Audio Capture | NAudio | 2.2.1 | Capturing system audio output | A popular, robust, and feature-rich audio library for .NET. |
| Unit Testing | xUnit | 2.8.0 | Framework for writing and running tests | A flexible and widely-used testing framework in the .NET community. |
| Mocking | Moq | 4.20.70 | Creating mock objects for testing | The industry standard for mocking in .NET. |
| UI Testing | WinAppDriver | 1.2.1 | Automating E2E tests for the UI | Microsoft's standard for UI test automation on Windows. |

## Data Models

**ApplicationSettings**
Purpose: To store all user-configurable settings, allowing them to be saved to a file and loaded at startup.

Key Attributes: IsEnabled (bool), SelectedEffectId (string), AudioSensitivity (float), SourceMonitorId (string), TargetMonitorIds (List

**EffectStyle**
Purpose: To represent a single visual effect that a user can choose.

Key Attributes: Id (string), Name (string), Description (string).

**DisplayMonitor**
Purpose: To store information about a single physical display connected to the user's computer.

Key Attributes: Id (string), Name (string), IsPrimary (bool).

## Components

**1. Capture Services (Screen & Audio)**
Responsibility: To efficiently capture raw video frames and audio streams from the OS.

**2. Data Processor**
Responsibility: To receive raw data and simplify it into "dominant color" and "audio intensity" values.

**3. Effects Engine & Renderer**
Responsibility: To take the processed data and draw the selected visual effect onto the target monitors.

**4. Settings Service**
Responsibility: A utility to save and load the ApplicationSettings data model to a local file.

**5. The View**
Responsibility: The visual part of the application (UI) defined in XAML. Contains no logic.

**6. The ViewModel**
Responsibility: The "brain" of the UI. It holds the application state and logic, communicating with the core services.

## Component Diagrams

**Core Workflow: User Changes Audio Sensitivity**

```mermaid
sequenceDiagram
    participant User
    participant View
    participant ViewModel
    participant EffectsManager
    participant DataProcessor
    participant Renderer

    User->>View: Drags sensitivity slider
    View->>ViewModel: Updates Sensitivity property
    ViewModel->>EffectsManager: Sets new sensitivity level
    loop Real-time Data Flow
        DataProcessor->>EffectsManager: Provides (Color, Intensity)
        EffectsManager->>Renderer: Renders effect with new sensitivity
    end
```

## External APIs & REST API Spec
Not applicable. The application is self-contained and does not use external APIs.

## Core Workflows

**Main Data Processing Loop**

```mermaid
sequenceDiagram
    participant ScreenCapture
    participant AudioCapture
    participant DataProcessor
    participant EffectsManager
    participant Renderer

    loop Every Frame
        ScreenCapture->>DataProcessor: New Video Frame
        AudioCapture->>DataProcessor: New Audio Buffer
        DataProcessor->>DataProcessor: Calculate Dominant Color & Audio Intensity
        DataProcessor->>EffectsManager: ProcessedData (Color, Intensity)
        EffectsManager->>Renderer: UpdateEffect(ProcessedData)
        Renderer->>Renderer: Draw frame on secondary monitors
    end
```

## Database Schema
Not applicable. Settings are saved to a local JSON file, not a formal database.

## Source Tree

```

/AmbientEffectsEngine/
|-- AmbientEffectsEngine.sln
|-- /AmbientEffectsEngine/
|   |-- Properties/
|   |-- Assets/
|   |-- /Models/
|   |   |-- ApplicationSettings.cs
|   |   |-- DisplayMonitor.cs
|   |   |-- EffectStyle.cs
|   |-- /Views/
|   |   |-- MainWindow.xaml
|   |   |-- MonitorSetupPage.xaml
|   |-- /ViewModels/
|   |   |-- MainViewModel.cs
|   |   |-- MonitorSetupViewModel.cs
|   |-- /Services/
|   |   |-- /Capture/
|   |   |-- /Processing/
|   |   |-- /Rendering/
|   |   |-- SettingsService.cs
|   |-- App.xaml
|   |-- MainWindow.xaml
|   |-- package.appxmanifest
|-- /AmbientEffectsEngine.Tests/
    |-- ... (test files)
```

## Infrastructure and Deployment
Deployment Strategy: The application will be packaged as a modern MSIX installer.

Automation: A GitHub Actions pipeline will be configured to automatically build and package the MSIX file for each new release.

Rollback Strategy: Users can uninstall a new version and reinstall a previous version, which will remain available for download.

## Error Handling Strategy
General Approach: A global exception handler will catch unexpected crashes, log the error to a file, and show a user-friendly message.

Logging: We will use Serilog to write application events and errors to a local log file for diagnostics.

## Coding Standards
Style: We will use the standard .NET code style analyzers built into Visual Studio.

Naming: We will follow the standard Microsoft C# Naming Conventions.

Critical Rules: Strict MVVM separation must be enforced; Services must be managed via Dependency Injection; OS interactions must be abstracted into Services.

## Test Strategy and Standards
Philosophy: All new logic will be accompanied by tests, aiming for 80% code coverage.

Test Types: We will use xUnit for unit tests, Moq for mocking dependencies, and WinAppDriver for automated end-to-end UI tests.

Automation: All tests will be run automatically by the GitHub Actions pipeline on every commit.

## Security

**Data Protection:** The settings file will be saved in the user's private AppData folder, protected by OS file permissions.

**Dependency Security:** We will use the built-in NuGet Package Manager vulnerability scanner to check for known security issues in our third-party libraries.

## Checklist Results Report
I have validated this architecture against the standard Architect's Checklist. The design is robust, complete, and directly addresses the requirements of the PRD and UI/UX Specification. The architecture is approved and ready for development.

## Epic 4: Unreal Engine 5 PoC Integration

**Overview**
Epic 4 introduces a Proof of Concept for integrating Unreal Engine 5 visual effects with the existing C#/WPF application. This is implemented as a hybrid architecture where the C# application continues to handle capture and the UE5 application provides advanced visual effects rendering.

**Integration Architecture**
The PoC uses Inter-Process Communication (IPC) to bridge the C# capture application with a separate Unreal Engine 5 process:

```mermaid
graph TD
    subgraph "C# Application (Existing)"
        A[Screen Capture Service] --> B[Spout/NDI Sender]
        C[Audio Capture Service] --> D[IPC Audio Sender]
    end
    
    subgraph "IPC Layer"
        B --> E[Spout/NDI Stream]
        D --> F[Named Pipe/Shared Memory]
    end
    
    subgraph "Unreal Engine 5 (New)"
        E --> G[Media Texture Receiver]
        F --> H[Audio Data Receiver]
        G --> I[Niagara Particle System]
        H --> I
        I --> J[UE5 Viewport/Display]
    end
```

**Key Architectural Decisions for PoC:**

1. **Process Isolation**: UE5 runs as a separate process to maintain stability and allow independent development
2. **Video Transport**: Spout or NDI for low-latency video streaming between processes
3. **Audio Transport**: Named pipes or shared memory for audio intensity/frequency data
4. **Fallback Support**: Original C# renderer remains available if UE5 process fails
5. **Feature Toggle**: Configuration flag to enable/disable UE5 integration

**New Components for PoC:**

- **SpoutSender/NDISender**: C# component to stream captured frames via Spout/NDI
- **AudioIPCSender**: C# component to send audio analysis data to UE5
- **UE5EffectsHost**: Separate Unreal Engine 5 application for visual effects
- **MediaTextureReceiver**: UE5 component to receive video stream
- **NiagaraController**: UE5 component to drive particle effects based on input data

**Performance Considerations:**
- Target: <10% additional overhead vs native C# rendering
- IPC latency: Expected 5-10ms for video streaming
- Audio sync: Timestamp-based synchronization between processes

**PoC Scope Limitations:**
- No bidirectional communication (UE5 doesn't control C# app)
- Basic error handling only
- Single display output from UE5
- Limited to purchased marketplace Niagara effects

## Next Steps
The planning and design phase is now complete. This Architecture Document, along with the PRD and UI/UX Specification, provides a comprehensive blueprint for implementation. The project is ready to move into the development phase, starting with the stories outlined in Epic 1. Epic 4 represents an experimental branch that can be developed in parallel without affecting the core application stability.







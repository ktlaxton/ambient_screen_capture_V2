# Components

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

## Epic 4 PoC Additional Components

**7. Spout/NDI Sender (C#)**
Responsibility: Streams captured video frames from the Screen Capture Service to external applications via Spout or NDI protocol.
Key Interfaces: IVideoStreamSender
Dependencies: Spout SDK or NDI SDK, Screen Capture Service

**8. Audio IPC Sender (C#)**
Responsibility: Sends processed audio data (intensity, frequency analysis) to the UE5 process via named pipes or shared memory.
Key Interfaces: IAudioDataSender
Dependencies: Data Processor, Windows IPC APIs

**9. UE5 Effects Host (Unreal Engine 5)**
Responsibility: Separate Unreal Engine 5 application that receives video/audio streams and renders advanced visual effects.
Key Components:
- Media Texture Receiver: Ingests Spout/NDI video stream
- Audio Data Receiver: Receives audio analysis via IPC
- Niagara Controller: Drives particle effects based on input data
- Viewport Manager: Renders effects to display

**10. Process Manager (C#)**
Responsibility: Manages the lifecycle of the UE5 process, including launch, monitoring, and graceful shutdown.
Key Interfaces: IProcessManager
Features: Process health monitoring, automatic restart on crash, configuration-based enable/disable

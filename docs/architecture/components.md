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

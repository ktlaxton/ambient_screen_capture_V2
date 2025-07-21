# Epic 2: The Ambient Effects Engine

**Expanded Goal:** The objective of this epic is to implement the core logic that transforms the raw data from the capture services into a tangible visual experience. This involves processing the data, rendering the first visual effect, and establishing the framework for multiple, selectable effects. By the end of this epic, the application will be fully functional, delivering its primary value to the user.

* **Story 2.1: Real-time Data Processor**
    * **As a** system, **I want** to process the raw screen and audio data into a simple, usable format, **so that** the rendering engine can easily consume it to create effects.
    * **Acceptance Criteria:**
        1.  A data processing module is created that receives data from the Screen Capture Service (from Story 1.2).
        2.  The module analyzes the captured image and determines a single, dominant color in real-time.
        3.  The module receives data from the Audio Capture Service (from Story 1.3) and calculates a simple intensity value (e.g., a number from 0 to 100).
        4.  The resulting color and audio intensity values are made available to other parts of the application with minimal latency.
* **Story 2.2: 'Soft Glow' Effect Renderer**
    * **As a** user, **I want** to see a simple, ambient glow effect on my side monitors that reacts to my game, **so that** my gaming experience feels more immersive.
    * **Acceptance Criteria:**
        1.  The secondary monitors display a solid color.
        2.  The color displayed is the dominant color determined by the Data Processor (from Story 2.1).
        3.  The brightness/intensity of the glow is controlled by the audio intensity value from the Data Processor.
        4.  The On/Off switch (from Story 1.4) now correctly enables and disables this effect.
        5.  The effect is smooth and performs efficiently, as per the non-functional requirements.
* **Story 2.3: 'Generative Visualizer' Effect Style**
    * **As a** user, **I want** to choose a more complex, generative visual style, like a classic music visualizer, **so that** I can have a more dynamic and visually interesting ambient experience.
    * **Acceptance Criteria:**
        1.  A new 'Generative Visualizer' effect is created that displays evolving, abstract, or fractal-like patterns.
        2.  The speed and complexity of the pattern's movement are controlled by the audio intensity from the Data Processor.
        3.  The color palette of the patterns is determined by the dominant screen color from the Data Processor.
        4.  The effect style selector in the UI (from Story 1.4) is now functional and allows switching between 'Soft Glow' and 'Generative Visualizer'.
        5.  The selected style is properly rendered on the secondary monitors.
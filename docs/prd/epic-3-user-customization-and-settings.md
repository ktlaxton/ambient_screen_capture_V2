# Epic 3: User Customization & Settings

**Expanded Goal:** The objective of this epic is to fully implement the user-facing controls and settings, transforming the application from a functional engine into a polished, user-friendly utility. This includes activating all UI controls, adding the crucial monitor selection screen, and ensuring user preferences are saved between sessions.

* **Story 3.1: Activate Main Control Panel**
    * **As a** user, **I want** the controls on the main panel to be fully functional, **so that** I can control the ambient effects in real-time.
    * **Acceptance Criteria:**
        1.  The 'Soft Glow' and 'Generative Visualizer' styles can be selected from the style selector and the change is reflected instantly.
        2.  The audio sensitivity slider is now functional and correctly adjusts the responsiveness of the visual effects.
        3.  The application correctly saves and applies the last used settings (style and sensitivity) when it is started.
* **Story 3.2: Implement Monitor Selection**
    * **As a** user with multiple monitors, **I want** to choose which of my secondary monitors will display the effects, **so that** I have full control over my setup.
    * **Acceptance Criteria:**
        1.  A 'Monitor Setup' screen is created.
        2.  The screen displays a visual representation of all connected monitors (e.g., boxes labeled 1, 2, 3).
        3.  The user's primary monitor is clearly identified and cannot be selected.
        4.  Users can select/deselect any of their secondary monitors using checkboxes or similar controls.
        5.  The ambient effects are only rendered on the selected secondary monitors.
        6.  The monitor selection is saved and persists between application restarts.

## Checklist Results Report

I have run a validation check on this PRD against the standard Product Manager checklist. The document is comprehensive, well-structured, and all major requirements have been addressed.

* **Overall PRD Completeness:** 95%+
* **MVP Scope Appropriateness:** Just Right
* **Readiness for Architecture Phase:** Ready
* **Final Decision:** **READY FOR ARCHITECT**

## Next Steps

This PRD is now complete and ready to be handed off to the next specialists in the BMad-Method workflow.

**UX Expert Prompt**
> "Hello Sally, please review the attached PRD, specifically the 'User Interface Design Goals' section and the related user stories. Your task is to create a more detailed UI/UX Specification document based on these requirements."

**Architect Prompt**
> "Hello Winston, please review the attached PRD, paying close attention to the 'Requirements' and 'Technical Assumptions' sections. Your task is to create a comprehensive Technical Architecture document that will serve as the blueprint for development."
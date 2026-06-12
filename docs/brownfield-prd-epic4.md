> **⚠️ SUPERSEDED (2026-06-10):** This Epic 4 / UE5 PRD is obsolete. Unreal Engine is **not** part of the product. The authoritative specification is [`REBUILD_PRD_AND_ARCHITECTURE.md`](../REBUILD_PRD_AND_ARCHITECTURE.md). Do not implement anything described below.

# Ambient Effects Engine UE5 Integration Brownfield Enhancement PRD

## Intro Project Analysis and Context

### Existing Project Overview

#### Analysis Source
- IDE-based fresh analysis
- Existing architecture documentation available at: `docs/architecture.md` and related files
- Epic 4 documentation available at: `docs/prd/epic-4-real-time-reactive-visuals-poc.md`

#### Current Project State
The Ambient Effects Engine is a C#/WPF (.NET 8) Windows desktop application that captures screen content and audio from a primary monitor and renders ambient visual effects on secondary monitors. The application uses:
- Windows.Graphics.Capture for screen capture
- NAudio for audio capture
- MVVM architecture pattern
- Custom effects rendering engine with strategy pattern for different visual effects

### Available Documentation Analysis

#### Available Documentation
- [x] Tech Stack Documentation (in `docs/architecture/tech-stack.md`)
- [x] Source Tree/Architecture (in `docs/architecture.md` and related files)
- [x] Coding Standards (in `docs/architecture/coding-standards.md`)
- [x] API Documentation (REST API not applicable - desktop app)
- [x] External API Documentation (minimal external APIs)
- [ ] UX/UI Guidelines (basic UI goals in PRD)
- [x] Technical Debt Documentation (none documented yet - new project)
- [x] Epic 4 PoC Integration Guide (in `docs/architecture/poc-integration.md`)

### Enhancement Scope Definition

#### Enhancement Type
- [x] Integration with New Systems (Unreal Engine 5)
- [x] New Feature Addition (Advanced visual effects via Niagara)

#### Enhancement Description
This enhancement adds a Proof of Concept for integrating Unreal Engine 5 as an advanced visual effects renderer. The C# application will stream captured video/audio to a separate UE5 process via Spout/NDI, where Niagara particle effects will react to the content.

#### Impact Assessment
- [x] Moderate Impact (some existing code changes)
- The enhancement requires adding IPC components to the existing C# application but maintains the core architecture. The UE5 integration runs as a separate process, minimizing risk to the existing system.

### Goals and Background Context

#### Goals
- Validate the technical feasibility of using Unreal Engine 5 for advanced visual effects
- Establish a pipeline for streaming video/audio from C# to UE5 via Spout/NDI
- Demonstrate reactive Niagara particle effects based on captured content
- Maintain existing C# renderer as fallback option
- Achieve <10% performance overhead when UE5 effects are enabled

#### Background Context
The current C# application provides basic visual effects through its native rendering engine. However, for more advanced particle-based effects and GPU-accelerated visuals, Unreal Engine 5's Niagara system offers superior capabilities. This PoC explores a hybrid architecture where the C# app continues handling capture while UE5 provides advanced rendering, allowing us to leverage the strengths of both platforms without a complete rewrite.

### Change Log
| Change | Date | Version | Description | Author |
| --- | --- | --- | --- | --- |
| Initial Creation | 2025-07-30 | 1.0 | Brownfield PRD for Epic 4 UE5 Integration | BMad Master |

## Requirements

### Functional Requirements

- FR1: The C# application shall stream captured screen frames to Unreal Engine 5 via Spout or NDI protocol without disrupting existing capture functionality
- FR2: The C# application shall transmit processed audio data (intensity and frequency analysis) to UE5 via named pipes or shared memory
- FR3: The system shall support launching and managing the UE5 effects process lifecycle from the C# application
- FR4: The UE5 application shall receive video streams and render them as Media Textures in real-time
- FR5: The Niagara particle effects shall react to video content properties (color, brightness) and audio data in real-time
- FR6: The system shall provide a configuration toggle to enable/disable UE5 effects, falling back to native C# renderer when disabled
- FR7: The C# application shall monitor UE5 process health and attempt automatic restart (max 3 attempts) on crash
- FR8: The system shall maintain timestamp synchronization between video and audio streams across processes

### Non-Functional Requirements

- NFR1: The UE5 integration shall not exceed 10% additional performance overhead compared to native C# rendering
- NFR2: Inter-process communication latency shall not exceed 20ms for combined video and audio streaming
- NFR3: The system shall gracefully degrade to C# renderer if UE5 process fails to launch or crashes repeatedly
- NFR4: Memory usage increase from UE5 process shall not exceed 500MB under normal operation
- NFR5: The enhancement shall maintain compatibility with existing Windows 10 v1803+ requirement
- NFR6: All IPC mechanisms shall use Windows-native protocols (Spout/NDI for video, named pipes for audio)

### Compatibility Requirements

- CR1: Existing screen capture and audio capture services shall remain unchanged in their external interfaces
- CR2: Current settings persistence and ApplicationSettings model shall accommodate new UE5-related configuration without breaking changes
- CR3: The existing WPF UI shall maintain its current design patterns, with UE5 settings integrated into existing settings pages
- CR4: Native C# effects renderer shall continue to function identically when UE5 integration is disabled

## Technical Constraints and Integration Requirements

### Existing Technology Stack

**Languages**: C# 12.0 (.NET 8 SDK), C++ (for Unreal Engine 5)
**Frameworks**: WPF (.NET 8), Unreal Engine 5.4+
**Database**: Not applicable (settings in JSON file)
**Infrastructure**: Local Windows desktop application
**External Dependencies**: 
- Windows.Graphics.Capture (Windows 10 v1803+)
- NAudio 2.2.1
- System.Drawing.Common 8.0.0
- Spout SDK or NDI SDK (for IPC)
- Unreal Engine 5 Runtime

### Integration Approach

**Database Integration Strategy**: Not applicable - settings remain in local JSON file with new UE5 configuration fields added
**API Integration Strategy**: Inter-process communication via Spout/NDI for video streaming and named pipes for audio data. No REST APIs involved.
**Frontend Integration Strategy**: WPF settings UI extended with new UE5 enable/disable toggle and process status indicator. No changes to existing UI patterns.
**Testing Integration Strategy**: Existing xUnit tests extended with mocked IPC interfaces. New integration tests for C#-to-UE5 communication. Manual testing required for UE5 visual output.

### Code Organization and Standards

**File Structure Approach**: New IPC components in `Services/IPC/` directory. UE5 project in separate `UE5Effects/` subdirectory.
**Naming Conventions**: Follow existing C# conventions - interfaces prefixed with 'I', services suffixed with 'Service'
**Coding Standards**: Maintain existing .NET code style analyzers and Microsoft C# naming conventions
**Documentation Standards**: XML documentation for new public APIs, inline comments for IPC protocol details

### Deployment and Operations

**Build Process Integration**: MSBuild for C# app remains unchanged. UE5 project built separately and packaged with MSIX installer.
**Deployment Strategy**: UE5 executable bundled in `UE5Effects/` subdirectory of main application. Single MSIX installer for both.
**Monitoring and Logging**: Extend Serilog to log IPC events and UE5 process lifecycle. UE5 logs to separate file.
**Configuration Management**: New settings in existing ApplicationSettings.json - `EnableUE5Effects`, `UE5ExecutablePath`, `StreamProtocol`

### Risk Assessment and Mitigation

**Technical Risks**: 
- IPC protocol incompatibility between C# and UE5
- GPU resource contention between capture and UE5 rendering
- Spout/NDI SDK integration complexity

**Integration Risks**: 
- Process synchronization issues during startup/shutdown
- Memory leaks in IPC layer
- Anti-virus software blocking IPC communication

**Deployment Risks**: 
- Increased installer size (UE5 runtime ~200MB)
- Missing UE5 dependencies on target machines
- Graphics driver compatibility issues

**Mitigation Strategies**: 
- Start with Spout (simpler) before attempting NDI
- Implement comprehensive IPC error handling and fallback
- Include UE5 prerequisite checker in installer
- Provide diagnostic mode to test IPC independently

## Epic and Story Structure

### Epic Approach

**Epic Structure Decision**: Single comprehensive epic for the UE5 PoC integration

**Rationale**: This enhancement represents a cohesive proof of concept for adding UE5 visual effects to the existing application. All work is interconnected - the IPC pipeline, UE5 integration, and effects implementation form a single logical unit. Breaking this into multiple epics would create artificial boundaries and complicate the PoC validation. The stories within this epic will be sequenced to minimize risk, starting with basic IPC validation before moving to full integration.

## Epic 1: Real-Time Reactive Visuals PoC

**Epic Goal**: Validate the technical feasibility of integrating Unreal Engine 5 as an advanced visual effects renderer while maintaining the stability and functionality of the existing C# capture application

**Integration Requirements**: 
- Maintain all existing C# application functionality unchanged
- Add IPC components following existing service patterns
- Ensure graceful fallback to native renderer on any failure
- Validate performance remains within 10% overhead target
- Package UE5 runtime with existing MSIX installer

### Story 1.1: IPC Infrastructure and Basic Validation

As a developer,
I want to implement and validate the IPC infrastructure between C# and a test process,
so that I can ensure reliable inter-process communication before integrating UE5.

#### Acceptance Criteria
1: Spout sender service implemented in C# following existing service patterns
2: Named pipe service implemented for audio data transmission
3: Test executable successfully receives video frames via Spout
4: Test executable successfully receives audio data via named pipes
5: IPC error handling and logging implemented via Serilog
6: Unit tests cover IPC service interfaces with mocked implementations

#### Integration Verification
- IV1: Existing screen capture service continues functioning normally with Spout sender added
- IV2: No memory leaks detected during 30-minute continuous streaming test
- IV3: CPU usage increase less than 5% with IPC active

### Story 1.2: UE5 Project Setup and Media Texture Reception

As a developer,
I want to create the UE5 project structure and implement video stream reception,
so that captured frames can be displayed as Media Textures in Unreal Engine.

#### Acceptance Criteria
1: UE5 project created in `UE5Effects/` subdirectory
2: Spout receiver plugin integrated and functioning
3: Media Texture updates in real-time from received video stream
4: Command-line argument parsing implemented for configuration
5: Basic UI showing connection status and frame rate
6: Project builds to standalone executable

#### Integration Verification
- IV1: UE5 executable launches successfully from C# application
- IV2: Video stream latency remains under 20ms
- IV3: No GPU resource conflicts with C# capture process

### Story 1.3: Process Management and Lifecycle Control

As a developer,
I want to implement robust process management for the UE5 application,
so that it integrates seamlessly with the C# application lifecycle.

#### Acceptance Criteria
1: Process manager service implemented following existing patterns
2: UE5 process launches on-demand when effects enabled
3: Health monitoring via heartbeat over named pipe
4: Automatic restart (max 3 attempts) on UE5 crash
5: Graceful shutdown when C# application exits
6: Configuration settings integrated into ApplicationSettings model

#### Integration Verification
- IV1: Existing settings persistence works with new UE5 configuration fields
- IV2: C# renderer remains available during UE5 launch/restart
- IV3: No orphaned UE5 processes after C# app shutdown

### Story 1.4: Niagara Effects Integration and Audio Reactivity

As a user,
I want to see Niagara particle effects that react to my screen content and audio,
so that I can experience advanced visual effects based on my primary monitor activity.

#### Acceptance Criteria
1: Marketplace Niagara effect asset integrated into UE5 project
2: Audio data from named pipe drives Niagara parameters
3: Media Texture color/brightness influences particle properties
4: Effects render at 60+ FPS on typical gaming hardware
5: Visual output displays on designated secondary monitor
6: Settings UI includes effect enable/disable toggle

#### Integration Verification
- IV1: Native C# effects continue working when UE5 effects disabled
- IV2: Total system performance overhead under 10%
- IV3: Audio-visual synchronization maintained within 50ms

### Story 1.5: Testing, Documentation, and PoC Completion

As a product owner,
I want comprehensive testing and documentation of the PoC,
so that we can make an informed decision about full implementation.

#### Acceptance Criteria
1: Integration tests validate entire C# to UE5 pipeline
2: Performance benchmarks documented comparing native vs UE5 rendering
3: Setup guide created for developers
4: Known limitations and issues documented
5: PoC findings report with go/no-go recommendation
6: Demo video showing working effects

#### Integration Verification
- IV1: All existing C# unit tests still pass
- IV2: Manual test confirms graceful degradation on UE5 failure
- IV3: Fresh setup using documentation succeeds

---

*This brownfield PRD captures the requirements and implementation plan for Epic 4's Unreal Engine 5 integration PoC. The enhancement maintains the existing C# application's stability while exploring advanced visual effects capabilities through a hybrid architecture.*
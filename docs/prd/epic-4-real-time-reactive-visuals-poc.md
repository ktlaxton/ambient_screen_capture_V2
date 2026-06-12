# Epic: Real-Time Reactive Visuals PoC - Brownfield Enhancement

## Epic Goal

Validate the technical feasibility of integrating Unreal Engine 5 as an advanced visual effects renderer while maintaining the stability and functionality of the existing C# capture application.

## Epic Description

### Existing System Context

The Ambient Effects Engine is a C#/WPF (.NET 8) Windows desktop application that captures screen content and audio from a primary monitor and renders ambient visual effects on secondary monitors. The application uses Windows.Graphics.Capture for screen capture, NAudio for audio capture, and a custom effects rendering engine.

### Enhancement Details

This epic will add a Proof of Concept for integrating Unreal Engine 5 as an advanced visual effects renderer. The C# application will stream captured video/audio to a separate UE5 process via Spout/NDI, where Niagara particle effects will react to the content. This hybrid architecture allows the C# app to continue handling capture while UE5 provides advanced rendering capabilities.

### Success Criteria

The PoC will be considered successful when:
- Live video from the main monitor is streamed from C# to UE5 and rendered as a Media Texture
- Niagara particle effects visibly react to video content properties (color, brightness) and audio data
- Performance overhead remains under 10% compared to native C# rendering
- The existing C# renderer remains available as a fallback option

## Integration Requirements

- Maintain all existing C# application functionality unchanged
- Add IPC components following existing service patterns
- Ensure graceful fallback to native renderer on any failure
- Validate performance remains within 10% overhead target
- Package UE5 runtime with existing MSIX installer

## Stories

### Story 1.1: IPC Infrastructure and Basic Validation
Set up inter-process communication infrastructure between C# and a test process. Implement Spout sender service and named pipe service for audio data transmission. Validate reliable communication before integrating UE5.

### Story 1.2: UE5 Project Setup and Media Texture Reception
Create the UE5 project structure and implement video stream reception. Set up Spout receiver plugin and ensure Media Texture updates in real-time from received video stream.

### Story 1.3: Process Management and Lifecycle Control
Implement robust process management for the UE5 application. Include health monitoring, automatic restart on crash, and graceful shutdown integration with C# application lifecycle.

### Story 1.4: Niagara Effects Integration and Audio Reactivity
Integrate marketplace Niagara effect asset and connect audio data to drive particle parameters. Ensure effects react to both video content and audio analysis in real-time.

### Story 1.5: Testing, Documentation, and PoC Completion
Validate entire pipeline with integration tests, document performance benchmarks, create setup guide, and prepare PoC findings report with go/no-go recommendation.

## Compatibility Requirements

- CR1: Existing screen capture and audio capture services shall remain unchanged in their external interfaces
- CR2: Current settings persistence and ApplicationSettings model shall accommodate new UE5-related configuration without breaking changes
- CR3: The existing WPF UI shall maintain its current design patterns, with UE5 settings integrated into existing settings pages
- CR4: Native C# effects renderer shall continue to function identically when UE5 integration is disabled

## Technical Requirements

- NFR1: The UE5 integration shall not exceed 10% additional performance overhead
- NFR2: Inter-process communication latency shall not exceed 20ms
- NFR3: The system shall gracefully degrade to C# renderer if UE5 process fails
- NFR4: Memory usage increase from UE5 process shall not exceed 500MB
- NFR5: Maintain compatibility with existing Windows 10 v1803+ requirement
- NFR6: Use Windows-native protocols (Spout/NDI for video, named pipes for audio)

## Risk Mitigation

### Technical Risks
- **IPC protocol incompatibility:** Start with Spout (simpler) before attempting NDI
- **GPU resource contention:** Implement GPU usage monitoring and throttling
- **Spout/NDI SDK complexity:** Create abstraction layer for easier swapping

### Integration Risks
- **Process synchronization issues:** Implement comprehensive IPC error handling
- **Memory leaks in IPC layer:** Add memory monitoring and periodic cleanup
- **Anti-virus blocking IPC:** Document firewall/AV exceptions needed

### Deployment Risks
- **Increased installer size:** UE5 runtime adds ~200MB to package
- **Missing UE5 dependencies:** Include prerequisite checker in installer
- **Graphics driver compatibility:** Provide diagnostic mode for testing

### Rollback Plan
The PoC runs as a separate process with feature toggle. Can be disabled via configuration without affecting core C# application functionality.

## Definition of Done

- [ ] All stories are completed and meet their acceptance criteria.
- [ ] The existing screen capture functionality is verified to be unaffected.
- [ ] The integration points (capture app to Unreal, Media Texture to Niagara) are working correctly.
- [ ] The PoC is documented with setup instructions and findings.
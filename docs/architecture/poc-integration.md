# Epic 4: Unreal Engine 5 PoC Integration Guide

## Overview

This document provides detailed technical guidance for implementing Epic 4's Proof of Concept, which integrates Unreal Engine 5 visual effects with the existing C#/WPF Ambient Effects Engine application.

## Architecture Summary

The PoC implements a hybrid architecture where:
- The existing C# application continues to handle screen and audio capture
- A separate Unreal Engine 5 process provides advanced visual effects rendering
- Inter-Process Communication (IPC) bridges the two applications

## Technical Integration Approach

### 1. Video Stream Pipeline

**Option A: Spout (Recommended for PoC)**
- Pros: Simple API, low latency (~5ms), GPU-accelerated
- Cons: Windows-only, requires both apps on same GPU
- Implementation: DirectX texture sharing

**Option B: NDI**
- Pros: Network-capable, more robust, industry standard
- Cons: Higher complexity, slightly higher latency (~10-15ms)
- Implementation: Network protocol over localhost

### 2. Audio Data Pipeline

**Approach: Named Pipes**
- Small payload (audio intensity, frequency bands)
- Low latency requirement
- Simple implementation in both C# and UE5

**Data Format:**
```json
{
  "timestamp": 1234567890,
  "intensity": 0.75,
  "frequencies": [0.1, 0.3, 0.5, 0.7, 0.2],
  "dominantFreq": 440.0
}
```

### 3. Process Management

**Launch Strategy:**
1. C# app checks for UE5 effects enabled in settings
2. Launches UE5 process with command-line arguments
3. Waits for IPC handshake
4. Falls back to native renderer if launch fails

**Monitoring:**
- Heartbeat via named pipe every 1 second
- Automatic restart on crash (max 3 attempts)
- Graceful shutdown on app exit

## Implementation Steps

### Phase 1: C# Application Modifications

1. **Add Spout/NDI Sender Service**
   ```csharp
   public interface IVideoStreamSender
   {
       void Initialize(string streamName);
       void SendFrame(Bitmap frame);
       void Shutdown();
   }
   ```

2. **Add Audio IPC Service**
   ```csharp
   public interface IAudioDataSender
   {
       void Connect(string pipeName);
       void SendAudioData(AudioAnalysis data);
       void Disconnect();
   }
   ```

3. **Modify Screen Capture Service**
   - Add configuration flag for stream output
   - Fork captured frames to both native renderer and stream sender

4. **Add Process Manager**
   - Handle UE5 process lifecycle
   - Monitor process health
   - Manage graceful shutdown

### Phase 2: Unreal Engine 5 Application

1. **Project Setup**
   - Create new UE5 project "AmbientEffectsUE5"
   - Configure for packaged deployment
   - Set up command-line argument parsing

2. **Media Texture Receiver**
   - Implement Spout/NDI receiver plugin
   - Create dynamic Media Texture
   - Update texture each frame

3. **Audio Data Receiver**
   - Create named pipe client
   - Parse JSON audio data
   - Update Niagara parameters

4. **Niagara Integration**
   - Import marketplace Niagara effect
   - Bind parameters to audio data
   - Apply media texture as particle source

## Configuration

### C# Application Settings
```json
{
  "EnableUE5Effects": false,
  "UE5ExecutablePath": "./UE5Effects/AmbientEffectsUE5.exe",
  "StreamProtocol": "Spout",
  "StreamName": "AmbientEffectsStream",
  "AudioPipeName": "AmbientEffectsAudio",
  "UE5ProcessTimeout": 5000
}
```

### UE5 Command Line Arguments
```
AmbientEffectsUE5.exe -stream=Spout -streamname=AmbientEffectsStream -audiopipe=AmbientEffectsAudio -windowed -ResX=1920 -ResY=1080
```

## Performance Considerations

### Target Metrics
- Additional CPU overhead: <5%
- Additional GPU overhead: <10%
- Total added latency: <20ms
- Memory overhead: ~500MB (UE5 process)

### Optimization Strategies
1. Use GPU-accelerated texture sharing (Spout)
2. Minimize audio data payload size
3. Run UE5 at native display resolution
4. Disable unnecessary UE5 features

## Testing Strategy

### Integration Tests
1. **Happy Path**: Both processes start and communicate
2. **UE5 Crash**: C# app continues with fallback renderer
3. **Communication Loss**: Graceful degradation
4. **Performance**: Measure overhead vs baseline

### Manual Test Scenarios
1. Start with UE5 disabled, enable at runtime
2. Force UE5 crash, verify auto-restart
3. Test different Niagara effects
4. Verify audio-visual synchronization

## Rollback Plan

Since this is a PoC with process isolation:

1. **Disable via Configuration**: Set `EnableUE5Effects` to false
2. **Remove UE5 Files**: Delete UE5Effects subfolder
3. **Code Removal**: Features are behind interface abstractions
4. **No Database Changes**: No persistent data modifications

## Known Limitations

1. **Single Display Output**: PoC only outputs to one monitor from UE5
2. **Fixed Effects**: Limited to purchased marketplace assets
3. **Windows Only**: Spout/NDI are Windows-specific
4. **No Bidirectional Control**: UE5 cannot control C# app settings

## Future Considerations

If PoC is successful, consider:
1. Custom Niagara effects development
2. Multiple monitor support from UE5
3. Bidirectional communication for unified settings
4. Performance optimizations (shared memory for video)
5. Plugin architecture for multiple effects engines

## Debugging Tips

1. **Enable Verbose Logging**: Both C# and UE5 should log IPC events
2. **Use Spout Demo Tools**: Test video stream independently
3. **Monitor Process**: Use Task Manager to verify UE5 launch
4. **Network Monitoring**: If using NDI, check with network tools
5. **GPU-Z**: Monitor GPU usage for both processes

## Success Criteria Validation

The PoC will be considered successful when:
- [ ] Live video from main monitor appears in UE5
- [ ] Niagara effects react to video content (color/brightness)
- [ ] Audio data drives particle parameters
- [ ] Performance overhead is <10%
- [ ] Fallback to C# renderer works on UE5 failure
- [ ] Setup documentation allows reproduction

---

*This document provides the technical blueprint for Epic 4's PoC implementation. It should be updated with findings and decisions made during development.*
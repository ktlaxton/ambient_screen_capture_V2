# Audio Capture Test Application

A console application for manually testing and verifying the AudioCaptureService functionality.

## Purpose

This application provides real-time verification that the audio capture service is working correctly with actual audio hardware. It's particularly useful for:

- Testing audio capture on different systems
- Verifying volume level calculations
- Performance testing and monitoring
- Debugging audio capture issues

## How to Run

```bash
cd AudioCaptureTestApp
dotnet run
```

## Features

### Real-time Audio Monitoring
- Displays first 5 audio events in detail
- Shows volume level, data size, sample rate, and timestamps
- Tracks volume spikes and significant audio activity

### Interactive Controls
- **'s'** - Show detailed statistics
- **'q'** - Quit the application

### Performance Monitoring
- Events per second calculation
- Min/max volume detection
- Runtime statistics
- Memory and CPU usage awareness

### Comprehensive Logging
The application shows detailed debug information about:
- Audio capture initialization
- Sample rates and buffer sizes
- Volume level calculations
- Event frequency and timing

## Expected Output

When working correctly, you should see:
```
=== Audio Capture Test Application ===
Starting audio capture...
Audio capture started. IsCapturing: True

Audio Event #1:
  Volume Level: 0.0234
  Data Size: 1764 bytes
  Sample Rate: 44100 Hz
  Timestamp: 10:30:15.123

Status: 150 audio events received. Max volume: 0.456, Min volume: 0.001
```

## Troubleshooting

If no audio events are received:
- Ensure audio is playing on the system
- Check that audio devices are available
- Verify Windows audio service is running
- Try running as Administrator if needed

The application gracefully handles environments without audio devices and provides clear error messages.

## Integration with Main Application

This test app uses the same AudioCaptureService implementation as the main AmbientEffectsEngine, ensuring that successful testing here indicates the service will work in the production application.
using System;
using System.Drawing;

namespace AmbientEffectsEngine.Models
{
    public class ProcessedData
    {
        public Color DominantColor { get; set; }
        public float AudioIntensity { get; set; } // 0.0-1.0 normalized range
        public DateTime Timestamp { get; set; }
        
        /// <summary>
        /// Audio intensity as a percentage (0-100 scale) for UI display purposes
        /// </summary>
        public float AudioIntensityPercent => AudioIntensity * 100.0f;

        public ProcessedData(Color dominantColor, float audioIntensity, DateTime timestamp)
        {
            DominantColor = dominantColor;
            AudioIntensity = Math.Clamp(audioIntensity, 0.0f, 1.0f);
            Timestamp = timestamp;
        }

        public ProcessedData() : this(Color.Black, 0.0f, DateTime.UtcNow)
        {
        }
    }
}
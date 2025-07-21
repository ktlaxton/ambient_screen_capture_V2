using System.Collections.Generic;

namespace AmbientEffectsEngine.Models
{
    public class ApplicationSettings
    {
        public bool IsEnabled { get; set; } = false;
        public string SelectedEffectId { get; set; } = string.Empty;
        public float AudioSensitivity { get; set; } = 0.5f;
        public string SourceMonitorId { get; set; } = string.Empty;
        public List<string> TargetMonitorIds { get; set; } = new List<string>();
    }
}
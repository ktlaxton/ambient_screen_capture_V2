using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AmbientEffectsEngine.Models;

namespace AmbientEffectsEngine.Services.Rendering.Effects
{
    public class SoftGlowEffect : IEffect
    {
        private readonly List<Form> _effectForms = new List<Form>();
        private bool _disposed = false;

        public string EffectId => "softglow";
        public string Name => "Soft Glow";
        public string Description => "Solid color ambient glow that responds to screen colors and audio intensity";

        public void Initialize(IEnumerable<DisplayMonitor> targetMonitors)
        {
            CleanupForms();

            foreach (var monitor in targetMonitors.Where(m => !m.IsPrimary))
            {
                var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == monitor.Id);
                if (screen != null)
                {
                    var form = CreateEffectForm(screen);
                    _effectForms.Add(form);
                }
            }
        }

        public void UpdateEffect(ProcessedData data)
        {
            if (_disposed || data == null) return;

            var color = data.DominantColor;
            var intensity = Math.Clamp(data.AudioIntensity, 0.0f, 1.0f);
            
            // Debug output
            System.Diagnostics.Debug.WriteLine($"SoftGlow Update: Color=({color.R},{color.G},{color.B}), Intensity={intensity:F2}");
            
            // Apply intensity to the color brightness, but ensure minimum visibility
            var minIntensity = 0.3f; // Always show at least 30% of the color
            var effectiveIntensity = Math.Max(intensity, minIntensity);
            
            var adjustedColor = Color.FromArgb(
                (int)(color.R * effectiveIntensity),
                (int)(color.G * effectiveIntensity),
                (int)(color.B * effectiveIntensity)
            );

            foreach (var form in _effectForms)
            {
                if (form != null && !form.IsDisposed)
                {
                    // Check if form handle is created before invoking
                    if (form.IsHandleCreated)
                    {
                        form.Invoke(new Action(() =>
                        {
                            form.BackColor = adjustedColor;
                        }));
                    }
                    else
                    {
                        // Directly set color if handle not created (safer for testing)
                        form.BackColor = adjustedColor;
                    }
                }
            }
        }

        public void Show()
        {
            foreach (var form in _effectForms.Where(f => f != null && !f.IsDisposed))
            {
                form.Show();
                form.WindowState = FormWindowState.Maximized;
            }
        }

        public void Hide()
        {
            foreach (var form in _effectForms.Where(f => f != null && !f.IsDisposed))
            {
                form.Hide();
            }
        }

        private Form CreateEffectForm(Screen screen)
        {
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                WindowState = FormWindowState.Maximized,
                TopMost = true,
                BackColor = Color.Black,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual
            };

            // Position form on the target screen
            form.Location = screen.Bounds.Location;
            form.Size = screen.Bounds.Size;

            return form;
        }

        private void CleanupForms()
        {
            foreach (var form in _effectForms.Where(f => f != null && !f.IsDisposed))
            {
                form.Close();
                form.Dispose();
            }
            _effectForms.Clear();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                CleanupForms();
                _disposed = true;
            }
        }
    }
}
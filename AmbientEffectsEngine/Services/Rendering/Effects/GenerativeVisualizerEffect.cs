using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using AmbientEffectsEngine.Models;

namespace AmbientEffectsEngine.Services.Rendering.Effects
{
    public class GenerativeVisualizerEffect : IEffect
    {
        private readonly List<VisualizerForm> _effectForms = new List<VisualizerForm>();
        private readonly List<Particle> _particles = new List<Particle>();
        private readonly object _particleLock = new object();
        private readonly Random _random = new Random();
        private readonly System.Windows.Forms.Timer _animationTimer = new System.Windows.Forms.Timer();
        private bool _disposed = false;

        private Color _currentDominantColor = Color.Blue;
        private float _currentAudioIntensity = 0.0f;
        private long _frameCount = 0;

        public string EffectId => "generativevisualizer";
        public string Name => "Generative Visualizer";
        public string Description => "Dynamic particle-based visualization that responds to audio intensity and screen colors";

        public GenerativeVisualizerEffect()
        {
            _animationTimer.Interval = 16; // ~60 FPS (1000ms/60 ≈ 16ms)
            _animationTimer.Tick += OnAnimationTick;
        }

        public void Initialize(IEnumerable<DisplayMonitor> targetMonitors)
        {
            CleanupForms();
            InitializeParticles();

            if (targetMonitors == null) return;

            foreach (var monitor in targetMonitors.Where(m => !m.IsPrimary))
            {
                var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == monitor.Id);
                if (screen != null)
                {
                    var form = CreateVisualizerForm(screen);
                    _effectForms.Add(form);
                }
            }

            _animationTimer.Start();
        }

        public void UpdateEffect(ProcessedData data)
        {
            if (_disposed || data == null) return;

            _currentDominantColor = data.DominantColor;
            _currentAudioIntensity = Math.Clamp(data.AudioIntensity, 0.0f, 1.0f);
            
            // Debug output
            System.Diagnostics.Debug.WriteLine($"GenerativeVisualizer Update: Color=({data.DominantColor.R},{data.DominantColor.G},{data.DominantColor.B}), Intensity={_currentAudioIntensity:F2}");
            
            // Update particle behavior based on audio intensity
            UpdateParticles();
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

        private void InitializeParticles()
        {
            lock (_particleLock)
            {
                _particles.Clear();
                
                // Use actual screen bounds instead of hardcoded values
                var screenBounds = _effectForms.FirstOrDefault()?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                
                // Create initial particle set
                for (int i = 0; i < 100; i++)
                {
                    _particles.Add(new Particle
                    {
                        X = _random.Next(0, screenBounds.Width),
                        Y = _random.Next(0, screenBounds.Height),
                        VelocityX = (float)(_random.NextDouble() * 4 - 2),
                        VelocityY = (float)(_random.NextDouble() * 4 - 2),
                        Size = (float)(_random.NextDouble() * 8 + 2),
                        Life = 1.0f,
                        MaxLife = (float)(_random.NextDouble() * 5 + 2)
                    });
                }
            }
        }

        private void UpdateParticles()
        {
            lock (_particleLock)
            {
                var speedMultiplier = 1.0f + (_currentAudioIntensity * 3.0f); // Speed increases with audio
                var spawnRate = (int)(_currentAudioIntensity * 10); // Spawn more particles with audio

                // Update existing particles
                for (int i = _particles.Count - 1; i >= 0; i--)
                {
                    var particle = _particles[i];
                    
                    // Update position with speed multiplier
                    particle.X += particle.VelocityX * speedMultiplier;
                    particle.Y += particle.VelocityY * speedMultiplier;
                    
                    // Update life (slower decay for longer visibility)
                    particle.Life -= 0.005f; // Slower decay than 60 FPS rate
                    
                    // Remove dead particles
                    if (particle.Life <= 0)
                    {
                        _particles.RemoveAt(i);
                    }
                }

                // Spawn new particles based on audio intensity
                var screenBounds = _effectForms.FirstOrDefault()?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                for (int i = 0; i < spawnRate; i++)
                {
                    if (_particles.Count < 500) // Limit total particles for performance
                    {
                        _particles.Add(new Particle
                        {
                            X = _random.Next(0, screenBounds.Width),
                            Y = _random.Next(0, screenBounds.Height),
                            VelocityX = (float)(_random.NextDouble() * 6 - 3),
                            VelocityY = (float)(_random.NextDouble() * 6 - 3),
                            Size = (float)(_random.NextDouble() * 12 + 3),
                            Life = 1.0f,
                            MaxLife = (float)(_random.NextDouble() * 3 + 1)
                        });
                    }
                }
            }
        }

        private void OnAnimationTick(object sender, EventArgs e)
        {
            _frameCount++;
            
            foreach (var form in _effectForms.Where(f => f != null && !f.IsDisposed))
            {
                if (form.IsHandleCreated)
                {
                    try
                    {
                        form.Invoke(new Action(() => form.Invalidate()));
                    }
                    catch
                    {
                        // Ignore invoke errors during shutdown
                    }
                }
            }
        }

        private VisualizerForm CreateVisualizerForm(Screen screen)
        {
            var form = new VisualizerForm(this)
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
            _animationTimer.Stop();
            
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
                _animationTimer?.Dispose();
                _disposed = true;
            }
        }

        // Internal methods for the custom form to access
        internal void RenderParticles(Graphics graphics, Rectangle bounds)
        {
            if (_disposed) return;

            // Create a safe copy of particles to avoid collection modification issues
            List<Particle> particlesCopy;
            lock (_particleLock)
            {
                particlesCopy = new List<Particle>(_particles);
            }

            using (var brush = new SolidBrush(_currentDominantColor))
            {
                foreach (var particle in particlesCopy)
                {
                    // Ensure particle is within bounds
                    if (particle.X >= 0 && particle.X < bounds.Width && 
                        particle.Y >= 0 && particle.Y < bounds.Height)
                    {
                        // Ensure minimum visibility with better alpha calculation
                        var normalizedLife = Math.Clamp(particle.Life / particle.MaxLife, 0.0f, 1.0f);
                        var alpha = (int)(Math.Max(normalizedLife, 0.3f) * 255); // Minimum 30% alpha
                        var color = Color.FromArgb(alpha, _currentDominantColor.R, _currentDominantColor.G, _currentDominantColor.B);
                        
                        using (var particleBrush = new SolidBrush(color))
                        {
                            var size = particle.Size * (particle.Life / particle.MaxLife);
                            graphics.FillEllipse(particleBrush, particle.X - size/2, particle.Y - size/2, size, size);
                        }
                    }
                }
            }

            // Add some connecting lines for extra visual complexity
            if (_currentAudioIntensity > 0.3f)
            {
                using (var pen = new Pen(Color.FromArgb(50, _currentDominantColor), 1))
                {
                    var nearbyParticles = particlesCopy.Where(p => p.Life > 0.5f).Take(50).ToList();
                    for (int i = 0; i < nearbyParticles.Count - 1; i++)
                    {
                        for (int j = i + 1; j < Math.Min(i + 5, nearbyParticles.Count); j++)
                        {
                            var p1 = nearbyParticles[i];
                            var p2 = nearbyParticles[j];
                            var distance = Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2));
                            
                            if (distance < 100)
                            {
                                graphics.DrawLine(pen, p1.X, p1.Y, p2.X, p2.Y);
                            }
                        }
                    }
                }
            }
        }
    }

    // Custom form for rendering the visualizer
    internal class VisualizerForm : Form
    {
        private readonly GenerativeVisualizerEffect _effect;

        public VisualizerForm(GenerativeVisualizerEffect effect)
        {
            _effect = effect;
            SetStyle(ControlStyles.AllPaintingInWmPaint | 
                     ControlStyles.UserPaint | 
                     ControlStyles.DoubleBuffer | 
                     ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Color.Black);
            _effect.RenderParticles(e.Graphics, ClientRectangle);
        }
    }

    // Particle data structure
    internal class Particle
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float VelocityX { get; set; }
        public float VelocityY { get; set; }
        public float Size { get; set; }
        public float Life { get; set; }
        public float MaxLife { get; set; }
    }
}
using System.Threading.Tasks;
using AmbientEffectsEngine.Models;

namespace AmbientEffectsEngine.Services
{
    public interface ISettingsService
    {
        Task<ApplicationSettings> LoadAsync();
        Task SaveAsync(ApplicationSettings settings);
        ApplicationSettings GetDefaults();
    }
}
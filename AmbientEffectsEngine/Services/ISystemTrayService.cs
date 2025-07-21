namespace AmbientEffectsEngine.Services;

public interface ISystemTrayService
{
    void Initialize();
    void ShowMainWindow();
    void HideMainWindow();
    void Shutdown();
}
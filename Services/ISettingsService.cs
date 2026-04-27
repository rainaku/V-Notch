using VNotch.Models;

namespace VNotch.Services;




public interface ISettingsService
{
    
    
    
    NotchSettings Load();

    
    
    
    void Save(NotchSettings settings);
}

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Blackglass.AssetStudio.App.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>An empty name tells WPF that every property changed.</summary>
    protected void RaiseAll() => Raise(string.Empty);
}

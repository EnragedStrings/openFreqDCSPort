using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.ViewModels;
using OpenFreqClient.Views;

namespace OpenFreqClient;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = Program.ServiceProvider?.GetService<MainWindowViewModel>()
                                ?? throw new InvalidOperationException("Service provider not initialized");
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel,
            };
            
            desktop.Exit += (s, e) =>
            {
                mainViewModel.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
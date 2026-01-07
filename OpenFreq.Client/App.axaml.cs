using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FalconBmsDataService.Services;
using FalconRadioService.Services;
using Microsoft.Extensions.DependencyInjection;
using OpenFreq.Client.Services.Interfaces;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.ViewModels;
using OpenFreqClient.Views;

namespace OpenFreqClient;

public partial class App : Application
{
    private List<ILifecycleService>? _services;
    
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            
            // Get services from DI
            var serviceProvider = Program.ServiceProvider;
            
            _services = new List<ILifecycleService>
            {
                serviceProvider.GetRequiredService<IFalconRadioSharedMemoryService>(),
                serviceProvider.GetRequiredService<IFalconSharedMemoryService>(),
                serviceProvider.GetRequiredService<IHotkeyService>(),
            };
            
            // Start services
            foreach (var service in _services)
            {
                service.Start();
            }
            
            var mainViewModel = Program.ServiceProvider?.GetService<MainWindowViewModel>()
                                ?? throw new InvalidOperationException("Service provider not initialized");
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel,
            };
            
            desktop.Exit += (s, e) =>
            {
                foreach (var service in _services)
                {
                    service.Stop();
                }
                
                mainViewModel.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
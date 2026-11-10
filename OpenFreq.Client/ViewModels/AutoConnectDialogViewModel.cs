using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DialogHostAvalonia;

namespace OpenFreqClient.ViewModels;

/// <summary>DCS auto-connect prompts (consent, server switch, password): a message, an optional
/// password box, an optional checkbox and up to three buttons. The dialog closes with the
/// clicked button's result string.</summary>
public sealed partial class AutoConnectDialogViewModel : ObservableObject
{
    public AutoConnectDialogViewModel(string title, string message, string primaryText, string primaryResult,
        string? secondaryText = null, string? secondaryResult = null, string cancelText = "Cancel",
        string cancelResult = "cancel", string? checkboxText = null, bool showPassword = false)
    {
        Title = title;
        Message = message;
        PrimaryText = primaryText;
        SecondaryText = secondaryText ?? "";
        CancelText = cancelText;
        CheckboxText = checkboxText ?? "";
        ShowSecondary = secondaryText != null;
        ShowCheckbox = checkboxText != null;
        ShowPassword = showPassword;
        PrimaryCommand = new RelayCommand(() => DialogHost.Close("MainDialogHost", primaryResult));
        SecondaryCommand = new RelayCommand(() => DialogHost.Close("MainDialogHost", secondaryResult));
        CancelCommand = new RelayCommand(() => DialogHost.Close("MainDialogHost", cancelResult));
    }

    public string Title { get; }
    public string Message { get; }
    public string PrimaryText { get; }
    public string SecondaryText { get; }
    public string CancelText { get; }
    public string CheckboxText { get; }
    public bool ShowSecondary { get; }
    public bool ShowCheckbox { get; }
    public bool ShowPassword { get; }

    [ObservableProperty] public partial bool IsChecked { get; set; }
    [ObservableProperty] public partial string Password { get; set; } = "";

    public ICommand PrimaryCommand { get; }
    public ICommand SecondaryCommand { get; }
    public ICommand CancelCommand { get; }
}

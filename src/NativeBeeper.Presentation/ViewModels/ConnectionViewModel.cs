using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NativeBeeper.Core.Backend;

namespace NativeBeeper.Presentation.ViewModels;

public sealed partial class ConnectionViewModel(IBeeperBackend backend) : ObservableObject
{
    [ObservableProperty] private string status = "Connect to Google Messages.";
    [ObservableProperty] private string backendDescription = "Google Messages · Direct connection";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isConnected;
    public ObservableCollection<ChatAccount> Accounts { get; } = [];

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        IsConnected = false;
        Accounts.Clear();
        try
        {
            Status = "Connecting…";
            var info = await backend.DiscoverAsync(cancellationToken);
            BackendDescription = $"{info.Name} {info.Version}";
            var accounts = await backend.GetAccountsAsync(cancellationToken);
            foreach (var account in accounts) Accounts.Add(account);
            IsConnected = true;
            Status = accounts.Count switch
            {
                0 => "Connected. No accounts are set up yet.",
                1 => "Connected. 1 account signed in.",
                var many => $"Connected. {many} accounts signed in."
            };
        }
        catch (BackendException ex) { Status = ex.Message; }
        catch (OperationCanceledException) { Status = "Connection cancelled."; }
        catch (Exception) { Status = "Messages couldn't read the saved pairing. Pair again in Settings → Connections."; }
        finally { IsBusy = false; }
    }

    // What the backend now says about a connection it stopped without anyone pressing Connect (a sign-in Google refused, a
    // pairing gone), so the status line does not keep an older "Connected" or "renewing". The accounts stay listed: the
    // account is still this one, and clearing them dropped the chat list's filter for it. Ignored while Connect runs,
    // which writes its own answer.
    public void ReportStopped(string status)
    {
        if (IsBusy) return;
        IsConnected = false;
        Status = status;
    }
}



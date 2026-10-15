using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VSL.Claude;

namespace VSL.ViewModels;

/// <summary>
/// Several Claude Code accounts (subscriptions), each a configuration folder of its own (~/.claude, ~/.claude-NAME), as
/// with <c>CLAUDE_CONFIG_DIR=~/.claude-work claude</c> in a terminal: pick one, or add one and sign in to it.
/// </summary>
public sealed partial class ClaudeViewModel
{
    bool _loadingAccounts;

    public ObservableCollection<ClaudeAccount> Accounts { get; } = [];

    /// <summary>The account conversations run with; changing it starts a fresh conversation there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountName), nameof(HasSeveralAccounts))]
    public partial ClaudeAccount? SelectedAccount { get; set; }

    public string AccountName => SelectedAccount?.Name ?? "Default";

    /// <summary>The picker only matters once there is more than one account.</summary>
    public bool HasSeveralAccounts => Accounts.Count > 1;

    [ObservableProperty]
    public partial bool IsAccountsOpen { get; set; }

    /// <summary>The name typed for a new account.</summary>
    [ObservableProperty]
    public partial string NewAccountName { get; set; } = "";

    [ObservableProperty]
    public partial string? AccountMessage { get; set; }

    /// <summary>Sign in to an account: the host opens the CLI for it in a terminal (it has no headless sign-in).</summary>
    public event Action<ClaudeAccount>? SignInRequested;

    public bool CanSignIn => SignInRequested is not null;

    void LoadAccounts()
    {
        _loadingAccounts = true;
        var chosen = SelectedAccount?.ConfigDirectory ?? _settings.ClaudeAccount;
        Accounts.Clear();
        foreach (var account in ClaudeAccounts.Discover())
            Accounts.Add(account);
        SelectedAccount = Accounts.FirstOrDefault(a => chosen is not null && ClaudeAccount.SamePath(a.ConfigDirectory, chosen))
            ?? ClaudeAccounts.Current(Accounts);
        OnPropertyChanged(nameof(HasSeveralAccounts));
        _loadingAccounts = false;
    }

    partial void OnSelectedAccountChanged(ClaudeAccount? oldValue, ClaudeAccount? newValue)
    {
        if (_loadingAccounts || newValue is null || oldValue is not null && ClaudeAccount.SamePath(oldValue.ConfigDirectory, newValue.ConfigDirectory))
            return;
        _settings.ClaudeAccount = newValue.ConfigDirectory;
        _settings.Save();
        _ = SwitchAccountAsync();
    }

    /// <summary>Conversations, history, usage and MCP servers belong to an account: start fresh in the new one.</summary>
    async Task SwitchAccountAsync()
    {
        await ResetAsync();
        Account = null;
        _account = default;
        Usage = null;
        ShowLimits([]);
        _mcpServers = [];
        McpGroups.Clear();
        WarmUp();
        FocusInputRequested?.Invoke();
    }

    [RelayCommand]
    void ToggleAccounts()
    {
        IsAccountsOpen = !IsAccountsOpen;
        if (!IsAccountsOpen)
            return;
        IsModesOpen = false;
        AccountMessage = null;
        // Picks up accounts signed in or added from a terminal meanwhile.
        LoadAccounts();
    }

    [RelayCommand]
    void SelectAccount(ClaudeAccount? account)
    {
        if (account is null)
            return;
        IsAccountsOpen = false;
        if (!account.IsSignedIn && CanSignIn)
        {
            SelectedAccount = account;
            SignIn(account);
            return;
        }
        SelectedAccount = account;
    }

    [RelayCommand]
    void SignIn(ClaudeAccount? account)
    {
        if (account is null || CliPath is null)
            return;
        SignInRequested?.Invoke(account);
        AccountMessage = $"Sign in to “{account.Name}” in the terminal that opened, then pick it here again.";
        Items.Add(new NoticeChatItem($"Signing in to the {account.Name} account in a terminal window. When you are done, send your prompt here.", isError: false));
    }

    /// <summary>Adds ~/.claude-NAME, switches to it and opens the sign-in.</summary>
    [RelayCommand]
    void AddAccount()
    {
        ClaudeAccount account;
        try
        {
            account = ClaudeAccounts.Create(NewAccountName);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            AccountMessage = ex.Message;
            return;
        }
        NewAccountName = "";
        LoadAccounts();
        var added = Accounts.FirstOrDefault(a => ClaudeAccount.SamePath(a.ConfigDirectory, account.ConfigDirectory)) ?? account;
        SelectAccount(added);
    }
}

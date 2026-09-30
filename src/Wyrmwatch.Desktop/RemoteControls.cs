using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Wyrmwatch.Core;

namespace Wyrmwatch.Desktop;

public partial class MainWindow
{
    private async Task LoadRemoteAsync()
    {
        try
        {
            var info = await service!.GetAsync<RemoteInfo>("admin/remote");
            RemoteEnabled.IsChecked = info.Settings.Enabled; RemotePort.Value = info.Settings.Port;
            RemoteAddressText.Text = info.Warning ?? info.Address ?? "Remote access is off.";
            CertificateText.Text = info.Certificate is null ? "" : "Certificate fingerprint: " + info.Certificate;
            AccessList.ItemsSource = await service.GetAsync<List<AccessGrantView>>("admin/access");
        }
        catch (Exception error) { model.Notice = error.Message; }
    }
    private async void RefreshRemote(object? sender, RoutedEventArgs e) { if (!Program.Demo) await LoadRemoteAsync(); }
    private async void SaveRemote(object? sender, RoutedEventArgs e)
    {
        if (Program.Demo || Program.HeadlessTest) return;
        if (RemoteEnabled.IsChecked == true && !await Confirm("Enable remote access?", "An HTTPS dashboard will listen on this computer. Only people with an access key can use it. No firewall or router rules will be changed.", "Enable remote access")) return;
        try
        {
            await service!.SendAsync("admin/remote", HttpMethod.Put, new RemoteSettings(RemoteEnabled.IsChecked == true, (int)(RemotePort.Value ?? 8843)));
            await service.RefreshAsync();
            if (service.PersistentHost) { model.Notice = "Remote settings saved. Restart the registered service or standalone agent to apply them."; return; }
            await RestartAgentAsync(); await LoadRemoteAsync(); model.Notice = "Remote access settings applied.";
        }
        catch (Exception error) { model.Notice = error.Message; }
    }
    private async Task RestartAgentAsync()
    {
        agentTransition = true;
        try { await service!.StopAgentAsync(); service.Resume(); await service.EnsureStartedAsync(); await service.RefreshAsync(); }
        finally { service!.Resume(); agentTransition = false; }
    }
    private async void CreateAccess(object? sender, RoutedEventArgs e)
    {
        if (Program.Demo || Program.HeadlessTest) return;
        try
        {
            if (RestrictAccess.IsChecked == true && model.SelectedProfile is null) throw new ArgumentException("Select a server to restrict this key to it.");
            var role = AccessRole.SelectedIndex switch { 1 => "Operator", 2 => "Maintainer", _ => "Viewer" };
            var result = await service!.PostAsync<IssuedAccessGrant>("admin/access", new CreateAccessGrant(AccessName.Text?.Trim() ?? "", role, RestrictAccess.IsChecked == true ? model.SelectedProfile?.Id : null, (int)(AccessDays.Value ?? 30)));
            IssuedSecret.Text = result.Secret; AccessName.Text = "";
            await LoadRemoteAsync(); model.Notice = "Access key created. Copy it now; only its hash is stored.";
        }
        catch (Exception error) { model.Notice = error.Message; }
    }
    private async void CopyAccess(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not null && !string.IsNullOrEmpty(IssuedSecret.Text)) await Clipboard.SetValueAsync(DataFormat.Text, IssuedSecret.Text);
    }
    private async void RevokeAccess(object? sender, RoutedEventArgs e)
    {
        if (Program.Demo || AccessList.SelectedItem is not AccessGrantView grant) return;
        if (!await Confirm("Revoke this access key?", $"{grant.Name} will lose access immediately. Your server will keep running.", "Revoke key")) return;
        try { await service!.SendAsync("admin/access/" + Uri.EscapeDataString(grant.Id), HttpMethod.Delete); await LoadRemoteAsync(); model.Notice = "Access revoked."; }
        catch (Exception error) { model.Notice = error.Message; }
    }
    private void OpenRemote(object? sender, RoutedEventArgs e)
    {
        if (service?.RemoteAddress is { } address) OpenPath(address);
        else model.Notice = "Enable remote access first.";
    }
    private void OpenCertificate(object? sender, RoutedEventArgs e) => OpenPath(Path.Combine(Program.DataDirectory, "remote-certificate.cer"));
}

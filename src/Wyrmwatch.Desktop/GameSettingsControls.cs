using Wyrmwatch.Core;

namespace Wyrmwatch.Desktop;

public partial class MainWindow
{
    private string ReadAdminPassword(ServerProfile profile, Dictionary<string, string> values)
    {
        if (!string.IsNullOrWhiteSpace(values.GetValueOrDefault("AdminPassword"))) return values["AdminPassword"];
        try { return new AdminPasswordStore(store).Read(profile) ?? ""; }
        catch (IOException)
        {
            // A Windows service may protect this value under its own account.
            // Retrieve it from the authenticated owner instead of weakening DPAPI.
            _ = ReadAgentAdminPasswordAsync(profile);
            return "";
        }
    }
    private async Task ReadAgentAdminPasswordAsync(ServerProfile profile)
    {
        await Task.Yield();
        try
        {
            var values = await service!.GetAsync<Dictionary<string, string>>($"admin/servers/{Uri.EscapeDataString(profile.Id)}/configuration", closing.Token);
            if (model.SelectedProfile?.Id == profile.Id && !AdminPassword.IsFocused && string.IsNullOrEmpty(AdminPassword.Text))
                AdminPassword.Text = values.GetValueOrDefault("AdminPassword", "");
        }
        catch (Exception error)
        {
            if (!closing.IsCancellationRequested && model.SelectedProfile?.Id == profile.Id) model.ReportError("Saved admin password could not be read. Re-enter it in Server settings if needed.", error.Message, "ServerSettings", "Open Server settings");
        }
    }
}

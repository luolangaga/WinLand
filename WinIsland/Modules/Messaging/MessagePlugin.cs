using WinIsland.Core;

namespace WinIsland.Modules.Messaging;

public sealed class MessagePlugin : IslandPluginBase
{
    protected override Task OnInitializeAsync()
    {
        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            "messenger", Manifest.Name, "\uE724", () => new MessageSettingsPage(Context), 40));
        return Task.CompletedTask;
    }
}

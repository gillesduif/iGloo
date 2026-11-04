using System.Text.Json;
using Igloo.Command.Contracts;
using Igloo.One.App;
using Xunit;

namespace Igloo.Command.Tests;

public sealed class OneChatConnectivityTests
{
    [Fact]
    public async Task DistributionQuestionUsesTypedCatalogueAndKeepsReadinessSeparate()
    {
        var inspections = 0;
        var listings = 0;
        var chat = new OneChatViewModel(
            _ => { inspections++; return Task.FromResult(Result("system.inspect", new { })); },
            _ =>
            {
                listings++;
                return Task.FromResult(Result("distro.list", new DistroList(
                [
                    new("linuxmint-cinnamon", "Linux Mint", "available"),
                    new("ubuntu", "Ubuntu", "in-development"),
                    new("fedora-kde", "Fedora KDE", "available"),
                    new("debian", "Debian", "available"),
                    new("deepin", "deepin", "coming-soon"),
                    new("nobara", "Nobara", "coming-soon"),
                    new("unknown", "Mystery Linux", null),
                ])));
            });

        await chat.SubmitAsync("Which Linux distributions can I install?", CancellationToken.None);

        Assert.Equal(0, inspections);
        Assert.Equal(1, listings);
        Assert.Contains("Listed as available: Debian, Fedora KDE, Linux Mint.",
            chat.LastAssistantText, StringComparison.Ordinal);
        Assert.Contains("In development: Ubuntu.", chat.LastAssistantText, StringComparison.Ordinal);
        Assert.Contains("Coming soon: 2 providers.", chat.LastAssistantText, StringComparison.Ordinal);
        Assert.Contains("Mystery Linux (status unavailable)", chat.LastAssistantText,
            StringComparison.Ordinal);
        Assert.Contains("not installation readiness for this device",
            chat.LastAssistantText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DistributionFailureGivesAnActionWithoutClaimingAProvider()
    {
        var chat = new OneChatViewModel(
            _ => throw new InvalidOperationException("Unexpected inspection"),
            _ => throw new IOException("Packaged CLI unavailable"));

        await chat.SubmitAsync("What distros are available?", CancellationToken.None);

        Assert.Contains("Restart iGloo One and try again", chat.LastAssistantText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Debian", chat.LastAssistantText, StringComparison.Ordinal);
        await chat.SubmitAsync("Tell me a story", CancellationToken.None);
        Assert.Contains("list packaged Linux distributions", chat.LastAssistantText,
            StringComparison.Ordinal);
        Assert.Contains("Broader conversation is not connected yet", chat.LastAssistantText,
            StringComparison.Ordinal);
    }

    private static CommandResult Result(string capability, object data) => new(
        CommandProtocol.Version, capability, 1, Guid.NewGuid(), Guid.NewGuid(),
        null, true, JsonSerializer.SerializeToElement(data, CommandProtocol.JsonOptions),
        null, [], []);
}

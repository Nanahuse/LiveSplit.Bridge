using System.Xml.Linq;

namespace LiveSplit.Bridge.Tests;

public class DeploymentProjectTests
{
    [Fact]
    public void DeploymentIsConfinedToLiveSplitComponentsDirectory()
    {
        var project = LoadBridgeProject();
        XNamespace msbuild = project.Root!.Name.Namespace;

        var deployTarget = project
            .Descendants(msbuild + "Target")
            .Single(element => (string?)element.Attribute("Name") == "DeployToLiveSplit");
        var destinations = deployTarget
            .Descendants(msbuild + "Copy")
            .Select(element => (string?)element.Attribute("DestinationFolder"))
            .ToArray();

        Assert.NotEmpty(destinations);
        Assert.All(destinations, destination =>
            Assert.Equal("$(LiveSplitComponentsPath)", destination));
    }

    [Fact]
    public void DeploymentDoesNotReplaceLiveSplitRuntimeConfiguration()
    {
        var project = LoadBridgeProject();

        Assert.DoesNotContain(
            project.DescendantNodes().OfType<XText>(),
            text => text.Value.Contains("LiveSplit.exe.config", StringComparison.OrdinalIgnoreCase));
    }

    private static XDocument LoadBridgeProject()
    {
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "LiveSplit.Bridge",
            "LiveSplit.Bridge.csproj");

        return XDocument.Load(projectPath);
    }
}

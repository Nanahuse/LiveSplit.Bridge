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
            text => text.Value.IndexOf(
                "LiveSplit.exe.config",
                StringComparison.OrdinalIgnoreCase) >= 0);
    }

    [Fact]
    public void DistributionAllowListContainsOnlyExpectedBridgeFiles()
    {
        var project = LoadBridgeProject();
        XNamespace msbuild = project.Root!.Name.Namespace;
        var files = ((string?)project
            .Descendants(msbuild + "BridgeDistributionFiles")
            .Single()
            .Value ?? string.Empty)
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            new[]
            {
                "LiveSplit.Bridge.dll",
                "LiveSplit.Bridge.Protocol.dll",
                "Google.Protobuf.dll",
            },
            files);
    }

    [Fact]
    public void DeploymentUsesDistributionAllowListInsteadOfReferenceCopyLocalPaths()
    {
        var project = LoadBridgeProject();
        XNamespace msbuild = project.Root!.Name.Namespace;
        var deployTarget = project
            .Descendants(msbuild + "Target")
            .Single(element => (string?)element.Attribute("Name") == "DeployToLiveSplit");
        var includes = deployTarget
            .Descendants(msbuild + "BridgeDeployFiles")
            .Select(element => (string?)element.Attribute("Include"))
            .ToArray();

        Assert.Contains(includes, include => include?.Contains("BridgeDistributionFile", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(includes, include => include?.Contains("ReferenceCopyLocalPaths", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ReleaseBuildDoesNotGenerateDebugSymbols()
    {
        var project = LoadBridgeProject();
        XNamespace msbuild = project.Root!.Name.Namespace;
        var releaseGroup = project
            .Descendants(msbuild + "PropertyGroup")
            .Single(element => (string?)element.Attribute("Condition") == "'$(Configuration)' == 'Release'");

        Assert.Equal("None", (string?)releaseGroup.Element(msbuild + "DebugType"));
        Assert.Equal("false", (string?)releaseGroup.Element(msbuild + "DebugSymbols"));
        Assert.DoesNotContain(
            project.Descendants(msbuild + "BridgeDistributionFiles").Single().Value.Split(';'),
            file => file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
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

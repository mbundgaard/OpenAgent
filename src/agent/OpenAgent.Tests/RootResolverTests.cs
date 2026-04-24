using OpenAgent;

namespace OpenAgent.Tests;

public class RootResolverTests
{
    [Fact]
    public void Resolve_WithDataDirSet_ReturnsEnvVarValue()
    {
        var result = RootResolver.Resolve(envVar: "/tmp/from-env", baseDirectory: "C:/app");

        Assert.Equal("/tmp/from-env", result);
    }

    [Fact]
    public void Resolve_WithDataDirUnset_ReturnsBaseDirectory()
    {
        var result = RootResolver.Resolve(envVar: null, baseDirectory: "C:/app");

        Assert.Equal("C:/app", result);
    }

    [Fact]
    public void Resolve_WithEmptyEnvVar_FallsBackToBaseDirectory()
    {
        var result = RootResolver.Resolve(envVar: "", baseDirectory: "C:/app");

        Assert.Equal("C:/app", result);
    }

    [Fact]
    public void Resolve_WithWhitespaceEnvVar_FallsBackToBaseDirectory()
    {
        var result = RootResolver.Resolve(envVar: "   ", baseDirectory: "C:/app");

        Assert.Equal("C:/app", result);
    }
}

using Microsoft.Build.Locator;
using NUnit.Framework;

namespace DepSniff.Tests;

[SetUpFixture]
public sealed class TestSetup
{
    [OneTimeSetUp]
    public void RegisterMSBuild() => MSBuildLocator.RegisterDefaults();
}

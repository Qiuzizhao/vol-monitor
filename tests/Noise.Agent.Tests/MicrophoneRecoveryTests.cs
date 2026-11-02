using Noise.Agent;
using Xunit;

namespace Noise.Agent.Tests;

public sealed class MicrophoneRecoveryTests
{
    [Fact]
    public void ExistingSelectionIsKeptAndAReplacementUsesTheCurrentDefault()
    {
        (string,string)[] devices=[("one","A"),("two","B")];
        Assert.Equal("one",AudioCapture.SelectDevice("one",devices,()=>"two"));
        Assert.Equal("two",AudioCapture.SelectDevice("removed",devices,()=>"two"));
        Assert.Equal("one",AudioCapture.SelectDevice("removed",devices,()=>"unavailable"));
    }
    [Fact]
    public void MissingMicrophoneCanBeRetriedAfterANewOneAppears()
    {
        Assert.Throws<InvalidOperationException>(()=>AudioCapture.SelectDevice("removed",[],()=>"none"));
        Assert.Equal("new",AudioCapture.SelectDevice("removed",[("new","USB")],()=>"new"));
    }
}

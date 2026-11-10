namespace OpenFreqAudio.Tests;

/// <summary>Ownship mic click: pick-up at key-down, put-down at key-up, one-shot, gain-scaled.</summary>
public class MicClickPlayerTests
{
    private static readonly float[] PickUp = [0.1f, 0.2f, 0.3f];
    private static readonly float[] PutDown = [-0.5f, -0.4f, -0.3f, -0.2f, -0.1f];

    private static MicClickPlayer Player()
    {
        var p = new MicClickPlayer();
        p.SetSamples(PickUp, PutDown);
        return p;
    }

    [Fact]
    public void SilentUntilTriggered()
    {
        var p = Player();
        var buf = new float[4] { 9, 9, 9, 9 };
        Assert.Equal(0, p.Render(buf, 1f));
        Assert.All(buf, s => Assert.Equal(0f, s));
        Assert.False(p.IsPlaying);
    }

    [Fact]
    public void KeyDownPlaysThePickUpOnceThenSilence()
    {
        var p = Player();
        p.Trigger(keyDown: true);
        var buf = new float[5];
        Assert.Equal(3, p.Render(buf, 1f));
        Assert.Equal([0.1f, 0.2f, 0.3f, 0f, 0f], buf);
        Assert.False(p.IsPlaying);
        Assert.Equal(0, p.Render(buf, 1f)); // one-shot
    }

    [Fact]
    public void KeyUpPlaysThePutDownAcrossCallbacksWithGain()
    {
        var p = Player();
        p.Trigger(keyDown: false);
        var a = new float[2];
        var b = new float[4];
        Assert.Equal(2, p.Render(a, 0.5f));
        Assert.Equal(3, p.Render(b, 0.5f));
        Assert.Equal([-0.25f, -0.2f], a);
        Assert.Equal([-0.15f, -0.1f, -0.05f, 0f], b);
    }

    [Fact]
    public void QuickReleaseCutsThePickUpShortAndPlaysThePutDownFromItsStart()
    {
        var p = Player();
        p.Trigger(true);
        p.Render(new float[1], 1f);
        p.Trigger(false);
        var buf = new float[5];
        Assert.Equal(5, p.Render(buf, 1f));
        Assert.Equal(PutDown, buf);
    }

    [Fact]
    public void StopAndMissingSamplesAreSilent()
    {
        var p = Player();
        p.Trigger(true);
        p.Stop();
        Assert.Equal(0, p.Render(new float[3], 1f));

        var none = new MicClickPlayer();
        none.SetSamples(null, null);
        none.Trigger(true);
        Assert.Equal(0, none.Render(new float[3], 1f));
    }
}

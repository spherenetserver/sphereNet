using SphereNet.Game.Objects.Characters;

namespace SphereNet.Tests;

public sealed class SourceXEnvironChangeWave236Tests
{
    [Fact]
    public void FirstEnvironmentObservation_EstablishesBaselineWithoutTrigger()
    {
        var character = new Character();
        int fires = 0;
        Character.OnEnvironChange = (_, _) => fires++;

        character.UpdateEnvironment(20, 0, 1);

        Assert.Equal(0, fires);
    }

    [Theory]
    [InlineData(21, 0, 1)]
    [InlineData(20, 2, 1)]
    [InlineData(20, 0, 3)]
    public void LightWeatherOrSeasonChange_FiresEnvironmentTrigger(
        int light, int weather, int season)
    {
        var character = new Character();
        var observedLights = new List<int>();
        Character.OnEnvironChange = (changed, newLight) =>
        {
            if (changed == character) observedLights.Add(newLight);
        };
        character.UpdateEnvironment(20, 0, 1);

        character.UpdateEnvironment(light, weather, season);

        Assert.Equal([light], observedLights);
    }

    [Fact]
    public void IdenticalEnvironmentSnapshot_DoesNotRefireTrigger()
    {
        var character = new Character();
        int fires = 0;
        Character.OnEnvironChange = (_, _) => fires++;
        character.UpdateEnvironment(20, 1, 2);

        character.UpdateEnvironment(20, 1, 2);

        Assert.Equal(0, fires);
    }
}

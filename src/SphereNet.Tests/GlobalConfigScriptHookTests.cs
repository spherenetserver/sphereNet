using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using GameTriggerArgs = SphereNet.Game.Scripting.TriggerArgs;

namespace SphereNet.Tests;

public class GlobalConfigScriptHookTests
{
    private static (TriggerDispatcher Dispatcher, ResourceHolder Resources) CreateDispatcher(string scriptText)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>());
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_hooks_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tmp, scriptText);
        resources.LoadResourceFile(tmp);

        var interpreter = new ScriptInterpreter(
            new ExpressionParser(),
            loggerFactory.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(
            interpreter,
            resources,
            loggerFactory.CreateLogger<TriggerRunner>());

        return (new TriggerDispatcher
        {
            Resources = resources,
            Runner = runner,
        }, resources);
    }

    [Fact]
    public void GlobalEventsPlayerAndItem_RunConfiguredEventResources()
    {
        var (dispatcher, _) = CreateDispatcher("""
            [EVENTS e_player_global]
            ON=@Mount
            TAG.GLOBAL_PLAYER=1
            RETURN 0

            [EVENTS ei_item_global]
            ON=@DClick
            TAG.GLOBAL_ITEM=1
            RETURN 0
            """);
        dispatcher.GlobalPlayerEvents.Add(ResourceId.FromEventName("e_player_global"));
        dispatcher.GlobalItemEvents.Add(ResourceId.FromEventName("ei_item_global"));

        var player = new Character { IsPlayer = true };
        var item = new Item();

        dispatcher.FireCharTrigger(player, CharTrigger.Mount, new GameTriggerArgs { CharSrc = player });
        dispatcher.FireItemTrigger(item, ItemTrigger.DClick, new GameTriggerArgs { CharSrc = player, ItemSrc = item });

        Assert.True(player.TryGetProperty("TAG.GLOBAL_PLAYER", out var playerValue));
        Assert.Equal("1", playerValue);
        Assert.True(item.TryGetProperty("TAG.GLOBAL_ITEM", out var itemValue));
        Assert.Equal("1", itemValue);
    }

    [Fact]
    public void GlobalSpeechSelfAndPet_RunConfiguredSpeechResources()
    {
        var (dispatcher, _) = CreateDispatcher("""
            [SPEECH spk_player]
            ON=*hello*
            TAG.SPEECH_SELF=1
            RETURN 1

            [SPEECH spk_pet]
            ON=*stay*
            TAG.SPEECH_PET=1
            RETURN 1
            """);
        dispatcher.SpeechSelfResources.Add(ResourceId.FromString("spk_player", ResType.Speech));
        dispatcher.SpeechPetResources.Add(ResourceId.FromString("spk_pet", ResType.Speech));

        var player = new Character { IsPlayer = true };
        var pet = new Character { IsPlayer = false };

        var selfResult = dispatcher.FireSpeechSelfTrigger(player, "hello there", 0);
        var petResult = dispatcher.FireSpeechTrigger(pet, player, "stay here", 0);

        Assert.Equal(TriggerResult.True, selfResult);
        Assert.Equal(TriggerResult.True, petResult);
        Assert.True(player.TryGetProperty("TAG.SPEECH_SELF", out var selfValue));
        Assert.Equal("1", selfValue);
        Assert.True(pet.TryGetProperty("TAG.SPEECH_PET", out var petValue));
        Assert.Equal("1", petValue);
    }

    [Fact]
    public void SpeechTrigger_StopsBodyAtNextSpeechOnBlock()
    {
        var (dispatcher, _) = CreateDispatcher("""
            [SPEECH spk_player]
            ON=*hello*
            TAG.SPEECH_MATCHED=1
            RETURN 1

            ON=*bye*
            TAG.SPEECH_WRONG_BLOCK=1
            RETURN 1
            """);
        dispatcher.SpeechSelfResources.Add(ResourceId.FromString("spk_player", ResType.Speech));

        var player = new Character { IsPlayer = true };

        var result = dispatcher.FireSpeechSelfTrigger(player, "hello there", 0);

        Assert.Equal(TriggerResult.True, result);
        Assert.True(player.TryGetProperty("TAG.SPEECH_MATCHED", out var matched));
        Assert.Equal("1", matched);
        Assert.False(player.TryGetTag("SPEECH_WRONG_BLOCK", out _));
    }
}

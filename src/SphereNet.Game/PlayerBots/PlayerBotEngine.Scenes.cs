using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// What players hear. For each connected player a director keeps the bots around
/// them alive: every so often (<see cref="ChatPaceSeconds"/>) a scene from
/// scenes.txt is played by one or two bots near them - a word about the place, a
/// trade shout and its haggle, a call for a group and its answer, real news and its
/// reaction. A player walking up to a bot may be greeted (by name, if the bot knows
/// them), a ghost near a caster may be raised, a murderer in town is called out.
/// Nobody near, nothing is played.
/// </summary>
public sealed partial class PlayerBotEngine
{
    public const string KnownTag = "PLAYERBOT.KNOWN";
    private const int KnownMax = 24;
    private const int SceneRange = 14;

    private readonly List<BotScene> _scenes = [];

    public IReadOnlyList<BotScene> ActiveScenes => _scenes;

    private void Direct(BotWatcher w, long now)
    {
        var player = w.Char;
        if (player.IsDead)
        {
            OfferRes(w, now);
            return;
        }
        if (player.IsMurderer && now - w.LastRedAlertMs > 600_000)
            CallOutRed(w, now);
        GreetOnSight(w, now);

        if (now < w.NextSceneMs) return;
        if (w.Crowd == 0)
        {
            w.NextSceneMs = now + 4000;
            return;
        }
        int pace = Math.Max(4, ChatPaceSeconds) * 1000;
        w.NextSceneMs = now + _rng.Next(pace / 2, pace * 3 / 2);
        // Two players standing together share the talk around them.
        int nearScenes = _scenes.Count(s => Within(s.A.Char.Position, player.Position, HearRange));
        if (nearScenes >= 1 + w.Crowd / 8) return;
        StartScene(w, now);
    }

    private bool IsFree(BotMind m) =>
        m.Mode == BotMode.Linger && m.Scene == null && m.PendingCategory == null && !m.Char.IsDead &&
        !m.Char.IsDeleted && Parties?.FindParty(m.Char.Uid) == null;

    private List<BotMind> FreeBotsNear(Point3D p, int range)
    {
        var list = new List<BotMind>();
        foreach (var c in _world.GetCharsInRange(p, range))
            if (c.IsPlayerBot && c.MapIndex == p.Map && _minds.TryGetValue(c.Uid.Value, out var m) && IsFree(m))
                list.Add(m);
        return list;
    }

    private static bool IsNight()
    {
        int h = DateTime.Now.Hour;
        return h >= 21 || h < 5;
    }

    private bool Fits(BotSceneDef def, BotMind a, bool haveTwo, long now, out BotGossipEvent? ev)
    {
        ev = null;
        if (def.NeedsTwo && !haveTwo) return false;
        if (def.Night && !IsNight()) return false;
        if (def.Day && IsNight()) return false;
        if (def.Fighter && !a.IsFighter) return false;
        if (def.Caster && !a.IsCaster) return false;
        if (def.At.Length > 0 && (a.At == null || !def.At.Any(t => t.Equals(a.At.Type, StringComparison.OrdinalIgnoreCase))))
            return false;
        if (def.Events.Length > 0)
        {
            ev = Gossip.Pick(_rng, now, def.Events);
            if (ev == null) return false;
        }
        return true;
    }

    private void StartScene(BotWatcher w, long now)
    {
        if (Scenes.Scenes.Count == 0) return;
        var free = FreeBotsNear(w.Char.Position, SceneRange);
        if (free.Count == 0) return;
        var a = free[_rng.Next(free.Count)];
        BotMind? b = null;
        int bestD = int.MaxValue;
        foreach (var m in free)
        {
            if (m == a) continue;
            int d = m.Char.Position.GetDistanceTo(a.Char.Position);
            if (d <= 6 && d < bestD) { b = m; bestD = d; }
        }

        var fits = new List<(BotSceneDef Def, BotGossipEvent? Ev)>();
        foreach (var def in Scenes.Scenes)
            if (Fits(def, a, b != null, now, out var ev))
                fits.Add((def, ev));
        if (fits.Count == 0) return;
        var (chosen, news) = WeightedPick(fits, f => f.Def.Weight);

        // The news is best told by whoever it happened to, when they stand here.
        if (news != null && b != null && news.ActorUid == b.Char.Uid.Value)
            (a, b) = (b, a);
        string lang = w.Language ?? Chat.Language;
        var scene = new BotScene
        {
            Def = chosen,
            A = a,
            B = chosen.NeedsTwo ? b : null,
            Language = lang,
            BasePrice = _rng.Next(4, 80) * 50,
            Event = news,
            NextMs = now + _rng.Next(300, 1800),
        };
        foreach (string list in TokenLists)
            if (Chat.Pick("token_" + list, _rng, null, lang) is { } word)
                scene.Tokens[list] = word;
        scene.Tokens["price"] = scene.BasePrice.ToString();
        if (DungeonName() is { } dungeon) scene.Tokens["dungeon"] = dungeon;
        if (news != null)
        {
            scene.Tokens["actor"] = news.Actor;
            if (news.Other.Length > 0) scene.Tokens["other"] = news.Other;
            if (news.Place.Length > 0) scene.Tokens["place"] = news.Place;
            scene.Tokens["count"] = Math.Max(2, Gossip.PkCount(news.OtherUid)).ToString();
            if (Chat.Pick(now - news.AtMs < 600_000 ? "token_when_now" : "token_when_earlier", _rng, null, lang) is { } when)
                scene.Tokens["when"] = when;
        }
        a.Scene = scene;
        if (scene.B != null) scene.B.Scene = scene;
        _scenes.Add(scene);
        WindowScenes++;
    }

    private void TickScenes(long now)
    {
        for (int i = _scenes.Count - 1; i >= 0; i--)
        {
            var scene = _scenes[i];
            if (now < scene.NextMs) continue;
            if (scene.Step >= scene.Def.Steps.Length) { EndScene(scene); continue; }
            var step = scene.Def.Steps[scene.Step++];
            var speaker = step.Speaker == 'A' ? scene.A : scene.B;
            var other = step.Speaker == 'A' ? scene.B : scene.A;
            if (speaker == null || !StillActing(speaker) || (other != null && !StillActing(other)) ||
                (other != null && !Within(speaker.Char.Position, other.Char.Position, 10)))
            {
                EndScene(scene);
                continue;
            }
            string category = step.Categories[_rng.Next(step.Categories.Length)];
            if (category == "$place") category = LingerCategory(speaker.At);
            if (scene.Event != null && scene.Event.ActorUid == speaker.Char.Uid.Value &&
                category.StartsWith("gossip_", StringComparison.Ordinal) && !category.StartsWith("gossip_react", StringComparison.Ordinal) &&
                Chat.Has(category + "_self", scene.Language))
                category += "_self";
            if (step.PricePct > 0)
                scene.Tokens["price"] = RoundPrice(scene.BasePrice * step.PricePct / 100).ToString();
            if (!Speak(speaker, category, fallback: null, to: other?.Char, lang: scene.Language, extra: scene.Tokens))
            {
                // A line that cannot be said (nobody hears any more, a token missing)
                // ends the scene rather than leave the other one answering nothing.
                EndScene(scene);
                continue;
            }
            if (scene.Step >= scene.Def.Steps.Length) { EndScene(scene); continue; }
            scene.NextMs = now + _rng.Next(1800, 4200);
        }
    }

    private static bool StillActing(BotMind m) =>
        !m.Char.IsDeleted && !m.Char.IsDead && m.Mode == BotMode.Linger;

    private void EndScene(BotScene scene)
    {
        if (scene.A.Scene == scene) scene.A.Scene = null;
        if (scene.B?.Scene == scene) scene.B.Scene = null;
        _scenes.Remove(scene);
    }

    internal static int RoundPrice(int price) => Math.Max(50, (price + 25) / 50 * 50);

    // ------------------------------------------------------------------
    //  Players
    // ------------------------------------------------------------------

    /// <summary>A bot that knows a player says hello by name when they come near; a
    /// stranger only now and then. Each bot decides once per meeting.</summary>
    private void GreetOnSight(BotWatcher w, long now)
    {
        if (now - w.LastGreetMs < 90_000) return;
        var player = w.Char;
        foreach (var m in FreeBotsNear(player.Position, 5))
        {
            uint uid = player.Uid.Value;
            if (m.Greeted.TryGetValue(uid, out long at) && now - at < 20 * 60_000) continue;
            m.Greeted[uid] = now;
            if (m.Greeted.Count > 64)
                foreach (uint old in m.Greeted.Where(kv => now - kv.Value > 20 * 60_000).Select(kv => kv.Key).ToList())
                    m.Greeted.Remove(old);
            bool known = Knows(m, player);
            if (_rng.Next(100) >= (known ? 80 : 12)) continue;
            QueueReply(m, known ? "friend_greet" : "respond_greet", now, to: player, lang: w.Language,
                fallback: "respond_greet");
            w.LastGreetMs = now;
            return;
        }
    }

    /// <summary>A caster near a ghost raises it, the way a passer-by did.</summary>
    private void OfferRes(BotWatcher w, long now)
    {
        var ghost = w.Char;
        if (now - w.LastGreetMs < 8000) return;
        foreach (var m in FreeBotsNear(ghost.Position, 8))
        {
            if (!CanRaise(m, now)) continue;
            if (_rng.Next(100) >= 35) return;
            w.LastGreetMs = now;
            Raise(m, ghost, w.Language, now);
            return;
        }
    }

    private static bool CanRaise(BotMind m, long now) =>
        m.IsCaster && now >= m.NextResMs && m.Char.Mana >= 50 && m.Char.GetSkill(SkillType.Magery) >= 700 &&
        !m.Char.TryGetCastingSpell(out _) && HasSpell(m.Char, SpellType.Resurrection);

    private void Raise(BotMind m, Character ghost, string? lang, long now)
    {
        m.NextResMs = now + 45_000;
        var client = m.Client;
        QueueReply(m, "res_offer", now, to: ghost, lang: lang, fallback: null,
            action: client == null ? null : () =>
            {
                if (ghost.IsDead && !ghost.IsDeleted && Within(ghost.Position, m.Char.Position, 10))
                    client.HandleCastSpell(SpellType.Resurrection, ghost.Uid.Value);
            });
    }

    /// <summary>A murderer standing among the bots: it is news, and in a guarded town
    /// somebody shouts about it.</summary>
    private void CallOutRed(BotWatcher w, long now)
    {
        var red = w.Char;
        var free = FreeBotsNear(red.Position, 12);
        if (free.Count == 0) return;
        w.LastRedAlertMs = now;
        var m = free[_rng.Next(free.Count)];
        string place = PlaceName(red.Position);
        Gossip.Add(new BotGossipEvent("red", BaseName(m.Char), m.Char.Uid.Value, red.Name, red.Uid.Value, place, now));
        bool guarded = _world.FindRegion(red.Position)?.IsGuarded == true;
        QueueReply(m, guarded ? "guards_call" : "red_alert", now, lang: w.Language, fallback: null,
            tokens: new Dictionary<string, string> { ["place"] = place.ToUpperInvariant(), ["other"] = BaseName(red) });
    }

    // ------------------------------------------------------------------
    //  News
    // ------------------------------------------------------------------

    /// <summary>A character died: a player's death, a murder or a big monster brought
    /// down becomes news bots tell each other.</summary>
    public void OnDeath(Character victim, Character? killer)
    {
        if (_minds.Count == 0) return;
        long now = NowMs();
        if (killer != null) NotePkKill(victim, killer, now);
        string place = PlaceName(victim.Position);
        if (victim.IsPlayer && killer != null && killer.IsPlayer && killer != victim)
            Gossip.Add(new BotGossipEvent("pk", BaseName(victim), victim.Uid.Value, BaseName(killer), killer.Uid.Value, place, now));
        else if (victim.IsPlayer)
            Gossip.Add(new BotGossipEvent("death", BaseName(victim), victim.Uid.Value,
                killer != null ? MonsterName(killer) : "", killer?.Uid.Value ?? 0, place, now));
        else if (killer is { IsPlayer: true } && victim.MaxHits >= NotableMonsterHits)
            Gossip.Add(new BotGossipEvent("kill", BaseName(killer), killer.Uid.Value, MonsterName(victim), victim.Uid.Value, place, now));
    }

    /// <summary>Hit points from which a monster's death is worth telling.</summary>
    public int NotableMonsterHits { get; set; } = 200;

    private static string MonsterName(Character c)
    {
        string n = c.GetName();
        foreach (string article in new[] { "a ", "an ", "the " })
            if (n.StartsWith(article, StringComparison.OrdinalIgnoreCase)) return n[article.Length..];
        return n;
    }

    /// <summary>A place name for news: the region, else the nearest town on the road.</summary>
    private string PlaceName(Point3D p)
    {
        string? region = _world.FindRegion(p)?.Name;
        if (!string.IsNullOrWhiteSpace(region)) return region;
        int node = Nav.FindNearest(p, 64);
        if (node < 0) return "";
        var near = Nav.Destinations
            .Where(d => d.City.Length > 0 && d.Pos.Map == p.Map)
            .OrderBy(d => d.Pos.GetDistanceTo(p))
            .FirstOrDefault();
        return near?.City ?? "";
    }

    // ------------------------------------------------------------------
    //  Players a bot knows
    // ------------------------------------------------------------------

    private static bool Knows(BotMind m, Character player) =>
        m.Char.TryGetTag(KnownTag, out string? known) && known != null &&
        known.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(player.Uid.Value.ToString());

    /// <summary>Remember a player this bot has talked with (kept with the character:
    /// a bot greets an old acquaintance by name after a restart too).</summary>
    private static void Remember(BotMind m, Character player)
    {
        string uid = player.Uid.Value.ToString();
        var list = m.Char.TryGetTag(KnownTag, out string? known) && known != null
            ? known.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList()
            : [];
        list.Remove(uid);
        list.Add(uid);
        if (list.Count > KnownMax) list.RemoveRange(0, list.Count - KnownMax);
        m.Char.SetTag(KnownTag, string.Join(' ', list));
    }
}

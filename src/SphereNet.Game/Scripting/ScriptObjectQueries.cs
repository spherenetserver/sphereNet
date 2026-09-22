using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Scripting;

/// <summary>CScriptObj world loops. The subject supplies the world, not a connected client.</summary>
public static class ScriptObjectQueries
{
    /// <summary>A bare non-negative integer argument, the form nearly every radius
    /// loop in a script pack actually uses. Anything else (a &lt;tag&gt;, an
    /// expression, a defname) falls through to the full expression parser.</summary>
    private static bool TryParsePlainRange(string args, out long range)
    {
        range = 0;
        var span = args.AsSpan().Trim();
        if (span.Length == 0 || span.Length > 5) return false;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] < '0' || span[i] > '9') return false;
            range = range * 10 + (span[i] - '0');
        }
        return true;
    }

    /// <summary>Source-X uses CWorldSearch for these radius loops (CScriptObj.cpp).
    /// Snapshot only matching sector residents before executing script bodies;
    /// copying the entire world here makes every TIMERD 1 / FORITEMS 0 scan allocate
    /// an array proportional to the whole save.</summary>
    private static IReadOnlyList<IScriptObj> QueryInRange(
        GameWorld world, ObjBase subject, string query, long range)
    {
        if (range < 0) return [];
        var center = subject.GetTopLevelObj().Position;
        bool wantItems = query is "FORITEMS" or "FOROBJS";
        bool wantChars = query is "FORCHARS" or "FOROBJS" or "FORPLAYERS" or "FORCLIENTS";

        var rawItems = wantItems ? new List<Item>() : null;
        var rawChars = wantChars ? new List<Character>() : null;
        world.CollectInRange(center, (int)Math.Min(range, ushort.MaxValue), rawItems, rawChars);

        // Items first, then characters - the order the OrderBy produced.
        var result = new List<IScriptObj>((rawItems?.Count ?? 0) + (rawChars?.Count ?? 0));
        if (rawItems != null)
            foreach (var item in rawItems)
                result.Add(item);
        if (rawChars != null)
            foreach (var ch in rawChars)
            {
                if (query is "FORCHARS" or "FOROBJS" ||
                    query == "FORPLAYERS" && ch.IsPlayer ||
                    query == "FORCLIENTS" && ch.IsPlayer && ch.IsOnline)
                    result.Add(ch);
            }
        return result;
    }

    public static IReadOnlyList<IScriptObj> Query(GameWorld world, IScriptObj target,
        string query, string args, ResourceHolder? resources)
    {
        query = query.ToUpperInvariant();

        // The radius loops run from @Timer bodies - thousands a second on a busy
        // shard - so they get their own path before the general machinery is built.
        // The old shape allocated an ExpressionParser plus a resolver lambda on
        // EVERY call (even for the literal "0" that FORITEMS 0 passes), then ran a
        // LINQ Where/OrderBy/Cast/ToArray chain on top of the sector walk. This does
        // the same selection with two lists and no closures, and keeps the ordering
        // the chain produced: matching items in sector-enumeration order, then
        // matching characters in theirs.
        if (query is "FORCHARS" or "FORITEMS" or "FOROBJS" or "FORCLIENTS" or "FORPLAYERS"
            && target is ObjBase fastSubject && TryParsePlainRange(args, out long fastRange))
            return QueryInRange(world, fastSubject, query, fastRange);

        var parser = new ExpressionParser
        {
            VariableResolver = name => resources?.TryResolveDefNameValue(name, out var n) == true
                ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : null
        };
        long Number(string text) => parser.Evaluate(text);
        int Resource(string text, ResType type)
        {
            var id = resources?.ResolveDefName(text) ?? ResourceId.Invalid;
            if (id.IsValid && id.Type != ResType.DefName) return id.Type == type ? id.Index : -1;
            return parser.TryEvaluate(text, out var value) ? unchecked((int)value) : -1;
        }

        if (query == "FORTIMERF")
            return world.GetTimerFObjects().Where(o => !o.IsDeleted)
                .SelectMany(o => o.TimerFEntries.Select(e => (Object: o, Entry: e)))
                .Where(pair => ObjBase.CommandOf(pair.Entry).Equals(args, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Entry.Sequence).Select(pair => (IScriptObj)pair.Object).ToArray();

        if (query == "FORINSTANCES")
        {
            string def = args.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (def.Length == 0) return [];
            int itemId = Resource(def, ResType.ItemDef), charId = Resource(def, ResType.CharDef);
            return world.GetAllObjects().Where(o => !o.IsDeleted &&
                (o is Item item && itemId >= 0 && ItemDefHelper.ResolveInstanceDefIndex(item, resources) == itemId ||
                 o is Character ch && charId >= 0 && ch.CharDefIndex == charId)).Cast<IScriptObj>().ToArray();
        }

        if (query is "FORCHARS" or "FORITEMS" or "FOROBJS" or "FORCLIENTS" or "FORPLAYERS")
        {
            if (target is not ObjBase subject) return [];
            return QueryInRange(world, subject, query,
                string.IsNullOrWhiteSpace(args) ? ScriptTouchAccess.Configuration.MapViewSize : Number(args));
        }

        if (query is "FORCONT" or "FORCONTID" or "FORCONTTYPE")
        {
            var parts = args.Split([' ', '\t', ','], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || query == "FORCONT" && parts.Length < 2) return [];
            ObjBase? container = query == "FORCONT" ? world.FindObject(new Serial(unchecked((uint)Number(parts[0])))) : target as ObjBase;
            if (container is not Character && container is not Item { IsContainerType: true }) return [];
            int depth = parts.Length > 1 ? (int)Math.Clamp(Number(parts[1]), 0, 255) : 255;
            int filter = query == "FORCONT" ? 0 : Resource(parts[0], query == "FORCONTID" ? ResType.ItemDef : ResType.TypeDef);
            if (query == "FORCONTTYPE" && filter < 0 && Enum.TryParse<ItemType>(parts[0].Replace("t_", "", StringComparison.OrdinalIgnoreCase), true, out var type))
                filter = (int)type;
            if (query != "FORCONT" && filter <= 0) return [];
            var result = new List<IScriptObj>();
            var seen = new HashSet<Serial>();
            void Collect(ObjBase parent, int remaining)
            {
                if (!seen.Add(parent.Uid)) return;
                // The reverse container index includes character equipment as well as backpacks.
                foreach (var item in world.GetContainerContents(parent.Uid).ToArray())
                {
                    if (query == "FORCONT" || query == "FORCONTID" && ItemDefHelper.ResolveInstanceDefIndex(item, resources) == filter ||
                        query == "FORCONTTYPE" && (int)item.ItemType == filter) result.Add(item);
                    if (remaining > 0 && item.IsContainerType && item.IsSearchableContainer) Collect(item, remaining - 1);
                }
            }
            Collect(container, depth);
            return result;
        }

        if (target is Character character)
        {
            if (query == "FORCHARLAYER")
                return world.GetContainerContents(character.Uid).Where(it => (int)it.EquipLayer == Number(args)).Cast<IScriptObj>().ToArray();
            if (query == "FORCHARMEMORYTYPE") return character.GetMemoryEntriesByType(args, world);
        }
        return [];
    }
}

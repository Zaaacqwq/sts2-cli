using System.Collections;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Models;

namespace Sts2Headless;

/// <summary>
/// RL curriculum protocol extension: atomic combat resets and model catalogs.
/// </summary>
public partial class RunSimulator
{
    /// <summary>
    /// Atomic curriculum reset. Starts a fresh run from <paramref name="seed"/>,
    /// optionally overrides the player (hp/max_hp/gold/deck/relics/potions),
    /// then enters the given combat encounter. The caller sees exactly one
    /// response: the first combat decision, or the first error (fail closed —
    /// a failed step never falls through to the next one).
    /// </summary>
    public Dictionary<string, object?> StartCombat(
        string character, int ascension, string? seed, string lang,
        Dictionary<string, JsonElement> playerArgs, string encounter,
        List<string>? drawOrder)
    {
        var started = StartRun(character, ascension, seed, lang);
        // STS2 completes ModManager lazily during the first RunState creation;
        // mirror the start_run router's single transparent retry.
        if (started.TryGetValue("message", out var warmup) && warmup is string text &&
            text.Contains("ModManager is not finished initializing"))
            started = StartRun(character, ascension, seed, lang);
        if (IsError(started)) return started;

        if (playerArgs.Count > 0)
        {
            var configured = SetPlayer(playerArgs);
            if (IsError(configured)) return configured;
        }

        var state = EnterRoom("combat", encounter, null);
        if (IsError(state)) return state;

        if (drawOrder is { Count: > 0 })
        {
            var ordered = SetDrawOrder(drawOrder);
            if (IsError(ordered)) return ordered;
            return DetectDecisionPoint();
        }
        return state;
    }

    /// <summary>
    /// Enumerate canonical game content so RL tooling can build curriculum
    /// configs and embedding vocabularies without scraping trajectories.
    /// </summary>
    public Dictionary<string, object?> ListModels(string kind)
    {
        try
        {
            try { return ListModelsOnce(kind); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("ModManager is not finished initializing"))
            {
                // Same lazy one-shot as StartRun: the throw completes the
                // initialization, so a single retry is safe (AllPowers walks
                // ReflectionHelper.ModTypes, a second lazy path).
                Console.Error.WriteLine($"[WARN] Retrying ListModels after lazy ModManager initialization: {ex.Message}");
                return ListModelsOnce(kind);
            }
        }
        catch (Exception ex) { return ErrorWithTrace($"ListModels({kind}) failed", ex); }
    }

    private Dictionary<string, object?> ListModelsOnce(string kind)
    {
        EnsureModelDbInitialized();
        var rows = new List<Dictionary<string, object?>>();
        switch (kind)
        {
            case "encounter":
                for (var actIndex = 0; actIndex < ModelDb.ActsByIndex.Count; actIndex++)
                    foreach (var act in ModelDb.ActsByIndex[actIndex])
                    {
                        AddEncounters(rows, act.AllWeakEncounters, actIndex + 1, act, "weak");
                        AddEncounters(rows, act.AllRegularEncounters, actIndex + 1, act, "regular");
                        AddEncounters(rows, act.AllEliteEncounters, actIndex + 1, act, "elite");
                        AddEncounters(rows, act.AllBossEncounters, actIndex + 1, act, "boss");
                    }
                break;
            case "card":
                foreach (var card in ModelDb.AllCards)
                {
                    var row = new Dictionary<string, object?>
                    {
                        ["id"] = card.Id.Entry,
                        ["type"] = card.Type.ToString(),
                        ["rarity"] = card.Rarity.ToString(),
                    };
                    try
                    {
                        row["dynamic_vars"] = card.ToMutable().DynamicVars.Values
                            .Select(value => value.Name.ToLowerInvariant()).Distinct().ToList();
                    }
                    catch { }
                    rows.Add(row);
                }
                break;
            case "relic":
                foreach (var relic in ModelDb.AllRelics)
                    rows.Add(new Dictionary<string, object?> { ["id"] = relic.Id.Entry });
                break;
            case "monster":
                foreach (var monster in ModelDb.Monsters)
                    rows.Add(new Dictionary<string, object?> { ["id"] = monster.Id.Entry });
                break;
            case "potion":
                foreach (var potion in ModelDb.AllPotions)
                    rows.Add(new Dictionary<string, object?> { ["id"] = potion.Id.Entry });
                break;
            case "event":
                // Full ModelId form ("EVENT.X" / ancients' category) so vocab keys
                // match the event_id states serialize.
                foreach (var evt in ModelDb.AllEvents)
                    rows.Add(new Dictionary<string, object?> { ["id"] = evt.Id.ToString() });
                foreach (var ancient in ModelDb.AllAncients)
                    rows.Add(new Dictionary<string, object?> { ["id"] = ancient.Id.ToString() });
                break;
            case "power":
                foreach (var power in ModelDb.AllPowers)
                    rows.Add(new Dictionary<string, object?> { ["id"] = power.Id.Entry });
                break;
            case "orb":
                AddReflectedModels(rows, "orb");
                break;
            case "enchantment":
                AddReflectedModels(rows, "enchant");
                break;
            case "affliction":
                AddReflectedModels(rows, "affliction");
                break;
            case "character":
                foreach (var ch in ModelDb.AllCharacters)
                    rows.Add(new Dictionary<string, object?> { ["id"] = ch.Id.Entry });
                break;
            default:
                return Error($"Unknown model kind: {kind} (expected encounter/card/monster/relic/potion/event/power/orb/enchantment/affliction/character)");
        }
        return new Dictionary<string, object?>
        {
            ["type"] = "model_list",
            ["kind"] = kind,
            ["models"] = rows,
        };
    }

    /// <summary>
    /// Enumerate less frequently used ModelDb catalogs without taking a compile-time
    /// dependency on their generated property names. The game has renamed these
    /// collections across builds; their values still expose the stable ModelId.
    /// </summary>
    private static void AddReflectedModels(
        List<Dictionary<string, object?>> rows, string propertyNameFragment)
    {
        var seen = new HashSet<string>();
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        foreach (var property in typeof(ModelDb).GetProperties(flags))
        {
            if (!property.Name.Contains(propertyNameFragment, StringComparison.OrdinalIgnoreCase))
                continue;
            if (property.GetValue(null) is not IEnumerable values)
                continue;
            foreach (var model in values)
            {
                if (model == null) continue;
                var id = model.GetType().GetProperty("Id", flags | BindingFlags.Instance)?.GetValue(model);
                var entry = id?.GetType().GetProperty("Entry", flags | BindingFlags.Instance)?.GetValue(id)?.ToString();
                if (!string.IsNullOrWhiteSpace(entry) && seen.Add(entry))
                    rows.Add(new Dictionary<string, object?> { ["id"] = entry });
            }
        }
    }

    private static void AddEncounters(
        List<Dictionary<string, object?>> rows, IEnumerable<EncounterModel> encounters,
        int actNumber, ActModel act, string category)
    {
        foreach (var encounter in encounters)
            rows.Add(new Dictionary<string, object?>
            {
                ["id"] = encounter.Id.Entry,
                ["act"] = actNumber,
                ["act_id"] = act.Id.Entry,
                ["category"] = category,
            });
    }

    private static bool IsError(Dictionary<string, object?> response) =>
        response.TryGetValue("type", out var type) && type as string == "error";
}

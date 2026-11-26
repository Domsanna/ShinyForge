namespace ShinyForge.Services.HomeSorter;

/// <summary>One HOME swap: Y on <see cref="From"/>, then Y on <see cref="To"/> (an empty <see cref="To"/> makes it a move).</summary>
public sealed record SwapStep(int From, int To, string What);

/// <summary>The result of planning: where everything should end up and the swaps that get it there.</summary>
public sealed class SortPlan
{
    public Dictionary<int, HomeMon?> Target { get; } = new();
    public List<SwapStep> Steps { get; } = new();
    public List<string> Notes { get; } = new();
    public int Pokemon, Empty, Excluded, Unfilled;
    /// <summary>Living dex: the layout slots (0-based from the starting box) no Pokémon could be found for.</summary>
    public List<int> MissingEntries { get; } = new();
}

/// <summary>
/// Works out the sort without touching the console. Smarter than Pokémon Automation's sorters in three ways:
/// Pokémon that already sit in a slot they may stay in don't move (equal Pokémon and empty slots are interchangeable),
/// every swap puts at least one Pokémon in its final slot and the cursor never travels back for the next swap
/// (each swap starts where the last one ended), and Pokémon excluded by their markings are locked in place or kept out.
/// </summary>
public static class HomeSortPlanner
{
    // ================================================================ Box Sorter
    public static SortPlan PlanBoxSort(Dictionary<int, HomeMon?> slots, HomeSorterOptions o, int cursor)
    {
        var plan = new SortPlan();
        foreach (var m in slots.Values.OfType<HomeMon>()) m.Excluded = o.IsExcluded(m);
        var positions = slots.Keys.OrderBy(k => k).ToList();
        var fixedPos = positions.Where(p => slots[p] is { Unread: true } || o.Marked == MarkedBehaviour.StayPut && slots[p] is { Excluded: true }).ToHashSet();
        var free = positions.Where(p => !fixedPos.Contains(p)).ToList();
        var cmp = Comparer(o.Rules);

        // sorted order of the movable Pokémon (marked ones moved aside go last), then empties
        var movable = free.Where(p => slots[p] != null).Select(p => (Pos: p, Mon: slots[p]!)).ToList();
        var sortedKeys = movable.Where(x => !x.Mon.Excluded).OrderBy(x => x.Mon, cmp).ThenBy(x => x.Pos)
            .Concat(movable.Where(x => x.Mon.Excluded).OrderBy(x => x.Pos)).ToList();

        // equal Pokémon (the rules can't tell them apart) are interchangeable: keep the ones already in a slot of their group
        var desired = new Dictionary<int, int?>();       // position → the original position of the Pokémon that should end there
        foreach (var p in fixedPos) desired[p] = p;
        var i = 0;
        while (i < sortedKeys.Count)
        {
            var j = i + 1;
            while (j < sortedKeys.Count && sortedKeys[j].Mon.Excluded == sortedKeys[i].Mon.Excluded &&
                   (sortedKeys[i].Mon.Excluded ? false : cmp.Compare(sortedKeys[j].Mon, sortedKeys[i].Mon) == 0)) j++;
            var groupSlots = free.Skip(i).Take(j - i).ToList();
            var group = sortedKeys.Skip(i).Take(j - i).Select(x => x.Pos).ToList();
            var stay = group.Where(groupSlots.Contains).ToHashSet();
            var rest = new Queue<int>(group.Where(g => !stay.Contains(g)));
            foreach (var s in groupSlots) desired[s] = stay.Contains(s) ? s : rest.Dequeue();
            i = j;
        }
        foreach (var p in free.Skip(sortedKeys.Count)) desired[p] = null;

        Finish(plan, slots, desired, cursor);
        plan.Excluded = slots.Values.Count(m => m is { Excluded: true });
        if (fixedPos.Count > 0) plan.Notes.Add($"{fixedPos.Count} marked Pokémon stay where they are; the rest is sorted around them.");
        return plan;
    }

    /// <summary>Pokémon Automation's comparison: rules top to bottom, (value &lt; other) != reverse; booleans put "true" first.</summary>
    public static IComparer<HomeMon> Comparer(IReadOnlyList<SortRule> rules) => Comparer<HomeMon>.Create((a, b) =>
    {
        foreach (var r in rules)
        {
            var c = r.Criterion switch
            {
                SortCriterion.DexNumber => a.Dex.CompareTo(b.Dex),
                SortCriterion.Level => a.Level.CompareTo(b.Level),
                SortCriterion.Shiny => b.Shiny.CompareTo(a.Shiny),
                SortCriterion.Gigantamax => b.Gigantamax.CompareTo(a.Gigantamax),
                SortCriterion.Alpha => b.Alpha.CompareTo(a.Alpha),
                SortCriterion.Ball => string.CompareOrdinal(a.Ball, b.Ball),
                SortCriterion.Gender => GenderOrder(a.Gender).CompareTo(GenderOrder(b.Gender)),
                SortCriterion.Nature => string.CompareOrdinal(a.Nature, b.Nature),
                SortCriterion.Ability => string.CompareOrdinal(a.Ability, b.Ability),
                SortCriterion.OriginMark => string.CompareOrdinal(a.OriginMark, b.OriginMark),
                SortCriterion.LanguageOfOrigin => string.CompareOrdinal(a.Language, b.Language),
                SortCriterion.Marked => (b.Marks.Count(x => x != MarkColor.None)).CompareTo(a.Marks.Count(x => x != MarkColor.None)),
                SortCriterion.OtName => string.Compare(a.OtName, b.OtName, StringComparison.OrdinalIgnoreCase),
                _ => 0,
            };
            if (c != 0) return r.Reverse ? -c : c;
        }
        return 0;
    });

    private static int GenderOrder(string g) => g switch { "male" => 0, "female" => 1, _ => 2 };

    // ================================================================ Living Dex
    /// <summary>
    /// Pokémon Automation's living dex rules (is_viable_for_dex: OT name, shiny = shiny dex, dex number, gender for gender
    /// differences, both types for regional forms, Gigantamax for G-Max entries), planned all at once.
    /// </summary>
    /// <param name="outside">Slots in other boxes (not the dex or reject boxes) that were searched for missing Pokémon: a
    /// Pokémon there is only taken when it fills a dex slot, everything else in those boxes stays where it is.</param>
    public static SortPlan PlanLivingDex(Dictionary<int, HomeMon?> slots, List<DexEntry> template, HomeSorterOptions o, int cursor,
                                         IReadOnlyCollection<int>? outside = null)
    {
        var plan = new SortPlan();
        foreach (var m in slots.Values.OfType<HomeMon>()) m.Excluded = o.IsExcluded(m);
        var dexStart = (o.LivingDexStartBox - 1) * Slot.PerBox;
        var dexBoxes = (template.Count + Slot.PerBox - 1) / Slot.PerBox;
        var dexSlots = Enumerable.Range(dexStart, dexBoxes * Slot.PerBox).ToList();
        var rejectSlots = Enumerable.Range((o.RejectBoxStart - 1) * Slot.PerBox, (o.RejectBoxEnd - o.RejectBoxStart + 1) * Slot.PerBox).ToList();
        var dexSet = dexSlots.ToHashSet();
        var rejectSet = rejectSlots.ToHashSet();
        var outsideSet = (outside ?? Array.Empty<int>()).Where(p => !dexSet.Contains(p) && !rejectSet.Contains(p)).ToHashSet();

        // every Pokémon that could go in a dex slot, by dex number (dex boxes, reject boxes, searched boxes)
        var byDex = dexSlots.Concat(rejectSlots).Concat(outsideSet)
            .Where(p => slots.GetValueOrDefault(p) != null).GroupBy(p => slots[p]!.Dex).ToDictionary(g => g.Key, g => g.ToList());
        int Rank(int p) => dexSet.Contains(p) ? 0 : rejectSet.Contains(p) ? 1 : 2; // has to move anyway < reject < elsewhere

        // unread Pokémon always keep their slot; excluded ones too when they "stay put"
        var fixedPos = dexSlots.Concat(rejectSlots).Where(p => slots.GetValueOrDefault(p) is { Unread: true }
            || o.Marked == MarkedBehaviour.StayPut && slots.GetValueOrDefault(p) is { Excluded: true }).ToHashSet();
        var desired = new Dictionary<int, int?>();
        foreach (var p in fixedPos) desired[p] = p;
        var used = new HashSet<int>(fixedPos);

        // 1) a dex slot that already holds a fitting Pokémon keeps it
        for (var i = 0; i < template.Count; i++)
        {
            var pos = dexStart + i;
            if (fixedPos.Contains(pos) || template[i].IsEmpty) continue;
            if (slots.GetValueOrDefault(pos) is { } m && Fits(template[i], m, o)) { desired[pos] = pos; used.Add(pos); }
        }
        // 2) the rest get the best unused fitting Pokémon: one that has to leave its dex slot anyway, then one from the reject
        //    boxes, then one from any other box
        for (var i = 0; i < template.Count; i++)
        {
            var pos = dexStart + i;
            if (desired.ContainsKey(pos)) continue;
            if (template[i].IsEmpty) continue;
            var pick = (byDex.TryGetValue(template[i].NatId, out var c) ? c : new List<int>())
                .Where(p => !used.Contains(p) && Fits(template[i], slots[p]!, o))
                .OrderBy(Rank)
                .ThenBy(p => o.PickBestDuplicate ? Badness(slots[p]!, o) : 0)
                .ThenBy(p => Distance(p, pos))   // the copy that's quickest to bring over
                .Cast<int?>().FirstOrDefault();
            if (pick is { } pk) { desired[pos] = pk; used.Add(pk); }
            else { plan.Unfilled++; plan.MissingEntries.Add(i); }
        }
        foreach (var p in fixedPos.Where(p => p - dexStart < template.Count && p >= dexStart && !template[p - dexStart].IsEmpty))
            plan.Notes.Add($"Dex slot {p - dexStart + 1} ({template[p - dexStart].Slug}) is taken by a marked Pokémon that stays put.");

        // 3) everything else in the dex boxes goes to free reject slots (never onto a kept Pokémon); reject Pokémon stay;
        //    Pokémon in the other boxes stay unless they were taken for the dex (their slot is left empty)
        var leftovers = dexSlots.Where(p => slots.GetValueOrDefault(p) != null && !used.Contains(p)).ToList();
        var targetsTaken = desired.Keys.ToHashSet();
        var freeReject = rejectSlots.Where(p => !targetsTaken.Contains(p) && (slots.GetValueOrDefault(p) == null || used.Contains(p))).ToList();
        foreach (var p in rejectSlots.Where(p => slots.GetValueOrDefault(p) != null && !used.Contains(p))) { desired[p] = p; used.Add(p); }
        foreach (var p in outsideSet) desired[p] = used.Contains(p) || slots.GetValueOrDefault(p) == null ? null : p;
        var freeDexTail = dexSlots.Where(p => p - dexStart >= template.Count || template[p - dexStart].IsEmpty)
            .Where(p => !desired.ContainsKey(p)).ToList();
        foreach (var p in leftovers)
        {
            // the free slot that's quickest to reach (reject boxes first; they may be just "behind" box 1 - the boxes wrap)
            int? to = freeReject.Count > 0 ? freeReject.OrderBy(x => Distance(p, x)).First()
                : freeDexTail.Count > 0 ? freeDexTail.OrderBy(x => Distance(p, x)).First() : null;
            if (to == null)
                throw new InvalidOperationException("No empty slots are left to move Pokémon out of the way - remove some duplicates or make the reject box range bigger.");
            freeReject.Remove(to.Value);
            freeDexTail.Remove(to.Value);
            desired[to.Value] = p;
            used.Add(p);
        }
        foreach (var p in dexSlots.Concat(rejectSlots)) desired.TryAdd(p, null);

        var all = dexSlots.Concat(rejectSlots).Concat(outsideSet).Distinct().ToDictionary(p => p, p => slots.GetValueOrDefault(p));
        // nothing is parked in a reject box or a searched box unless it ends there
        Finish(plan, all, desired, cursor, rejectSet.Concat(outsideSet).ToHashSet());
        plan.Excluded = all.Values.Count(m => m is { Excluded: true });
        var fromOutside = outsideSet.Count(p => used.Contains(p));
        if (fromOutside > 0) plan.Notes.Add($"{fromOutside} Pokémon come from other boxes (found by searching all boxes).");
        if (plan.Unfilled > 0) plan.Notes.Add($"{plan.Unfilled} living dex slot(s) have no fitting Pokémon in the boxes read and stay empty.");
        return plan;
    }

    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["alolan"] = "alola", ["galarian"] = "galar", ["hisuian"] = "hisui", ["paldean"] = "paldea",
    };

    /// <summary>The words that make a form ("minior-red-core" → red, core), without the species, gender and shiny.</summary>
    private static HashSet<string> FormWords(string slug, string species) =>
        slug.Split('-').Select(t => Aliases.GetValueOrDefault(t, t))
            .Where(t => t.Length > 0 && t is not ("male" or "female" or "shiny" or "none") && !species.Split('-').Contains(t)).ToHashSet();

    /// <summary>
    /// Does the Pokémon's identified form fit the template slot? The slot's form words must all be in the icon's (a shiny
    /// Alcremie's icon only shows the sweet - every cream looks the same when shiny - so then only the sweet must match).
    /// Regional forms are told by their types instead (checked separately). Unknown form: only slots without a form.
    /// </summary>
    public static bool FormFits(DexEntry e, HomeMon m)
    {
        var species = e.Species.Length > 0 ? e.Species : e.Slug.Split('-')[0];
        var want = FormWords(e.Slug, species);
        want.RemoveWhere(t => t is "alola" or "galar" or "hisui" or "paldea" or "gmax" or "gigantamax"); // types / G-Max icon decide those
        if (want.Count == 0) return true;
        if (m.Form.Length == 0 || !m.FormSure) return false;
        var have = FormWords(m.Form, species);
        // shiny icons can merge forms (shiny Minior cores all look alike: "minior-core-shiny"): only what the icon shows must match
        return m.Shiny && have.Count < want.Count ? have.All(want.Contains) : want.All(have.Contains);
    }

    /// <summary>
    /// Pokémon Automation's living dex rules (is_viable_for_dex): not excluded, OT name (if filtered), shiny = the slot's
    /// shiny-ness, dex number, gender for gender differences, both types for regional forms, Gigantamax for G-Max entries.
    /// </summary>
    public static bool Fits(DexEntry e, HomeMon m, HomeSorterOptions o)
    {
        if (m.Excluded || o.IsExcluded(m)) return false;
        var otName = Normalize(o.OtName);
        if (o.OtNameLanguage.Length > 0 && otName.Length > 0 && Normalize(m.OtName) != otName) return false;
        if ((e.Shiny ?? o.ShinyDex) != m.Shiny) return false; // the regular part of a combined layout wants non-shinies
        if (e.NatId != m.Dex) return false;
        if (e.GenderDifference)
        {
            if (e.Slug.EndsWith("-female") && m.Gender != "female") return false;
            if (e.Slug.EndsWith("-male") && m.Gender != "male") return false;
        }
        // types tell regional forms apart; an unread type (no reference pictures) doesn't block
        if (m.PrimaryType.Length > 0 && (e.PrimaryType != m.PrimaryType || e.SecondaryType != m.SecondaryType)) return false;
        if (e.Form == "gigantamax" && !m.Gigantamax) return false;
        // forms: HOME doesn't say which form a Pokémon is, so its box icon decided (HomeMon.Form). A slot for a particular
        // form ("minior-red-core", "unown-q", "alcremie-ruby-cream-berry") only takes a Pokémon whose icon clearly is that form.
        if (!FormFits(e, m)) return false;
        // origin mark dex: only Pokémon with that mark (GO also from the "first met in Pokémon GO" line)
        if (o.OriginMarkFilter.Length > 0 && m.OriginMark != o.OriginMarkFilter && !(o.OriginMarkFilter == "go" && m.FromGo)) return false;
        return true;
    }

    /// <summary>Lower is better among duplicates: shiny for a shiny dex, OT name match, then the lower level.</summary>
    private static int Badness(HomeMon m, HomeSorterOptions o) =>
        (o.OtName.Length > 0 && Normalize(m.OtName) != Normalize(o.OtName) ? 1000 : 0) + m.Level;

    private static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // ================================================================ swaps
    /// <summary>
    /// Turns "position → original position of the Pokémon that ends there" into HOME swaps. Each swap fills one slot with
    /// its final Pokémon and the next swap starts in the slot the last one ended in, so the cursor never goes back.
    /// </summary>
    /// <param name="noParking">Slots a Pokémon may only be put in when it ends up there (the reject boxes): a Pokémon that
    /// belongs in the living dex is never parked in a reject box on the way to its dex slot.</param>
    private static void Finish(SortPlan plan, Dictionary<int, HomeMon?> slots, Dictionary<int, int?> desired, int cursor, HashSet<int>? noParking = null)
    {
        var at = slots.Keys.ToDictionary(p => p, p => slots[p] != null ? p : (int?)null); // position → original position of what's there now
        var where = at.Where(kv => kv.Value != null).ToDictionary(kv => kv.Value!.Value, kv => kv.Key);
        var targetOf = desired.Where(kv => kv.Value != null).ToDictionary(kv => kv.Value!.Value, kv => kv.Key);
        foreach (var (p, src) in desired) plan.Target[p] = src == null ? null : slots[src.Value];
        plan.Pokemon = slots.Values.Count(m => m != null);
        plan.Empty = slots.Count - plan.Pokemon;
        noParking ??= new HashSet<int>();

        bool Wrong(int p) => at[p] != desired.GetValueOrDefault(p);
        // would the Pokémon (original position) land in a no-parking slot that isn't its final one?
        bool Parks(int? item, int landing) => item is { } i && noParking.Contains(landing) && targetOf[i] != landing;
        void Swap(int a, int b)
        {
            string what;
            if (at[a] != null && at[b] != null) what = $"{Name(slots[at[a]!.Value]!)} ↔ {Name(slots[at[b]!.Value]!)}";
            else if (at[a] != null) what = $"{Name(slots[at[a]!.Value]!)} → empty slot";
            else what = $"{Name(slots[at[b]!.Value]!)} → empty slot";
            // HOME's Y starts on a Pokémon: begin on the slot that has one
            plan.Steps.Add(at[a] != null ? new SwapStep(a, b, what) : new SwapStep(b, a, what));
            (at[a], at[b]) = (at[b], at[a]);
            if (at[a] is { } ia) where[ia] = a;
            if (at[b] is { } ib) where[ib] = b;
        }

        // At a wrong slot t there are two moves, each puts at least one Pokémon in its final slot:
        //  fill  - bring t's final Pokémon in from s (t's Pokémon goes to s)
        //  send  - send t's Pokémon to its final slot u (u's Pokémon comes to t)
        // A move is "clean" when the Pokémon that gets pushed aside doesn't land in a reject slot it doesn't belong in.
        (int Other, bool Fill)? Move(int t, bool cleanOnly)
        {
            if (desired[t] is { } src)
            {
                var s = where[src];
                if (!cleanOnly || !Parks(at[t], s)) return (s, true);
            }
            if (at[t] is { } item)
            {
                var u = targetOf[item];
                if (!cleanOnly || !Parks(at[u], t)) return (u, false);
            }
            return null;
        }

        var here = cursor;
        var guard = 0;
        while (guard++ < 200000)
        {
            var wrong = at.Keys.Where(Wrong).ToList();
            if (wrong.Count == 0) break;
            // the nearest slot with a clean move (else the nearest at all - only when every move would park something)
            var byDistance = wrong.OrderBy(p => Distance(here, p)).ToList();
            var t = byDistance.FirstOrDefault(p => Move(p, true) != null, byDistance[0]);
            while (Wrong(t) && guard++ < 200000)
            {
                var m = Move(t, true) ?? (t == byDistance[0] && byDistance.All(p => Move(p, true) == null) ? Move(t, false) : null);
                if (m == null) break; // look for another slot with a clean move
                var (other, fill) = m.Value;
                Swap(t, other);
                here = other;
                if (fill) t = other; // the pushed-aside Pokémon is now at "other": carry on from there
            }
        }
    }

    private static string Name(HomeMon m) => m.Species.Length > 0 ? m.Species : $"#{m.Dex}";

    /// <summary>How many boxes HOME has (read from its counter, "N/300"). Its boxes wrap around: L on box 1 goes to the last box.</summary>
    public static int Boxes { get; set; } = Slot.MaxBoxes;

    /// <summary>L/R presses from one box to another, the shorter way round (verified: L on box 1 shows box 300).</summary>
    public static int BoxSteps(int from, int to)
    {
        var d = Math.Abs(from - to);
        return Math.Min(d, Boxes - d);
    }

    /// <summary>
    /// The cost of getting from one slot to another, in button presses (a box change counts double: HOME slides the box in
    /// and every change is checked on screen). Boxes the shorter way round; rows straight or, between the first and last row,
    /// the 3-press way round through the box name; columns the shorter way round the 6 columns.
    /// </summary>
    public static int Distance(int a, int b)
    {
        var sa = Slot.FromIndex(a); var sb = Slot.FromIndex(b);
        var rows = Math.Abs(sa.Row - sb.Row);
        var cols = Math.Abs(sa.Col - sb.Col);
        return BoxSteps(sa.Box, sb.Box) * 2 + (rows == 4 ? 3 : rows) + Math.Min(cols, Slot.Cols - cols);
    }
}

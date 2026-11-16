using IH.Shared;

namespace IH.Core;

public sealed partial class Game
{
    // ---------------------------------------------------------- turn flow
    private void StartFirstTurn()
    {
        if (S.Settings.Simultaneous)
        {
            foreach (var p in Active) BeginPlayerTurn(p);
            RunAiSimultaneous();
        }
        else
        {
            S.CurrentPlayer = Active.First().Id;
            BeginPlayerTurn(P(S.CurrentPlayer));
            if (P(S.CurrentPlayer).IsAI && Active.Any(p => !p.IsAI)) { RunAi(P(S.CurrentPlayer)); AdvanceSequential(); }
        }
    }

    private void DoEndTurn(Player p)
    {
        if (S.Settings.Simultaneous)
        {
            p.EndedTurn = true;
            TryAdvanceSimultaneous();
        }
        else
        {
            ProcessPlayerEnd(p);
            AdvanceSequential();
        }
    }

    /// <summary>Ends the turn for a player who cannot do it themselves (timer expired, disconnected).</summary>
    public void ForceEndTurn(int pid)
    {
        var p = P(pid);
        if (!p.Alive || p.Neutral) return;
        if (S.Settings.Simultaneous) { if (!p.EndedTurn) DoEndTurn(p); }
        else if (S.CurrentPlayer == pid) DoEndTurn(p);
    }

    /// <summary>Advances a game without human players by one global turn (used by simulations).</summary>
    public void StepAiOnly()
    {
        if (S.Settings.Simultaneous) TryAdvanceSimultaneous();
        else
        {
            var cur = P(S.CurrentPlayer);
            if (cur.Alive && cur.IsAI) RunAi(cur);
            AdvanceSequential();
        }
    }

    private void TryAdvanceSimultaneous()
    {
        if (Active.Any(x => !x.IsAI && !x.EndedTurn)) return;
        foreach (var p in Active.ToList()) if (p.Alive) ProcessPlayerEnd(p);
        GlobalEnd();
        foreach (var p in Active.ToList()) BeginPlayerTurn(p);
        RunAiSimultaneous();
    }

    private void RunAiSimultaneous()
    {
        foreach (var ai in Active.Where(x => x.IsAI).ToList())
        {
            if (!ai.Alive) continue;
            SimpleAI.TakeTurn(this, ai.Id);
            ai.EndedTurn = true;
        }
    }

    private void RunAi(Player ai)
    {
        SimpleAI.TakeTurn(this, ai.Id);
        if (ai.Alive) ProcessPlayerEnd(ai);
    }

    private void AdvanceSequential()
    {
        bool humans = Active.Any(p => !p.IsAI);
        for (int guard = 0; guard < 64; guard++)
        {
            var order = Active.Select(p => p.Id).ToList();
            if (order.Count == 0) return;
            int next = order.FirstOrDefault(id => id > S.CurrentPlayer, -1);
            bool wrapped = next < 0;
            if (wrapped) { GlobalEnd(); next = order[0]; }
            S.CurrentPlayer = next;
            var np = P(next);
            BeginPlayerTurn(np);
            if (!np.IsAI) return;
            if (!humans && wrapped) return; // AI-only game: one global turn per call
            RunAi(np);
        }
    }

    private void BeginPlayerTurn(Player p)
    {
        p.EndedTurn = false;
        foreach (var u in UnitsOf(p.Id).ToList())
        {
            u.Moves = u.MaxMoves;
            if (u.AutoExplore) ContinueExploring(p, u);
            else if (u.Goto >= 0) ContinueGoto(u);
        }
    }

    private void GlobalEnd()
    {
        S.Turn++;
        TickWeather();
        if (S.Turn % Rules.CongressPeriod == 0)
        {
            // world congress: whoever received the most favor chairs it until the next session
            int winner = -1, best = 0;
            foreach (var (id, votes) in S.Votes.OrderBy(v => v.Key))
                if (votes > best && P(id).Alive) { best = votes; winner = id; }
            S.Chair = winner;
            S.Votes.Clear();
            Emit(EventKind.Congress, winner, best);
            // ...and decides the resolution on the table; the next one in line is proposed for the following session
            bool passed = S.VotesFor > S.VotesAgainst;
            Emit(EventKind.Resolution, passed ? 1 : 0, S.VotesFor, S.VotesAgainst, s: S.Proposed);
            S.Resolution = passed ? S.Proposed : null;
            int next = (Array.FindIndex(Rules.Resolutions, r => r.Id == S.Proposed) + 1) % Rules.Resolutions.Length;
            S.Proposed = Rules.Resolutions[next].Id;
            S.VotesFor = S.VotesAgainst = 0;
        }
        AwardMilestones();
        foreach (var u in UnitsOf(Neutral.Id)) u.Hp = Math.Min(100, u.Hp + 10);
        foreach (var c in CitiesOf(Neutral.Id)) c.Culture += 1;
        RecomputeBorders();
        SpreadReligion();
        TickContracts();
        // Settle friendship together for both participants, regardless of who ended their turn first.
        foreach (var p in Active) p.Favor += FriendsOf(p).Count();
        foreach (var p in Active) RefreshEconomy(p);
        AntiSnowball.Recompute(this);
        foreach (var p in Active) RefreshEconomy(p);
        TickDiplomacy();
    }

    // ------------------------------------------------------------ economy
    /// <summary>Recomputes all derived per-city and per-player rates. Idempotent and cheap.</summary>
    public void RefreshEconomy(Player p)
    {
        var cities = CitiesOf(p.Id).OrderBy(c => c.Id).ToList();
        var capital = cities.FirstOrDefault(c => c.Capital);
        var taken = new HashSet<int>();
        p.HousingCapacity = cities.Sum(CityHousingCapacity);
        p.HousingUsed = cities.Sum(c => c.Pop) + UnitsOf(p.Id).Count(u => Rules.IsStacked(u.Def));
        double sci = 0, gold = 0, bUp = 0, maint = 0;
        var cand = new List<(int tile, int score, int f, int pr, int co)>();

        // connected resources: improved inside the borders, or lying under a city
        var connected = new SortedSet<int>();
        int roads = 0;
        p.HasLumberMill = p.HasQuarry = false;
        for (int i = 0; i < S.Tiles.Length; i++)
        {
            var t = S.Tiles[i];
            if (t.Owner != p.Id) continue;
            if (t.FireTurns == 0)
            {
                p.HasLumberMill |= t.Improvement == Improvement.LumberMill;
                p.HasQuarry |= t.Improvement == Improvement.Quarry;
            }
            if (t.Road && t.CityId < 0) roads++;
            if (t.Resource != Resource.None && (Rules.ResourceWorked(t) || t.CityId >= 0) && Rules.ResourceVisible(t.Resource, p.Has))
                connected.Add((int)t.Resource);
        }
        p.Resources = connected.ToList();
        p.ImportedResources = ContractImports(p.Id).ToList();
        p.Luxuries = connected.Count(r => Rules.Res((Resource)r).Class == ResClass.Luxury);
        // Links to other players: negotiated imports, long wars, shared faith.
        var agreements = S.LuxuryAgreements.Where(x => (x.From == p.Id || x.To == p.Id) && ValidLuxuryAgreement(x)).ToArray();
        var foreignLux = agreements.Where(x => x.To == p.Id && !connected.Contains(x.Resource)).Select(x => x.Resource).Distinct();
        int weariness = 0, brethren = 0;
        bool freeTradePartner = false;
        foreach (var o in Active)
        {
            if (o.Id == p.Id) continue;
            var rel = Rel(p.Id, o.Id);
            if (rel.AtWar) { if (S.Resolution == "peace" || S.Turn - rel.SinceTurn > Rules.WarWearinessAfter) weariness++; continue; }
            if (rel.Met && SameFaith(p.Id, o.Id)) brethren++;
            if (rel.OpenBorders && CanTrade(p, o)) freeTradePartner = true;
        }
        p.TradeLuxuries = Math.Min(Rules.MaxTradeLuxuries, foreignLux.Concat(p.ImportedResources
            .Where(r => !connected.Contains(r) && Rules.Res((Resource)r).Class == ResClass.Luxury)).Distinct().Count());
        p.WarWeariness = Math.Min(3, weariness);
        p.Brethren = brethren;
        bool chair = S.Chair == p.Id;
        double faith = 0, culture = 0;
        var fx = Rules.CivicBonus(p.Civics, p.CivicLevel);
        fx.Add(Rules.NodeBonus(p.Nodes)); // custom branches use the same kinds of bonus
        p.CivicFx = fx;
        var founded = FoundedBy(p.Id);
        // branch power comes from learned nodes and buildings in the player's cities
        var power = Rules.Branches.ToDictionary(b => b.Id, b => b.Nodes.Where(n => p.Nodes.Contains(n.Id)).Sum(n => n.Power)
            // ...and from the map: resources tied to the branch, once the branch is begun
            + (b.Nodes.Any(n => p.Nodes.Contains(n.Id)) ? b.PowerResources.Count(r => connected.Contains((int)Rules.ParseResource(r))) : 0));

        var reserved = new Dictionary<int, int>();
        foreach (var c in cities)
        {
            var belief = ReligionOf(c.Religion) is { } creed ? Rules.Beliefs[creed.Belief].Effect : null;
            int comfort = 5 + p.Luxuries + p.TradeLuxuries + p.Future[Rules.BranchSocial] / 2 + fx.Happy + (belief?.Happy ?? 0)
                + c.Buffs.Where(b => b.Stat == "Happy" && b.Until > S.Turn).Sum(b => b.Amount)
                + c.Buildings.Sum(id => Rules.Building(id)?.Happy ?? 0);
            c.Happy = Math.Max(0, comfort);
            c.Unhappy = Math.Max(0, c.Pop - c.Happy);
            int unassigned = c.Pop - c.Unhappy;
            foreach (int tile in c.CitizenAssignments)
            {
                if (unassigned == 0) break;
                if (CanWorkCityTile(p, c, tile) && reserved.TryAdd(tile, c.Id)) unassigned--;
            }
        }

        foreach (var c in cities)
        {
            // institutions apply everywhere, a belief only where the city follows its religion
            var bel = ReligionOf(c.Religion) is { } creed ? Rules.Beliefs[creed.Belief].Effect : null;
            int foodFlat = fx.Food + (bel?.Food ?? 0), comFlat = 0, culFlat = 1 + fx.Culture + (bel?.Culture ?? 0);
            double prodPct = fx.ProdPct + (bel?.ProdPct ?? 0), sciPct = fx.SciPct + (bel?.SciPct ?? 0), goldPct = fx.GoldPct + (bel?.GoldPct ?? 0);
            double culPct = fx.CulPct + (bel?.CulPct ?? 0), maintCut = 0, upkeep = 0, cityFaith = 0, foodPct = 0;
            // congress resolutions in force
            if (S.Resolution == "aid" && p.Lag > 0) prodPct += 15;
            if (S.Resolution == "trade" && freeTradePartner) goldPct += 10;
            foreach (var buff in c.Buffs)
            {
                if (buff.Until <= S.Turn) continue;
                switch (buff.Stat)
                {
                    case "Food": foodPct += buff.Amount; break;
                    case "Prod": prodPct += buff.Amount; break;
                    case "Sci": sciPct += buff.Amount; break;
                    case "Gold": goldPct += buff.Amount; break;
                    case "Culture": culPct += buff.Amount; break;
                }
            }
            foreach (var id in c.Buildings)
            {
                var b = Rules.Building(id);
                if (b == null) continue;
                foodFlat += b.Food; comFlat += b.Commerce; culFlat += b.Culture;
                prodPct += b.ProdPct; sciPct += b.SciPct; goldPct += b.GoldPct; culPct += b.CulPct;
                maintCut = Math.Max(maintCut, b.MaintCutPct); upkeep += b.Upkeep;
                cityFaith += b.Faith;
                if (b.With.Length > 0 && b.With.Any(r => connected.Contains((int)r)))
                {
                    // the building makes more of a resource the empire actually has
                    foodFlat += b.WithFood; prodPct += b.WithProdPct; goldPct += b.WithGoldPct;
                    cityFaith += b.WithFaith;
                }
                if (b.X.Power > 0 && Rules.BranchOfBuilding(b.Id) is { } owner)
                    power[owner.Id] = power.GetValueOrDefault(owner.Id) + b.X.Power;
            }
            int workers = c.Pop - c.Unhappy;

            cand.Clear();
            foreach (int t in Grid.Range(c.Tile, Rules.CityWorkRadius))
            {
                if (taken.Contains(t) || reserved.TryGetValue(t, out int other) && other != c.Id || !CanWorkCityTile(p, c, t)) continue;
                var (tf, tp, tc) = Rules.TileYield(Seen(p, S.Tiles[t]));
                cand.Add((t, tf * 4 + tp * 3 + tc * 2, tf, tp, tc));
            }
            cand.Sort((a, b) =>
            {
                int aPin = c.CitizenAssignments.IndexOf(a.tile), bPin = c.CitizenAssignments.IndexOf(b.tile);
                if (aPin != bPin)
                    return (aPin < 0 ? int.MaxValue : aPin).CompareTo(bPin < 0 ? int.MaxValue : bPin);
                return b.score != a.score ? b.score.CompareTo(a.score) : a.tile.CompareTo(b.tile);
            });

            var (f, pr, co) = Rules.TileYield(Seen(p, S.Tiles[c.Tile]), true);
            c.Worked.Clear();
            for (int i = 0; i < workers && i < cand.Count; i++)
            {
                c.Worked.Add(cand[i].tile); taken.Add(cand[i].tile);
                f += cand[i].f; pr += cand[i].pr; co += cand[i].co;
            }
            f += foodFlat; co += comFlat + workers; // every working citizen also trades
            if (foodPct != 0) f = (int)(f * (1 + foodPct / 100.0));

            c.FoodRate = f - 2 * c.Pop;
            c.ProdRate = (int)(pr * p.Bonus * (1 + prodPct / 100 + 0.05 * p.Future[Rules.BranchNano]));
            co = (int)Math.Round(co * p.Bonus);
            c.Commerce = co;
            double s = co * p.Slider / 100.0;
            c.SciRate = s * (1 + sciPct / 100 + 0.05 * p.Future[Rules.BranchQuantum]) * p.SciMult;
            c.GoldRate = (co - s) * (1 + goldPct / 100 + 0.05 * p.Future[Rules.BranchSpace]);
            c.CulRate = culFlat * (1 + culPct / 100 + 0.05 * p.Future[Rules.BranchSocial]) * p.CulMult;
            if (c.Queue.Count > 0 && c.Queue[0] == Rules.Wealth) c.GoldRate += c.ProdRate;
            if (chair) { c.GoldRate *= 1.1; c.CulRate *= 1.1; } // chair of the world congress
            int dist = capital == null ? 0 : Grid.Distance(capital.Tile, c.Tile);
            c.Maint = c.Capital ? 0 : (0.5 + dist / 8.0 + 0.25 * (cities.Count - 1)) * (1 - maintCut / 100);
            c.BuildingUpkeep = upkeep;
            faith += cityFaith * (1 + (fx.FaithPct + (bel?.FaithPct ?? 0)) / 100.0);
            culture += c.CulRate;
            sci += c.SciRate; gold += c.GoldRate; bUp += upkeep; maint += c.Maint;
        }

        int units = UnitsOf(p.Id).Count(u => !u.Def.X.Construct && u.Expires == 0); // constructs and summons cost nothing
        p.Income = gold; p.SciRate = sci;
        p.UnitUpkeep = Math.Max(0, units - (4 + 3 * cities.Count));
        p.BuildingUpkeep = bUp; p.CityMaint = maint; p.RoadUpkeep = roads * 0.15;
        if (founded != null)
        {
            // the founder is paid by the faithful: faith from every follower city, gold from the foreign ones
            foreach (var c in S.Cities.Values)
            {
                if (c.Religion != founded.Id) continue;
                faith += 1;
                if (c.Owner != p.Id) gold += 1;
            }
        }
        p.Income = gold;
        p.CultureRate = culture;
        double baseCost = (p.UnitUpkeep + p.BuildingUpkeep + p.CityMaint + p.RoadUpkeep) * (1 - Math.Min(60, fx.UpkeepCutPct) / 100.0);
        p.StrainCost = baseCost * (p.UpkeepMult - 1);
        p.FaithRate = faith;
        p.PowerRate = power;
        p.FavorRate = FriendsOf(p).Count() + fx.Favor;
        foreach (var b in Rules.Branches)
            p.BranchRate[b.Id] = BranchUnlocked(p, b) ? b.FromScience * sci + b.FromCulture * culture + b.FromFaith * faith : 0;
        p.EchoBonus = AntiSnowball.Echo(this, p);
        p.GoldRate = (int)Math.Floor(p.Income - baseCost - p.StrainCost);
    }

    private void ProcessPlayerEnd(Player p)
    {
        RefreshEconomy(p);

        foreach (var u in UnitsOf(p.Id).ToList())
        {
            if (u.Work != WorkKind.None && --u.WorkTurns <= 0)
            {
                ref var t = ref S.Tiles[u.Tile];
                if (Rules.WorkFits(Seen(p, t), u.Work))
                    t = Rules.AfterWork(t, u.Work);
                u.Work = WorkKind.None; u.WorkTurns = 0;
            }
            if (u.Def.X.Regen > 0) u.Hp = Math.Min(100, u.Hp + u.Def.X.Regen);
            if (u.Hp < 100 && u.Moves == u.MaxMoves && !u.Def.X.Construct)
            {
                var city = CityAt(u.Tile);
                u.Hp = Math.Min(100, u.Hp + (city != null && city.Owner == p.Id ? 25 : S.Tiles[u.Tile].Owner == p.Id ? 15 : 10));
            }
        }

        ProcessBranchEffects(p);

        foreach (var c in CitiesOf(p.Id).OrderBy(c => c.Id).ToList())
        {
            if (c.Owner != p.Id) continue;
            c.Food += c.FoodRate;
            int need = Rules.FoodToGrow(c.Pop);
            if (c.Food >= need)
            {
                if (CanGrow(p, c)) { c.Pop++; c.Food -= need; }
                else c.Food = need;
            }
            else if (c.Food < 0) { if (c.Pop > 1) c.Pop--; c.Food = 0; }
            ProcessProduction(p, c);
            c.Culture += Math.Max(1, (int)Math.Round(c.CulRate));
        }

        double sci = p.SciRate + p.EchoBonus + p.SciFrac;
        int add = (int)Math.Floor(sci);
        p.SciFrac = sci - add;
        p.ResearchProgress += add;
        if (p.Research != null)
        {
            var t = Rules.Tech(p.Research);
            if (t == null) p.Research = null;
            else
            {
                int cost = AntiSnowball.TechCost(this, p, t);
                if (p.ResearchProgress >= cost) { p.ResearchProgress -= cost; Learn(p, t); }
            }
        }

        p.Faith += (int)p.FaithRate;
        foreach (var b in Rules.Branches)
        {
            int have = p.BranchProgress.GetValueOrDefault(b.Id) + (int)Math.Round(p.BranchRate.GetValueOrDefault(b.Id));
            if (p.BranchCurrent.TryGetValue(b.Id, out var cur))
            {
                var node = Rules.Node(cur);
                if (node == null || p.Nodes.Contains(cur)) p.BranchCurrent.Remove(b.Id); // the mod changed under a saved game
                else if (have >= AntiSnowball.NodeCost(this, p, node))
                {
                    have -= AntiSnowball.NodeCost(this, p, node);
                    p.Nodes.Add(node.Id);
                    p.NodePoints += node.Cost;
                    p.BranchCurrent.Remove(b.Id);
                    Emit(EventKind.NodeDone, s: node.Id, only: p.Id);
                }
            }
            p.BranchProgress[b.Id] = have;
        }
        p.CivicProgress += (int)Math.Round(p.CultureRate);
        if (p.Civic != null)
        {
            var civic = Rules.Civic(p.Civic);
            if (civic == null) p.Civic = null;
            else if (p.CivicProgress >= AntiSnowball.CivicCost(this, p, civic))
            {
                p.CivicProgress -= AntiSnowball.CivicCost(this, p, civic);
                if (civic.Level > 0) p.CivicLevel = Math.Max(p.CivicLevel, civic.Level); else p.Civics.Add(civic.Id);
                p.Civic = null;
                AdvanceCivicPlan(p);
                p.CivicPoints += civic.Cost;
                p.ResearchProgress += (int)(civic.Cost * Rules.Inspiration); // a new institution inspires its scholars
                Emit(EventKind.CivicDone, s: civic.Id, only: p.Id);
            }
        }
        p.Favor += p.CivicFx.Favor;
        p.Gold += p.GoldRate;
        if (p.Gold < 0)
        {
            p.Gold = 0;
            if (p.Slider > 0)
            {
                p.Slider = Math.Max(0, p.Slider - 20);
                Emit(EventKind.Bankrupt, s: "финансирование науки урезано", only: p.Id);
            }
            else
            {
                var victim = UnitsOf(p.Id).Where(IsMilitary).OrderBy(u => UnitStr(u)).ThenBy(u => u.Id).FirstOrDefault();
                if (victim != null)
                {
                    RemoveUnit(victim);
                    Emit(EventKind.Bankrupt, s: $"распущен юнит ({victim.Def.Name})", only: p.Id);
                }
            }
        }
        RefreshEconomy(p);
    }

    /// <summary>World firsts: checked once per global turn; each milestone goes to the first civilisation to qualify.</summary>
    private void AwardMilestones()
    {
        foreach (var m in Rules.Milestones)
        {
            if (S.Milestones.ContainsKey(m.Id)) continue;
            foreach (var p in Active.OrderBy(x => x.Id).ToList())
            {
                int era = Rules.EraOf(p.Known, p.Future);
                bool done = m.Id switch
                {
                    "cities3" => CitiesOf(p.Id).Count() >= 3,
                    "religion" => FoundedBy(p.Id) != null,
                    "civics6" => p.Civics.Count >= 6,
                    "pop25" => CitiesOf(p.Id).Sum(c => c.Pop) >= 25,
                    "ocean" => p.Has("astronomy"),
                    "branch" => Rules.Branches.Any(b => b.Nodes.Length > 0 && b.Nodes.All(n => p.Nodes.Contains(n.Id))),
                    "future" => p.Future.Any(f => f > 0),
                    "era1" => era >= 1, "era2" => era >= 2, "era4" => era >= 4, "era6" => era >= 6,
                    _ => false,
                };
                if (!done) continue;
                S.Milestones[m.Id] = p.Id;
                p.Favor += Rules.MilestoneFavor;
                Emit(EventKind.Milestone, p.Id, s: m.Id);
                break;
            }
        }
    }

    /// <summary>End-of-turn mechanics that come from branch content: summons fading, auras, bombardment, power income.</summary>
    private void ProcessBranchEffects(Player p)
    {
        foreach (var u in UnitsOf(p.Id).Where(u => u.Expires > 0 && S.Turn + 1 >= u.Expires).ToList())
        {
            RemoveUnit(u);
            Emit(EventKind.Info, s: $"{u.Def.Name}: срок призыва истёк", only: p.Id);
        }
        foreach (var healer in UnitsOf(p.Id).Where(u => u.Def.X.HealAura > 0).OrderBy(u => u.Id).ToList())
            foreach (int t in Grid.Range(healer.Tile, 1))
                foreach (var f in At(t))
                    if (f.Owner == p.Id && f.Hp < 100 && !f.Def.X.Construct) f.Hp = Math.Min(100, f.Hp + healer.Def.X.HealAura);

        foreach (var c in CitiesOf(p.Id).OrderBy(c => c.Id).ToList())
        {
            c.Buffs.RemoveAll(b => b.Until <= S.Turn + 1);
            foreach (string id in c.Buildings.OrderBy(x => x, StringComparer.Ordinal))
            {
                var x = Rules.Building(id)?.X;
                if (x == null) continue;
                if (x.UnitHeal > 0)
                    foreach (int t in Grid.Range(c.Tile, 1))
                        foreach (var f in At(t))
                            if (f.Owner == p.Id && f.Hp < 100) f.Hp = Math.Min(100, f.Hp + x.UnitHeal);
                if (x.Bombard > 0)
                {
                    var target = S.Units.Values
                        .Where(e => e.Owner != p.Id && AtWar(p.Id, e.Owner) && Grid.Distance(c.Tile, e.Tile) <= Math.Max(1, x.BombardRange))
                        .OrderBy(e => Grid.Distance(c.Tile, e.Tile)).ThenBy(e => e.Id).FirstOrDefault();
                    if (target == null) continue;
                    int owner = target.Owner, tile = target.Tile;
                    target.Hp -= x.Bombard;
                    bool dead = target.Hp <= 0;
                    Emit(EventKind.Ranged, c.Tile, tile, p.Id, owner, dead ? "1" : "0", new[] { c.Tile, tile });
                    if (dead) { RemoveUnit(target); CheckDefeat(owner); }
                }
            }
        }
        foreach (var b in Rules.Branches)
            p.Power[b.Id] = Math.Min(Rules.PowerCap, p.Power.GetValueOrDefault(b.Id) + p.PowerRate.GetValueOrDefault(b.Id));
    }

    public void Learn(Player p, TechDef t)
    {
        if (t.Branch >= 0) p.Future[t.Branch] = Math.Max(p.Future[t.Branch], t.Level);
        else p.Known.Add(t.Id);
        p.TechPoints += t.Cost;
        p.CivicProgress += (int)(t.Cost * Rules.Inspiration); // discoveries feed public life
        if (p.Research == t.Id) p.Research = null;
        AdvanceResearchPlan(p);
        // newly understood strategic resources appear on every tile the player has already explored
        for (int i = 0; i < p.KnownTiles.Length && i < S.Tiles.Length; i++)
        {
            var r = S.Tiles[i].Resource;
            if (p.KnownTiles[i] != 0 && r != Resource.None && Rules.Res(r).Reveal == t.Id)
                p.KnownTiles[i] = (p.KnownTiles[i] & ~TileData.ResourceMask) | ((ulong)r << 6);
        }
        Emit(EventKind.TechDone, s: t.Id, only: p.Id);
    }

    private void ProcessProduction(Player p, City c)
    {
        if (c.Queue.Count == 0) { c.Prod = Math.Min(c.Prod + c.ProdRate, 60); return; }
        string item = c.Queue[0];
        if (item == Rules.Wealth) return; // already converted to gold in RefreshEconomy
        if (!CanBuild(p, c, item)) return;
        c.Prod += c.ProdRate * (item == "h:wood" && ActiveImprovement(p, Improvement.LumberMill) ? 2 : 1);
        int cost = CityProjectCost(item);
        if (c.Prod < cost) return;
        int type = item.StartsWith("u:") ? Rules.UnitIndex(item[2..]) : -1;
        if (type >= 0 && UnitProductionError(p, c, type) != null)
        {
            c.Prod = cost;
            return;
        }
        c.Prod -= cost;
        c.Queue.RemoveAt(0);
        DeliverProject(p, c, item, type);
    }

    private string? UnitProductionError(Player p, City c, int type)
    {
        if (!HasUnitSpace(p.Id, c.Tile, type)) return "В городе нет места для нового юнита";
        if (Rules.IsStacked(Rules.Units[type]) && !HasRecruitHousing(p)) return "В державе нет свободного жилья";
        if (Rules.Units[type].Role == UnitRole.Settler && c.Pop < 2) return "Для поселенца нужно население не меньше 2";
        return null;
    }

}

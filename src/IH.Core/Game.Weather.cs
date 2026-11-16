using IH.Shared;

namespace IH.Core;

public sealed partial class Game
{
    private const int WeatherRegionSide = 8;

    private void TickWeather()
    {
        // Weather follows the saved seed and turn without consuming combat or exploration rolls.
        ulong seed = S.WeatherSeed != 0 ? S.WeatherSeed : unchecked((uint)S.Settings.Seed);
        ulong turnSeed = seed ^ (unchecked((uint)S.Turn) * 0x9E3779B97F4A7C15UL);
        var random = new Random(unchecked((int)(turnSeed ^ (turnSeed >> 32))));
        var ignitions = new HashSet<int>();
        SetRegionalWeather(random, ignitions);

        // Newly ignited sites never spread during the same turn. Collect all spread before
        // extinguishing any current fires so the outcome is independent of neighbours' lifetimes.
        var burning = new List<int>();
        Span<int> neighbours = stackalloc int[6];
        for (int tile = 0; tile < Grid.Count; tile++)
        {
            if (S.Tiles[tile].FireTurns == 0) continue;
            burning.Add(tile);
            if (random.Next(100) >= Bal.ForestFireSpreadPct) continue;
            int count = 0;
            foreach (int next in Grid.Nb[tile])
                if (CanIgnite(next)) neighbours[count++] = next;
            if (count > 0) ignitions.Add(neighbours[random.Next(count)]);
        }
        foreach (int tile in burning)
            if (--S.Tiles[tile].FireTurns == 0) FinishFire(tile);
        foreach (int tile in ignitions)
            S.Tiles[tile].FireTurns = (byte)Math.Clamp(Bal.ForestFireTurns, 1, byte.MaxValue);

        foreach (int tile in S.ForestRegrowth.Where(x => x.Value <= S.Turn).Select(x => x.Key).ToArray())
        {
            S.ForestRegrowth.Remove(tile);
            ref var land = ref S.Tiles[tile];
            if (land.Passable && land.CityId < 0 && land.Improvement == Improvement.None &&
                land.Terrain is Terrain.Grassland or Terrain.Plains or Terrain.Tundra)
                land.Forest = true;
        }
    }

    private void SetRegionalWeather(Random random, HashSet<int> ignitions)
    {
        var strikeTargets = new List<int>(WeatherRegionSide * WeatherRegionSide);
        int stormChance = Math.Clamp(Bal.ThunderstormChancePct, 0, 100);
        int wetChance = Math.Min(100, stormChance + Math.Clamp(Bal.RainChancePct, 0, 100));
        for (int row = 0; row < Grid.H; row += WeatherRegionSide)
            for (int col = 0; col < Grid.W; col += WeatherRegionSide)
            {
                int roll = random.Next(100);
                var weather = roll < stormChance ? WeatherKind.Thunderstorm : roll < wetChance ? WeatherKind.Rain : WeatherKind.Clear;
                bool strike = weather == WeatherKind.Thunderstorm && random.Next(100) < Bal.LightningChancePct;
                strikeTargets.Clear();
                for (int y = row; y < Math.Min(row + WeatherRegionSide, Grid.H); y++)
                    for (int x = col; x < Math.Min(col + WeatherRegionSide, Grid.W); x++)
                    {
                        int tile = y * Grid.W + x;
                        ref var land = ref S.Tiles[tile];
                        land.Weather = weather;
                        land.Lightning = false;
                        if (weather == WeatherKind.Thunderstorm && land.CityId >= 0 &&
                            S.Cities.TryGetValue(land.CityId, out var city) && city.HouseCount(HouseMaterial.Clay) > 0 &&
                            random.Next(100) < Bal.ClayWashChancePct && city.RemoveHouse(HouseMaterial.Clay))
                            Emit(EventKind.Info, s: $"{city.Name}: ливень размыл глиняный дом.", only: city.Owner);
                        if (strike && CanIgnite(tile)) strikeTargets.Add(tile);
                    }
                if (strikeTargets.Count == 0) continue;
                int target = strikeTargets[random.Next(strikeTargets.Count)];
                S.Tiles[target].Lightning = true;
                ignitions.Add(target);
            }
    }

    private bool CanIgnite(int tile)
    {
        ref readonly var land = ref S.Tiles[tile];
        if (!land.Passable || land.FireTurns > 0) return false;
        return land.Forest || (land.CityId >= 0 && S.Cities.TryGetValue(land.CityId, out var city) &&
            city.HouseCount(HouseMaterial.Wood) > 0);
    }

    private void FinishFire(int tile)
    {
        ref var land = ref S.Tiles[tile];
        if (land.Forest)
        {
            land.Forest = false;
            if (land.Improvement == Improvement.LumberMill) land.Improvement = Improvement.None;
            S.ForestRegrowth[tile] = S.Turn + Math.Clamp(Bal.ForestRegrowthTurns, 1, 100_000);
        }
        if (land.CityId >= 0 && S.Cities.TryGetValue(land.CityId, out var city) && city.RemoveHouse(HouseMaterial.Wood))
            Emit(EventKind.Info, s: $"{city.Name}: пожар уничтожил деревянный дом.", only: city.Owner);
    }
}
